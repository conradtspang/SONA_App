using UnityEngine;
using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;

[Serializable]
public class LanguageData
{
    public Dictionary<string, Dictionary<string, string>> languages;
}

public class AppLanguageManager : MonoBehaviour
{
    private static AppLanguageManager _instance;
    public static AppLanguageManager Instance
    {
        get
        {
            if (_instance == null)
            {
                GameObject go = new GameObject("AppLanguageManager");
                _instance = go.AddComponent<AppLanguageManager>();
                DontDestroyOnLoad(go);
            }
            return _instance;
        }
    }

    [Header("Configuration")]
    public string defaultLanguage = "en";
    public string currentLanguage = "en";

    private Dictionary<string, object> _langData;
    private bool _isLoaded = false;

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
        LoadLanguageFile();
    }

    private void LoadSettings()
    {
        currentLanguage = PlayerPrefs.GetString("AppLanguage", defaultLanguage);
    }

    private void LoadLanguageFile()
    {
        string filePath = Path.Combine(Application.streamingAssetsPath, "app_languages.json");
        if (File.Exists(filePath))
        {
            try
            {
                string jsonText = File.ReadAllText(filePath);
                // Simple nested dictionary parsing using MiniJSON or equivalent would be ideal, 
                // but for simplicity we'll use a basic structure or manual parsing if needed.
                // Since Unity's JsonUtility doesn't support nested dictionaries well, 
                // we'll use a more flexible approach or a helper.
                
                // Let's use a wrapper for the structure
                _langData = Newtonsoft.Json.JsonConvert.DeserializeObject<Dictionary<string, object>>(jsonText);
                _isLoaded = true;
                Debug.Log($"[AppLanguageManager] Language file loaded successfully. Current: {currentLanguage}");
            }
            catch (Exception e)
            {
                Debug.LogError($"[AppLanguageManager] Failed to load language file: {e.Message}");
            }
        }
        else
        {
            Debug.LogError($"[AppLanguageManager] Language file not found at {filePath}");
        }
    }

    public void ChangeLanguage(string langCode)
    {
        currentLanguage = langCode;
        PlayerPrefs.SetString("AppLanguage", currentLanguage);
        PlayerPrefs.Save();
        Debug.Log($"[AppLanguageManager] Language changed to: {currentLanguage}");
        OnLanguageChanged?.Invoke();
    }

    public event Action OnLanguageChanged;

    public string GetText(string category, string key)
    {
        if (!_isLoaded || _langData == null) return key;

        try
        {
            var languages = _langData["languages"] as Newtonsoft.Json.Linq.JObject;
            if (languages == null || !languages.ContainsKey(currentLanguage)) return key;

            var langObj = languages[currentLanguage] as Newtonsoft.Json.Linq.JObject;
            if (langObj == null || !langObj.ContainsKey(category)) return key;

            var catObj = langObj[category] as Newtonsoft.Json.Linq.JObject;
            if (catObj == null || !catObj.ContainsKey(key)) return key;

            return catObj[key].ToString();
        }
        catch
        {
            return key;
        }
    }
}
