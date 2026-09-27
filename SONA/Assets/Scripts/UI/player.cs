using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Video;
using TMPro;


public class player : MonoBehaviour
{
    public Toggle toggle1;
    public VideoPlayer videoplayer1;
    public TextMeshProUGUI buttonText;
    bool isPlaying = false;

    // Start is called before the first frame update
    void Start()
    {
        
        videoplayer1.Pause();
    }

    // Update is called once per frame
    void Update()
    {
        videoplayer1.isLooping = toggle1.isOn;
    }
    public void playButton()
    {
        if (isPlaying)
        {
            videoplayer1.Pause();
            buttonText.text = "Play";
        }
        else
        {
            videoplayer1.Play();
            buttonText.text = "Pause";
        }
        isPlaying = !isPlaying;
    }
}
