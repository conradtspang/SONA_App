using UnityEngine;
using UnityEngine.UI;
using UnityEngine.SceneManagement;

public class PasscodeController : MonoBehaviour
{
    [Header("Passcode Settings")]
    public string correctPasscode = "1234"; // Set your target password here
    private string currentInput = "";

    [Header("UI References")]
    // Array to hold the 4 UI Images representing the dots
    public Image[] passcodeIndicators; 
    public Sprite emptyBoxSprite;  // Assign the empty box graphic
    public Sprite filledDotSprite; // Assign the graphic with the dot inside

    // Link this to your 0-9 buttons in the Inspector
    public void OnNumberButtonPressed(string number)
    {
        // Get the maximum length based on how many UI indicator boxes you assigned
        int maxPasscodeLength = passcodeIndicators.Length;

        // Only accept input if we haven't filled all the boxes yet
        if (currentInput.Length < maxPasscodeLength)
        {
            currentInput += number;
            UpdateIndicators();

            // Auto-check the passcode exactly when the last digit is entered
            if (currentInput.Length == maxPasscodeLength)
            {
                CheckPasscode();
            }
        }
    }

    // Link this to your delete/backspace button
    public void OnDeleteButtonPressed()
    {
        if (currentInput.Length > 0)
        {
            // Remove the last character from the string
            currentInput = currentInput.Substring(0, currentInput.Length - 1);
            UpdateIndicators();
        }
    }

    // Updates the visual UI elements
    private void UpdateIndicators()
    {
        for (int i = 0; i < passcodeIndicators.Length; i++)
        {
            // If the index is less than the input length, show the filled dot
            if (i < currentInput.Length)
            {
                passcodeIndicators[i].sprite = filledDotSprite;
            }
            else
            {
                passcodeIndicators[i].sprite = emptyBoxSprite;
            }
        }
    }

    private void CheckPasscode()
    {
        if (currentInput == correctPasscode)
        {
            Debug.Log("Passcode Correct!");
            SceneManager.LoadScene("language");
            // TODO: Add your logic here (e.g., unlock door, load next scene)
        }
        else
        {
            Debug.Log("Passcode Incorrect!");
            // Reset the input so they can try again
            currentInput = "";
            UpdateIndicators();
        }
    }
}