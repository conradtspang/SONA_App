import socket
import subprocess
import threading
import time
import os
import signal
import math
import cv2
import numpy as np
from pathlib import Path

UNITY_PORT = int(os.getenv("UNITY_PORT", "6000"))
SLAM_CMD_PORT = int(os.getenv("SLAM_CMD_PORT", "5555"))
BUFFER_SIZE = 4096
DEFAULT_UNITY_STREAM_PORT = int(os.getenv("UNITY_STREAM_PORT", "8180"))
UNITY_STREAM_PATH = os.getenv("UNITY_STREAM_PATH", "")
GRACEFUL_EXIT_TIMEOUT_SEC = int(os.getenv("SLAM_GRACEFUL_EXIT_TIMEOUT_SEC", "120"))
SIGTERM_WAIT_SEC = int(os.getenv("SLAM_SIGTERM_WAIT_SEC", "10"))

ORB_ROOT = os.getenv("ORB_ROOT", "/home/benjamin/orb-slam3-root/ORB-SLAM3")
VOCAB_FILE = os.path.join(ORB_ROOT, "Vocabulary/ORBvoc.txt")
MAP_SETTINGS = os.path.join(ORB_ROOT, "Examples/Monocular/OppoReno13_save.yaml")
LOC_SETTINGS = os.path.join(ORB_ROOT, "Examples/Monocular/OppoReno13_load.yaml")

EXE_MAP = os.path.join(ORB_ROOT, "Examples/Monocular/mono_buildmap")
# Prefer _ver2 if it exists, as seen in some environments
EXE_LOC = os.path.join(ORB_ROOT, "Examples/Monocular/mono_localization_ver2")
if not os.path.exists(EXE_LOC):
    EXE_LOC = os.path.join(ORB_ROOT, "Examples/Monocular/mono_localization")

TRAJ_FILE = os.path.join(ORB_ROOT, "keyframe_trajectory_euroc.txt")
ATLAS_BASENAME = os.getenv("ATLAS_BASENAME", "atlas")
# ORB-SLAM3 expects System.*Atlas* values to be a basename, then it appends .osa.
# Because subprocess cwd=ORB_ROOT, basename "atlas" becomes ORB_ROOT/atlas.osa.
ATLAS_FILE = os.path.join(ORB_ROOT, ATLAS_BASENAME + ".osa")

DEFAULT_VIDEO = os.getenv(
    "SLAM_SOURCE_VIDEO",
    # "/mnt/c/Users/USER/Documents/Benjamin/Facebook_YoloSlam/MJPEG server/Assets/IMG_3242.mp4"
    "/mnt/c/Users/USER/Documents/Benjamin/Facebook_YoloSlam/MJPEG server/Assets/VID20260220153807.mp4"
)

SLAM_SOURCE_MAP = os.getenv("SLAM_SOURCE_MAP", DEFAULT_VIDEO)
SLAM_SOURCE_LOC = os.getenv("SLAM_SOURCE_LOC", "")  # empty = use Unity MJPEG stream for localization

def build_unity_stream_url(client_ip=None, stream_port=None):
    """Return the Unity MJPEG URL that SLAM should use for localization.

    Unity runs on the Windows host; in your WSL setup that is usually 172.17.144.1.
    The Localize button may pass the current Unity streamPort as MODE LOC <port>.
    """
    if SLAM_SOURCE_LOC.strip():
        return SLAM_SOURCE_LOC.strip()

    host = os.getenv("UNITY_STREAM_HOST", "").strip()
    if not host:
        host = client_ip or os.getenv("UNITY_UDP_HOST", "172.17.144.1")

    port = int(stream_port or DEFAULT_UNITY_STREAM_PORT)
    path = UNITY_STREAM_PATH.strip()
    if path and not path.startswith("/"):
        path = "/" + path
    return f"http://{host}:{port}{path}"


CHESSBOARD_SIZE = (8, 5)
SQUARE_SIZE_METERS = 0.025
MIN_CAPTURES = 30
MIN_CAPTURE_INTERVAL_SEC = 0.35
MIN_BOARD_AREA_RATIO = 0.06
MIN_SHARPNESS_LAPLACIAN = 90.0
MAX_REPROJ_ERROR = 1.2

SAVE_PATH_BASE = os.path.join(ORB_ROOT, "Examples/Monocular/")
SAVE_FILENAME_LOAD = "OppoReno13_load.yaml"
SAVE_FILENAME_SAVE = "OppoReno13_save.yaml"

def send_unity_udp(msg):
    """Send a status message to Unity via UDP."""
    try:
        with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as sock:
            sock.sendto(msg.encode('utf-8'), (UNITY_UDP_HOST, UNITY_UDP_PORT))
    except Exception as e:
        print(f"[ORCH][UDP_ERR] {e}")

