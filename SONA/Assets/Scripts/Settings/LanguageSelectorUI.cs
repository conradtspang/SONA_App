using UnityEngine;
using UnityEngine.SceneManagement;

public class LanguageSelectorUI : MonoBehaviour
{
    [Header("Configuration")]
    public string mainSceneName = "OD_slam";

    public void SelectEnglish()
    {
        AppLanguageManager.Instance.ChangeLanguage("en");
        LoadMainScene();
    }

    public void SelectSpanish()
    {
        AppLanguageManager.Instance.ChangeLanguage("es");
        LoadMainScene();
    }

    private void LoadMainScene()
    {
        Debug.Log("[LanguageSelectorUI] Loading main scene: " + mainSceneName);
        SceneManager.LoadScene(mainSceneName);
    }
}
