using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;
using UnityEngine.UI;

[Serializable]
public class ObstacleDetection
{
    public string label;
    public float conf;
    public float x; // Normalized 0-1 (Top-Left origin)
    public float y; // Normalized 0-1 (Top-Left origin)
    public float w;
    public float h;
    public int id;
}

[Serializable]
public class DetectionPacket
{
    public List<ObstacleDetection> d;
}

public class ObstacleReceiver : MonoBehaviour
{
    [Header("Server Settings")]
    public string pythonServerIP = "127.0.0.1";
    public int controlPort = 6001;
    public int dataPort = 6002;
    public int mjpegPort = 8080;

    [Header("Robustness Settings")]
    public bool autoStart = true;
    public float reconnectDelay = 3.0f;
    public float heartbeatInterval = 5.0f;
    public bool verboseLog = true;

    [Header("Voice Announcements")]
    public SpeechClient speechClient;
    public bool enableVoiceAnnouncements = true;
    public float announcementCooldown = 5f;

    [Header("References")]
    public RawImage displayTexture; // for saving detection images

    // State
    private bool _isLinked = false;
    private float _nextHeartbeatTime = 0f;
    
    // Networking - Control (TCP)
    private TcpClient _tcpClient;
    private NetworkStream _tcpStream;
    private Thread _tcpThread;
    private volatile bool _shouldRunTcp = true;

    // Networking - Data (UDP)
    private UdpClient _udpClient;
    private Thread _udpThread;
    private volatile bool _shouldRunUdp = true;
    private object _queueLock = new object();
    private Queue<List<ObstacleDetection>> _detectionQueue = new Queue<List<ObstacleDetection>>();
    
    // Tracking
    private Dictionary<string, float> _labelCooldowns = new Dictionary<string, float>();
    public int packetCount = 0;

    public delegate void DetectionsReceived(List<ObstacleDetection> detections);
    public event DetectionsReceived OnDetectionsReceived;

    void Start()
    {
        if (speechClient == null) speechClient = FindObjectOfType<SpeechClient>();

        _shouldRunTcp = true;
        _tcpThread = new Thread(TcpControlLoop) { IsBackground = true };
        _tcpThread.Start();

        _shouldRunUdp = true;
        _udpThread = new Thread(UdpDataLoop) { IsBackground = true };
        _udpThread.Start();
    }

    void OnDisable()
    {
        _shouldRunTcp = false;
        _shouldRunUdp = false;
        
        if (_tcpClient != null) { _tcpClient.Close(); _tcpClient = null; }
        if (_udpClient != null) { _udpClient.Close(); _udpClient = null; }

        if (_tcpThread != null && _tcpThread.IsAlive) _tcpThread.Join(500);
        if (_udpThread != null && _udpThread.IsAlive) _udpThread.Join(500);
    }

    void Update()
    {
        List<ObstacleDetection> latestDetections = null;
        lock (_queueLock)
        {
            if (_detectionQueue.Count > 0)
            {
                while (_detectionQueue.Count > 0)
                    latestDetections = _detectionQueue.Dequeue();
            }
        }

        if (latestDetections != null)
        {
            OnDetectionsReceived?.Invoke(latestDetections);
            if (enableVoiceAnnouncements && speechClient != null)
                AnnounceNewDetections(latestDetections);
        }

        // Send periodic heartbeats via TCP if linked
        if (_isLinked && Time.time > _nextHeartbeatTime)
        {
            SendTcpCommandInternal("PING");
            _nextHeartbeatTime = Time.time + heartbeatInterval;
        }
    }

