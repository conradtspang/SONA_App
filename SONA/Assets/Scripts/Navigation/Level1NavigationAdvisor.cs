using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

public class Level1NavigationAdvisor : MonoBehaviour
{
    public enum GuidanceState { Safe, Caution, Stop }

    [Header("Required Sources")]
    public ObstacleMOD obstacleMOD;
    public ObstacleReceiver obstacleReceiver;

    [Header("SLAM Depth Fusion")]
    public SlamDepthReceiver slamDepthReceiver;
    public bool preferSlamDepth = true;
    public int minSlamDepthPoints = 1;
    public bool showDistanceSource = true;

    [Header("Output")]
    public TMP_Text navigationText;
    public Text legacyNavigationText;
    public SpeechClient speechClient;
    public bool enableVoicePrompt = true;

    [Header("Risk Zones")]
    [Range(0f, 1f)] public float leftBoundary = 0.33f;
    [Range(0f, 1f)] public float rightBoundary = 0.66f;
    [Range(0f, 1f)] public float minCenterYForPathRisk = 0.35f;

    [Header("BBox Distance Fallback")]
    public float distanceScale = 0.54f;
    public float cautionDistanceMeters = 1.60f;
    public float stopDistanceMeters = 0.75f;
    [Range(0.01f, 1f)] public float minBoxHeightRatio = 0.10f;
    [Range(0.01f, 1f)] public float cautionBoxHeightRatio = 0.18f;
    [Range(0.01f, 1f)] public float stopBoxHeightRatio = 0.36f;

    [Header("Track Filtering")]
    public int minTrackAge = 2;
    public int maxFramesSinceUpdate = 2;

    [Header("Prompt Rate Limiting")]
    public float visualUpdateInterval = 0.15f;
    public float voiceCooldownSeconds = 3.5f;
    public float repeatSameMessageCooldownSeconds = 6.0f;
    public bool debugLog = true;

    [Header("Obstacle Status UI Integration")]
    public TrajectoryPanelRenderer pathRenderer;
    public Image statusPanel;
    public Sprite clearBackground;
    public Sprite dangerBackground;
    public Image statusIcon;
    public Sprite clearIcon;
    public Sprite dangerIcon;
    public TMP_Text statusUiText; // Box 1: Obstacle too close / Path clear
    [Header("Specific Warning Text Boxes")]
    public TMP_Text pathClearText;
    public TMP_Text pathClearText2;
    public TMP_Text obstacleTooCloseText1;
    public TMP_Text obstacleTooCloseText2;
    public Color clearColor = new Color(0.1f, 0.7f, 0.1f, 1f);
    public Color clearBgColor = new Color(0.9f, 1f, 0.9f, 1f);
    public Color dangerColor = new Color(0.8f, 0.1f, 0.1f, 1f);
    public Color dangerBgColor = new Color(1f, 0.9f, 0.9f, 1f);

    [Header("Flashing Settings")]
    public float flashSpeed = 5.0f;
    private float _flashAlpha = 1.0f;

    private readonly List<Track> _latestTracks = new List<Track>();
    private float _lastVisualUpdateTime = -999f;
    private float _lastVoiceTime = -999f;
    private string _lastVoiceMessage = "";
    private float _lastSameMessageTime = -999f;
    private GuidanceState _lastState = GuidanceState.Safe;

    private class Candidate
    {
        public Track track;
        public string label;
        public string direction;
        public float centerX;
        public float centerY;
        public float boxHeight;
        public float boxArea;
        public float distanceMeters;
        public bool usedSlamDepth;
        public int slamPointCount;
        public string distanceSource;
        public GuidanceState state;
        public float riskScore;
    }

