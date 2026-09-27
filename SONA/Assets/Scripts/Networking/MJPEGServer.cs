/**
 * MJPEGServer API:
 * - streamPort: The port to broadcast the MJPEG stream.
 * - serverIpText: TMP_Text to display the server's local IP and port.
 * - displayImage: RawImage to preview the stream locally.
 * - inputSource: Select between Camera or Video file as the stream source.
 */

using UnityEngine;
using UnityEngine.Rendering;
using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.IO;
using System.Text;
using UnityEngine.UI;
using UnityEngine.Video;
using TMPro;
using Unity.Collections;

public class MJPEGServer : MonoBehaviour
{
    public enum InputSource { Camera, Video }

    [Header("Input Source")]
    public InputSource inputSource = InputSource.Video;

    [Header("Network Settings")]
    public int streamPort = 8080;

    [Header("UI References")]
    public TMP_Text serverIpText;
    public RawImage displayImage;

    [Header("Streaming Dimensions")]
    [Tooltip("Target resolution for the MJPEG stream. 640x360 is recommended for performance.")]
    public int requestedWidth = 640;
    public int requestedHeight = 360;
    public int requestedFPS = 15;

    [Header("Camera Settings")]
    public string specificCameraName = "";

    [Header("Video Settings")]
    public VideoPlayer videoPlayer;
    private RenderTexture _videoSourceTexture;
    private RenderTexture _downscaleRT;

    [Header("Quality Settings")]
    [Range(10, 100)] public int jpegQuality = 50;

    [Header("Rotation")]
    [Tooltip("Rotate the video 90 degrees counter-clockwise (Portrait mode).")]
    public bool rotate90 = false;
    private byte[] _rotationBuffer;

    private TcpListener _listener;
    private Thread _serverThread;
    private volatile bool _isRunning = false;

    private WebCamTexture _webCam;
    private Texture2D _outputTexture;
    
    private byte[] _latestFrame;
    private long _frameId = 0;
    private readonly object _frameLock = new object();

    private float _encodeTimer = 0f;
    private string _localIp = "127.0.0.1";

    // Performance tracking
    private int _framesEncoded = 0;
    private float _fpsTimer = 0f;

    private Coroutine _captureCoroutine;

    void Start()
    {
        _localIp = GetLocalIPAddress();
        _isRunning = true;
        UpdateServerIpText();

        // Initialize textures
        int outW = rotate90 ? requestedHeight : requestedWidth;
        int outH = rotate90 ? requestedWidth : requestedHeight;

        _outputTexture = new Texture2D(outW, outH, TextureFormat.RGB24, false);
        _downscaleRT = new RenderTexture(requestedWidth, requestedHeight, 0, RenderTextureFormat.ARGB32);
        _downscaleRT.Create();

        if (inputSource == InputSource.Camera) StartCamera();
        else if (inputSource == InputSource.Video) StartVideo();

        _serverThread = new Thread(StartServer) { IsBackground = true };
        _serverThread.Start();
        
        _captureCoroutine = StartCoroutine(CaptureLoop());
        
        Debug.Log($"[MJPEGServer] Server started on port {streamPort}. Target: {outW}x{outH} @ {requestedFPS}fps (Rotated: {rotate90})");
    }

    void StartCamera()
    {
        WebCamDevice[] devices = WebCamTexture.devices;
        if (devices.Length == 0) { Debug.LogError("[MJPEGServer] No camera detected!"); return; }

        string deviceName = devices[0].name;
        if (!string.IsNullOrEmpty(specificCameraName))
        {
            foreach (var d in devices) if (d.name == specificCameraName) { deviceName = d.name; break; }
        }

        _webCam = new WebCamTexture(deviceName, requestedWidth, requestedHeight, requestedFPS);
        _webCam.Play();
        Debug.Log($"[MJPEGServer] Using camera: {deviceName}");

        if (displayImage != null) displayImage.texture = _outputTexture;
    }

