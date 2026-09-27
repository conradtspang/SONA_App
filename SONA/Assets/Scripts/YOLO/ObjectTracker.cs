using System.Collections.Generic;
using UnityEngine;
using System.Linq;

// This class now handles the logic for tracking objects detected by the Python server
public class ObjectTracker
{
    private List<Track> tracks = new List<Track>();
    private int nextTrackId = 0;
    
    // Config
    public float iouThreshold = 0.3f;
    public int maxFramesToCoast = 10;  // Keep track alive for detection gaps
    public float movingThreshold = 0.002f; // Normalized distance per frame (approx 0.2% of screen width)

    public List<Track> Update(List<ObstacleDetection> detections, float dt)
    {
        // 1. Predict all existing tracks
        foreach (var track in tracks)
        {
            track.kf.Predict(dt);
            // Update box center based on prediction (keep size same for now)
            Vector2 predPos = track.kf.GetPosition();
            track.lastBox.center = predPos;
            track.age++;
            track.framesSinceLastUpdate++;
        }

        // 2. Associate Detections to Tracks (Simple Greedy IoU)
        int[] assignment = new int[detections.Count];
        for (int i = 0; i < assignment.Length; i++) assignment[i] = -1;

        List<int> unassignedTracks = Enumerable.Range(0, tracks.Count).ToList();

        for (int i = 0; i < detections.Count; i++)
        {
            // Convert normalized x,y,w,h to Rect for IoU calculation
            // Note: Detection x,y is Top-Left usually in the JSON
            Rect detBox = new Rect(detections[i].x, detections[i].y, detections[i].w, detections[i].h);

            float bestIoU = 0;
            int bestTrackIdx = -1;

            for (int t = 0; t < unassignedTracks.Count; t++)
            {
                int trackIdx = unassignedTracks[t];
                float iou = CalculateIoU(detBox, tracks[trackIdx].lastBox);
                
                if (iou > bestIoU && iou > iouThreshold)
                {
                    bestIoU = iou;
                    bestTrackIdx = trackIdx;
                }
            }

            if (bestTrackIdx != -1)
            {
                assignment[i] = bestTrackIdx;
                unassignedTracks.Remove(bestTrackIdx);
            }
        }

        // 3. Update Matched Tracks
        for (int i = 0; i < detections.Count; i++)
        {
            Rect detBox = new Rect(detections[i].x, detections[i].y, detections[i].w, detections[i].h);

            if (assignment[i] != -1)
            {
                var track = tracks[assignment[i]];
                
                // Update Kalman Filter
                track.kf.Update(detBox.center);
                track.lastBox = detBox;
                track.framesSinceLastUpdate = 0;
                track.label = detections[i].label; // Update label

                // Update Velocity Stat (Normalized units per second)
                // We divide by dt to get speed in units/sec, but raw frame diff is often enough for simple threshold
                Vector2 vel = track.kf.GetVelocity(); 
                track.velocity = vel;
                
                // Simple motion check: is the velocity magnitude significant?
                track.speed = vel.magnitude;
                track.isMoving = track.speed > movingThreshold;
            }
            else
            {
                // 4. Create New Tracks for Unassigned Detections
                Track newTrack = new Track(nextTrackId++, detBox, detections[i].label);
                tracks.Add(newTrack);
            }
        }

        // 5. Remove Dead Tracks
        tracks.RemoveAll(t => t.framesSinceLastUpdate > maxFramesToCoast);

        return tracks;
    }

    private float CalculateIoU(Rect a, Rect b)
    {
        float intersectionArea = Mathf.Max(0, Mathf.Min(a.xMax, b.xMax) - Mathf.Max(a.xMin, b.xMin)) *
                                 Mathf.Max(0, Mathf.Min(a.yMax, b.yMax) - Mathf.Max(a.yMin, b.yMin));
        float unionArea = (a.width * a.height) + (b.width * b.height) - intersectionArea;
        return intersectionArea / unionArea;
    }
}

// Update Track class to support String labels instead of int classId
public class Track
{
    public int trackId;
    public ObstacleKalmanFilter kf;
    public int age;
    public int framesSinceLastUpdate;
    public string label; // Changed from int classId to string label
    public Rect lastBox;
    
    // MOD Logic
    public bool isMoving;
    public float speed;
    public Vector2 velocity;

    public Track(int id, Rect box, string lbl)
    {
        this.trackId = id;
        this.age = 0;
        this.framesSinceLastUpdate = 0;
        this.label = lbl;
        this.lastBox = box;
        this.kf = new ObstacleKalmanFilter(box.center);
    }
}