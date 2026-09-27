using System.Collections.Generic;
using UnityEngine;

[RequireComponent(typeof(ObstacleReceiver))]
public class ObstacleMOD : MonoBehaviour
{
    [Header("Tracking Settings")]
    public float movingThreshold = 0.005f; // Sensitivity for moving detection
    public int maxCoastFrames = 10;

    // Internal
    private ObstacleReceiver receiver;
    private ObjectTracker tracker;
    private List<Track> activeTracks = new List<Track>();

    // Event for Visualizer (Sends Tracks instead of raw Detections)
    public delegate void TracksUpdated(List<Track> tracks);
    public event TracksUpdated OnTracksUpdated;

    void Start()
    {
        receiver = GetComponent<ObstacleReceiver>();
        tracker = new ObjectTracker();
        
        // Apply settings
        tracker.movingThreshold = movingThreshold;
        tracker.maxFramesToCoast = maxCoastFrames;

        // Subscribe to Receiver
        receiver.OnDetectionsReceived += HandleDetections;
    }

    void OnDestroy()
    {
        if (receiver != null)
        {
            receiver.OnDetectionsReceived -= HandleDetections;
        }
    }

    void HandleDetections(List<ObstacleDetection> detections)
    {
        // Run the Tracker Update
        // Note: Time.deltaTime might be slightly off if UDP comes in on a separate thread,
        // but since OnDetectionsReceived is invoked from Update() in Receiver, it is safe MainThread time.
        activeTracks = tracker.Update(detections, Time.deltaTime);

        // Notify listeners (Visualizer)
        OnTracksUpdated?.Invoke(activeTracks);
    }
}