    private void Awake()
    {
        if (obstacleMOD == null) obstacleMOD = GetComponent<ObstacleMOD>();
        if (obstacleReceiver == null) obstacleReceiver = GetComponent<ObstacleReceiver>();

        if (slamDepthReceiver == null)
            slamDepthReceiver = FindObjectOfType<SlamDepthReceiver>();

        if (slamDepthReceiver == null)
        {
            slamDepthReceiver = gameObject.AddComponent<SlamDepthReceiver>();
            slamDepthReceiver.depthListenPort = 5007;
            slamDepthReceiver.autoStart = true;
            slamDepthReceiver.debugLog = true;
            slamDepthReceiver.maxDepthAgeSeconds = 1.20f;
            slamDepthReceiver.bboxInnerMarginRatio = -0.05f;
            slamDepthReceiver.minPointsForMedian = 1;
            slamDepthReceiver.enableAdaptiveExpansion = true;
            slamDepthReceiver.expansionSteps = 3;
            slamDepthReceiver.expansionStepRatio = 0.08f;
            slamDepthReceiver.useTemporalCache = true;
            slamDepthReceiver.temporalCacheSeconds = 1.20f;
            slamDepthReceiver.maxCachedPackets = 12;
            Debug.Log("[Level1NavigationAdvisor] Auto-added SlamDepthReceiver on " + gameObject.name);
        }

        if (speechClient == null) speechClient = FindObjectOfType<SpeechClient>();
    }

    private void OnEnable()
    {
        if (obstacleMOD != null) obstacleMOD.OnTracksUpdated += HandleTracksUpdated;
    }

    private void OnDisable()
    {
        if (obstacleMOD != null) obstacleMOD.OnTracksUpdated -= HandleTracksUpdated;
    }

    private void Update()
    {
        // Handle Icon Flashing
        if (_lastState != GuidanceState.Safe && statusIcon != null)
        {
            _flashAlpha = 0.4f + 0.6f * Mathf.Abs(Mathf.Sin(Time.time * flashSpeed));
            Color c = statusIcon.color;
            c.a = _flashAlpha;
            statusIcon.color = c;
        }
        else if (statusIcon != null)
        {
            Color c = statusIcon.color;
            c.a = 1.0f;
            statusIcon.color = c;
        }

        if (Time.time - _lastVisualUpdateTime < visualUpdateInterval) return;
        _lastVisualUpdateTime = Time.time;
        lock (_latestTracks) UpdateGuidance(new List<Track>(_latestTracks));
    }

    private void HandleTracksUpdated(List<Track> tracks)
    {
        lock (_latestTracks)
        {
            _latestTracks.Clear();
            if (tracks != null) _latestTracks.AddRange(tracks);
        }
    }

    private void UpdateGuidance(List<Track> tracks)
    {
        Candidate best = SelectHighestRiskCandidate(tracks);
        string message;
        GuidanceState state;

        if (best == null)
        {
            state = GuidanceState.Safe;
            message = AppLanguageManager.Instance.GetText("ui", "path_clear");
        }
        else
        {
            state = best.state;
            string template = AppLanguageManager.Instance.GetText("templates", "warning");
            message = string.Format(template, best.direction, best.distanceMeters, best.label);
        }

        SetNavigationText(message, state);
        UpdateStatusUi(state, best, tracks, message);
        
        MaybeSpeak(best, state);

        _lastState = state;
    }

