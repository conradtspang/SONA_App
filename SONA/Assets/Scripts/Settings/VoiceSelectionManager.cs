using UnityEngine;
using UnityEngine.UI;
using System;

public class VoiceSelectionManager : MonoBehaviour
{
    private static VoiceSelectionManager _instance;
    public static VoiceSelectionManager Instance
    {
        get
        {
            if (_instance == null)
            {
                _instance = FindObjectOfType<VoiceSelectionManager>();
                if (_instance == null)
                {
                    GameObject go = new GameObject("VoiceSelectionManager");
                    _instance = go.AddComponent<VoiceSelectionManager>();
                    DontDestroyOnLoad(go);
                }
            }
            return _instance;
        }
    }

    [Header("UI Buttons")]
    public Button maleButton;
    public Button femaleButton;

    [Header("Visual Settings - Female")]
    public Color femaleBorderColor = new Color(1f, 0.4f, 0.7f); // Pink
    public Color femalePrimaryColor = new Color(0f, 0.5f, 1f); // Blue
    
    [Header("Visual Settings - Male")]
    public Color maleBorderColor = new Color(0f, 0.5f, 1f); // Default Blue
    public Color malePrimaryColor = Color.white;

    [Header("Common Visual Settings")]
    public float borderWidth = 6f; // Thick

    [Header("State")]
    public string currentGender = "female"; // Default

    private void Awake()
    {
        if (_instance != null && _instance != this)
        {
            Destroy(gameObject);
            return;
        }
        _instance = this;
        DontDestroyOnLoad(gameObject);
        LoadSettings();
    }

    private void Start()
    {
        UpdateUI();
        
        if (maleButton != null)
            maleButton.onClick.AddListener(() => SetGender("male"));
        
        if (femaleButton != null)
            femaleButton.onClick.AddListener(() => SetGender("female"));
    }

    private void LoadSettings()
    {
        currentGender = PlayerPrefs.GetString("VoiceGender", "female");
    }

    public void SetGender(string gender)
    {
        currentGender = gender;
        PlayerPrefs.SetString("VoiceGender", currentGender);
        PlayerPrefs.Save();
        UpdateUI();
        Debug.Log($"[VoiceSelectionManager] Voice gender set to: {currentGender}");
    }

    private void UpdateUI()
    {
        // Update visual borders/outlines and primary colors
        SetButtonSelected(maleButton, currentGender == "male", maleBorderColor, malePrimaryColor);
        SetButtonSelected(femaleButton, currentGender == "female", femaleBorderColor, femalePrimaryColor);
    }

    private void SetButtonSelected(Button btn, bool selected, Color borderColor, Color primaryColor)
    {
        if (btn == null) return;

        // 1. Handle Border (Outline)
        Outline outline = btn.GetComponent<Outline>();
        if (outline == null)
        {
            outline = btn.gameObject.AddComponent<Outline>();
        }

        outline.effectColor = borderColor;
        outline.effectDistance = selected ? new Vector2(borderWidth, borderWidth) : Vector2.zero;
        outline.enabled = selected;

        // 2. Handle Primary Color (Button Image or Icon)
        // We assume the button has an Image component we want to color
        Image img = btn.GetComponent<Image>();
        if (img != null)
        {
            img.color = selected ? primaryColor : Color.white;
        }
    }
}
