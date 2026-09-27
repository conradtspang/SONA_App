using UnityEngine;
using UnityEngine.SceneManagement;

public class Switch_scene : MonoBehaviour
{
    [SerializeField] private string sceneToLoad; 

    public void LoadScene()
    {
        SceneManager.LoadScene(sceneToLoad);
    }
}