    private Candidate SelectHighestRiskCandidate(List<Track> tracks)
    {
        if (tracks == null || tracks.Count == 0) return null;
        Candidate best = null;

        foreach (Track track in tracks)
        {
            if (track == null) continue;
            if (track.age < minTrackAge) continue;
            if (track.framesSinceLastUpdate > maxFramesSinceUpdate) continue;

            Rect box = NormalizeRect(track.lastBox);
            float h = Mathf.Clamp01(box.height);
            float area = Mathf.Clamp01(box.width * box.height);
            float cx = Mathf.Clamp01(box.center.x);
            float cy = Mathf.Clamp01(box.center.y);
            if (h < minBoxHeightRatio) continue;
            if (cy < minCenterYForPathRisk) continue;

            bool usedSlamDepth = false;
            int slamPointCount = 0;
            string source = "ground plane fallback";
            float distance = EstimateDistanceFromGroundPlane(box.yMax);

            if (preferSlamDepth && slamDepthReceiver != null)
            {
                SlamDepthReceiver.DepthQueryResult q = slamDepthReceiver.QueryDepthForNormalizedRect(box);
                if (q.ok && q.pointCount >= minSlamDepthPoints)
                {
                    distance = q.medianDepth;
                    usedSlamDepth = true;
                    slamPointCount = q.pointCount;
                    source = $"{q.source}, {q.pointCount} pts";
                }
            }

            GuidanceState state = GuidanceState.Safe;
            if (distance <= stopDistanceMeters || h >= stopBoxHeightRatio) state = GuidanceState.Stop;
            else if (distance <= cautionDistanceMeters || h >= cautionBoxHeightRatio) state = GuidanceState.Caution;
            if (state == GuidanceState.Safe) continue;

            float centerWeight = 1f - Mathf.Clamp01(Mathf.Abs(cx - 0.5f) / 0.5f);
            float depthBonus = usedSlamDepth ? 15f : 0f;
            float risk = (state == GuidanceState.Stop ? 100f : 40f) + area * 80f + h * 50f + centerWeight * 20f + depthBonus + (track.isMoving ? 10f : 0f);

            Candidate c = new Candidate
            {
                track = track,
                label = TranslateLabel(track.label),
                direction = DirectionName(cx), 
                centerX = cx,
                centerY = cy,
                boxHeight = h,
                boxArea = area,
                distanceMeters = Mathf.Clamp(distance, 0.15f, 8f),
                usedSlamDepth = usedSlamDepth,
                slamPointCount = slamPointCount,
                distanceSource = source,
                state = state,
                riskScore = risk
            };

            if (best == null || c.riskScore > best.riskScore) best = c;
        }
        return best;
    }

    private Rect NormalizeRect(Rect box)
    {
        if (box.width > 1.5f || box.height > 1.5f || box.xMax > 1.5f || box.yMax > 1.5f)
            return new Rect(box.x / 1920f, box.y / 1080f, box.width / 1920f, box.height / 1080f);
        return new Rect(Mathf.Clamp01(box.x), Mathf.Clamp01(box.y), Mathf.Clamp01(box.width), Mathf.Clamp01(box.height));
    }

    [Header("Ground Plane Fallback Camera Parameters")]
    public float cameraHeightMeters = 1.0f; 
    public float cameraPitchDegrees = 10.0f; // Positive = tilted downward
    public float cameraFocalLengthY = 274.10723f; // From Camera1.fy
    public float cameraCenterY = 176.27777f;      // From Camera1.cy
    public float cameraImageHeight = 360f;        // From Camera.height

    private float EstimateDistanceFromGroundPlane(float normalizedBoxBottomY)
    {
        // 1. Convert normalized Y (0.0 to 1.0) to pixel Y (0 to 360)
        float pixelY = normalizedBoxBottomY * cameraImageHeight;
        
        // 2. Calculate the angle of the ray passing through the bottom pixel
        float alphaRad = Mathf.Atan((pixelY - cameraCenterY) / cameraFocalLengthY);
        
        // 3. Add the physical camera tilt (converted to radians)
        float thetaRad = cameraPitchDegrees * Mathf.Deg2Rad;
        float gammaRad = thetaRad + alphaRad;
        
        // Prevent division by zero if looking above the horizon
        if (gammaRad <= 0.01f) return 5.0f; // Max fallback distance
        
        // 4. Calculate distance along the ground
        float distance = cameraHeightMeters / Mathf.Tan(gammaRad);
        
        // Clamp to a reasonable range
        return Mathf.Clamp(distance, 0.25f, 5.0f);
    }

