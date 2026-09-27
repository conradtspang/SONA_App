using UnityEngine;
using TMPro;
using System.IO;
using UnityEngine.SceneManagement;

public class login_script : MonoBehaviour
{
    public TMP_InputField usernameInput;
    public TMP_InputField passwordInput;
    public TMP_Text feedbackText;

    private string filePath;

    void Start()
    {
        filePath = Path.Combine(Application.persistentDataPath, "userdata.txt");
        Debug.Log("Save file path: " + filePath);
    }

    public void OnSignUpClicked()
    {
        string user = usernameInput.text;
        string pass = passwordInput.text;

        if (string.IsNullOrEmpty(user) || string.IsNullOrEmpty(pass))
        {
            feedbackText.color = Color.red;
            feedbackText.text = "Fields cannot be empty.";
            return;
        }

        string data = user + ":" + pass + "\n";
        File.AppendAllText(filePath, data);
        
        Debug.Log("Account Created!");
        
        // Set color to 15A34A for success
        feedbackText.color = new Color(0.082f, 0.639f, 0.290f); // Hex: #15A34A
        feedbackText.text = "Account created!\nPlease log in again.";
        
        ClearInputs();
    }

    public void OnLoginClicked()
    {
        if (!File.Exists(filePath))
        {
            //EE220C for no users
            feedbackText.color = new Color(0.863f, 0.149f, 0.149f); // Hex: #DC2626
            feedbackText.text = "Invalid username or password.";
            return;
        }

        string user = usernameInput.text;
        string pass = passwordInput.text;

        string[] lines = File.ReadAllLines(filePath);
        bool isAuthenticated = false;

        foreach (string line in lines)
        {
            string[] entry = line.Split(':');
            if (entry.Length == 2)
            {
                if (entry[0] == user && entry[1] == pass)
                {
                    isAuthenticated = true;
                    break;
                }
            }
        }

        if (isAuthenticated)
        {
            Debug.Log("Login Successful!");
            SceneManager.LoadScene("language");
        }
        else
        {
            // Set color to Red for failure
            feedbackText.color = new Color(0.863f, 0.149f, 0.149f); // Hex: #DC2626
            feedbackText.text = "Invalid username or password.";
        }
    }

    private void ClearInputs()
    {
        usernameInput.text = "";
        passwordInput.text = "";
    }
}