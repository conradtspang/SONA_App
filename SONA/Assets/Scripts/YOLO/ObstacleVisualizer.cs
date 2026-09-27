using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

public class ObstacleVisualizer : MonoBehaviour
{
    [Header("Dependencies")]
    public ObstacleReceiver receiver;
    public ObstacleMOD mod; // Optional: If assigned, visualizes Tracks instead of raw Detections
    
    [Header("UI References")]
    public GameObject boxPrefab; 
    public RectTransform container; 

    [Header("Settings")]
    public Color staticColor = Color.yellow;
    public Color movingColor = Color.red;
    public float strokeWidth = 2f;

    private List<GameObject> _pool = new List<GameObject>();
    private List<GameObject> _activeBoxes = new List<GameObject>();

    void Start()
    {
        if (container == null) container = GetComponent<RectTransform>();

        // Prefer MOD (Tracking) if available
        if (mod != null)
        {
            mod.OnTracksUpdated += UpdateTracks;
        }
        else if (receiver != null)
        {
            receiver.OnDetectionsReceived += UpdateRawDetections;
        }
        else
        {
            Debug.LogError("ObstacleVisualizer: No Receiver or MOD assigned!");
        }
    }

    void OnDestroy()
    {
        if (mod != null) mod.OnTracksUpdated -= UpdateTracks;
        if (receiver != null) receiver.OnDetectionsReceived -= UpdateRawDetections;
    }

    // --- Visualization Logic ---

    void UpdateTracks(List<Track> tracks)
    {
        ResetBoxes();

        foreach (var track in tracks)
        {
            // Only show established tracks
            if (track.age < 2) continue;

            // Draw based on track state
            Color c = track.isMoving ? movingColor : staticColor;
            string label = $"{track.label} [ID:{track.trackId}]";
            if (track.isMoving) label += " (MOVING)";

            DrawBox(track.lastBox.x, track.lastBox.y, track.lastBox.width, track.lastBox.height, label, c);
        }
    }

    void UpdateRawDetections(List<ObstacleDetection> detections)
    {
        ResetBoxes();

        foreach (var det in detections)
        {
            string label = $"{det.label} ({det.conf:P0})";
            DrawBox(det.x, det.y, det.w, det.h, label, staticColor);
        }
    }

    // --- Drawing Helpers ---

    void ResetBoxes()
    {
        foreach (var box in _activeBoxes)
        {
            box.SetActive(false);
            _pool.Add(box);
        }
        _activeBoxes.Clear();
    }

    void DrawBox(float x, float y, float w, float h, string text, Color color)
    {
        GameObject boxObj = GetBoxFromPool();
        boxObj.SetActive(true);
        _activeBoxes.Add(boxObj);

        // Position
        RectTransform rt = boxObj.GetComponent<RectTransform>();
        
        // Unity UI: (0,0) Bottom-Left
        // Input: (0,0) Top-Left normalized
        float xMin = x;
        float yMin = 1.0f - (y + h);
        float xMax = x + w;
        float yMax = 1.0f - y;

        rt.anchorMin = new Vector2(xMin, yMin);
        rt.anchorMax = new Vector2(xMax, yMax);
        rt.offsetMin = Vector2.zero;
        rt.offsetMax = Vector2.zero;

        // Color
        Image img = boxObj.GetComponent<Image>();
        if (img != null) img.color = new Color(color.r, color.g, color.b, 0.25f); // Transparent fill
        
        // Outline (if you have an Outline component, or just use the image color as border if implemented that way)
        Outline outline = boxObj.GetComponent<Outline>();
        if (outline != null) outline.effectColor = color;

        // Text
        TMPro.TextMeshProUGUI labelText = boxObj.GetComponentInChildren<TMPro.TextMeshProUGUI>();
        if (labelText != null)
        {
            labelText.text = text;
            labelText.color = color;
        }
    }

    GameObject GetBoxFromPool()
    {
        if (_pool.Count > 0)
        {
            GameObject obj = _pool[_pool.Count - 1];
            _pool.RemoveAt(_pool.Count - 1);
            return obj;
        }
        else
        {
            return Instantiate(boxPrefab, container);
        }
    }
}
