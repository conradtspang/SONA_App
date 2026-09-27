using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using UnityEngine;

public class SlamDepthReceiver : MonoBehaviour
{
    [Header("Network")]
    public int depthListenPort = 5007;
    public bool autoStart = true;

    [Header("Depth Validity")]
    public float maxDepthAgeSeconds = 1.20f;
    public float minDepthMeters = 0.15f;
    public float maxDepthMeters = 6.00f;

    [Header("BBox Matching")]
    [Tooltip("Negative value expands the bbox. Example: -0.05 expands bbox by 5%.")]
    public float bboxInnerMarginRatio = -0.05f;

    [Tooltip("Minimum points required inside bbox to accept SLAM depth.")]
    public int minPointsForMedian = 1;

    [Tooltip("If not enough points are found, expand bbox step by step.")]
    public bool enableAdaptiveExpansion = true;
    public int expansionSteps = 3;
    public float expansionStepRatio = 0.08f;

    [Header("Temporal Cache")]
    [Tooltip("Use recent SLAMPOINTS packets, not only the latest packet.")]
    public bool useTemporalCache = true;
    public float temporalCacheSeconds = 1.20f;
    public int maxCachedPackets = 12;

    [Header("Debug")]
    public bool debugLog = false;
    public int latestPointCount = 0;
    public int latestFrame = -1;
    public float latestPacketAge = 999f;

    public struct SlamDepthPoint
    {
        public float uNorm;
        public float vNorm;
        public float depth;
        public int frame;
        public double timestamp;
        public float receivedTime;
    }

    public struct DepthQueryResult
    {
        public bool ok;
        public float medianDepth;
        public int pointCount;
        public string source;
    }

    private class DepthPacket
    {
        public int frame;
        public double timestamp;
        public int width;
        public int height;
        public float receivedTime;
        public readonly List<SlamDepthPoint> points = new List<SlamDepthPoint>();
    }

    private readonly ConcurrentQueue<string> _packetQueue = new ConcurrentQueue<string>();
    private readonly List<DepthPacket> _packets = new List<DepthPacket>();
    private UdpClient _udp;
    private Thread _thread;
    private volatile bool _running;
    private readonly object _lock = new object();

    private void Start()
    {
        if (autoStart) StartReceiver();
    }

    private void OnDestroy()
    {
        StopReceiver();
    }

    public void StartReceiver()
    {
        if (_running) return;
        try
        {
            _udp = new UdpClient(depthListenPort);
            _udp.Client.ReceiveTimeout = 500;
            _running = true;
            _thread = new Thread(ReceiveLoop) { IsBackground = true };
            _thread.Start();
            if (debugLog) Debug.Log($"[SlamDepthReceiver] Listening on UDP {depthListenPort}");
        }
        catch (Exception e)
        {
            Debug.LogError("[SlamDepthReceiver] Failed to start: " + e.Message);
        }
    }

    public void StopReceiver()
    {
        _running = false;
        try { _udp?.Close(); _udp = null; } catch { }
        try { if (_thread != null && _thread.IsAlive) _thread.Join(500); } catch { }
        _thread = null;
    }

    private void ReceiveLoop()
    {
        IPEndPoint ep = new IPEndPoint(IPAddress.Any, depthListenPort);
        while (_running)
        {
            try
            {
                byte[] data = _udp.Receive(ref ep);
                string text = System.Text.Encoding.UTF8.GetString(data);
                _packetQueue.Enqueue(text);
            }
            catch (SocketException) { }
            catch (ObjectDisposedException) { break; }
            catch (Exception e)
            {
                if (debugLog) Debug.LogWarning("[SlamDepthReceiver] Receive error: " + e.Message);
            }
        }
    }

    private void Update()
    {
        int processed = 0;
        while (_packetQueue.TryDequeue(out string packet) && processed < 8)
        {
            processed++;
            ParsePacket(packet);
        }
        CleanupOldPackets();
        lock (_lock)
        {
            if (_packets.Count > 0)
            {
                DepthPacket latest = _packets[_packets.Count - 1];
                latestPointCount = latest.points.Count;
                latestFrame = latest.frame;
                latestPacketAge = Time.time - latest.receivedTime;
            }
            else
            {
                latestPointCount = 0;
                latestFrame = -1;
                latestPacketAge = 999f;
            }
        }
    }

