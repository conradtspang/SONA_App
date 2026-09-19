import socket
import threading
import time
import json
import cv2
import numpy as np
import os
from collections import defaultdict, deque, Counter

# --- NETWORK CONFIGURATION ---
CONTROL_PORT = 6001          # TCP port to receive commands from Unity
DATA_PORT = 6002             # UDP port to send detection results back to Unity
DEFAULT_STREAM_PORT = 8080   # Port where Unity is streaming MJPEG

def get_windows_host_ip():
    """
    Attempt to find the Windows host IP if running in WSL.
    Defaults to 127.0.0.1 if not in WSL or if resolution fails.
    """
    if os.path.exists("/proc/sys/fs/binfmt_misc/WSLInterop"):
        try:
            # Common way to get the host IP from WSL2
            import subprocess
            cmd = "ip route | grep default | awk '{print $3}'"
            host_ip = subprocess.check_output(cmd, shell=True).decode().strip()
            if host_ip:
                print(f"WSL detected. Resolved Windows Host IP: {host_ip}")
                return host_ip
        except:
            pass
    return "127.0.0.1"

WINDOWS_HOST_IP = get_windows_host_ip()

# --- DETECTION / STABILITY TUNING ---
CONF_THRESH = 0.45
TRACK_IOU_THRESH = 0.50
MIN_BOX_AREA_NORM = 0.0100
MAX_DETECTIONS = 12
BOX_SMOOTH_ALPHA = 0.72
LABEL_HISTORY_SIZE = 5
TRACK_STALE_SECONDS = 1.5

YOLO_WORLD_CLASSES = [
    "shoe", "sneaker", "boot", "slipper", "sock", "backpack", "handbag", "duffel bag", "suitcase", "luggage",
    "cardboard box", "parcel", "package", "plastic bag", "shopping bag", "pile of clothes", "towel", "blanket", "pillow",
    "toy", "ball", "book", "stack of books", "magazine", "trash can", "waste bin", "recycle bin", 
    "laundry basket", "bucket", "mop", "broom", "potted plant", "flower pot", "vase",
    "chair", "office chair", "stool", "sofa", "couch", "ottoman", "coffee table", "pedestal fan", "space heater",
    "power strip", "extension cord", "cable", "wire", "vacuum cleaner", "robot vacuum", "scale",
    "person", "foot", "leg", "cat", "dog", "pet"
]

try:
    from ultralytics import YOLOWorld
    has_yolo = True
    print("Loading YOLO-World model...")
    model = YOLOWorld("yolov8s-worldv2.pt")
    model.set_classes(YOLO_WORLD_CLASSES)
    print("Model ready.")
except ImportError:
    try:
        from ultralytics import YOLO
        print("Falling back to YOLOv8n...")
        has_yolo = True
        model = YOLO("yolov8n.pt")
    except ImportError:
        has_yolo = False
        print("WARNING: 'ultralytics' not found. Running in dummy mode.")


class TemporalStabilizer:
    def __init__(self):
        self.box_ema = {}
        self.label_history = defaultdict(lambda: deque(maxlen=LABEL_HISTORY_SIZE))
        self.last_seen = {}

    def cleanup(self, now: float):
        stale = [tid for tid, t in self.last_seen.items() if now - t > TRACK_STALE_SECONDS]
        for tid in stale:
            self.box_ema.pop(tid, None); self.label_history.pop(tid, None); self.last_seen.pop(tid, None)

    def smooth_box(self, tid: int, x, y, w, h):
        curr = np.array([x, y, w, h], dtype=np.float32)
        if tid < 0: return curr.tolist()
        prev = self.box_ema.get(tid)
        if prev is None: self.box_ema[tid] = curr
        else: self.box_ema[tid] = BOX_SMOOTH_ALPHA * prev + (1.0 - BOX_SMOOTH_ALPHA) * curr
        return self.box_ema[tid].tolist()

    def stable_label(self, tid: int, label: str) -> str:
        if tid < 0: return label
        self.label_history[tid].append(label)
        return Counter(self.label_history[tid]).most_common(1)[0][0]

    def touch(self, tid: int, now: float):
        if tid >= 0: self.last_seen[tid] = now