    private void TcpControlLoop()
    {
        while (_shouldRunTcp)
        {
            if (_tcpClient == null || !_tcpClient.Connected)
            {
                _isLinked = false;
                try
                {
                    if (verboseLog) Debug.Log($"[ObstacleReceiver] Attempting TCP Link to {pythonServerIP}:{controlPort}...");
                    _tcpClient = new TcpClient();
                    var result = _tcpClient.BeginConnect(pythonServerIP, controlPort, null, null);
                    var success = result.AsyncWaitHandle.WaitOne(TimeSpan.FromSeconds(2));

                    if (success)
                    {
                        _tcpClient.EndConnect(result);
                        _tcpStream = _tcpClient.GetStream();
                        if (verboseLog) Debug.Log("[ObstacleReceiver] TCP Linked.");
                        
                        if (autoStart)
                        {
                            SendTcpCommandInternal($"START:{mjpegPort}:{dataPort}");
                        }
                        _isLinked = true;
                    }
                    else
                    {
                        _tcpClient.Close();
                        _tcpClient = null;
                    }
                }
                catch (Exception e)
                {
                    if (verboseLog) Debug.LogWarning($"[ObstacleReceiver] TCP Connect Error: {e.Message}");
                    _tcpClient = null;
                }
            }

            // If we have a stream, check for incoming data/errors
            if (_tcpClient != null && _tcpClient.Connected)
            {
                try
                {
                    if (_tcpStream.DataAvailable)
                    {
                        byte[] buffer = new byte[1024];
                        int bytesRead = _tcpStream.Read(buffer, 0, buffer.Length);
                        if (bytesRead > 0)
                        {
                            string response = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                            if (verboseLog) Debug.Log($"[ObstacleReceiver] TCP <= {response}");
                        }
                    }
                }
                catch (Exception e)
                {
                    if (verboseLog) Debug.LogWarning($"[ObstacleReceiver] TCP Stream Error: {e.Message}");
                    _tcpClient.Close();
                    _tcpClient = null;
                }
            }

            Thread.Sleep((int)(reconnectDelay * 1000));
        }
    }

    private void UdpDataLoop()
    {
        while (_shouldRunUdp)
        {
            try
            {
                _udpClient = new UdpClient();
                _udpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                _udpClient.Client.Bind(new IPEndPoint(IPAddress.Any, dataPort));
                _udpClient.Client.ReceiveTimeout = 5000;

                IPEndPoint remoteEndPoint = new IPEndPoint(IPAddress.Any, 0);
                if (verboseLog) Debug.Log($"[ObstacleReceiver] UDP Listening on {dataPort}...");

                while (_shouldRunUdp)
                {
                    try
                    {
                        byte[] data = _udpClient.Receive(ref remoteEndPoint);
                        string json = Encoding.UTF8.GetString(data);
                        DetectionPacket packet = JsonUtility.FromJson<DetectionPacket>(json);
                        
                        if (packet != null && packet.d != null)
                        {
                            lock (_queueLock) { _detectionQueue.Enqueue(packet.d); }
                            packetCount++;
                        }
                    }
                    catch (SocketException) { /* Timeout or Close */ }
                    catch (Exception e) { if (verboseLog) Debug.LogWarning($"UDP Data Error: {e.Message}"); }
                }
            }
            catch (Exception e)
            {
                if (verboseLog) Debug.LogError($"UDP Setup Error: {e.Message}");
                Thread.Sleep(2000);
            }
            finally
            {
                if (_udpClient != null) { _udpClient.Close(); _udpClient = null; }
            }
        }
    }

    public void StartDetection() => SendTcpCommandInternal($"START:{mjpegPort}:{dataPort}");
    public void StopDetection() => SendTcpCommandInternal("STOP");

    private void SendTcpCommandInternal(string cmd)
    {
        if (_tcpClient == null || !_tcpClient.Connected || _tcpStream == null) return;
        try
        {
            byte[] data = Encoding.UTF8.GetBytes(cmd);
            _tcpStream.Write(data, 0, data.Length);
            if (verboseLog) Debug.Log($"[ObstacleReceiver] TCP => {cmd}");
        }
        catch (Exception e)
        {
            Debug.LogError($"[ObstacleReceiver] Send Error: {e.Message}");
            _tcpClient.Close();
        }
    }

    private void AnnounceNewDetections(List<ObstacleDetection> detections)
    {
        foreach (var det in detections)
        {
            string label = det.label.ToLower();
            string localizedLabel = AppLanguageManager.Instance.GetText("hazards", label);
            
            if (!_labelCooldowns.ContainsKey(localizedLabel) || Time.time > _labelCooldowns[localizedLabel])
            {
                _labelCooldowns[localizedLabel] = Time.time + announcementCooldown;
            }
        }
    }
}
