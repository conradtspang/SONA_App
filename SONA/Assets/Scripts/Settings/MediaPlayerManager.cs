using System.Collections;
using System.IO;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Networking;
using UnityEngine.EventSystems;
using TMPro;

public class MediaPlayerManager : MonoBehaviour
{
    [Header("UI References")]
    public AudioSource audioSource;
    public RawImage displayImage;
    public TMP_Text statusText; 

    [Header("Song Buttons (Assign 4 buttons)")]
    public Button[] songButtons = new Button[4];
    public TMP_Text[] songButtonTexts = new TMP_Text[4]; 

    [Header("Image Buttons (Assign 4 buttons)")]
    public Button[] imageButtons = new Button[4];
    public RawImage[] imageButtonThumbnails = new RawImage[4];

    [Header("Playback Controls")]
    public Button playPauseButton;
    public Image playPauseIconTarget; 
    public Sprite playIcon;
    public Sprite pauseIcon;
    public TMP_Text playPauseText; 

    [Header("Visual Settings (Selection Outline)")]
    public Color selectionBorderColor = new Color(0.2f, 0.4f, 1f, 1f); 
    public Vector2 borderThickness = new Vector2(6f, -6f); 

    // Data Storage
    private string[] songPaths = new string[4];
    private string[] imagePaths = new string[4];

    private int selectedSongIndex = -1;
    private int selectedImageIndex = -1;
    
    // Playback State Tracker
    private int currentlyLoadedSongIndex = -1;
    private bool isPlaying = false;
    
    // Size Tracker
    private Vector2 defaultDisplaySize;

    void Start()
    {
        // Remember the original size you set in the Unity Editor
        if (displayImage != null)
        {
            defaultDisplaySize = displayImage.rectTransform.rect.size;
        }

        SetupButtonOutlines(songButtons);
        SetupButtonOutlines(imageButtons);
        
        LoadSavedData();
        SetupButtonListeners();
        
        if (playPauseButton != null)
            playPauseButton.onClick.AddListener(TogglePlayPause);

        UpdatePlayPauseUI(); 
    }

    private void SetupButtonOutlines(Button[] buttons)
    {
        foreach (Button btn in buttons)
        {
            if (btn == null) continue;
            
            Outline outline = btn.gameObject.GetComponent<Outline>();
            if (outline == null) outline = btn.gameObject.AddComponent<Outline>();
            
            outline.effectColor = selectionBorderColor;
            outline.effectDistance = borderThickness;
            outline.enabled = false; 
        }
    }

    private void SetupButtonListeners()
    {
        for (int i = 0; i < 4; i++)
        {
            int index = i; 

            songButtons[i].onClick.RemoveAllListeners();
            imageButtons[i].onClick.RemoveAllListeners();

            LongPressHandler songHandler = songButtons[i].gameObject.AddComponent<LongPressHandler>();
            songHandler.onShortClick = () => OnSongSlotClicked(index);
            songHandler.onLongPress = () => ClearSong(index);

            LongPressHandler imageHandler = imageButtons[i].gameObject.AddComponent<LongPressHandler>();
            imageHandler.onShortClick = () => OnImageSlotClicked(index);
            imageHandler.onLongPress = () => ClearImage(index);
        }
    }

    private void LoadSavedData()
    {
        for (int i = 0; i < 4; i++)
        {
            songPaths[i] = PlayerPrefs.GetString($"SongPath_{i}", "");
            if (!string.IsNullOrEmpty(songPaths[i]) && File.Exists(songPaths[i]))
                songButtonTexts[i].text = Path.GetFileName(songPaths[i]); 
            else
            {
                songButtonTexts[i].text = $"Upload Song {i + 1}";
                songPaths[i] = ""; 
            }

            imagePaths[i] = PlayerPrefs.GetString($"ImagePath_{i}", "");
            if (!string.IsNullOrEmpty(imagePaths[i]) && File.Exists(imagePaths[i]))
                StartCoroutine(LoadImageToTextureRoutine(imagePaths[i], imageButtonThumbnails[i], false));
            else
                imagePaths[i] = "";
        }
    }

    // --- INTERACTION LOGIC ---

    public void OnSongSlotClicked(int index)
    {
        if (string.IsNullOrEmpty(songPaths[index]))
            AssignSong(index); 
        else if (selectedSongIndex != index)
        {
            selectedSongIndex = index;
            if(statusText) statusText.text = $"Selected Song {index + 1}. Press again to change. Long press to delete.";
            HighlightSelection(songButtons, index);
            if (isPlaying) StopAudio();
        }
        else
            AssignSong(index);
    }