    private string DirectionName(float centerX)
    {
        if (centerX < leftBoundary) return AppLanguageManager.Instance.GetText("ui", "obstacle_left");
        if (centerX > rightBoundary) return AppLanguageManager.Instance.GetText("ui", "obstacle_right");
        return AppLanguageManager.Instance.GetText("ui", "obstacle_ahead");
    }

    private string BuildAvoidanceSuggestion(Candidate obstacle, List<Track> tracks)
    {
        // Suggestions are not yet fully localized in JSON, using simple logic for now
        string moveLeft = AppLanguageManager.Instance.currentLanguage == "es" ? "Mover a la izquierda" : "Move left";
        string moveRight = AppLanguageManager.Instance.currentLanguage == "es" ? "Mover a la derecha" : "Move right";

        if (obstacle.centerX < leftBoundary) return moveRight;
        if (obstacle.centerX > rightBoundary) return moveLeft;

        float leftScore = 0f;
        float rightScore = 0f;
        if (tracks != null)
        {
            foreach (Track t in tracks)
            {
                if (t == null || t.framesSinceLastUpdate > maxFramesSinceUpdate) continue;
                Rect b = NormalizeRect(t.lastBox);
                float area = Mathf.Clamp01(b.width * b.height);
                float h = Mathf.Clamp01(b.height);
                if (h < minBoxHeightRatio) continue;
                if (b.center.x < 0.5f) leftScore += area + h * 0.25f;
                else rightScore += area + h * 0.25f;
            }
        }
        return leftScore <= rightScore ? moveLeft : moveRight;
    }

    private string TranslateLabel(string rawLabel)
    {
        if (string.IsNullOrWhiteSpace(rawLabel)) return "obstacle";
        string label = rawLabel.Trim().ToLowerInvariant();
        return AppLanguageManager.Instance.GetText("hazards", label);
    }

    private void SetNavigationText(string message, GuidanceState state)
    {
        bool isDanger = (state == GuidanceState.Stop || state == GuidanceState.Caution);

        if (navigationText != null) 
        {
            // Box 2: Warning text. Only visible when there is an obstacle.
            navigationText.gameObject.SetActive(isDanger);
            navigationText.text = message;
            navigationText.color = dangerColor; // Red per user request
        }

        if (legacyNavigationText != null) 
        {
            legacyNavigationText.gameObject.SetActive(isDanger);
            legacyNavigationText.text = message;
            legacyNavigationText.color = dangerColor;
        }

        if (debugLog && isDanger) Debug.Log("[Level1NavigationAdvisor] " + message);
    }

    private void MaybeSpeak(Candidate best, GuidanceState state)
    {
        if (!enableVoicePrompt || speechClient == null) return;
        if (state == GuidanceState.Safe && _lastState == GuidanceState.Safe) return;
        if (Time.time - _lastVoiceTime < voiceCooldownSeconds) return;

        string finalMessage;

        if (state == GuidanceState.Safe)
        {
            finalMessage = AppLanguageManager.Instance.GetText("ui", "path_clear");
        }
        else
        {
            if (best == null) return;
            string localizedDir = DirectionName(best.centerX);
            string template = AppLanguageManager.Instance.GetText("templates", "warning");
            finalMessage = string.Format(template, localizedDir, best.distanceMeters, best.label);
        }

        if (finalMessage == _lastVoiceMessage && Time.time - _lastSameMessageTime < repeatSameMessageCooldownSeconds) return;

        _lastVoiceTime = Time.time;
        if (finalMessage == _lastVoiceMessage) _lastSameMessageTime = Time.time;
        else { _lastVoiceMessage = finalMessage; _lastSameMessageTime = Time.time; }

        speechClient.Speak(finalMessage, AppLanguageManager.Instance.currentLanguage);
    }

    public void SpeakExecutingCommand(string commandText)
    {
        if (speechClient == null) return;
        string prefix = AppLanguageManager.Instance.GetText("ui", "executing_prefix");
        string message = prefix + commandText;
        speechClient.Speak(message, AppLanguageManager.Instance.currentLanguage);
    }

