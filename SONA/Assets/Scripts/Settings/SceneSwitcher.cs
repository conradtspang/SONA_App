using UnityEngine;
using UnityEngine.SceneManagement;

public class SceneSwitcher : MonoBehaviour
{
    public void LoadSetupScene()
    {
        SceneManager.LoadScene("Cali_Map_Setup");
    }

    public void LoadNavigationScene()
    {
        SceneManager.LoadScene("Loc_YOLO_Navigation");
    }
}
