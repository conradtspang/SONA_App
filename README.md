<div align="center">
  <img src="sona_logo.png" alt="SONA Logo" width="150" />
  <h1>🌟 SONA (Spatial Obstacle Navigation Aid)</h1>
  <p><strong>A Proactive Digital Co-Pilot for the Elderly & Visually Impaired</strong></p>
</div>

## 📖 Overview

**SONA** is the world's first real-world elderly and visually impaired assistive application that combines **real-time Visual SLAM** with **YOLO-World object detection**. 

Hidden tripping hazards cause over 80% of indoor falls. Inspired by the need to navigate cramped indoor spaces safely, SONA acts as a "digital co-pilot." A mobile phone is placed on the user's walker, utilizing the camera to map the environment, identify hidden hazards, and provide instantaneous voice and visual warnings when obstacles enter a critical danger zone.

## ✨ Key Features

- 🗺️ **Live Object Navigation:** Real-time localization and 3D spatial mapping powered by **ORB-SLAM3**.
- 🚧 **Proactive Hazard Detection:** Identifies over 50+ classes of everyday tripping hazards (cords, shoes, furniture) using **YOLO-World**.
- 🎙️ **Voice Interaction:** Hands-free control with Speech-to-Text (STT) and native-accent Text-to-Speech (TTS) warnings.
- 📱 **Assistive UI Modes:** Features an Obstacle Image Gallery (auto-labels and saves hazards), a Remote Viewing mode for family members to check in, and an Idle Music player for privacy.

## 🎨 App UI Design

The user interface of SONA was custom-designed by our team from the ground up to ensure maximum accessibility and ease of use for the elderly and visually impaired. For a comprehensive look at our design process, mockups, and user flows, please refer to the [App UI Design document (PDF)](./SONA%20App_UI%20Design.pdf) located in the root directory.

## 🏗️ The Three-Tier Architecture

Running state-of-the-art SLAM and Object Detection simultaneously on a mobile device is computationally impossible. SONA solves this via a highly optimized, distributed three-tier architecture communicating over low-latency UDP/TCP streams:

1. **Unity Frontend (Mobile Phone):** Handles the UI, camera MJPEG streaming, voice recording, and renders the AR localization marker and bounding boxes.
2. **Edge SLAM Server (Raspberry Pi):** Runs a custom Python orchestrator that manages ORB-SLAM3 C++ binaries to process the camera stream and build the spatial map.
3. **Heavy AI Server (Remote PC):** Runs the YOLO-World model to execute high-speed, real-time object recognition on the incoming video stream.

## 📂 Project Structure & Key Files

### 🎮 Unity Frontend (`SONA/`)
The Unity application provides the user interface and sensor streaming. 
#### Important Scenes (`Assets/Scenes/`)
* **`OD_slam.unity`**: The core Live Object Navigation scene combining SLAM paths and YOLO bounding boxes.
* **`cali_and_map.unity`**: Scene for camera calibration and initial 3D environment mapping.
* **`language.unity`**: Select preferred language (English, Spanish, Chinese) for voice warnings.
* **`view_png.unity`**: Obstacle Image Gallery to review automatically saved hazard snapshots.
* **`idle.unity`** / **`remote.unity`**: Idle music player for privacy, and a remote viewing mode for family check-ins.

#### Important Scripts (`Assets/Scripts/`)
* **`MJPEGServer.cs`**: Captures and streams the mobile camera feed to the backend Python servers.
* **`SLAMReceiver.cs`**: Receives UDP pose/trajectory telemetry from the Python SLAM server.
* **`ObstacleReceiver.cs`**: Receives YOLO bounding box coordinates and object labels via UDP.
* **`Level1NavigationAdvisor.cs`**: The core navigational logic that evaluates spatial danger zones and triggers voice/visual alerts.
* **`SpeechClient.cs`**: Manages HTTP requests to the Flask backend for Speech-to-Text (STT) and Text-to-Speech (TTS).
* **`TrajectoryPanelRenderer.cs`**: Dynamically renders the SLAM path and navigation trajectory in the AR view.
* **`ObstacleKalmanFilter.cs`**: Applies Kalman filtering to stabilize detected hazard positions and prevent UI jitter on the client side.
* **`ImageGalleryLoader.cs`**: Handles the logic for automatically saving, labeling, and loading obstacle snapshots for the gallery.
* **`AppLanguageManager.cs`**: Manages dynamic localization, seamlessly switching voice warnings and UI text between languages (e.g., English, Spanish, Chinese).
* **`LoginAndStream.cs`**: Secures the application and handles the network connection initialization process.

### 🍓 Edge SLAM & Voice Services (`respberrryPi/`)
* **`unified_slam_server_optimized.py`**: A custom Python orchestrator that manages the SLAM C++ binaries, utilizing a fallback thread to prevent visual freezing during relocalization.
* **`speech_server.py`**: A Flask API offering STT (via Google) and native-accent TTS (via gTTS/pyttsx3).
* **`mono_buildmap.cc` & `mono_localization.cc`**: Custom C++ ORB-SLAM3 entry points. `mono_localization.cc` is specifically modified to integrate cleanly with the Python orchestrator.

### 💻 Heavy AI Server (`yolo_world/`)
* **`obstacle_server_yoloworld_optimized.py`**: Consumes the MJPEG stream, runs YOLO-World object detection, applies Exponential Moving Average (EMA) temporal smoothing to prevent UI jitter, and broadcasts bounding boxes back to Unity.

## 🚀 Getting Started

### 1. Backend Setup
1. Clone the repository. Deploy the `respberrryPi` folder to your Raspberry Pi, and the `yolo_world` folder to your remote PC.
2. Install Python dependencies: `pip install ultralytics opencv-python numpy flask pyttsx3 gTTS speechrecognition`
3. Compile the customized ORB-SLAM3 C++ binaries (`mono_buildmap.cc` & `mono_localization.cc`) on your Raspberry Pi. Ensure the paths are correctly set in `unified_slam_server_optimized.py`.

### 2. Running the Servers
Start the backend services on your respective networks:
```bash
# On Remote PC
python yolo_world/obstacle_server_yoloworld_optimized.py

# On Raspberry Pi
python respberrryPi/speech_server.py
python respberrryPi/unified_slam_server_optimized.py
```

### 3. Unity Client
*(Note: A pre-compiled APK is available in the **Releases** section.)*

1. Open **Unity Hub** and add the `SONA/` folder as an existing project.
2. Open the project. Configure the target IP addresses in the Unity Network Settings or relevant C# script variables to point to your Python servers (Raspberry Pi and Remote PC).
3. Build and deploy to your Android/iOS mobile device.

---

<div align="center">
  <p>Use SONA <img src="sona_logo.png" alt="SONA Logo" width="25" align="middle" /> to create a no-accident living environment.</p>
</div>