    private void UpdateStatusUi(GuidanceState state, Candidate best, List<Track> tracks, string message)
    {
        bool isDanger = (state == GuidanceState.Stop || state == GuidanceState.Caution);
        SetStatus(isDanger, message);

        if (pathRenderer != null)
        {
            if (!isDanger || best == null)
            {
                pathRenderer.SetSimulatedAvoidancePath("straight");
            }
            else
            {
                string internalDirection = "left";
                if (best.centerX < leftBoundary) internalDirection = "right";
                else if (best.centerX > rightBoundary) internalDirection = "left";
                else
                {
                    float leftScore = 0f, rightScore = 0f;
                    if (tracks != null)
                    {
                        foreach (Track t in tracks)
                        {
                            if (t == null || t.framesSinceLastUpdate > maxFramesSinceUpdate) continue;
                            Rect b = NormalizeRect(t.lastBox);
                            float area = Mathf.Clamp01(b.width * b.height);
                            float h = Mathf.Clamp01(b.height);
                            if (h < minBoxHeightRatio) continue;
                            if (b.center.x < 0.5f) leftScore += area + h * 0.25f;
                            else rightScore += area + h * 0.25f;
                        }
                    }
                    internalDirection = leftScore <= rightScore ? "left" : "right";
                }
                pathRenderer.SetSimulatedAvoidancePath(internalDirection);
            }
        }
    }

    private void SetStatus(bool isDanger, string message = "")
    {
        if (statusPanel != null)
        {
            statusPanel.sprite = isDanger ? dangerBackground : clearBackground;
            // Set color to white if we have a custom sprite to avoid tinting the PNG
            if (statusPanel.sprite != null) statusPanel.color = Color.white;
            else statusPanel.color = isDanger ? dangerBgColor : clearBgColor;
        }

        if (statusIcon != null)
        {
            statusIcon.sprite = isDanger ? dangerIcon : clearIcon;
            if (statusIcon.sprite != null) 
            {
                // Flashing is handled in Update() via alpha pulsing.
                // We keep the icon color white if it's a full-color sprite.
                statusIcon.color = Color.white;
            }
        }

        if (statusUiText != null)
        {
            // Box 1: "Obstacle too close" (Danger) or "Path clear" (Safe)
            statusUiText.text = isDanger 
                ? AppLanguageManager.Instance.GetText("ui", "obstacle too close") 
                : AppLanguageManager.Instance.GetText("ui", "path clear");
            
            // Set color per user request: Red for obstacle, Green for safe
            statusUiText.color = isDanger ? dangerColor : clearColor;
        }

        // Specific separate text boxes
        if (pathClearText != null)
        {
            pathClearText.gameObject.SetActive(!isDanger);
            if (!isDanger)
            {
                pathClearText.text = AppLanguageManager.Instance.GetText("ui", "path_clear");
                pathClearText.color = clearColor;
            }
        }

        if (pathClearText2 != null)
        {
            pathClearText2.gameObject.SetActive(!isDanger);
            if (!isDanger)
            {
                pathClearText2.text = "Clear 2m ahead";
                pathClearText2.color = clearColor;
            }
        }

        if (obstacleTooCloseText1 != null)
        {
            obstacleTooCloseText1.gameObject.SetActive(isDanger);
            if (isDanger)
            {
                obstacleTooCloseText1.text = AppLanguageManager.Instance.GetText("ui", "obstacle_too_close");
                obstacleTooCloseText1.color = dangerColor;
            }
        }

        if (obstacleTooCloseText2 != null)
        {
            obstacleTooCloseText2.gameObject.SetActive(isDanger);
            if (isDanger)
            {
                // Use the detailed message for the second box
                obstacleTooCloseText2.text = message;
                obstacleTooCloseText2.color = dangerColor;
            }
        }
    }
}