    void StartVideo()
    {
        if (videoPlayer == null) return;
        
        videoPlayer.isLooping = true;
        videoPlayer.renderMode = VideoRenderMode.RenderTexture;
        videoPlayer.skipOnDrop = false; // Disable skip on drop as it can sometimes cause freezes
        
        // Critical fix for "First video frame not zero" sync issues:
        // Disable audio to prevent the player from waiting infinitely for delayed audio tracks.
        videoPlayer.audioOutputMode = VideoAudioOutputMode.None;
        
        videoPlayer.prepareCompleted += (vp) => {
            if (_videoSourceTexture != null) _videoSourceTexture.Release();
            _videoSourceTexture = new RenderTexture((int)vp.width, (int)vp.height, 16, RenderTextureFormat.ARGB32);
            _videoSourceTexture.Create();
            vp.targetTexture = _videoSourceTexture;
            
            vp.Play();
            
            Debug.Log($"[MJPEGServer] Video Source Prepared: {vp.width}x{vp.height}");
            if (displayImage != null) displayImage.texture = _outputTexture;
        };
        
        if (!videoPlayer.isPrepared) videoPlayer.Prepare();
    }

    private System.Collections.IEnumerator CaptureLoop()
    {
        var wait = new WaitForSecondsRealtime(1f / Mathf.Max(1, requestedFPS));
        
        while (_isRunning)
        {
            yield return wait;

            try
            {
                Texture source = null;
                if (inputSource == InputSource.Camera && _webCam != null && _webCam.isPlaying)
                {
                    source = _webCam;
                }
                else if (inputSource == InputSource.Video && videoPlayer != null && videoPlayer.isPlaying && _videoSourceTexture != null)
                {
                    if (videoPlayer.frame >= 0 && videoPlayer.isPrepared)
                        source = _videoSourceTexture;
                }

                if (source != null)
                {
                    Graphics.Blit(source, _downscaleRT);
                    AsyncGPUReadback.Request(_downscaleRT, 0, TextureFormat.RGB24, OnCompleteReadback);
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[MJPEGServer] Capture Error: {e.Message}");
            }
        }
    }

    void Update()
    {
        if (!_isRunning) return;

        _fpsTimer += Time.deltaTime;
        if (_fpsTimer >= 5f)
        {
            long vFrame = videoPlayer != null ? videoPlayer.frame : -1;
            Debug.Log($"[MJPEGServer] Performance: {(_framesEncoded / 5f):F1} FPS encoded over last 5s. Video Frame: {vFrame}");
            _framesEncoded = 0;
            _fpsTimer = 0f;
        }
    }

    void OnCompleteReadback(AsyncGPUReadbackRequest request)
    {
        if (!_isRunning || request.hasError) return;

        NativeArray<byte> data = request.GetData<byte>();
        if (data.IsCreated && data.Length > 0)
        {
            if (rotate90)
            {
                if (_rotationBuffer == null || _rotationBuffer.Length != data.Length)
                    _rotationBuffer = new byte[data.Length];

                int w = requestedWidth;
                int h = requestedHeight;

                // 90 Degrees Counter-Clockwise: (x, y) -> (y, w - 1 - x)
                // Pixel (x, y) index in src: (y * w + x) * 3
                // Pixel (y, w - 1 - x) index in dst: ((w - 1 - x) * h + y) * 3
                for (int y = 0; y < h; y++)
                {
                    int rowOffset = y * w;
                    for (int x = 0; x < w; x++)
                    {
                        int srcIdx = (rowOffset + x) * 3;
                        int dstIdx = ((w - 1 - x) * h + y) * 3;
                        _rotationBuffer[dstIdx] = data[srcIdx];
                        _rotationBuffer[dstIdx + 1] = data[srcIdx + 1];
                        _rotationBuffer[dstIdx + 2] = data[srcIdx + 2];
                    }
                }
                _outputTexture.LoadRawTextureData(_rotationBuffer);
            }
            else
            {
                _outputTexture.LoadRawTextureData(data);
            }

            _outputTexture.Apply();
            
            // Encode to JPG
            byte[] bytes = _outputTexture.EncodeToJPG(jpegQuality);
            
            lock (_frameLock)
            {
                _latestFrame = bytes;
                _frameId++;
                _framesEncoded++;
            }
        }
    }

    void StartServer()
    {
        try
        {
            _listener = new TcpListener(IPAddress.Any, streamPort);
            _listener.Start();
            while (_isRunning)
            {
                if (!_listener.Pending()) { Thread.Sleep(20); continue; }
                TcpClient client = _listener.AcceptTcpClient();
                new Thread(() => HandleClient(client)) { IsBackground = true }.Start();
            }
        }
        catch (Exception e) { if (_isRunning) Debug.LogError($"[MJPEGServer] Listener error: {e.Message}"); }
        finally { _listener?.Stop(); }
    }

    void HandleClient(TcpClient client)
    {
        string clientEndPoint = client.Client.RemoteEndPoint.ToString();
        Debug.Log($"[MJPEGServer] Client connected: {clientEndPoint}");

        try
        {
            client.NoDelay = true;
            client.SendBufferSize = 512 * 1024;
            client.SendTimeout = 3000;

            using NetworkStream stream = client.GetStream();
            string boundary = "myboundary";

            void WriteStr(string s) {
                byte[] b = Encoding.ASCII.GetBytes(s);
                stream.Write(b, 0, b.Length);
            }

            WriteStr("HTTP/1.1 200 OK\r\n");
            WriteStr($"Content-Type: multipart/x-mixed-replace; boundary={boundary}\r\n");
            WriteStr("Cache-Control: no-cache, no-store, must-revalidate\r\n");
            WriteStr("Connection: close\r\n\r\n");

            long lastSentFrameId = -1;

            while (_isRunning && client.Connected)
            {
                byte[] frame = null;
                long currentFrameId = -1;

                lock (_frameLock)
                {
                    if (_latestFrame != null && _frameId != lastSentFrameId)
                    {
                        frame = _latestFrame;
                        currentFrameId = _frameId;
                    }
                }

                if (frame != null)
                {
                    WriteStr($"--{boundary}\r\n");
                    WriteStr("Content-Type: image/jpeg\r\n");
                    WriteStr($"Content-Length: {frame.Length}\r\n\r\n");
                    stream.Write(frame, 0, frame.Length);
                    WriteStr("\r\n");
                    stream.Flush();
                    lastSentFrameId = currentFrameId;
                }
                else Thread.Sleep(10);
            }
        }
        catch (Exception e) { if (_isRunning) Debug.Log($"[MJPEGServer] Client {clientEndPoint} disconnected: {e.Message}"); }
        finally { client.Close(); }
    }

    void OnDisable()
    {
        _isRunning = false;
        _listener?.Stop();
        if (_webCam != null) _webCam.Stop();
        if (videoPlayer != null) videoPlayer.Stop();
        if (_videoSourceTexture != null) { _videoSourceTexture.Release(); _videoSourceTexture = null; }
        if (_downscaleRT != null) { _downscaleRT.Release(); _downscaleRT = null; }
        if (_serverThread != null && _serverThread.IsAlive) _serverThread.Join(500);
    }

    void UpdateServerIpText()
    {
        if (serverIpText != null) serverIpText.text = $"STREAM\nhttp://{_localIp}:{streamPort}";
    }

    private string GetLocalIPAddress()
    {
        try
        {
            var host = Dns.GetHostEntry(Dns.GetHostName());
            foreach (var ip in host.AddressList) if (ip.AddressFamily == AddressFamily.InterNetwork) return ip.ToString();
        }
        catch { }
        return "127.0.0.1";
    }
}
