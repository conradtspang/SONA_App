using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UI;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using TMPro;

public class SpeechClient : MonoBehaviour
{
    public enum SpeechState { Idle, Recording, Processing, Speaking }

    [Header("Server Settings")]
    [Tooltip("Endpoint for Speech-To-Text")]
    public string sttServerUrl = "http://127.0.0.1:5007/transcribe";
    
    [Tooltip("Endpoint for Text-To-Speech")]
    public string ttsServerUrl = "http://127.0.0.1:5007/tts";

    [Header("UI References")]
    public TMP_Text statusText;
    public TMP_Text resultText;
    public Image statusIcon;
    public Sprite micIdleSprite;
    public Sprite micSpeakingSprite;

    [Header("Audio Output")]
    public AudioSource audioSource;

    [Header("Core References")]
    public Level1NavigationAdvisor navigationAdvisor;
    public SLAMReceiver slamReceiver;
    public ObstacleReceiver obstacleReceiver;

    [Header("Options")]
    public bool verboseLog = true;

    [Header("Hands-Free (VAD)")]
    public bool enableAutoVAD = true;
    [Range(0.001f, 0.5f)] public float volumeThreshold = 0.02f;
    public float silenceTimeout = 1.2f;
    public int maxRecordingDuration = 6;
    public float currentVolume;

    // API for other scripts
    public event Action<string> OnSpeechRecognized;
    public string LastRecognizedText { get; private set; }

    private SpeechState _state = SpeechState.Idle;
    private AudioClip recording;
    private string microphoneDevice;
    private AudioClip _vadMonitorClip;

    // Rate limiting for TTS
    private string _latestPendingText;
    private string _latestPendingLang;
    private Coroutine _ttsQueueCoroutine;
    private const float TTS_COOLDOWN = 10f;

    public SpeechState CurrentState => _state;

    void Start()
    {
        if (Microphone.devices.Length > 0)
        {
            microphoneDevice = Microphone.devices[0];
            UpdateStatus("Ready. Hold SPACE to record.");
            // Start a looping clip to monitor volume for VAD
            _vadMonitorClip = Microphone.Start(microphoneDevice, true, 1, 44100);
        }
        else
        {
            UpdateStatus("No Microphone detected!");
        }

        if (navigationAdvisor == null) navigationAdvisor = FindObjectOfType<Level1NavigationAdvisor>();
        if (slamReceiver == null) slamReceiver = FindObjectOfType<SLAMReceiver>();
        
        if (audioSource == null) 
            audioSource = gameObject.AddComponent<AudioSource>();
    }

    void Update()
    {
        if (enableAutoVAD && _state == SpeechState.Idle)
        {
            VADUpdate();
        }

        if (Input.GetKeyDown(KeyCode.Space) && _state == SpeechState.Idle)
        {
            StartCoroutine(RecordingRoutine(false));
        }
    }

    private void VADUpdate()
    {
        if (microphoneDevice == null || _vadMonitorClip == null) return;

        currentVolume = GetMaxVolume(_vadMonitorClip);
        if (currentVolume > volumeThreshold)
        {
            StartCoroutine(RecordingRoutine(true));
        }
    }

    private float GetMaxVolume(AudioClip clip)
    {
        float[] samples = new float[128];
        int pos = Microphone.GetPosition(microphoneDevice);
        if (pos < 128) return 0f;
        
        clip.GetData(samples, pos - 128);
        float max = 0;
        foreach (var s in samples)
        {
            float a = Mathf.Abs(s);
            if (a > max) max = a;
        }
        return max;
    }

