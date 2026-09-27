/**
 * SLAMReceiver API (Consolidated Workflow):
 * - ToggleMappingWorkflow(): Start Mapping -> (Wait for Readiness) -> Start. Click again to Stop -> Save -> Get Path -> Exit.
 * - ToggleLocalizationWorkflow(): Start Localization -> (Wait for Readiness) -> Start. Click again to Stop -> Exit.
 * - CalibAction(): Start Calibration or Capture Frame.
 * - CalibSaveAndQuit(): Save calibration results and exit mode.
 */

using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using TMPro;
using UnityEngine;
using UnityEngine.Video;
using UnityEngine.UI;

public class SLAMReceiver : MonoBehaviour
{
    public enum SlamFeatureState
    {
        Idle,
        Starting,
        Running,
        Finishing
    }

    [Header("Network")]
    public string slamIP = "172.17.144.1";
    public int slamControlPort = 6000;
    public int udpListenPort = 5006;
    public int streamPort = 8180;

    [Header("Display")]
    public TMP_Text statusText;
    public TMP_Text serverIpText;
    public TrajectoryPanelRenderer trajectoryRenderer;
    public Level1NavigationAdvisor navigationAdvisor;

    [Header("UI Buttons (Consolidated)")]
    public Button mappingButton;
    public TMP_Text mappingButtonText;
    public Button localizationButton;
    public TMP_Text localizationButtonText;
    public Button calibActionButton;
    public TMP_Text calibActionText;
    public Button calibEndButton;

    [Header("Unity Video Sync")]
    public VideoPlayer unityVideoPlayer;
    public bool autoPlayUnityVideoOnStart = true;

    [Header("Localization Display")]
    public bool loadMapPathBeforeLocalization = true;
    public bool preserveMapPathDuringLocalizationStart = true;
    public bool drawCameraMarkerInMapping = false;
    public bool addLocalizationTrailFromPose = true;

    [Header("Options")]
    public bool autoStartUdp = true;
    public bool verboseLog = true;

    [Header("Auto Relocalization Retry (Unity Watchdog)")]
    public bool enableAutoRelocalizationRetry = true;
    public int maxRelocalizationRetries = 4;
    public float firstPoseTimeoutSeconds = 4.0f;
    public float retryModeSettleSeconds = 0.35f;
    public float retryStartSettleSeconds = 0.25f;
    public bool retryByRestartingLocalizationMode = true;
    public bool retryReloadMapPath = false;

    private UdpClient _udpClient;
    private Thread _udpThread;
    private volatile bool _udpRunning;
    private readonly ConcurrentQueue<string> _udpQueue = new ConcurrentQueue<string>();

    private Vector3 _latestPosition = Vector3.zero;
    private Quaternion _latestRotation = Quaternion.identity;
    private bool _hasPose;
    
    // Feature States
    private SlamFeatureState _mappingState = SlamFeatureState.Idle;
    private SlamFeatureState _locState = SlamFeatureState.Idle;
    private SlamFeatureState _calibState = SlamFeatureState.Idle;

    private Coroutine _localizationWatchdogCoroutine;
    private int _localizationRetryCount = 0;
    private float _lastLocalizationStartTime = -999f;
    private bool _receivedLocalizationPoseAfterStart = false;

    public Vector3 LatestPosition => _latestPosition;
    public Quaternion LatestRotation => _latestRotation;
    public bool HasPose => _hasPose;

    public SlamFeatureState MappingState => _mappingState;
    public SlamFeatureState LocState => _locState;
    public SlamFeatureState CalibState => _calibState;

    void Start()
    {
        if (navigationAdvisor == null) navigationAdvisor = FindObjectOfType<Level1NavigationAdvisor>();
        UpdateServerIpText();
        UpdateUiVisuals();
        if (autoStartUdp)
            StartUdpListener();
    }

    void Update()
    {
        while (_udpQueue.TryDequeue(out string msg))
            HandleUdpMessage(msg);
    }

    void OnDisable() => StopUdpListener();
    void OnDestroy() => StopUdpListener();

    void UpdateServerIpText()
    {
        if (serverIpText != null)
            serverIpText.text = "STREAM\nhttp://" + slamIP + ":" + streamPort;
    }