class ObstacleServer:
    def __init__(self):
        self.is_running = False
        self.thread = None
        self.stream_url = ""
        self.target_ip = WINDOWS_HOST_IP
        self.target_port = DATA_PORT
        self.lock = threading.Lock()
        self.stabilizer = TemporalStabilizer()

    def start_detection(self, stream_port, data_port):
        with self.lock:
            self.is_running = False # Reset existing loop if any
            if self.thread: self.thread.join(0.5)
            
            self.target_port = data_port
            self.stream_url = f"http://{WINDOWS_HOST_IP}:{stream_port}"
            self.is_running = True
            self.thread = threading.Thread(target=self._detection_loop, daemon=True)
            self.thread.start()
            print(f"--- DETECTION STARTED ---")
            print(f"Target Stream: {self.stream_url}")
            print(f"Sending UDP to: {self.target_ip}:{self.target_port}")

    def stop_detection(self):
        with self.lock:
            self.is_running = False
            print("--- DETECTION STOPPED ---")

    def _detection_loop(self):
        print(f"Connecting to MJPEG stream at {self.stream_url}...")
        cap = cv2.VideoCapture(self.stream_url)
        udp_sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        
        frame_count = 0
        last_log_time = time.time()

        while self.is_running:
            if not cap.isOpened():
                print(f"ERROR: Cannot open stream {self.stream_url}. Retrying...")
                cap.release(); time.sleep(2.0); cap = cv2.VideoCapture(self.stream_url)
                continue

            ret, frame = cap.read()
            if not ret or frame is None:
                # MJPEG streams sometimes return empty frames if the buffer is dry
                time.sleep(0.01); continue

            frame_count += 1
            if time.time() - last_log_time > 5.0:
                print(f"Status: Detection loop active. Frames processed: {frame_count}")
                last_log_time = time.time()

            if has_yolo:
                results = model.track(frame, persist=True, verbose=False, conf=CONF_THRESH, iou=TRACK_IOU_THRESH)
                detections = self._postprocess_results(frame, results)
            else:
                detections = [{"label": "dummy", "conf": 0.9, "x": 0.1, "y": 0.1, "w": 0.1, "h": 0.1, "id": 1}]
            
            if detections:
                try:
                    msg = json.dumps({"d": detections})
                    udp_sock.sendto(msg.encode(), (self.target_ip, self.target_port))
                except Exception as e:
                    print(f"UDP Error: {e}")

        cap.release(); udp_sock.close()
        print("Detection loop terminated.")

    def _postprocess_results(self, frame, results):
        detections = []
        h, w, _ = frame.shape
        boxes = getattr(results[0], "boxes", None)
        if not boxes: return detections

        now = time.time()
        for box in boxes:
            x1, y1, x2, y2 = box.xyxy[0].tolist()
            conf = float(box.conf[0]) if box.conf is not None else 0.0
            cls = int(box.cls[0]) if box.cls is not None else -1
            tid = int(box.id[0]) if box.id is not None else -1

            bw, bh = max(0, x2-x1), max(0, y2-y1)
            if conf < CONF_THRESH or (bw*bh)/(w*h) < MIN_BOX_AREA_NORM: continue

            label = model.names[cls] if hasattr(model, 'names') and cls in model.names else str(cls)
            nx, ny, nw, nh = self.stabilizer.smooth_box(tid, x1/w, y1/h, bw/w, bh/h)
            label = self.stabilizer.stable_label(tid, label)
            self.stabilizer.touch(tid, now)

            detections.append({
                "label": label, "conf": round(conf, 2),
                "x": round(float(nx), 4), "y": round(float(ny), 4),
                "w": round(float(nw), 4), "h": round(float(nh), 4),
                "id": tid
            })
        self.stabilizer.cleanup(now)
        return detections[:MAX_DETECTIONS]


def run_control_server():
    server = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    server.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    server.bind(("0.0.0.0", CONTROL_PORT))
    server.listen(5)
    print(f"YOLO Control Server active on port {CONTROL_PORT}")
    print(f"Targeting Windows Host at: {WINDOWS_HOST_IP}")

    detector = ObstacleServer()

    while True:
        client, addr = server.accept()
        print(f"Connected: {addr}")
        
        def handle_client(c):
            try:
                while True:
                    data = c.recv(1024).decode().strip()
                    if not data: break
                    if data.startswith("START"):
                        p = data.split(":")
                        s = int(p[1]) if len(p)>1 else DEFAULT_STREAM_PORT
                        d = int(p[2]) if len(p)>2 else DATA_PORT
                        detector.start_detection(s, d)
                        c.sendall(b"OK: STARTED\n")
                    elif data == "STOP":
                        detector.stop_detection()
                        c.sendall(b"OK: STOPPED\n")
                    elif data == "PING":
                        c.sendall(b"PONG\n")
            except: pass
            finally: c.close()
        
        threading.Thread(target=handle_client, args=(client,), daemon=True).start()

if __name__ == "__main__":
    run_control_server()
