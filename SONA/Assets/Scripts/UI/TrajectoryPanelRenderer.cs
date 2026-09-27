/**
 * TrajectoryPanelRenderer API:
 * - ClearAll(): Clear both map and localization paths.
 * - ClearLocalizationOnly(): Clear only the localization trail.
 * - ClearMapOnly(): Clear only the map path.
 * - AddMappingPoint(Vector2/float, float): Add a point to the map path.
 * - AddLocalizationPoint(Vector2/float, float): Add a point to the localization trail.
 * - SetMapPath(IEnumerable<Vector2>): Set the entire map path.
 * - LoadMapPathPayload(string): Parse and set the map path from a string payload.
 * - SetCurrentPose(float, float, float, bool): Set the current camera position and orientation.
 */

using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using UnityEngine.UI;

public class TrajectoryPanelRenderer : MonoBehaviour
{
    [Header("UI")]
    public RawImage targetImage;

    [Header("Texture")]
    public int textureWidth = 720;
    public int textureHeight = 720;
    public int maxRenderFps = 15;

    [Header("Colors")]
    public Color backgroundColor = Color.white;
    public Color gridColor = new Color(0.9f, 0.9f, 0.9f, 1f);
    public Color mapPathColor = new Color(0.25f, 0.9f, 0.25f, 1f);
    public Color localizationTrailColor = new Color(0.1f, 0.75f, 1f, 1f);
    public Color startColor = Color.yellow;
    public Color currentColor = Color.red;
    public Color cameraColor = Color.red;

    [Header("Drawing")]
    public int lineThickness = 2;
    public int pointRadius = 4;
    public int cameraSize = 14;
    public int padding = 40;
    public bool drawGrid = true;
    public bool autoFit = true;
    public float fixedMetersPerPixel = 0.01f;

    [Header("Filtering")]
    public float minPointDistance = 0.02f;
    public int maxLocalizationTrailPoints = 1500; // 0 = unlimited

    [Header("Simulated Path (Avoidance)")]
    public bool useFixedSimulatedBounds = false;
    public float simulatedMinX = -1f;
    public float simulatedMaxX = 1f;
    public float simulatedMinZ = 0f;
    public float simulatedMaxZ = 2f;

    private Texture2D _tex;
    private Color32[] _clearPixels;
    private bool _dirty = true;
    private float _nextRenderTime;

    private readonly List<Vector2> _mapPoints = new List<Vector2>();
    private readonly List<Vector2> _localizationTrail = new List<Vector2>();

    private bool _hasCameraPose;
    private Vector2 _cameraPos;
    private float _cameraYawRad;
    private bool _hasCameraYaw;

    public IReadOnlyList<Vector2> MapPoints => _mapPoints;
    public IReadOnlyList<Vector2> LocalizationTrail => _localizationTrail;
    public IReadOnlyList<Vector2> Points => _mapPoints.Count > 0 ? _mapPoints : _localizationTrail;

    void Awake()
    {
        EnsureTexture();
        MarkDirty();
    }

    void Start()
    {
        if (targetImage == null)
            targetImage = GetComponent<RawImage>();
        if (targetImage != null)
            targetImage.texture = _tex;
        ForceRedraw();
    }

    void Update()
    {
        if (!_dirty) return;
        float interval = 1f / Mathf.Max(1, maxRenderFps);
        if (Time.unscaledTime < _nextRenderTime) return;
        _nextRenderTime = Time.unscaledTime + interval;
        ForceRedraw();
    }

    void MarkDirty()
    {
        _dirty = true;
    }

    public void ClearAll()
    {
        _mapPoints.Clear();
        _localizationTrail.Clear();
        _hasCameraPose = false;
        MarkDirty();
    }

    public void ClearLocalizationOnly()
    {
        _localizationTrail.Clear();
        _hasCameraPose = false;
        MarkDirty();
    }

    public void ClearMapOnly()
    {
        _mapPoints.Clear();
        MarkDirty();
    }

    public void AddMappingPoint(float x, float z) => AddMappingPoint(new Vector2(x, z));

    public void AddMappingPoint(Vector2 p)
    {
        if (_mapPoints.Count > 0 && Vector2.Distance(_mapPoints[_mapPoints.Count - 1], p) < minPointDistance)
            return;
        _mapPoints.Add(p);
        MarkDirty();
    }

