using System.Collections;
using System.Collections.Generic;
using UnityEngine;
public class singoutandexit : MonoBehaviour
{
    // Call this function from your "Close" or "Exit" button's OnClick event
    public void CloseApplication()
    {
        Debug.Log("Exit button clicked. Closing application...");

        // This works for the actual built application (Windows, Mac, Android, etc.)
        Application.Quit();

        // This allows the "Exit" button to work while you are testing inside the Unity Editor
        #if UNITY_EDITOR
        UnityEditor.EditorApplication.isPlaying = false;
        #endif
    }
}


