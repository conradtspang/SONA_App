using UnityEngine;
using UnityEngine.UI;
using TMPro;

public class LocalizedText : MonoBehaviour
{
    [Header("Localization Settings")]
    public string category = "ui";
    public string key;

    private TMP_Text _tmpText;
    private Text _legacyText;

    void Awake()
    {
        _tmpText = GetComponent<TMP_Text>();
        _legacyText = GetComponent<Text>();
    }

    void OnEnable()
    {
        if (AppLanguageManager.Instance != null)
        {
            AppLanguageManager.Instance.OnLanguageChanged += RefreshText;
        }
        RefreshText();
    }

    void OnDisable()
    {
        if (AppLanguageManager.Instance != null)
        {
            AppLanguageManager.Instance.OnLanguageChanged -= RefreshText;
        }
    }

    public void RefreshText()
    {
        if (AppLanguageManager.Instance == null || string.IsNullOrEmpty(key)) return;

        string localized = AppLanguageManager.Instance.GetText(category, key);
        
        if (_tmpText != null) _tmpText.text = localized;
        if (_legacyText != null) _legacyText.text = localized;
    }

    // Context menu helper to automatically pull the current text as a key
    [ContextMenu("Use Current Text As Key")]
    private void UseCurrentTextAsKey()
    {
        _tmpText = GetComponent<TMP_Text>();
        _legacyText = GetComponent<Text>();

        if (_tmpText != null) key = _tmpText.text.ToLower().Replace(" ", "_");
        else if (_legacyText != null) key = _legacyText.text.ToLower().Replace(" ", "_");
    }
}
