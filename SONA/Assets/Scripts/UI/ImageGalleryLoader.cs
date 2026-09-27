using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using System.Linq;
using System.Globalization;

public class ImageGalleryLoader : MonoBehaviour
{
    public enum DateCategory { Today, Yesterday, TwoDaysAgo, ThreeDaysAgo, OneWeekAgoOrOlder }

    [Header("Configuration")]
    public DateCategory filterMode;
    public string folderName = "RenderOutput";

    [Header("References")]
    public TMP_Dropdown targetDropdown; 
    public RawImage previewImage;
    public TMP_Text timestampText;

    private string _targetPath;
    private List<string> _fullFilePaths = new List<string>();

    void Start()
    {
        _targetPath = Path.Combine(Application.dataPath, folderName);
        if (!Directory.Exists(_targetPath)) Directory.CreateDirectory(_targetPath);

        if (targetDropdown == null) targetDropdown = GetComponent<TMP_Dropdown>();

        targetDropdown.onValueChanged.AddListener(HandleSelection);
        RefreshThisDropdown();
    }

    public void RefreshThisDropdown()
    {
        if (!Directory.Exists(_targetPath)) return;

        // 1. Get all PNG files
        string[] files = Directory.GetFiles(_targetPath, "*.png");
        DateTime today = DateTime.Today;
        
        List<FileData> validFiles = new List<FileData>();

        foreach (string path in files)
        {
            string fileName = Path.GetFileNameWithoutExtension(path); // e.g., "20260410_185501_chair"
            string[] parts = fileName.Split('_');

            // Expecting at least [Date], [Time], [Label]
            if (parts.Length >= 3)
            {
                // Parse the date part (20260410)
                if (DateTime.TryParseExact(parts[0], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime fileDate))
                {
                    // Filter based on the assigned category
                    bool matches = filterMode switch
                    {
                        DateCategory.Today => fileDate.Date == today,
                        DateCategory.Yesterday => fileDate.Date == today.AddDays(-1),
                        DateCategory.TwoDaysAgo => fileDate.Date == today.AddDays(-2),
                        DateCategory.ThreeDaysAgo => fileDate.Date == today.AddDays(-3),
                        DateCategory.OneWeekAgoOrOlder => fileDate.Date <= today.AddDays(-7),
                        _ => false
                    };

                    if (matches)
                    {
                        // Parse time for the display name (185501 -> 18:55:01)
                        string rawTime = parts[1];
                        string formattedTime = rawTime.Length == 6 
                            ? $"{rawTime.Substring(0, 2)}:{rawTime.Substring(2, 2)}:{rawTime.Substring(4, 2)}" 
                            : rawTime;

                        // Get label (everything after the second underscore)
                        string label = string.Join("_", parts.Skip(2));

                        validFiles.Add(new FileData {
                            FullPath = path,
                            DisplayName = $"{formattedTime} | {label}",
                            SortTime = rawTime // Used to sort by time within the same day
                        });
                    }
                }
            }
        }

        // 2. Sort by time (newest first)
        var sorted = validFiles.OrderByDescending(f => f.SortTime).ToList();
        _fullFilePaths = sorted.Select(f => f.FullPath).ToList();

        // 3. Update Dropdown
        targetDropdown.ClearOptions();
        targetDropdown.AddOptions(sorted.Select(f => f.DisplayName).ToList());

        if (_fullFilePaths.Count > 0)
        {
            targetDropdown.value = 0;
            LoadAndShowImage(_fullFilePaths[0]);
        }
    }

    private void HandleSelection(int index)
    {
        if (index >= 0 && index < _fullFilePaths.Count)
            LoadAndShowImage(_fullFilePaths[index]);
    }

    private void LoadAndShowImage(string path)
    {
        if (!File.Exists(path)) return;
        byte[] fileData = File.ReadAllBytes(path);
        Texture2D tex = new Texture2D(2, 2);
        if (tex.LoadImage(fileData)) 
        {
            if (previewImage != null)
            {
                if (previewImage.texture is Texture2D oldTex) Destroy(oldTex);
                previewImage.texture = tex;
            }

            // Update timestamp text
            if (timestampText != null)
            {
                string fileName = Path.GetFileNameWithoutExtension(path);
                string[] parts = fileName.Split('_');
                if (parts.Length >= 1 && DateTime.TryParseExact(parts[0], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime fileDate))
                {
                    DateTime today = DateTime.Today;
                    string prefix = "";
                    if (fileDate.Date == today) prefix = "Today, ";
                    else if (fileDate.Date == today.AddDays(-1)) prefix = "Yesterday, ";
                    else if (fileDate.Date == today.AddDays(-2)) prefix = "Two Days Ago, ";
                    else if (fileDate.Date == today.AddDays(-3)) prefix = "Three Days Ago, ";
                    else prefix = "Previous, ";

                    timestampText.text = $"{prefix}{fileDate:MM/dd/yyyy}";
                }
            }
        }
    }

    // Helper class to manage parsed data
    private class FileData
    {
        public string FullPath;
        public string DisplayName;
        public string SortTime;
    }
}