    private void ParsePacket(string packet)
    {
        if (string.IsNullOrWhiteSpace(packet)) return;
        if (!packet.StartsWith("SLAMPOINTS|", StringComparison.Ordinal)) return;

        string[] parts = packet.Split('|');
        if (parts.Length < 3) return;

        string[] header = parts[1].Split(',');
        if (header.Length < 4) return;

        if (!int.TryParse(header[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int frame)) return;
        double.TryParse(header[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double ts);
        int.TryParse(header[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int width);
        int.TryParse(header[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out int height);
        if (width <= 0) width = 1920;
        if (height <= 0) height = 1080;

        DepthPacket dp = new DepthPacket { frame = frame, timestamp = ts, width = width, height = height, receivedTime = Time.time };
        string[] pointTokens = parts[2].Split(';');
        foreach (string token in pointTokens)
        {
            if (string.IsNullOrWhiteSpace(token)) continue;
            string[] xyz = token.Split(',');
            if (xyz.Length < 3) continue;
            if (!float.TryParse(xyz[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float u)) continue;
            if (!float.TryParse(xyz[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float v)) continue;
            if (!float.TryParse(xyz[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float d)) continue;
            if (!float.IsFinite(d) || d < minDepthMeters || d > maxDepthMeters) continue;

            float uNorm = u > 1.5f ? u / width : u;
            float vNorm = v > 1.5f ? v / height : v;
            if (uNorm < -0.2f || uNorm > 1.2f || vNorm < -0.2f || vNorm > 1.2f) continue;

            dp.points.Add(new SlamDepthPoint { uNorm = uNorm, vNorm = vNorm, depth = d, frame = frame, timestamp = ts, receivedTime = dp.receivedTime });
        }

        lock (_lock)
        {
            _packets.Add(dp);
            while (_packets.Count > maxCachedPackets) _packets.RemoveAt(0);
        }

        if (debugLog && frame % 30 == 0)
            Debug.Log($"[SlamDepthReceiver] frame={frame}, points={dp.points.Count}, cache={_packets.Count}");
    }

    private void CleanupOldPackets()
    {
        float now = Time.time;
        float keepSeconds = useTemporalCache ? temporalCacheSeconds : maxDepthAgeSeconds;
        lock (_lock) _packets.RemoveAll(p => now - p.receivedTime > keepSeconds);
    }

    public DepthQueryResult QueryDepthForNormalizedRect(Rect normalizedBox)
    {
        DepthQueryResult fail = new DepthQueryResult { ok = false, medianDepth = 0f, pointCount = 0, source = "bbox fallback" };
        if (normalizedBox.width <= 0f || normalizedBox.height <= 0f) return fail;

        List<SlamDepthPoint> candidatePool = new List<SlamDepthPoint>();
        float now = Time.time;
        lock (_lock)
        {
            if (_packets.Count == 0) return fail;
            for (int i = _packets.Count - 1; i >= 0; i--)
            {
                DepthPacket p = _packets[i];
                float age = now - p.receivedTime;
                if (age > maxDepthAgeSeconds && !useTemporalCache) continue;
                if (age > temporalCacheSeconds) continue;
                candidatePool.AddRange(p.points);
            }
        }
        if (candidatePool.Count == 0) return fail;

        int maxSteps = enableAdaptiveExpansion ? Mathf.Max(0, expansionSteps) : 0;
        for (int step = 0; step <= maxSteps; step++)
        {
            float margin = bboxInnerMarginRatio - step * expansionStepRatio;
            Rect queryRect = ApplyMargin(normalizedBox, margin);
            List<float> depths = new List<float>();
            foreach (SlamDepthPoint p in candidatePool)
            {
                if (queryRect.Contains(new Vector2(p.uNorm, p.vNorm)) && p.depth >= minDepthMeters && p.depth <= maxDepthMeters)
                    depths.Add(p.depth);
            }
            if (depths.Count >= minPointsForMedian)
            {
                float median = RobustMedian(depths);
                return new DepthQueryResult { ok = true, medianDepth = median, pointCount = depths.Count, source = step == 0 ? "SLAM depth" : $"SLAM depth expanded x{step}" };
            }
        }
        return fail;
    }

    private Rect ApplyMargin(Rect r, float marginRatio)
    {
        float dx = r.width * marginRatio;
        float dy = r.height * marginRatio;
        float xMin = Mathf.Clamp01(r.xMin + dx);
        float xMax = Mathf.Clamp01(r.xMax - dx);
        float yMin = Mathf.Clamp01(r.yMin + dy);
        float yMax = Mathf.Clamp01(r.yMax - dy);
        if (xMax < xMin) { float c = (xMin + xMax) * 0.5f; xMin = c; xMax = c; }
        if (yMax < yMin) { float c = (yMin + yMax) * 0.5f; yMin = c; yMax = c; }
        return Rect.MinMaxRect(xMin, yMin, xMax, yMax);
    }

    private float RobustMedian(List<float> values)
    {
        values.Sort();
        if (values.Count == 0) return 0f;
        if (values.Count >= 5)
        {
            int trim = Mathf.Max(1, Mathf.FloorToInt(values.Count * 0.15f));
            int start = trim;
            int end = values.Count - trim;
            if (end > start) values = values.GetRange(start, end - start);
        }
        int mid = values.Count / 2;
        return values.Count % 2 == 1 ? values[mid] : 0.5f * (values[mid - 1] + values[mid]);
    }
}