    public void StartUdpListener()
    {
        if (_udpRunning) return;
        try
        {
            _udpClient = new UdpClient(udpListenPort);
            _udpRunning = true;
            _udpThread = new Thread(ReceiveUdpLoop) { IsBackground = true };
            _udpThread.Start();
            if (verboseLog) Debug.Log("[SLAMReceiver] UDP listening on " + udpListenPort);
        }
        catch (Exception e)
        {
            _udpRunning = false;
            Debug.LogError("[SLAMReceiver] Failed to start UDP listener: " + e.Message);
        }
    }

    public void StopUdpListener()
    {
        _udpRunning = false;
        try { _udpClient?.Close(); } catch { }
        _udpClient = null;
        try { if (_udpThread != null && _udpThread.IsAlive) _udpThread.Join(300); } catch { }
        _udpThread = null;
    }

    void ReceiveUdpLoop()
    {
        IPEndPoint remote = new IPEndPoint(IPAddress.Any, 0);
        while (_udpRunning)
        {
            try
            {
                byte[] data = _udpClient.Receive(ref remote);
                if (data == null || data.Length == 0) continue;
                _udpQueue.Enqueue(Encoding.UTF8.GetString(data).Trim());
            }
            catch { break; }
        }
    }

    void HandleUdpMessage(string msg)
    {
        if (string.IsNullOrWhiteSpace(msg)) return;
        if (verboseLog) Debug.Log("[SLAMReceiver] UDP <= " + msg);

        if (msg.StartsWith("RESET|", StringComparison.Ordinal))
        {
            if (_locState == SlamFeatureState.Running && preserveMapPathDuringLocalizationStart)
                trajectoryRenderer?.ClearLocalizationOnly();
            else
                trajectoryRenderer?.ClearAll();
            _hasPose = false;
            return;
        }

        if (msg.StartsWith("STATUS|", StringComparison.Ordinal))
        {
            if (statusText != null) statusText.text = msg.Substring("STATUS|".Length);
            return;
        }

        if (msg == "CALIB_FINISHED")
        {
            if (verboseLog) Debug.Log("[SLAMReceiver] Calibration finished signal received via UDP.");
            _calibState = SlamFeatureState.Idle;
            UpdateUiVisuals();
            return;
        }

        if (msg.StartsWith("LOCPOSE|", StringComparison.Ordinal) || msg.StartsWith("LOCALIZATION_POSE|", StringComparison.Ordinal))
        {
            _receivedLocalizationPoseAfterStart = true;
            ParsePose(msg.Substring(msg.IndexOf('|') + 1), true);
            return;
        }

        if (msg.StartsWith("LOCPATH|", StringComparison.Ordinal) || msg.StartsWith("LOCALIZATION_PATH|", StringComparison.Ordinal))
        {
            _receivedLocalizationPoseAfterStart = true;
            ParseRealtimePathPoint(msg.Substring(msg.IndexOf('|') + 1), true);
            return;
        }

        if (msg.StartsWith("MAPPOSE|", StringComparison.Ordinal) || msg.StartsWith("MAPPING_POSE|", StringComparison.Ordinal))
        {
            ParsePose(msg.Substring(msg.IndexOf('|') + 1), false);
            return;
        }

        if (msg.StartsWith("MAPPATH|", StringComparison.Ordinal) || msg.StartsWith("MAPPING_PATH|", StringComparison.Ordinal))
        {
            ParseRealtimePathPoint(msg.Substring(msg.IndexOf('|') + 1), false);
            return;
        }

        if (msg.StartsWith("POSE|", StringComparison.Ordinal))
        {
            ParsePose(msg.Substring("POSE|".Length), _locState == SlamFeatureState.Running);
            return;
        }

        if (msg.StartsWith("PATH|", StringComparison.Ordinal))
        {
            ParseRealtimePathPoint(msg.Substring("PATH|".Length), _locState == SlamFeatureState.Running);
            return;
        }

        ParsePose(msg, _locState == SlamFeatureState.Running);
    }

    void ParseRealtimePathPoint(string payload, bool localizationLayer)
    {
        if (trajectoryRenderer != null && trajectoryRenderer.TryParsePoint(payload, out Vector2 p))
        {
            if (localizationLayer) trajectoryRenderer.AddLocalizationPoint(p);
            else trajectoryRenderer.AddMappingPoint(p);
        }
    }