    public void OnImageSlotClicked(int index)
    {
        if (string.IsNullOrEmpty(imagePaths[index]))
            AssignImage(index);
        else if (selectedImageIndex != index)
        {
            selectedImageIndex = index;
            if (statusText) statusText.text = $"Selected Image {index + 1}. Press again to change. Long press to delete.";
            HighlightSelection(imageButtons, index);
            
            if (isPlaying && !string.IsNullOrEmpty(imagePaths[index]))
                StartCoroutine(LoadImageToTextureRoutine(imagePaths[index], displayImage, true));
        }
        else
            AssignImage(index);
    }

    // --- ASSIGNING / PICKING FILES ---

    public void AssignSong(int index)
    {
        NativeFilePicker.PickFile((path) =>
        {
            if (path == null) return;
            songPaths[index] = path;
            PlayerPrefs.SetString($"SongPath_{index}", path);
            songButtonTexts[index].text = Path.GetFileName(path);
            selectedSongIndex = index;
            HighlightSelection(songButtons, index);
            if(statusText) statusText.text = "Song saved successfully!";
            if (currentlyLoadedSongIndex == index) StopAudio(); 
        }, new string[] { "audio/*" });
    }

    public void AssignImage(int index)
    {
        NativeFilePicker.PickFile((path) =>
        {
            if (path == null) return;
            imagePaths[index] = path;
            PlayerPrefs.SetString($"ImagePath_{index}", path);
            StartCoroutine(LoadImageToTextureRoutine(path, imageButtonThumbnails[index], false));
            selectedImageIndex = index;
            HighlightSelection(imageButtons, index);
            if(statusText) statusText.text = "Image saved successfully!";
            if (isPlaying && selectedImageIndex == index)
                 StartCoroutine(LoadImageToTextureRoutine(path, displayImage, true));
        }, new string[] { "image/*" });
    }

    // --- DELETING FILES ---

    public void ClearSong(int index)
    {
        if (currentlyLoadedSongIndex == index) StopAudio(); 
        songPaths[index] = "";
        PlayerPrefs.SetString($"SongPath_{index}", "");
        songButtonTexts[index].text = $"Upload Song {index + 1}";
        if (selectedSongIndex == index) 
        {
            selectedSongIndex = -1;
            HighlightSelection(songButtons, -1); 
        }
        if(statusText) statusText.text = "Song deleted.";
    }

    public void ClearImage(int index)
    {
        imagePaths[index] = "";
        PlayerPrefs.SetString($"ImagePath_{index}", "");
        imageButtonThumbnails[index].texture = null;
        imageButtonThumbnails[index].color = Color.white; 
        
        if (selectedImageIndex == index) 
        {
            selectedImageIndex = -1;
            HighlightSelection(imageButtons, -1); 
        }
        if(statusText) statusText.text = "Image deleted.";
    }

    // --- PLAYBACK CONTROL ---

    private void TogglePlayPause()
    {
        if (selectedSongIndex == -1 || string.IsNullOrEmpty(songPaths[selectedSongIndex]))
        {
            if(statusText) statusText.text = "Please select a song first.";
            return;
        }

        if (isPlaying)
        {
            audioSource.Pause();
            isPlaying = false;
            if (statusText) statusText.text = "Paused";
        }
        else
        {
            if (currentlyLoadedSongIndex == selectedSongIndex && audioSource.clip != null)
            {
                audioSource.Play();
                isPlaying = true;
                if (statusText) statusText.text = "Playing Audio...";
            }
            else
                LoadNewMedia();
        }
        UpdatePlayPauseUI();
    }

    private void StopAudio()
    {
        audioSource.Stop();
        audioSource.clip = null;
        currentlyLoadedSongIndex = -1;
        isPlaying = false;
        UpdatePlayPauseUI();
    }

    private void LoadNewMedia()
    {
        if (displayImage != null) displayImage.color = new Color(1f, 1f, 1f, 0f);
        if (selectedImageIndex != -1 && !string.IsNullOrEmpty(imagePaths[selectedImageIndex]))
            StartCoroutine(LoadImageToTextureRoutine(imagePaths[selectedImageIndex], displayImage, true));
        if (selectedSongIndex != -1 && !string.IsNullOrEmpty(songPaths[selectedSongIndex]))
            StartCoroutine(LoadAndPlayAudio(songPaths[selectedSongIndex]));
    }