YAML_TEMPLATE = """%YAML:1.0
#--------------------------------------------------------------------------------------------
# Camera Parameters. Calibrated on {calibration_time}
#--------------------------------------------------------------------------------------------
File.version: "1.0"
Camera.type: "PinHole"
System.LoadAtlasFromFile: "{load_atlas}"
System.SaveAtlasToFile: "{save_atlas}"
Camera1.fx: {fx}
Camera1.fy: {fy}
Camera1.cx: {cx}
Camera1.cy: {cy}
Camera1.k1: {k1}
Camera1.k2: {k2}
Camera1.p1: {p1}
Camera1.p2: {p2}
Camera1.k3: {k3}
Camera.width: {width}
Camera.height: {height}
Camera.fps: {fps}
Camera.RGB: 1
#--------------------------------------------------------------------------------------------
# ORB Parameters
#--------------------------------------------------------------------------------------------
ORBextractor.nFeatures: {nFeatures}
ORBextractor.scaleFactor: 1.2
ORBextractor.nLevels: 8
ORBextractor.iniThFAST: {iniThFAST}
ORBextractor.minThFAST: {minThFAST}
#--------------------------------------------------------------------------------------------
# Viewer Parameters
#--------------------------------------------------------------------------------------------
Viewer.KeyFrameSize: 0.05
Viewer.KeyFrameLineWidth: 1.0
Viewer.GraphLineWidth: 0.9
Viewer.PointSize: 2.0
Viewer.CameraSize: 0.08
Viewer.CameraLineWidth: 3.0
Viewer.ViewpointX: 0.0
Viewer.ViewpointY: -0.7
Viewer.ViewpointZ: -1.8
Viewer.ViewpointF: 500.0
"""

def generate_yaml_content(params, is_save_mode):
    n_features = 5000
    ini_fast = 10
    min_fast = 3
    atlas_name = ATLAS_BASENAME

    return YAML_TEMPLATE.format(
        calibration_time=time.strftime("%Y-%m-%d %H:%M:%S"),
        load_atlas=atlas_name if not is_save_mode else "",
        save_atlas=atlas_name if is_save_mode else "",
        fx=params['fx'],
        fy=params['fy'],
        cx=params['cx'],
        cy=params['cy'],
        k1=params['k1'],
        k2=params['k2'],
        p1=params['p1'],
        p2=params['p2'],
        k3=params['k3'],
        width=params['width'],
        height=params['height'],
        fps=params.get('fps', 30),
        nFeatures=n_features,
        iniThFAST=ini_fast,
        minThFAST=min_fast,
    )

def patch_existing_yaml_atlas_paths():
    """Patch already-written YAML files. ORB-SLAM3 atlas YAML values must be basenames.

    Do NOT write absolute paths here and do NOT include .osa.
    ORB-SLAM3 internally writes/reads ./<basename>.osa, and the process cwd is ORB_ROOT.
    """
    atlas_name = ATLAS_BASENAME

    def patch_file(path, load_value=None, save_value=None):
        if not os.path.exists(path):
            return
        with open(path, 'r') as f:
            text = f.read()
        old_text = text
        import re
        if load_value is not None:
            text = re.sub(r'System\.LoadAtlasFromFile:\s*"[^"\n]*"',
                          f'System.LoadAtlasFromFile: "{load_value}"', text)
        if save_value is not None:
            text = re.sub(r'System\.SaveAtlasToFile:\s*"[^"\n]*"',
                          f'System.SaveAtlasToFile: "{save_value}"', text)
        if text != old_text:
            with open(path, 'w') as f:
                f.write(text)
            print(f"[ORCH] Patched YAML atlas basename: {path}")

    patch_file(MAP_SETTINGS, load_value="", save_value=atlas_name)
    patch_file(LOC_SETTINGS, load_value=atlas_name, save_value="")

def print_yaml_atlas_lines():
    import re
    for path in [MAP_SETTINGS, LOC_SETTINGS]:
        if not os.path.exists(path):
            continue
        with open(path, 'r') as f:
            for line in f:
                if 'System.LoadAtlasFromFile' in line or 'System.SaveAtlasToFile' in line:
                    print(f"[ORCH][YAML] {os.path.basename(path)}: {line.strip()}")

def point_line_distance(point, start, end):
    if start == end:
        return math.hypot(point[0] - start[0], point[1] - start[1])
    n = abs((end[0] - start[0]) * (start[1] - point[1]) - (start[0] - point[0]) * (end[1] - start[1]))
    d = math.hypot(end[0] - start[0], end[1] - start[1])
    return n / d

def ramer_douglas_peucker(points, epsilon):
    if len(points) <= 2:
        return points
    dmax = 0.0
    index = 0
    end = len(points) - 1
    for i in range(1, end):
        d = point_line_distance(points[i], points[0], points[end])
        if d > dmax:
            index = i
            dmax = d
    if dmax > epsilon:
        rec_results1 = ramer_douglas_peucker(points[:index + 1], epsilon)
        rec_results2 = ramer_douglas_peucker(points[index:], epsilon)
        return rec_results1[:-1] + rec_results2
    return [points[0], points[end]]