    public void AddLocalizationPoint(float x, float z) => AddLocalizationPoint(new Vector2(x, z));

    public void AddLocalizationPoint(Vector2 p)
    {
        if (_localizationTrail.Count > 0 && Vector2.Distance(_localizationTrail[_localizationTrail.Count - 1], p) < minPointDistance)
            return;

        _localizationTrail.Add(p);
        if (maxLocalizationTrailPoints > 0 && _localizationTrail.Count > maxLocalizationTrailPoints)
            _localizationTrail.RemoveRange(0, _localizationTrail.Count - maxLocalizationTrailPoints);

        MarkDirty();
    }

    public void SetMapPath(IEnumerable<Vector2> points)
    {
        _mapPoints.Clear();
        foreach (Vector2 p in points)
            _mapPoints.Add(p);
        MarkDirty();
    }

    public void LoadMapPathPayload(string payload)
    {
        List<Vector2> parsed = ParsePathPayload(payload);
        if (parsed.Count > 0)
            SetMapPath(parsed);
    }

    public void LoadLocalizationTrailPayload(string payload)
    {
        List<Vector2> parsed = ParsePathPayload(payload);
        if (parsed.Count > 0)
        {
            _localizationTrail.Clear();
            _localizationTrail.AddRange(parsed);
            MarkDirty();
        }
    }

    public void SetCurrentPose(float x, float z, float yawRad, bool hasYaw = true)
    {
        _cameraPos = new Vector2(x, z);
        _cameraYawRad = yawRad;
        _hasCameraYaw = hasYaw;
        _hasCameraPose = true;
        MarkDirty();
    }

    public void SetSimulatedAvoidancePath(string suggestion)
    {
        useFixedSimulatedBounds = true;
        _mapPoints.Clear();
        _localizationTrail.Clear();
        _hasCameraPose = false;

        int numPoints = 20;
        float stepZ = 0.1f;
        float currentX = 0f;
        float currentZ = 0f;

        for (int i = 0; i < numPoints; i++)
        {
            currentZ += stepZ;
            
            if (suggestion == "left" && i > 5)
            {
                currentX -= 0.05f * (i - 5);
            }
            else if (suggestion == "right" && i > 5)
            {
                currentX += 0.05f * (i - 5);
            }

            _mapPoints.Add(new Vector2(currentX, currentZ));
        }

        MarkDirty();
    }

    public void HideCurrentPose()
    {
        _hasCameraPose = false;
        MarkDirty();
    }

    public bool TryParsePoint(string token, out Vector2 point)
    {
        point = Vector2.zero;
        if (string.IsNullOrWhiteSpace(token)) return false;

        string[] parts = token.Trim().Split(',');
        if (parts.Length < 2) return false;

        if (!float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float x))
            return false;

