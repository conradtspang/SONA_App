using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

/**
 * ObstacleStatusUI API:
 * - UpdateStatus(List<ObstacleDetection>): Evaluates detections and updates the UI.
 * - SetThreshold(float): Adjusts the area threshold for "too close" detection.
 */

public class ObstacleStatusUI : MonoBehaviour
{
    [Header("Dependencies")]
    public ObstacleReceiver receiver;

    [Header("UI References - Panel")]
    public Image statusPanel;
    public Image statusIcon;
    public TMP_Text statusText;

    [Header("UI Configuration - Clear")]
    public string clearText = "Path is clear";
    public Color clearColor = new Color(0.1f, 0.7f, 0.1f, 1f); // Dark Green
    public Color clearBgColor = new Color(0.9f, 1f, 0.9f, 1f); // Light Green
    public Sprite clearIcon;

    [Header("UI Configuration - Danger")]
    public string dangerText = "Obstacle too close";
    public Color dangerColor = new Color(0.8f, 0.1f, 0.1f, 1f); // Dark Red
    public Color dangerBgColor = new Color(1f, 0.9f, 0.9f, 1f); // Light Red
    public Sprite dangerIcon;

    [Header("Detection Settings")]
    [Tooltip("If any obstacle area (w * h) exceeds this, it is 'too close'")]
    public float areaThreshold = 0.15f; 
    [Tooltip("How long to stay in 'danger' state after object is gone")]
    public float dangerLingerTime = 0.5f;

    private float _lastDangerTime = -1f;
    private bool _isDanger = false;

    void Start()
    {
        if (receiver != null)
        {
            receiver.OnDetectionsReceived += HandleDetections;
        }
        
        // Initialize as clear
        SetStatus(false);
    }

    void OnDestroy()
    {
        if (receiver != null)
        {
            receiver.OnDetectionsReceived -= HandleDetections;
        }
    }

    void Update()
    {
        // Linger in danger state if recently triggered
        if (_isDanger && Time.time > _lastDangerTime + dangerLingerTime)
        {
            SetStatus(false);
        }
    }

    void HandleDetections(List<ObstacleDetection> detections)
    {
        bool dangerFound = false;
        foreach (var det in detections)
        {
            float area = det.w * det.h;
            if (area > areaThreshold)
            {
                dangerFound = true;
                break;
            }
        }

        if (dangerFound)
        {
            _lastDangerTime = Time.time;
            SetStatus(true);
        }
    }

    void SetStatus(bool isDanger)
    {
        _isDanger = isDanger;

        if (statusPanel != null)
            statusPanel.color = isDanger ? dangerBgColor : clearBgColor;
        
        if (statusIcon != null)
        {
            statusIcon.sprite = isDanger ? dangerIcon : clearIcon;
            statusIcon.color = isDanger ? dangerColor : clearColor;
        }

        if (statusText != null)
        {
            statusText.text = isDanger ? dangerText : clearText;
            statusText.color = isDanger ? dangerColor : clearColor;
        }

        // Handle Outline/Border if present
        Outline outline = statusPanel?.GetComponent<Outline>();
        if (outline != null)
        {
            outline.effectColor = isDanger ? dangerColor : clearColor;
        }
    }

    public void SetThreshold(float val) => areaThreshold = val;
}
