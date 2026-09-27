using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UI; 
using TMPro; 
using System.Collections;
using System.Collections.Generic;
using System.IO;

public class MusicManager : MonoBehaviour
{
    public AudioSource audioSource;
    public TMP_Dropdown musicDropdown;
    public Toggle loopToggle;
    
    // NEW: Reference to the text component on your Play Button
    public TextMeshProUGUI playButtonText; 

    private List<string> songPaths = new List<string>();
    private const int MaxSongs = 5;
    private string lastLoadedPath = "";

    void Start()
    {
        musicDropdown.ClearOptions();
        musicDropdown.options.Add(new TMP_Dropdown.OptionData("No Music Uploaded"));
        
        audioSource.loop = loopToggle.isOn;
        loopToggle.onValueChanged.AddListener(SetLooping);
    }

    void Update()
    {
        // Auto-reset button text if the song finishes playing on its own
        if (!audioSource.isPlaying && playButtonText.text == "Pause" && !audioSource.loop)
        {
            playButtonText.text = "Play";
        }
    }

    public void OpenFilePicker()
    {
        if (songPaths.Count >= MaxSongs)
        {
            Debug.LogWarning("Storage full!");
            return;
        }

        NativeFilePicker.PickFile((path) =>
        {
            if (path == null) return;
            songPaths.Add(path);
            UpdateDropdown();
        }, new string[] { "audio/*" });
    }

    void UpdateDropdown()
    {
        musicDropdown.ClearOptions();
        foreach (string path in songPaths)
        {
            musicDropdown.options.Add(new TMP_Dropdown.OptionData(Path.GetFileName(path)));
        }
        musicDropdown.RefreshShownValue();
    }

    // --- MODIFIED PLAY BUTTON LOGIC ---
    public void OnPlayButtonClick()
    {
        if (songPaths.Count == 0) return;

        int selectedIndex = musicDropdown.value;
        string selectedPath = songPaths[selectedIndex];

        // 1. If we are currently playing THIS exact song, pause it
        if (audioSource.isPlaying && lastLoadedPath == selectedPath)
        {
            audioSource.Pause();
            playButtonText.text = "Play";
        }
        // 2. If it was paused but it's the same song, unpause it
        else if (!audioSource.isPlaying && audioSource.clip != null && lastLoadedPath == selectedPath)
        {
            audioSource.UnPause();
            playButtonText.text = "Pause";
        }
        // 3. If it's a brand new song or nothing is loaded yet, load and play
        else
        {
            StopAllCoroutines();
            StartCoroutine(LoadAudio(selectedPath));
            lastLoadedPath = selectedPath;
            playButtonText.text = "Pause";
        }
    }

    IEnumerator LoadAudio(string path)
    {
        using (UnityWebRequest uwr = UnityWebRequestMultimedia.GetAudioClip("file://" + path, AudioType.UNKNOWN))
        {
            yield return uwr.SendWebRequest();

            if (uwr.result != UnityWebRequest.Result.Success)
            {
                Debug.LogError("Error loading audio: " + uwr.error);
            }
            else
            {
                audioSource.clip = DownloadHandlerAudioClip.GetContent(uwr);
                audioSource.Play();
                playButtonText.text = "Pause"; 
            }
        }
    }

    public void SetLooping(bool shouldLoop)
    {
        audioSource.loop = shouldLoop;
    }
}