    private void UpdatePlayPauseUI()
    {
        if (playPauseIconTarget != null && playIcon != null && pauseIcon != null)
            playPauseIconTarget.sprite = isPlaying ? pauseIcon : playIcon;
        if (playPauseText != null)
            playPauseText.text = isPlaying ? "Pause" : "Start";
    }

    // --- UTILITIES ---

    private IEnumerator LoadImageToTextureRoutine(string path, RawImage targetImage, bool applyAspectRatio)
    {
        string fileUri = "file://" + path.Replace("\\", "/"); 
        using (UnityWebRequest www = UnityWebRequestTexture.GetTexture(fileUri))
        {
            yield return www.SendWebRequest();
            if (www.result == UnityWebRequest.Result.ConnectionError || www.result == UnityWebRequest.Result.ProtocolError)
                Debug.LogError("Error loading image: " + www.error);
            else
            {
                Texture2D tex = DownloadHandlerTexture.GetContent(www);
                targetImage.texture = tex;
                targetImage.color = new Color(1f, 1f, 1f, 1f); 

                if (applyAspectRatio && defaultDisplaySize.x > 0 && defaultDisplaySize.y > 0)
                {
                    // Clean up old fitter if it exists from the previous script version
                    AspectRatioFitter fitter = targetImage.GetComponent<AspectRatioFitter>();
                    if (fitter != null) Destroy(fitter);

                    // Manually calculate the box size so it perfectly fits inside your original editor dimensions
                    float imgRatio = (float)tex.width / tex.height;
                    float boxRatio = defaultDisplaySize.x / defaultDisplaySize.y;

                    if (imgRatio > boxRatio)
                    {
                        // Image is wider than the box
                        targetImage.rectTransform.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, defaultDisplaySize.x);
                        targetImage.rectTransform.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, defaultDisplaySize.x / imgRatio);
                    }
                    else
                    {
                        // Image is taller than the box
                        targetImage.rectTransform.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, defaultDisplaySize.y * imgRatio);
                        targetImage.rectTransform.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, defaultDisplaySize.y);
                    }
                }
            }
        }
    }

    private IEnumerator LoadAndPlayAudio(string path)
    {
        string fileUri = "file://" + path.Replace("\\", "/");
        using (UnityWebRequest www = UnityWebRequestMultimedia.GetAudioClip(fileUri, AudioType.UNKNOWN))
        {
            yield return www.SendWebRequest();
            if (www.result == UnityWebRequest.Result.ConnectionError || www.result == UnityWebRequest.Result.ProtocolError)
                Debug.LogError("Error loading audio: " + www.error);
            else
            {
                AudioClip clip = DownloadHandlerAudioClip.GetContent(www);
                audioSource.clip = clip;
                audioSource.Play();
                currentlyLoadedSongIndex = selectedSongIndex;
                isPlaying = true;
                UpdatePlayPauseUI();
                if(statusText) statusText.text = "Playing Audio...";
            }
        }
    }

    private void HighlightSelection(Button[] array, int activeIndex)
    {
        for (int i = 0; i < array.Length; i++)
        {
            Outline outline = array[i].GetComponent<Outline>();
            if (outline != null) outline.enabled = (i == activeIndex);

            ColorBlock cb = array[i].colors;
            cb.normalColor = Color.white;
            cb.highlightedColor = Color.white;
            cb.selectedColor = Color.white;
            array[i].colors = cb;
        }
    }
}

// =====================================================================
// HELPER CLASS: Adds Long Press functionality to standard Unity Buttons
// =====================================================================
public class LongPressHandler : MonoBehaviour, IPointerDownHandler, IPointerUpHandler, IPointerExitHandler
{
    public float holdDuration = 1.0f; 
    public System.Action onShortClick;
    public System.Action onLongPress;

    private bool isPointerDown = false;
    private bool longPressTriggered = false;
    private float timePressStarted;

    public void OnPointerDown(PointerEventData eventData)
    {
        isPointerDown = true;
        longPressTriggered = false;
        timePressStarted = Time.time;
    }

    public void OnPointerUp(PointerEventData eventData)
    {
        isPointerDown = false;
        if (!longPressTriggered) onShortClick?.Invoke();
    }

    public void OnPointerExit(PointerEventData eventData)
    {
        isPointerDown = false;
    }

    void Update()
    {
        if (isPointerDown && !longPressTriggered)
        {
            if (Time.time - timePressStarted >= holdDuration)
            {
                longPressTriggered = true;
                onLongPress?.Invoke();
            }
        }
    }
}