        float z;
        if (parts.Length >= 3)
        {
            if (!float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out z))
                return false;
        }
        else
        {
            if (!float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out z))
                return false;
        }

        point = new Vector2(x, z);
        return true;
    }

    List<Vector2> ParsePathPayload(string payload)
    {
        List<Vector2> parsed = new List<Vector2>();
        if (string.IsNullOrWhiteSpace(payload)) return parsed;

        string[] pointTokens = payload.Split('|');
        foreach (string token in pointTokens)
        {
            if (TryParsePoint(token, out Vector2 p))
                parsed.Add(p);
        }
        return parsed;
    }

    void EnsureTexture()
    {
        if (_tex != null && _tex.width == textureWidth && _tex.height == textureHeight)
            return;

        _tex = new Texture2D(textureWidth, textureHeight, TextureFormat.RGBA32, false);
        _tex.wrapMode = TextureWrapMode.Clamp;
        _tex.filterMode = FilterMode.Bilinear;
        _clearPixels = new Color32[textureWidth * textureHeight];

        if (targetImage == null)
            targetImage = GetComponent<RawImage>();
        if (targetImage != null)
            targetImage.texture = _tex;
    }

    void ForceRedraw()
    {
        _dirty = false;
        EnsureTexture();
        Fill(backgroundColor);

        Bounds2D bounds = ComputeBounds();
        if (!bounds.valid)
        {
            ApplyTexture();
            return;
        }

        if (drawGrid)
            DrawGrid(bounds);

        DrawPath(_mapPoints, bounds, mapPathColor, lineThickness, true);
        DrawPath(_localizationTrail, bounds, localizationTrailColor, Mathf.Max(1, lineThickness), false);

        if (_mapPoints.Count > 0)
        {
            Vector2Int start = ToPixel(_mapPoints[0], bounds);
            DrawCircle(start.x, start.y, pointRadius, startColor);
        }

        if (_localizationTrail.Count > 0)
        {
            Vector2Int locNow = ToPixel(_localizationTrail[_localizationTrail.Count - 1], bounds);
            DrawCircle(locNow.x, locNow.y, pointRadius, currentColor);
        }

        if (_hasCameraPose)
            DrawCameraMarker(bounds);

        ApplyTexture();
    }

    struct Bounds2D
    {
        public bool valid;
        public float minX, maxX, minZ, maxZ;
        public float scale;
    }

    Bounds2D ComputeBounds()
    {
        Bounds2D b = new Bounds2D { valid = false };
        if (useFixedSimulatedBounds)
        {
            b.valid = true;
            b.minX = simulatedMinX;
            b.maxX = simulatedMaxX;
            b.minZ = simulatedMinZ;
            b.maxZ = simulatedMaxZ;
            
            float rX = Mathf.Max(0.001f, b.maxX - b.minX);
            float rZ = Mathf.Max(0.001f, b.maxZ - b.minZ);
            float uW = Mathf.Max(1f, textureWidth - 2f * padding);
            float uH = Mathf.Max(1f, textureHeight - 2f * padding);
            b.scale = autoFit ? Mathf.Min(uW / rX, uH / rZ) : 1f / Mathf.Max(0.0001f, fixedMetersPerPixel);
            return b;
        }

        float minX = float.MaxValue, maxX = float.MinValue;
        float minZ = float.MaxValue, maxZ = float.MinValue;

        for (int i = 0; i < _mapPoints.Count; i++) Include(_mapPoints[i]);
        for (int i = 0; i < _localizationTrail.Count; i++) Include(_localizationTrail[i]);
        if (_hasCameraPose) Include(_cameraPos);

        if (!b.valid) return b;

        float rangeX = Mathf.Max(0.001f, maxX - minX);
        float rangeZ = Mathf.Max(0.001f, maxZ - minZ);

        minX -= rangeX * 0.1f + 0.05f;
        maxX += rangeX * 0.1f + 0.05f;
        minZ -= rangeZ * 0.1f + 0.05f;
        maxZ += rangeZ * 0.1f + 0.05f;

        rangeX = Mathf.Max(0.001f, maxX - minX);
        rangeZ = Mathf.Max(0.001f, maxZ - minZ);

        float usableW = Mathf.Max(1f, textureWidth - 2f * padding);
        float usableH = Mathf.Max(1f, textureHeight - 2f * padding);
        float scale = autoFit ? Mathf.Min(usableW / rangeX, usableH / rangeZ) : 1f / Mathf.Max(0.0001f, fixedMetersPerPixel);

        b.minX = minX; b.maxX = maxX; b.minZ = minZ; b.maxZ = maxZ; b.scale = scale;
        return b;

        void Include(Vector2 p)
        {
            minX = Mathf.Min(minX, p.x);
            maxX = Mathf.Max(maxX, p.x);
            minZ = Mathf.Min(minZ, p.y);
            maxZ = Mathf.Max(maxZ, p.y);
            b.valid = true;
        }
    }

    Vector2Int ToPixel(Vector2 p, Bounds2D b)
    {
        int px = Mathf.RoundToInt(padding + (p.x - b.minX) * b.scale);
        int py = Mathf.RoundToInt(textureHeight - padding - (p.y - b.minZ) * b.scale);
        return new Vector2Int(px, py);
    }

    void DrawPath(List<Vector2> points, Bounds2D b, Color color, int thickness, bool drawEnd)
    {
        if (points.Count < 2) return;
        for (int i = 1; i < points.Count; i++)
        {
            Vector2Int a = ToPixel(points[i - 1], b);
            Vector2Int c = ToPixel(points[i], b);
            DrawLine(a.x, a.y, c.x, c.y, color, thickness);
        }

        if (drawEnd)
        {
            Vector2Int end = ToPixel(points[points.Count - 1], b);
            DrawCircle(end.x, end.y, Mathf.Max(2, pointRadius - 1), color);
        }
    }

    void DrawGrid(Bounds2D b)
    {
        int step = 80;
        for (int x = padding; x < textureWidth - padding; x += step)
            DrawLine(x, padding, x, textureHeight - padding, gridColor, 0);
        for (int y = padding; y < textureHeight - padding; y += step)
            DrawLine(padding, y, textureWidth - padding, y, gridColor, 0);
    }

    void DrawCameraMarker(Bounds2D b)
    {
        Vector2Int center = ToPixel(_cameraPos, b);

        Vector2 dir;
        if (_hasCameraYaw)
            dir = new Vector2(Mathf.Sin(_cameraYawRad), Mathf.Cos(_cameraYawRad)).normalized;
        else if (_localizationTrail.Count >= 2)
            dir = (_localizationTrail[_localizationTrail.Count - 1] - _localizationTrail[_localizationTrail.Count - 2]).normalized;
        else
            dir = Vector2.up;

        if (dir.sqrMagnitude < 0.001f)
            dir = Vector2.up;

        Vector2 side = new Vector2(-dir.y, dir.x);
        Vector2 p0 = new Vector2(center.x, center.y) + new Vector2(dir.x, -dir.y) * cameraSize;
        Vector2 p1 = new Vector2(center.x, center.y) - new Vector2(dir.x, -dir.y) * cameraSize * 0.65f + new Vector2(side.x, -side.y) * cameraSize * 0.55f;
        Vector2 p2 = new Vector2(center.x, center.y) - new Vector2(dir.x, -dir.y) * cameraSize * 0.65f - new Vector2(side.x, -side.y) * cameraSize * 0.55f;

        DrawLine(Mathf.RoundToInt(p0.x), Mathf.RoundToInt(p0.y), Mathf.RoundToInt(p1.x), Mathf.RoundToInt(p1.y), cameraColor, 2);
        DrawLine(Mathf.RoundToInt(p1.x), Mathf.RoundToInt(p1.y), Mathf.RoundToInt(p2.x), Mathf.RoundToInt(p2.y), cameraColor, 2);
        DrawLine(Mathf.RoundToInt(p2.x), Mathf.RoundToInt(p2.y), Mathf.RoundToInt(p0.x), Mathf.RoundToInt(p0.y), cameraColor, 2);
        DrawCircle(center.x, center.y, 2, cameraColor);
    }

    void Fill(Color c)
    {
        Color32 c32 = c;
        for (int i = 0; i < _clearPixels.Length; i++)
            _clearPixels[i] = c32;
        _tex.SetPixels32(_clearPixels);
    }

    void ApplyTexture()
    {
        _tex.Apply(false, false);
    }

    void DrawPixel(int x, int y, Color c)
    {
        if (x < 0 || x >= textureWidth || y < 0 || y >= textureHeight)
            return;
        _tex.SetPixel(x, y, c);
    }

    void DrawCircle(int cx, int cy, int r, Color c)
    {
        for (int y = -r; y <= r; y++)
            for (int x = -r; x <= r; x++)
                if (x * x + y * y <= r * r)
                    DrawPixel(cx + x, cy + y, c);
    }

    void DrawLine(int x0, int y0, int x1, int y1, Color c, int thickness)
    {
        int dx = Mathf.Abs(x1 - x0);
        int dy = Mathf.Abs(y1 - y0);
        int sx = x0 < x1 ? 1 : -1;
        int sy = y0 < y1 ? 1 : -1;
        int err = dx - dy;
        int radius = Mathf.Max(0, thickness);

        while (true)
        {
            for (int oy = -radius; oy <= radius; oy++)
                for (int ox = -radius; ox <= radius; ox++)
                    DrawPixel(x0 + ox, y0 + oy, c);

            if (x0 == x1 && y0 == y1) break;

            int e2 = 2 * err;
            if (e2 > -dy) { err -= dy; x0 += sx; }
            if (e2 < dx) { err += dx; y0 += sy; }
        }
    }
}