    IEnumerator RecordingRoutine(bool triggeredByVAD)
    {
        _state = SpeechState.Recording;
        string currentLang = AppLanguageManager.Instance.currentLanguage;
        UpdateStatus($"Recording ({currentLang})...");
        
        // Stop monitor and start actual recording
        Microphone.End(microphoneDevice);
        recording = Microphone.Start(microphoneDevice, false, 10, 44100);

        float startTime = Time.time;
        float lastSoundTime = Time.time;

        while (true)
        {
            yield return null;
            float vol = GetMaxVolume(recording);
            currentVolume = vol;

            if (vol > volumeThreshold)
            {
                lastSoundTime = Time.time;
            }

            if (triggeredByVAD)
            {
                if (Time.time - lastSoundTime > silenceTimeout) break;
                if (Time.time - startTime > maxRecordingDuration) break;
            }
            else
            {
                if (!Input.GetKey(KeyCode.Space)) break;
            }
        }

        Microphone.End(microphoneDevice);
        _state = SpeechState.Processing;
        UpdateStatus("Processing Speech...");

        yield return StartCoroutine(SendSTTRequest(recording, currentLang));
        
        // Restart VAD monitor
        if (enableAutoVAD)
        {
            _vadMonitorClip = Microphone.Start(microphoneDevice, true, 1, 44100);
        }

        _state = SpeechState.Idle;
        UpdateStatus("Ready.");
    }

    IEnumerator SendSTTRequest(AudioClip clip, string langCode)
    {
        byte[] wavData = WavUtility.FromAudioClip(clip);

        WWWForm form = new WWWForm();
        form.AddBinaryData("file", wavData, "recording.wav", "audio/wav");
        form.AddField("language", langCode);

        using (UnityWebRequest www = UnityWebRequest.Post(sttServerUrl, form))
        {
            yield return www.SendWebRequest();

            if (www.result != UnityWebRequest.Result.Success)
            {
                Debug.LogError("STT Error: " + www.error);
                UpdateStatus("Error: " + www.error);
            }
            else
            {
                string jsonResponse = www.downloadHandler.text;
                if (verboseLog) Debug.Log("Server Response: " + jsonResponse);
                
                if (jsonResponse.Contains("\"text\":"))
                {
                    int start = jsonResponse.IndexOf("\"text\":") + 8;
                    int end = jsonResponse.LastIndexOf("\"");
                    string text = jsonResponse.Substring(start, end - start).ToLower();
                    text = text.Trim('"', ' ', '}').Replace("\n", ""); 
                    
                    if (resultText != null) resultText.text = "Detected: " + text;
                    
                    LastRecognizedText = text;
                    OnSpeechRecognized?.Invoke(text);

                    ProcessVoiceCommand(text);

                    if (navigationAdvisor != null)
                    {
                        navigationAdvisor.SpeakExecutingCommand(text);
                    }
                }
            }
        }
    }

    void ProcessVoiceCommand(string text)
    {
        if (slamReceiver == null) return;

        bool isMapping = (text.Contains("map") || text.Contains("mapping") || text.Contains("mapa"));
        bool isLoc = (text.Contains("localize") || text.Contains("localization") || text.Contains("localizar") || text.Contains("location"));
        bool isStop = (text.Contains("stop") || text.Contains("finish") || text.Contains("parar") || text.Contains("detener") || text.Contains("exit") || text.Contains("quit") || text.Contains("salir"));
        bool isSave = (text.Contains("save") || text.Contains("guardar") || text.Contains("atlas"));
        bool isCalib = (text.Contains("calibrate") || text.Contains("calibration") || text.Contains("calibrar"));

        if (isMapping)
        {
            if (slamReceiver.MappingState == SLAMReceiver.SlamFeatureState.Idle)
                slamReceiver.ToggleMappingWorkflow();
        }
        else if (isLoc)
        {
            if (slamReceiver.LocState == SLAMReceiver.SlamFeatureState.Idle)
                slamReceiver.ToggleLocalizationWorkflow();
        }
        else if (isStop || isSave)
        {
            if (slamReceiver.MappingState == SLAMReceiver.SlamFeatureState.Running)
                slamReceiver.ToggleMappingWorkflow();
            else if (slamReceiver.LocState == SLAMReceiver.SlamFeatureState.Running)
                slamReceiver.ToggleLocalizationWorkflow();
        }
        else if (isCalib)
        {
             if (slamReceiver.CalibState == SLAMReceiver.SlamFeatureState.Idle)
                slamReceiver.CalibAction();
        }
    }

    public void Speak(string text, string langCode)
    {
        _latestPendingText = text;
        _latestPendingLang = langCode;
        if (_ttsQueueCoroutine == null)
        {
            _ttsQueueCoroutine = StartCoroutine(TTSQueueProcessor());
        }
    }