def simplify_trajectory_robust(points, epsilon):
    if len(points) < 3:
        return points
    start_node = points[0]
    max_dist_sq = 0
    farthest_index = 0
    for i in range(1, len(points)):
        d_sq = (points[i][0] - start_node[0]) ** 2 + (points[i][1] - start_node[1]) ** 2
        if d_sq > max_dist_sq:
            max_dist_sq = d_sq
            farthest_index = i
    seg1 = points[:farthest_index + 1]
    seg2 = points[farthest_index:]
    res1 = ramer_douglas_peucker(seg1, epsilon)
    res2 = ramer_douglas_peucker(seg2, epsilon)
    return res1[:-1] + res2

def dedupe_nearby_points(points, min_dist=0.01):
    if not points:
        return points
    out = [points[0]]
    for p in points[1:]:
        if math.hypot(p[0] - out[-1][0], p[1] - out[-1][1]) >= min_dist:
            out.append(p)
    return out

def maybe_close_loop(points, closure_dist=0.18):
    if len(points) < 3:
        return points
    if math.hypot(points[0][0] - points[-1][0], points[0][1] - points[-1][1]) <= closure_dist:
        if points[-1] != points[0]:
            return points + [points[0]]
    return points

def estimate_fps(frame_times):
    if len(frame_times) < 8:
        return 30
    diffs = np.diff(np.array(frame_times[-30:], dtype=np.float64))
    diffs = diffs[(diffs > 0.005) & (diffs < 0.2)]
    if len(diffs) == 0:
        return 30
    fps = int(round(1.0 / float(np.median(diffs))))
    return max(15, min(60, fps))


# ---------------- Python-side localization visual playback ----------------
# This is intentionally outside mono_localization.cc. In this custom ORB-SLAM3
# setup TrackMonocular() may block during relocalization, so a C++ fallback
# thread may stop after one packet. The Python orchestrator stays responsive and
# can keep Unity's 2D localization marker moving independently while ORB-SLAM3
# performs real relocalization in the subprocess.
UNITY_UDP_HOST = os.getenv("UNITY_UDP_HOST", "172.17.144.1")
UNITY_UDP_PORT = int(os.getenv("UNITY_UDP_PORT", "5006"))
LOC_REF_FILE = os.path.join(ORB_ROOT, "localization_reference_trajectory.txt")
LOC_KEYFRAME_FILE = os.path.join(ORB_ROOT, "keyframe_trajectory_euroc.txt")

class ReferencePoseStreamer:
    def __init__(self):
        self._thread = None
        self._stop_event = threading.Event()
        self._lock = threading.Lock()

    def stop(self):
        with self._lock:
            self._stop_event.set()
            t = self._thread
            self._thread = None
        if t and t.is_alive():
            t.join(timeout=2.0)

    def start(self):
        self.stop()
        poses = self._load_visible_poses()
        if not poses:
            print("[PY_LOC_REF][WARN] No reference/keyframe trajectory available; Unity visual playback disabled.")
            return False
        self._stop_event = threading.Event()
        fps = max(1, min(60, int(os.getenv("PY_LOC_REF_FPS", os.getenv("LOC_REF_VISUAL_FPS", "12")))))
        stride = max(1, min(50, int(os.getenv("PY_LOC_REF_STRIDE", os.getenv("LOC_REF_STRIDE", "1")))))
        loop = os.getenv("PY_LOC_REF_LOOP", os.getenv("LOC_REF_LOOP", "1")) != "0"
        self._thread = threading.Thread(target=self._run, args=(poses, fps, stride, loop), daemon=True)
        self._thread.start()
        print(f"[PY_LOC_REF] Started Unity visual playback: poses={len(poses)}, fps={fps}, stride={stride}, loop={loop}, target={UNITY_UDP_HOST}:{UNITY_UDP_PORT}")
        return True

    def _send_udp(self, text, sock):
        try:
            sock.sendto(text.encode('utf-8'), (UNITY_UDP_HOST, UNITY_UDP_PORT))
        except Exception as e:
            print(f"[PY_LOC_REF][UDP_ERROR] {e}")

    def _run(self, poses, fps, stride, loop):
        sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        sleep_s = 1.0 / fps
        idx = 0
        send_count = 0
        last_path = None
        self._send_udp("STATUS|Python reference localization playback running", sock)
        while not self._stop_event.is_set():
            x, z, yaw = poses[idx]
            half = yaw * 0.5
            qz = math.sin(half)
            qw = math.cos(half)
            self._send_udp(f"LOCPOSE|{x:.6f},0.000000,{z:.6f},0.000000,0.000000,{qz:.6f},{qw:.6f}", sock)
            if last_path is None or math.hypot(x-last_path[0], z-last_path[1]) >= 0.006:
                self._send_udp(f"LOCPATH|{x:.6f},{z:.6f}", sock)
                last_path = (x, z)
            if send_count % 10 == 0:
                print(f"[PY_LOC_REF] sent LOCPOSE/LOCPATH send={send_count} idx={idx} x={x:.4f} z={z:.4f}")
            send_count += 1
            idx += stride
            if idx >= len(poses):
                if loop:
                    idx = 0
                    last_path = None
                    print("[PY_LOC_REF] loop back to idx=0")
                else:
                    idx = len(poses) - 1
            self._stop_event.wait(sleep_s)
        sock.close()
        print("[PY_LOC_REF] stopped")

    def _load_visible_poses(self):
        # Visual fallback should follow the full map path. keyframe_trajectory_euroc.txt
        # is usually much longer than localization_reference_trajectory.txt in this project.
        raw = self._load_keyframe_file(LOC_KEYFRAME_FILE)
        source = LOC_KEYFRAME_FILE
        if not raw:
            raw = self._load_ref_file(LOC_REF_FILE)
            source = LOC_REF_FILE
        if not raw:
            return []
        min_step = float(os.getenv("PY_LOC_REF_MIN_STEP", os.getenv("LOC_REF_MIN_STEP", "0.018")))
        visible = []
        last = None
        for x, z, yaw in raw:
            if last is None or math.hypot(x-last[0], z-last[1]) >= min_step:
                if last is not None:
                    yaw = math.atan2(x-last[0], z-last[1])
                visible.append((x, z, yaw))
                last = (x, z)
        if len(visible) < 2:
            visible = raw
        print(f"[PY_LOC_REF] Loaded {len(raw)} raw poses from {source}; visible playback poses={len(visible)} min_step={min_step}")
        return visible

    def _load_ref_file(self, path):
        poses = []
        p = Path(path)
        if not p.exists():
            return poses
        for line in p.read_text(errors='ignore').splitlines():
            if not line.strip() or line.lstrip().startswith('#'):
                continue
            parts = line.split()
            if len(parts) >= 5:
                try:
                    # frame timestamp x z yaw
                    poses.append((float(parts[2]), float(parts[3]), float(parts[4])))
                except ValueError:
                    pass
        return poses

    def _load_keyframe_file(self, path):
        poses = []
        p = Path(path)
        if not p.exists():
            return poses
        prev = None
        for line in p.read_text(errors='ignore').splitlines():
            if not line.strip() or line.lstrip().startswith('#'):
                continue
            parts = line.split()
            if len(parts) >= 4:
                try:
                    # timestamp tx ty tz qx qy qz qw
                    x, z = float(parts[1]), float(parts[3])
                    yaw = 0.0 if prev is None else math.atan2(x-prev[0], z-prev[1])
                    poses.append((x, z, yaw))
                    prev = (x, z)
                except ValueError:
                    pass
        return poses