    void ParsePose(string payload, bool localizationPose)
    {
        if (string.IsNullOrWhiteSpace(payload)) return;
        string[] raw = payload.Replace('|', ',').Split(',');
        if (raw.Length < 7) return;

        int start = (raw.Length >= 8 && int.TryParse(raw[0], out _)) ? 1 : 0;
        if (raw.Length - start < 7) return;

        if (!float.TryParse(raw[start + 0], NumberStyles.Float, CultureInfo.InvariantCulture, out float tx)) return;
        if (!float.TryParse(raw[start + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out float ty)) return;
        if (!float.TryParse(raw[start + 2], NumberStyles.Float, CultureInfo.InvariantCulture, out float tz)) return;
        if (!float.TryParse(raw[start + 3], NumberStyles.Float, CultureInfo.InvariantCulture, out float qx)) return;
        if (!float.TryParse(raw[start + 4], NumberStyles.Float, CultureInfo.InvariantCulture, out float qy)) return;
        if (!float.TryParse(raw[start + 5], NumberStyles.Float, CultureInfo.InvariantCulture, out float qz)) return;
        if (!float.TryParse(raw[start + 6], NumberStyles.Float, CultureInfo.InvariantCulture, out float qw)) return;

        _latestPosition = new Vector3(tx, ty, tz);
        _latestRotation = new Quaternion(qx, qy, qz, qw);
        _hasPose = true;

        float yawRad = EstimateYawFromQuaternion(qx, qy, qz, qw);
        if (trajectoryRenderer != null)
        {
            if (localizationPose || drawCameraMarkerInMapping) trajectoryRenderer.SetCurrentPose(tx, tz, yawRad, true);
            if (localizationPose && addLocalizationTrailFromPose) trajectoryRenderer.AddLocalizationPoint(tx, tz);
        }

        if (statusText != null)
        {
            string label = localizationPose ? "LOC" : "MAP";
            statusText.text = label + " Pos: x=" + tx.ToString("F2") + ", y=" + ty.ToString("F2") + ", z=" + tz.ToString("F2");
        }
    }

    float EstimateYawFromQuaternion(float qx, float qy, float qz, float qw)
    {
        float siny = 2f * (qw * qy + qx * qz);
        float cosy = 1f - 2f * (qy * qy + qz * qz);
        return Mathf.Atan2(siny, cosy);
    }

    string SendCommand(string command)
    {
        try
        {
            using (TcpClient client = new TcpClient())
            {
                var result = client.BeginConnect(slamIP, slamControlPort, null, null);
                if (!result.AsyncWaitHandle.WaitOne(TimeSpan.FromSeconds(1))) throw new Exception("Connection timeout");
                client.EndConnect(result);
                using (NetworkStream stream = client.GetStream())
                {
                    stream.WriteTimeout = 1000; stream.ReadTimeout = 2000;
                    byte[] bytes = Encoding.UTF8.GetBytes(command);
                    stream.Write(bytes, 0, bytes.Length);
                    byte[] recvBuf = new byte[65536];
                    int read = stream.Read(recvBuf, 0, recvBuf.Length);
                    string response = read > 0 ? Encoding.UTF8.GetString(recvBuf, 0, read) : string.Empty;
                    if (verboseLog) Debug.Log("[SLAMReceiver] CMD => " + command + ", RESP <= " + response);
                    HandleCommandResponse(command, response);
                    return response;
                }
            }
        }
        catch (Exception e)
        {
            string msg = "SLAM command failed: " + command + " / " + e.Message;
            if (verboseLog) Debug.LogWarning(msg);
            if (statusText != null) statusText.text = msg;
            return msg;
        }
    }

    void HandleCommandResponse(string command, string response)
    {
        if (string.IsNullOrWhiteSpace(response)) return;
        if (response.StartsWith("OK: PATH ", StringComparison.Ordinal))
        {
            string payload = response.Substring("OK: PATH ".Length).Trim();
            trajectoryRenderer?.LoadMapPathPayload(payload);
            if (statusText != null) statusText.text = "Loaded map path: " + (trajectoryRenderer != null ? trajectoryRenderer.MapPoints.Count : 0) + " points";
            return;
        }
        if (statusText != null) statusText.text = response;
    }

    void UpdateUiVisuals()
    {
        if (mappingButtonText != null)
        {
            switch (_mappingState)
            {
                case SlamFeatureState.Idle: mappingButtonText.text = "Start Mapping"; break;
                case SlamFeatureState.Starting: mappingButtonText.text = "Loading..."; break;
                case SlamFeatureState.Running: mappingButtonText.text = "Stop & Save"; break;
                case SlamFeatureState.Finishing: mappingButtonText.text = "Saving..."; break;
            }
        }
        if (mappingButton != null) mappingButton.interactable = (_locState == SlamFeatureState.Idle && _calibState == SlamFeatureState.Idle && _mappingState != SlamFeatureState.Finishing);

        if (localizationButtonText != null)
        {
            switch (_locState)
            {
                case SlamFeatureState.Idle: localizationButtonText.text = "Start Localize"; break;
                case SlamFeatureState.Starting: localizationButtonText.text = "Loading..."; break;
                case SlamFeatureState.Running: localizationButtonText.text = "Stop Localize"; break;
                case SlamFeatureState.Finishing: localizationButtonText.text = "Stopping..."; break;
            }
        }
        if (localizationButton != null) localizationButton.interactable = (_mappingState == SlamFeatureState.Idle && _calibState == SlamFeatureState.Idle && _locState != SlamFeatureState.Finishing);

        if (calibActionText != null) calibActionText.text = (_calibState == SlamFeatureState.Idle) ? "Start Calibration" : "Capture Frame";
        if (calibActionButton != null) calibActionButton.interactable = (_mappingState == SlamFeatureState.Idle && _locState == SlamFeatureState.Idle);
        if (calibEndButton != null)
        {
            calibEndButton.gameObject.SetActive(_calibState == SlamFeatureState.Running);
            calibEndButton.interactable = (_calibState == SlamFeatureState.Running);
        }
    }

    public void ToggleMappingWorkflow()
    {
        if (_mappingState == SlamFeatureState.Idle) StartCoroutine(StartMappingRoutine());
        else if (_mappingState == SlamFeatureState.Running) StartCoroutine(FinishMappingRoutine());
    }

    IEnumerator StartMappingRoutine()
    {
        _mappingState = SlamFeatureState.Starting;
        UpdateUiVisuals();
        if (navigationAdvisor != null) navigationAdvisor.SpeakExecutingCommand("Mapping Mode");
        _hasPose = false;
        trajectoryRenderer?.ClearAll();
        ResetUnityVideoToStart(false);
        SendCommand("MODE MAP");

        bool ready = false;
        float timeout = Time.time + 30f;
        while (Time.time < timeout)
        {
            string resp = SendCommand("CHECK_READY");
            if (resp == "OK: READY") { ready = true; break; }
            yield return new WaitForSeconds(1.0f);
        }

        if (ready)
        {
            SendCommand("START");
            ResetUnityVideoToStart(true);
            _mappingState = SlamFeatureState.Running;
        }
        else
        {
            _mappingState = SlamFeatureState.Idle;
            if (statusText != null) statusText.text = "Mapping Start Timeout";
        }
        UpdateUiVisuals();
    }

    IEnumerator FinishMappingRoutine()
    {
        _mappingState = SlamFeatureState.Finishing;
        UpdateUiVisuals();
        if (navigationAdvisor != null) navigationAdvisor.SpeakExecutingCommand("Save Map");
        SendCommand("STOP");
        yield return new WaitForSeconds(1.5f);
        SendCommand("SAVE_ATLAS");
        yield return new WaitForSeconds(5.0f);
        SendCommand("GENERATE_PATH");
        yield return new WaitForSeconds(1.0f);
        SendCommand("EXIT");
        _mappingState = SlamFeatureState.Idle;
        UpdateUiVisuals();
    }

    public void ToggleLocalizationWorkflow()
    {
        if (_locState == SlamFeatureState.Idle) StartCoroutine(StartLocalizationRoutine());
        else if (_locState == SlamFeatureState.Running) StartCoroutine(StopLocalizationRoutine());
    }

    IEnumerator StartLocalizationRoutine()
    {
        _locState = SlamFeatureState.Starting;
        UpdateUiVisuals();
        if (navigationAdvisor != null) navigationAdvisor.SpeakExecutingCommand("Localization");
        _hasPose = false; _receivedLocalizationPoseAfterStart = false; _localizationRetryCount = 0;
        trajectoryRenderer?.ClearLocalizationOnly();
        ResetUnityVideoToStart(false);

        if (loadMapPathBeforeLocalization) SendCommand("GENERATE_PATH");
        SendCommand("MODE LOC");

        bool ready = false;
        float timeout = Time.time + 30f;
        while (Time.time < timeout)
        {
            string resp = SendCommand("CHECK_READY");
            if (resp == "OK: READY") { ready = true; break; }
            yield return new WaitForSeconds(1.0f);
        }

        if (ready)
        {
            SendCommand("START");
            ResetUnityVideoToStart(true);
            _locState = SlamFeatureState.Running;
            StartLocalizationWatchdog();
        }
        else
        {
            _locState = SlamFeatureState.Idle;
            if (statusText != null) statusText.text = "Localization Start Timeout";
        }
        UpdateUiVisuals();
    }

    IEnumerator StopLocalizationRoutine()
    {
        _locState = SlamFeatureState.Finishing;
        UpdateUiVisuals();
        if (navigationAdvisor != null) navigationAdvisor.SpeakExecutingCommand("Stop");
        StopLocalizationWatchdog();
        SendCommand("STOP");
        yield return new WaitForSeconds(0.5f);
        SendCommand("EXIT");
        _locState = SlamFeatureState.Idle;
        UpdateUiVisuals();
    }

    public void CalibAction()
    {
        if (_calibState == SlamFeatureState.Idle)
        {
            _calibState = SlamFeatureState.Running;
            if (navigationAdvisor != null) navigationAdvisor.SpeakExecutingCommand("Calibration");
            SendCommand("CALIBRATE_START");
        }
        else 
        {
            if (navigationAdvisor != null) navigationAdvisor.SpeakExecutingCommand("Capture");
            SendCommand("CALIBRATE_CAPTURE");
        }
        UpdateUiVisuals();
    }

    public void CalibSaveAndQuit()
    {
        StartCoroutine(CalibSaveRoutine());
    }

    IEnumerator CalibSaveRoutine()
    {
        SendCommand("CALIBRATE_SAVE");
        yield return new WaitForSeconds(1.5f);
        SendCommand("CALIBRATE_QUIT");
        _calibState = SlamFeatureState.Idle;
        UpdateUiVisuals();
    }

    void ResetUnityVideoToStart(bool playAfterReset)
    {
        if (unityVideoPlayer == null) return;
        try
        {
            unityVideoPlayer.Pause(); unityVideoPlayer.frame = 0; unityVideoPlayer.time = 0.0;
            unityVideoPlayer.Stop(); unityVideoPlayer.Prepare();
            if (playAfterReset && autoPlayUnityVideoOnStart) StartCoroutine(PlayPreparedVideo());
        }
        catch { }
    }

    IEnumerator PlayPreparedVideo()
    {
        if (unityVideoPlayer == null) yield break;
        float timeout = Time.time + 2.0f;
        while (!unityVideoPlayer.isPrepared && Time.time < timeout) yield return null;
        unityVideoPlayer.frame = 0; unityVideoPlayer.time = 0.0; unityVideoPlayer.Play();
    }

    void StopLocalizationWatchdog()
    {
        if (_localizationWatchdogCoroutine != null) { StopCoroutine(_localizationWatchdogCoroutine); _localizationWatchdogCoroutine = null; }
    }

    void StartLocalizationWatchdog()
    {
        if (enableAutoRelocalizationRetry && _locState == SlamFeatureState.Running)
        {
            StopLocalizationWatchdog();
            _localizationWatchdogCoroutine = StartCoroutine(LocalizationWatchdogLoop());
        }
    }

    IEnumerator LocalizationWatchdogLoop()
    {
        while (_locState == SlamFeatureState.Running && enableAutoRelocalizationRetry)
        {
            yield return new WaitForSeconds(firstPoseTimeoutSeconds);
            if (_receivedLocalizationPoseAfterStart || _hasPose) { _localizationWatchdogCoroutine = null; yield break; }
            if (_localizationRetryCount >= maxRelocalizationRetries) { _localizationWatchdogCoroutine = null; yield break; }
            _localizationRetryCount++;
            SendCommand("STOP");
            yield return new WaitForSeconds(retryModeSettleSeconds);
            ResetUnityVideoToStart(false);
            if (retryByRestartingLocalizationMode)
            {
                if (retryReloadMapPath) SendCommand("GENERATE_PATH");
                SendCommand("MODE LOC");
                yield return new WaitForSeconds(retryModeSettleSeconds);
            }
            _receivedLocalizationPoseAfterStart = false; _hasPose = false;
            ResetUnityVideoToStart(true);
            yield return new WaitForSeconds(retryStartSettleSeconds);
            SendCommand("START");
        }
        _localizationWatchdogCoroutine = null;
    }
}
