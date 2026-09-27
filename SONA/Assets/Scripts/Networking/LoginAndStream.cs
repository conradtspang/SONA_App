using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using UnityEngine.Networking;

public class LoginAndStream : MonoBehaviour
{
    [Header("Login UI")]
    [Tooltip("TMP Input Field for Username")]
    public TMP_InputField usernameInput;
    [Tooltip("TMP Input Field for Password")]
    public TMP_InputField passwordInput;
    [Tooltip("Button to trigger the login check")]
    public Button loginButton;

    [Header("Credentials Settings")]
    public string correctUsername = "admin";
    public string correctPassword = "password";

    [Header("Streaming Settings")]
    [Tooltip("RawImage to display the MJPEG stream")]
    public RawImage streamDisplay;
    [Tooltip("The IP and port for the MJPEG stream, e.g., http://192.168.1.100:8080/stream")]
    public string streamUrl = "http://127.0.0.1:8080/stream";

    private bool isStreaming = false;
    private Texture2D streamTexture;

    void Start()
    {
        // Hook up the button click event if the button is assigned
        if (loginButton != null)
        {
            loginButton.onClick.AddListener(OnLoginButtonClicked);
        }

        // Initialize the texture that will hold the stream frames
        streamTexture = new Texture2D(2, 2);
        if (streamDisplay != null)
        {
            streamDisplay.texture = streamTexture;
        }
    }

    public void OnLoginButtonClicked()
    {
        // Compare the inputted text with the correct credentials
        if (usernameInput.text == correctUsername && passwordInput.text == correctPassword)
        {
            Debug.Log("Login successful! Connecting to stream...");
            StartStream();
        }
        else
        {
            Debug.LogWarning("Login failed: Incorrect username or password.");
        }
    }

    public void StartStream()
    {
        if (!isStreaming)
        {
            isStreaming = true;
            StartCoroutine(FetchMJPEGStream());
        }
    }

    public void StopStream()
    {
        isStreaming = false;
    }

    private IEnumerator FetchMJPEGStream()
    {
        using (UnityWebRequest webRequest = UnityWebRequest.Get(streamUrl))
        {
            // Use a custom download handler to parse the MJPEG frames continuously
            webRequest.downloadHandler = new MJPEGDownloadHandler(streamTexture);
            webRequest.SendWebRequest();

            while (isStreaming && !webRequest.isDone)
            {
                yield return null;
            }

            if (webRequest.result != UnityWebRequest.Result.Success)
            {
                Debug.LogError("Stream Error: " + webRequest.error);
            }
        }
    }
}

/// <summary>
/// A custom download handler to read and parse the multipart MJPEG stream frame by frame.
/// </summary>
public class MJPEGDownloadHandler : DownloadHandlerScript
{
    private Texture2D texture;
    private List<byte> buffer = new List<byte>();
    
    // JPEG start and end markers
    private readonly byte[] jpegSOI = new byte[] { 0xFF, 0xD8 };
    private readonly byte[] jpegEOI = new byte[] { 0xFF, 0xD9 };

    public MJPEGDownloadHandler(Texture2D targetTexture) : base(new byte[1024 * 256]) // 256kb buffer for incoming data
    {
        texture = targetTexture;
    }

    protected override bool ReceiveData(byte[] data, int dataLength)
    {
        if (data == null || dataLength == 0) return true;

        for (int i = 0; i < dataLength; i++)
        {
            buffer.Add(data[i]);
        }

        ProcessBuffer();

        return true;
    }

    private void ProcessBuffer()
    {
        int startIdx = FindMarker(buffer, jpegSOI);
        if (startIdx != -1)
        {
            // Remove any garbage data before the start marker
            if (startIdx > 0)
            {
                buffer.RemoveRange(0, startIdx);
                startIdx = 0;
            }

            int endIdx = FindMarker(buffer, jpegEOI, startIdx);
            if (endIdx != -1)
            {
                // A full JPEG frame has been received
                int frameLength = endIdx - startIdx + 2;
                byte[] frameData = new byte[frameLength];
                buffer.CopyTo(startIdx, frameData, 0, frameLength);

                // Load the image into the texture
                texture.LoadImage(frameData);
                
                // Clear the processed frame from the buffer
                buffer.RemoveRange(0, endIdx + 2);
            }
        }
        else if (buffer.Count > 1024 * 1024 * 2) 
        {
            // Safety measure: clear buffer if it grows too large (2MB) without finding a valid frame
            buffer.Clear();
        }
    }

    private int FindMarker(List<byte> list, byte[] marker, int startIndex = 0)
    {
        for (int i = startIndex; i < list.Count - marker.Length + 1; i++)
        {
            bool match = true;
            for (int j = 0; j < marker.Length; j++)
            {
                if (list[i + j] != marker[j])
                {
                    match = false;
                    break;
                }
            }
            if (match) return i;
        }
        return -1;
    }
}