    private IEnumerator TTSQueueProcessor()
    {
        while (!string.IsNullOrEmpty(_latestPendingText))
        {
            string textToSpeak = _latestPendingText;
            string langToUse = _latestPendingLang;
            _latestPendingText = null;

            _state = SpeechState.Speaking;
            yield return StartCoroutine(GetTTSAndPlay(textToSpeak, langToUse));
            _state = SpeechState.Idle;

            yield return new WaitForSeconds(TTS_COOLDOWN);
        }
        _ttsQueueCoroutine = null;
    }

    IEnumerator GetTTSAndPlay(string text, string langCode)
    {
        string gender = PlayerPrefs.GetString("VoiceGender", "female");
        string json = "{\"text\":\"" + text + "\", \"language\":\"" + langCode + "\", \"gender\":\"" + gender + "\"}";
        
        using (UnityWebRequest www = new UnityWebRequest(ttsServerUrl, "POST"))
        {
            byte[] bodyRaw = System.Text.Encoding.UTF8.GetBytes(json);
            www.uploadHandler = new UploadHandlerRaw(bodyRaw);
            www.downloadHandler = new DownloadHandlerAudioClip(ttsServerUrl, AudioType.WAV);
            www.SetRequestHeader("Content-Type", "application/json");

            yield return www.SendWebRequest();

            if (www.result == UnityWebRequest.Result.Success)
            {
                string contentType = www.GetResponseHeader("Content-Type");
                if (contentType != null && contentType.Contains("application/json"))
                {
                    Debug.LogError($"[SpeechClient] Server error: {www.downloadHandler.text}");
                    yield break;
                }

                AudioClip clip = DownloadHandlerAudioClip.GetContent(www);
                if (clip != null)
                {
                    audioSource.clip = clip;
                    audioSource.Play();
                    while (audioSource.isPlaying) yield return null;
                }
            }
            else
            {
                Debug.LogError("TTS Request Failed: " + www.error);
            }
        }
    }

    void UpdateStatus(string msg)
    {
        if (statusText != null) statusText.text = msg;

        if (statusIcon != null)
        {
            if (_state == SpeechState.Recording)
            {
                if (micSpeakingSprite != null) statusIcon.sprite = micSpeakingSprite;
                statusIcon.color = new Color(1f, 0.4f, 0.4f); // Slight red tint for recording
            }
            else
            {
                if (micIdleSprite != null) statusIcon.sprite = micIdleSprite;
                statusIcon.color = Color.white;
            }
        }
    }
}

public static class WavUtility
{
    public static byte[] FromAudioClip(AudioClip clip)
    {
        using (MemoryStream stream = new MemoryStream())
        {
            stream.Write(System.Text.Encoding.UTF8.GetBytes("RIFF"), 0, 4);
            stream.Write(BitConverter.GetBytes(36 + clip.samples * 2), 0, 4);
            stream.Write(System.Text.Encoding.UTF8.GetBytes("WAVE"), 0, 4);
            stream.Write(System.Text.Encoding.UTF8.GetBytes("fmt "), 0, 4);
            stream.Write(BitConverter.GetBytes(16), 0, 4);
            stream.Write(BitConverter.GetBytes((ushort)1), 0, 2);
            stream.Write(BitConverter.GetBytes((ushort)clip.channels), 0, 2);
            stream.Write(BitConverter.GetBytes(clip.frequency), 0, 4);
            stream.Write(BitConverter.GetBytes(clip.frequency * clip.channels * 2), 0, 4);
            stream.Write(BitConverter.GetBytes((ushort)(clip.channels * 2)), 0, 2);
            stream.Write(BitConverter.GetBytes((ushort)16), 0, 2);
            stream.Write(System.Text.Encoding.UTF8.GetBytes("data"), 0, 4);
            stream.Write(BitConverter.GetBytes(clip.samples * 2), 0, 4);

            float[] data = new float[clip.samples * clip.channels];
            clip.GetData(data, 0);
            foreach (var sample in data)
            {
                short s = (short)(Mathf.Clamp(sample, -1f, 1f) * short.MaxValue);
                stream.Write(BitConverter.GetBytes(s), 0, 2);
            }
            return stream.ToArray();
        }
    }
}