class SLAMManager:
    def __init__(self):
        self.process = None
        self.mode = "IDLE"
        self.lock = threading.Lock()
        self.calibration_thread = None
        self.calibration_state = {
            "running": False,
            "should_save": False,
            "should_quit": False,
            "should_capture": False,
            "captures_needed": MIN_CAPTURES,
            "current_captures": 0,
            "last_capture_time": 0.0,
        }
        self.calibration_event = threading.Event()
        self.ref_streamer = ReferencePoseStreamer()

    def _validate_paths(self):
        missing = []
        for path in [VOCAB_FILE, MAP_SETTINGS, LOC_SETTINGS, EXE_MAP, EXE_LOC]:
            if not Path(path).exists():
                missing.append(path)
        if missing:
            raise FileNotFoundError("Missing required path(s):\n" + "\n".join(missing))

    def _clean_gui_env(self):
        env = os.environ.copy()
        env.pop("QT_QPA_PLATFORM_PLUGIN_PATH", None)
        env.pop("QT_QPA_FONTDIR", None)
        env.pop("QT_PLUGIN_PATH", None)
        env.pop("PYQT_PLUGIN_PATH", None)
        return env

    def kill_process(self):
        self.ref_streamer.stop()
        with self.lock:
            if self.process:
                print(f"[ORCH] Killing process {self.process.pid}...")
                self._send_tcp_command("EXIT")
                for _ in range(20):
                    if self.process.poll() is not None:
                        break
                    time.sleep(0.3)
                if self.process.poll() is None:
                    try:
                        os.kill(self.process.pid, signal.SIGTERM)
                        self.process.wait(timeout=2)
                    except Exception as e:
                        print(f"[ORCH] Error sending SIGTERM: {e}")
                if self.process.poll() is None:
                    try:
                        self.process.kill()
                    except Exception:
                        pass
                self.process = None
                self.mode = "IDLE"
                print("[ORCH] Process terminated.")

    def _send_tcp_command(self, cmd):
        try:
            with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as s:
                s.settimeout(1.0)
                s.connect(('127.0.0.1', SLAM_CMD_PORT))
                s.sendall(cmd.encode('utf-8'))
                print(f"[ORCH] Sent to SLAM: {cmd}")
                return True
        except ConnectionRefusedError:
            print(f"[ORCH] SLAM process not listening on {SLAM_CMD_PORT}.")
            return False
        except Exception as e:
            print(f"[ORCH] TCP Error: {e}")
            return False

    def start_mapping(self, source_path=SLAM_SOURCE_MAP):
        self.kill_process()
        try:
            patch_existing_yaml_atlas_paths()
            print_yaml_atlas_lines()
            print(f"[ORCH] Atlas basename: {ATLAS_BASENAME}; expected output file: {ATLAS_FILE}")
            self._validate_paths()
            cmd = [EXE_MAP, VOCAB_FILE, MAP_SETTINGS, source_path]
            print(f"[ORCH] Starting MAPPING: {' '.join(cmd)}")
            env = self._clean_gui_env()
            # Python orchestrator owns visual fallback playback; disable C++ fallback thread to avoid duplicate/stalled packets.
            env["LOC_REF_THREAD"] = "0"
            with self.lock:
                self.process = subprocess.Popen(cmd, cwd=ORB_ROOT, env=env)
                self.mode = "MAPPING"
            return f"OK: MAPPING_STARTED {source_path}"
        except Exception as e:
            print(f"[ORCH] Failed to start mapping: {e}")
            return f"ERROR: {str(e)}"

    def start_localization(self, source_path=None, client_ip=None, stream_port=None):
        self.kill_process()
        try:
            source_path = source_path or build_unity_stream_url(client_ip=client_ip, stream_port=stream_port)
            patch_existing_yaml_atlas_paths()
            print_yaml_atlas_lines()
            print(f"[ORCH] Atlas basename: {ATLAS_BASENAME}; expected load file: {ATLAS_FILE}")
            self._validate_paths()
            if not Path(ATLAS_FILE).exists():
                return f"ERROR: ATLAS_NOT_FOUND {ATLAS_FILE}"
            cmd = [EXE_LOC, VOCAB_FILE, LOC_SETTINGS, source_path]
            print(f"[ORCH] Starting LOCALIZATION: {' '.join(cmd)}")
            env = self._clean_gui_env()
            # True stream-sync localization: keep all visual playback fallbacks OFF by default.
            # The pose should come from mono_localization reading Unity's MJPEG stream.
            env["LOC_REF_THREAD"] = "0"
            env["LOC_REF_ENABLE"] = "0"
            # Low-latency OpenCV/FFmpeg capture hints for MJPEG/HTTP streams. Some backends ignore them,
            # but when honored they reduce stale-buffer relocalization failures.
            env.setdefault("OPENCV_FFMPEG_CAPTURE_OPTIONS", "fflags;nobuffer|flags;low_delay|max_delay;500000")
            env.setdefault("LOC_STREAM_WARMUP_FRAMES", "5")
            print("[ORCH] Stream-sync localization: C++ fallback OFF, Python fallback OFF, low-latency capture hints enabled")
            with self.lock:
                self.process = subprocess.Popen(cmd, cwd=ORB_ROOT, env=env)
                self.mode = "LOCALIZATION"
            return f"OK: LOCALIZATION_STARTED {source_path}"
        except Exception as e:
            print(f"[ORCH] Failed to start localization: {e}")
            return f"ERROR: {str(e)}"

    def _detect_corners(self, gray):
        flags = cv2.CALIB_CB_ADAPTIVE_THRESH | cv2.CALIB_CB_NORMALIZE_IMAGE | cv2.CALIB_CB_FAST_CHECK
        found, corners = cv2.findChessboardCorners(gray, CHESSBOARD_SIZE, flags)
        if not found and hasattr(cv2, 'findChessboardCornersSB'):
            found, corners = cv2.findChessboardCornersSB(gray, CHESSBOARD_SIZE)
        return found, corners

    def _capture_quality_ok(self, frame, corners):
        h, w = frame.shape[:2]
        sharpness = cv2.Laplacian(cv2.cvtColor(frame, cv2.COLOR_BGR2GRAY), cv2.CV_64F).var()
        if sharpness < MIN_SHARPNESS_LAPLACIAN:
            return False, f"BLURRY ({sharpness:.1f})"

        xs = corners[:, 0, 0]
        ys = corners[:, 0, 1]
        area_ratio = ((xs.max() - xs.min()) * (ys.max() - ys.min())) / float(w * h)
        if area_ratio < MIN_BOARD_AREA_RATIO:
            return False, f"BOARD_TOO_SMALL ({area_ratio:.3f})"
        return True, f"OK blur={sharpness:.1f} area={area_ratio:.3f}"

    def _open_source(self, source_path):
        if source_path.isdigit():
            return cv2.VideoCapture(int(source_path))
        return cv2.VideoCapture(source_path)

    def _run_calibration_process(self, source_path):
        print("--- Camera Calibration (Direct Source) ---")
        print(f"Connecting to source: {source_path}")
        print(f"Chessboard size (inner corners): {CHESSBOARD_SIZE}")

        objp = np.zeros((CHESSBOARD_SIZE[0] * CHESSBOARD_SIZE[1], 3), np.float32)
        objp[:, :2] = np.mgrid[0:CHESSBOARD_SIZE[0], 0:CHESSBOARD_SIZE[1]].T.reshape(-1, 2)
        objp = objp * SQUARE_SIZE_METERS

        objpoints, imgpoints = [], []
        frame_times = []
        cap = self._open_source(source_path)

        if not cap.isOpened():
            print(f"Error: Could not open source at {source_path}")
            send_unity_udp("STATUS|Calibration Error: Source not open")
            self.calibration_state["running"] = False
            self.calibration_event.set()
            return "ERROR: SOURCE_NOT_OPEN"

        frame_width, frame_height = 0, 0
        gray = None
        quality_msg = ""
        
        send_unity_udp(f"STATUS|Calibration started. Need {self.calibration_state['captures_needed']} captures.")

        while self.calibration_state["running"]:
            ret, frame = cap.read()
            frame_times.append(time.time())

            if not ret:
                if not source_path.isdigit():
                    cap.set(cv2.CAP_PROP_POS_FRAMES, 0)
                time.sleep(0.1)
                continue

            if frame_width == 0:
                frame_height, frame_width = frame.shape[:2]
                print(f"[CALIB] Resolution: {frame_width}x{frame_height}")

            gray = cv2.cvtColor(frame, cv2.COLOR_BGR2GRAY)
            found_corners, corners = self._detect_corners(gray)
            corners2 = None
            if found_corners:
                criteria = (cv2.TERM_CRITERIA_EPS + cv2.TERM_CRITERIA_MAX_ITER, 40, 0.0005)
                corners2 = cv2.cornerSubPix(gray, corners, (11, 11), (-1, -1), criteria)
                cv2.drawChessboardCorners(frame, CHESSBOARD_SIZE, corners2, found_corners)
                ok, quality_msg = self._capture_quality_ok(frame, corners2)
                color = (0, 255, 0) if ok else (0, 165, 255)
                cv2.putText(frame, quality_msg, (10, 150), cv2.FONT_HERSHEY_SIMPLEX, 0.7, color, 2)
            else:
                quality_msg = "NO_CORNERS"

            cv2.putText(frame, f"Captures: {len(objpoints)}/{self.calibration_state['captures_needed']}", (10, 30), cv2.FONT_HERSHEY_SIMPLEX, 0.8, (0, 255, 0), 2)
            cv2.putText(frame, "Capture: 'c' or CMD", (10, 60), cv2.FONT_HERSHEY_SIMPLEX, 0.8, (0, 255, 255), 2)
            cv2.putText(frame, "Save & Exit: 's' or CMD", (10, 90), cv2.FONT_HERSHEY_SIMPLEX, 0.8, (0, 255, 255), 2)
            cv2.putText(frame, "Quit: 'q' or CMD", (10, 120), cv2.FONT_HERSHEY_SIMPLEX, 0.8, (0, 0, 255), 2)
            cv2.imshow('Calibration', frame)

            key = -1
            if self.calibration_state["should_quit"]:
                key = ord('q')
                self.calibration_state["should_quit"] = False
            elif self.calibration_state["should_save"]:
                key = ord('s')
                self.calibration_state["should_save"] = False
            elif self.calibration_state["should_capture"]:
                key = ord('c')
                self.calibration_state["should_capture"] = False

            if key == -1:
                key = cv2.waitKey(1) & 0xFF

            if key == ord('q'):
                break
            elif key == ord('c'):
                if not found_corners or corners2 is None:
                    print("[CALIB] Capture ignored: no corners found.")
                    continue
                ok, quality_msg = self._capture_quality_ok(frame, corners2)
                if not ok:
                    print(f"[CALIB] Capture ignored: {quality_msg}")
                    continue
                now = time.time()
                if now - self.calibration_state["last_capture_time"] < MIN_CAPTURE_INTERVAL_SEC:
                    print("[CALIB] Capture ignored: too close to previous frame.")
                    continue
                objpoints.append(objp.copy())
                imgpoints.append(corners2)
                self.calibration_state["current_captures"] = len(objpoints)
                self.calibration_state["last_capture_time"] = now
                print(f"[CALIB] Capture {len(objpoints)} accepted: {quality_msg}")
                send_unity_udp(f"STATUS|Calibration: {len(objpoints)}/{self.calibration_state['captures_needed']} captured")
                
                # Auto-trigger save if requirement met
                if len(objpoints) >= self.calibration_state['captures_needed']:
                    print(f"[CALIB] Target reached ({len(objpoints)}), auto-saving...")
                    self.calibration_state["should_save"] = True
            elif key == ord('s'):
                if len(objpoints) < self.calibration_state['captures_needed']:
                    print(f"[CALIB] Not enough captures ({len(objpoints)}/{self.calibration_state['captures_needed']}).")
                    continue
                
                send_unity_udp("STATUS|Calibration: Processing & Saving...")
                try:
                    rms, mtx, dist, rvecs, tvecs = cv2.calibrateCamera(objpoints, imgpoints, gray.shape[::-1], None, None)
                    if not np.isfinite(rms) or rms > MAX_REPROJ_ERROR:
                        msg = f"REJECTED: RMS={rms:.4f} > {MAX_REPROJ_ERROR}"
                        print(f"[CALIB] {msg}")
                        send_unity_udp(f"STATUS|Calibration Error: {msg}")
                        continue

                    params = {
                        'fx': mtx[0, 0], 'fy': mtx[1, 1],
                        'cx': mtx[0, 2], 'cy': mtx[1, 2],
                        'k1': float(dist.ravel()[0]) if dist.size > 0 else 0.0,
                        'k2': float(dist.ravel()[1]) if dist.size > 1 else 0.0,
                        'p1': float(dist.ravel()[2]) if dist.size > 2 else 0.0,
                        'p2': float(dist.ravel()[3]) if dist.size > 3 else 0.0,
                        'k3': float(dist.ravel()[4]) if dist.size > 4 else 0.0,
                        'width': frame_width,
                        'height': frame_height,
                        'fps': estimate_fps(frame_times),
                    }

                    save_path_s = os.path.join(SAVE_PATH_BASE, SAVE_FILENAME_SAVE)
                    with open(save_path_s, 'w') as f:
                        f.write(generate_yaml_content(params, is_save_mode=True))
                    print(f"[CALIB] Saved mapping settings to: {save_path_s}")

                    save_path_l = os.path.join(SAVE_PATH_BASE, SAVE_FILENAME_LOAD)
                    with open(save_path_l, 'w') as f:
                        f.write(generate_yaml_content(params, is_save_mode=False))
                    print(f"[CALIB] Saved localization settings to: {save_path_l}")
                    print(f"[CALIB] RMS reprojection error: {rms:.4f}")
                    send_unity_udp(f"STATUS|Calibration Finished! RMS={rms:.4f}")
                    send_unity_udp("CALIB_FINISHED")
                    break
                except Exception as e:
                    print(f"[CALIB] Error during calibration or saving: {e}")
                    send_unity_udp(f"STATUS|Calibration Error: {str(e)}")
                    break

        cap.release()
        cv2.destroyAllWindows()
        self.calibration_state["running"] = False
        self.calibration_event.set()
        print("[CALIB] Calibration process finished.")
        return "OK: CALIBRATION_COMPLETE"

    def start_calibration(self, source_path=SLAM_SOURCE_MAP):
        self.kill_process()
        if self.calibration_state["running"]:
            return "ERROR: CALIBRATION_ALREADY_RUNNING"
        self.mode = "CALIBRATING"
        self.calibration_state.update({
            "running": True,
            "should_save": False,
            "should_quit": False,
            "should_capture": False,
            "current_captures": 0,
            "last_capture_time": 0.0,
        })
        self.calibration_event.clear()
        self.calibration_thread = threading.Thread(target=self._run_calibration_process, args=(source_path,), daemon=True)
        self.calibration_thread.start()
        return f"OK: CALIBRATION_STARTED {source_path}"

    def send_calibration_command(self, command):
        if not self.calibration_state["running"]:
            return "ERROR: CALIBRATION_NOT_RUNNING"
        if command == "SAVE":
            if self.calibration_state["current_captures"] < self.calibration_state["captures_needed"]:
                return f"ERROR: NOT_ENOUGH_CAPTURES ({self.calibration_state['current_captures']}/{self.calibration_state['captures_needed']})"
            self.calibration_state["should_save"] = True
            return "OK: CALIBRATION_SAVE_TRIGGERED"
        if command == "QUIT":
            self.calibration_state["should_quit"] = True
            return "OK: CALIBRATION_QUIT_TRIGGERED"
        if command == "CAPTURE":
            self.calibration_state["should_capture"] = True
            return "OK: CALIBRATION_CAPTURE_TRIGGERED"
        return "ERROR: UNKNOWN_CALIBRATION_COMMAND"

    def process_trajectory(self):
        if not os.path.exists(TRAJ_FILE):
            return "ERROR: TRAJ_FILE_NOT_FOUND"
        try:
            points_2d = []
            with open(TRAJ_FILE, 'r') as f:
                for line in f:
                    parts = line.strip().split()
                    if len(parts) < 4:
                        continue
                    try:
                        x = float(parts[1])
                        z = float(parts[3])
                        points_2d.append((x, z))
                    except ValueError:
                        continue
            if not points_2d:
                return "ERROR: EMPTY_TRAJECTORY"
            points_2d = dedupe_nearby_points(points_2d, min_dist=0.015)
            simplified_points = simplify_trajectory_robust(points_2d, epsilon=0.02)
            simplified_points = maybe_close_loop(simplified_points, closure_dist=0.20)
            path_str = "|".join([f"{p[0]:.4f},0,{p[1]:.4f}" for p in simplified_points])
            print(f"[ORCH] Path processed: {len(points_2d)} -> {len(simplified_points)} points")
            return f"OK: PATH {path_str}"
        except Exception as e:
            print(f"[ORCH] Error processing trajectory: {e}")
            return f"ERROR: PROCESS_FAILED {str(e)}"

    def _report_atlas_after_process_exits(self, pid):
        deadline = time.time() + GRACEFUL_EXIT_TIMEOUT_SEC
        while time.time() < deadline:
            proc = self.process
            if proc is None or proc.pid != pid or proc.poll() is not None:
                break
            time.sleep(1.0)

        proc = self.process
        if proc is not None and proc.pid == pid and proc.poll() is None:
            print(f"[ORCH][WARN] SLAM process {pid} is still running after {GRACEFUL_EXIT_TIMEOUT_SEC}s while saving atlas. Not killing automatically.")
            return

        if Path(ATLAS_FILE).exists():
            size_mb = Path(ATLAS_FILE).stat().st_size / 1024 / 1024
            print(f"[ORCH] Atlas save check: {ATLAS_FILE} exists, size={size_mb:.1f} MB")
            if size_mb < 1.0:
                print("[ORCH][WARN] Atlas file is very small; it may be invalid.")
        else:
            print(f"[ORCH][ERROR] Atlas save check: {ATLAS_FILE} was not created.")

        with self.lock:
            if self.process is not None and self.process.pid == pid and self.process.poll() is not None:
                self.process = None
                self.mode = "IDLE"


    def handle_command(self, cmd_str, client_ip=None):
        parts = cmd_str.strip().split()
        if not parts:
            return "ERROR: EMPTY_COMMAND"

        op = parts[0].upper()

        if op == "CHECK_READY":
            if self.process is None:
                return "ERROR: NO_PROCESS"
            try:
                # Check if the SLAM C++ process is listening on the local control port
                with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as s:
                    s.settimeout(0.5)
                    s.connect(('127.0.0.1', SLAM_CMD_PORT))
                    return "OK: READY"
            except Exception:
                return "ERROR: NOT_READY"

        if op == "MODE":
            if len(parts) < 2:
                return "ERROR: MISSING_MODE_ARG"
            mode_arg = parts[1].upper()
            if mode_arg == "MAP":
                return self.start_mapping(SLAM_SOURCE_MAP)
            if mode_arg == "LOC":
                stream_port = None
                # Accepted commands:
                #   MODE LOC              -> http://<client_ip>:UNITY_STREAM_PORT
                #   MODE LOC 8080         -> http://<client_ip>:8080
                #   MODE LOC http://...   -> explicit localization source
                explicit_source = None
                if len(parts) >= 3:
                    if parts[2].lower().startswith(("http://", "https://", "rtsp://")):
                        explicit_source = parts[2]
                    else:
                        try:
                            stream_port = int(parts[2])
                        except ValueError:
                            return f"ERROR: BAD_LOC_PORT_{parts[2]}"
                return self.start_localization(explicit_source, client_ip=client_ip, stream_port=stream_port)
            return f"ERROR: UNKNOWN_MODE_{mode_arg}"

        if op == "CALIBRATE_START":
            return self.start_calibration(SLAM_SOURCE_MAP)
        if op == "CALIBRATE_SAVE":
            return self.send_calibration_command("SAVE")
        if op == "CALIBRATE_QUIT":
            return self.send_calibration_command("QUIT")
        if op == "CALIBRATE_CAPTURE":
            return self.send_calibration_command("CAPTURE")
        if op == "EXIT":
            self.kill_process()
            return "OK: EXIT_SLAM"
        if op == "GENERATE_PATH":
            return self.process_trajectory()

        if op == "START":
            ok = self._send_tcp_command("START")
            if ok and self.mode == "LOCALIZATION":
                # Default OFF: mono_localization now sends real LOCPOSE/LOCPATH.
                # Turn this on only when you want fake/reference visual playback:
                #   export PY_LOC_REF_ENABLE=1
                if os.getenv("PY_LOC_REF_ENABLE", "0") == "1":
                    self.ref_streamer.start()
                else:
                    print("[PY_LOC_REF] disabled (default). Set PY_LOC_REF_ENABLE=1 only for visual fallback testing.")
            return "OK: SENT_START" if ok else "ERROR: SLAM_NOT_CONNECTED"

        if op == "STOP":
            self.ref_streamer.stop()
            return "OK: SENT_STOP" if self._send_tcp_command("STOP") else "ERROR: SLAM_NOT_CONNECTED"

        if op == "SAVE_ATLAS":
            self.ref_streamer.stop()
            ok = self._send_tcp_command("SAVE_ATLAS")
            if ok:
                self.mode = "SAVING_ATLAS"
                return "OK: SAVE_ATLAS_STARTED_WAIT_FOR_SHUTDOWN"
            return "ERROR: SLAM_NOT_CONNECTED"

        return f"ERROR: UNKNOWN_COMMAND_{op}"

def unity_server_thread(manager):
    server = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    server.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    try:
        server.bind(('0.0.0.0', UNITY_PORT))
        server.listen(5)
        print(f"[ORCH] Listening for Unity on port {UNITY_PORT}")
        print(f"[ORCH] Direct SLAM source MAP: {SLAM_SOURCE_MAP}")
        print(f"[ORCH] Direct SLAM source LOC override: {SLAM_SOURCE_LOC or '<Unity MJPEG stream>'}")

        while True:
            client, addr = server.accept()
            client_ip = addr[0]
            try:
                data = client.recv(BUFFER_SIZE)
                if data:
                    cmd_str = data.decode('utf-8').strip()
                    print(f"[ORCH] Received from {client_ip}: {cmd_str}")
                    response = manager.handle_command(cmd_str, client_ip)
                    client.sendall(response.encode('utf-8'))
                    print(f"[ORCH] Sent response to {client_ip}: {response}")
            except Exception as e:
                print(f"[ORCH] Error handling client: {e}")
            finally:
                client.close()
    except Exception as e:
        print(f"[ORCH] Server crashed: {e}")
    finally:
        server.close()

if __name__ == "__main__":
    slam_manager = SLAMManager()
    unity_server_thread(slam_manager)