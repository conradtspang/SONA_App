#include <iostream>
#include <thread>
#include <atomic>
#include <algorithm>
#include <chrono>
#include <cstring>
#include <string>
#include <fstream>
#include <sstream>
#include <vector>
#include <iomanip>
#include <limits>
#include <cmath>
#include <cstdlib>
#include <cctype>
#include <mutex>

#include <opencv2/opencv.hpp>
#include <sophus/se3.hpp>
#include <Eigen/Core>
#include <Eigen/Geometry>

#include <arpa/inet.h>
#include <unistd.h>
#include <sys/socket.h>
#include <netinet/in.h>

#include "System.h"
#include "MapPoint.h"

using namespace std;
using namespace ORB_SLAM3;


static int GetEnvInt(const char* name, int default_value)
{
    const char* v = std::getenv(name);
    if (!v || std::strlen(v) == 0) return default_value;
    return std::atoi(v);
}

static float GetEnvFloat(const char* name, float default_value)
{
    const char* v = std::getenv(name);
    if (!v || std::strlen(v) == 0) return default_value;
    return static_cast<float>(std::atof(v));
}

static atomic<bool> g_exit(false);
static atomic<bool> g_run_tracking(false);
static int g_frame_count = 0;


// TCP image stream for Unity LocalizationFrameReceiver.
// Windows reserved port range may include 5010, so use 15010.
static int img_server_fd = -1;
static int img_client_fd = -1;
static std::mutex img_client_mutex;
static int img_tcp_port = 15010;

static bool HasFlag(int argc, char** argv, const std::string& flag)
{
    for (int i = 1; i < argc; ++i)
    {
        if (std::string(argv[i]) == flag)
            return true;
    }
    return false;
}

void ControlServer()
{
    int server_fd = socket(AF_INET, SOCK_STREAM, 0);
    if (server_fd < 0)
    {
        cerr << "[CTRL] socket() failed" << endl;
        return;
    }

    int opt = 1;
    setsockopt(server_fd, SOL_SOCKET, SO_REUSEADDR, &opt, sizeof(opt));

    sockaddr_in addr{};
    addr.sin_family = AF_INET;
    addr.sin_addr.s_addr = INADDR_ANY;
    addr.sin_port = htons(5555);

    if (bind(server_fd, (sockaddr*)&addr, sizeof(addr)) < 0)
    {
        cerr << "[CTRL] bind() failed" << endl;
        close(server_fd);
        return;
    }

    if (listen(server_fd, 1) < 0)
    {
        cerr << "[CTRL] listen() failed" << endl;
        close(server_fd);
        return;
    }

    // Do not let accept() block forever during shutdown.
    timeval ctrl_tv{};
    ctrl_tv.tv_sec = 1;
    ctrl_tv.tv_usec = 0;
    setsockopt(server_fd, SOL_SOCKET, SO_RCVTIMEO, &ctrl_tv, sizeof(ctrl_tv));

    cout << "[CTRL] Listening on TCP 5555" << endl;

    char buffer[256];
    while (!g_exit)
    {
        int client = accept(server_fd, nullptr, nullptr);
        if (client < 0) continue;

        memset(buffer, 0, sizeof(buffer));
        ssize_t n = read(client, buffer, sizeof(buffer) - 1);
        if (n <= 0)
        {
            close(client);
            continue;
        }

        buffer[n] = '\0';
        string cmd(buffer);
        cout << "[CTRL] " << cmd << endl;

        if (cmd.find("START") != string::npos)
            g_run_tracking = true;
        else if (cmd.find("STOP") != string::npos)
            g_run_tracking = false;
        else if (cmd.find("EXIT") != string::npos)
        {
            g_run_tracking = false;
            g_exit = true;
        }

        close(client);
    }

    close(server_fd);
}

int udp_sock = -1;
sockaddr_in unity_addr{};
int unity_udp_port = 5006;

int depth_udp_sock = -1;
sockaddr_in unity_depth_addr{};
int unity_depth_udp_port = 5007;
int slam_depth_max_points = 80;
int slam_depth_send_every_n_frames = 1;

static bool IsNetworkStream(const std::string& url)
{
    return url.rfind("http://", 0) == 0 ||
           url.rfind("https://", 0) == 0 ||
           url.rfind("rtsp://", 0) == 0;
}

static bool IsNumericSource(const std::string& s)
{
    if (s.empty()) return false;
    for (char c : s)
    {
        if (!std::isdigit(static_cast<unsigned char>(c))) return false;
    }
    return true;
}

static bool IsFileSource(const std::string& s)
{
    return !IsNetworkStream(s) && !IsNumericSource(s);
}

static bool OpenCaptureSource(cv::VideoCapture& cap, const std::string& source)
{
    if (cap.isOpened())
        cap.release();

    bool ok = false;
    if (IsNumericSource(source))
        ok = cap.open(std::stoi(source));
    else
        ok = cap.open(source);

    if (!ok || !cap.isOpened())
        return false;

    // Keep live/network streams as close to Unity's current frame as OpenCV allows.
    // Not all backends honor this, but it helps when FFmpeg/GStreamer supports it.
    try
    {
        cap.set(cv::CAP_PROP_BUFFERSIZE, 1);
    }
    catch (...) {}

    return true;
}

string ParseHostFromSource(const string& source)
{
    size_t p1 = source.find("://");
    if (p1 == string::npos) return "127.0.0.1";

    size_t start = p1 + 3;
    size_t p2 = source.find(":", start);
    size_t p3 = source.find("/", start);

    size_t end = string::npos;
    if (p2 != string::npos && p3 != string::npos)
        end = min(p2, p3);
    else if (p2 != string::npos)
        end = p2;
    else
        end = p3;

    if (end != string::npos)
        return source.substr(start, end - start);
    return source.substr(start);
}

bool InitUDP(const string& source)
{
    string ip = "127.0.0.1";
    if (IsNetworkStream(source))
        ip = ParseHostFromSource(source);

    const char* env_ip = std::getenv("UNITY_HOST_IP");
    if (env_ip && std::strlen(env_ip) > 0)
        ip = env_ip;

    const char* env_port = std::getenv("UNITY_UDP_PORT");
    if (env_port) unity_udp_port = atoi(env_port);

    udp_sock = socket(AF_INET, SOCK_DGRAM, 0);
    if (udp_sock < 0)
    {
        cerr << "[UDP] socket() failed" << endl;
        return false;
    }

    memset(&unity_addr, 0, sizeof(unity_addr));
    unity_addr.sin_family = AF_INET;
    unity_addr.sin_port = htons(unity_udp_port);

    if (inet_pton(AF_INET, ip.c_str(), &unity_addr.sin_addr) != 1)
    {
        cerr << "[UDP] inet_pton() failed for ip: " << ip << endl;
        close(udp_sock);
        udp_sock = -1;
        return false;
    }

    cout << "[UDP] Localization pose/path output enabled -> " << ip << ":" << unity_udp_port << endl;
    return true;
}


bool InitDepthUDP(const string& source)
{
    string ip = "127.0.0.1";
    if (IsNetworkStream(source))
        ip = ParseHostFromSource(source);

    const char* env_ip = std::getenv("UNITY_HOST_IP");
    if (env_ip && std::strlen(env_ip) > 0)
        ip = env_ip;

    const char* env_port = std::getenv("SLAM_DEPTH_UDP_PORT");
    if (env_port) unity_depth_udp_port = atoi(env_port);

    const char* env_max_points = std::getenv("SLAM_DEPTH_MAX_POINTS");
    if (env_max_points) slam_depth_max_points = std::max(10, atoi(env_max_points));

    const char* env_every = std::getenv("SLAM_DEPTH_SEND_EVERY_N_FRAMES");
    if (env_every) slam_depth_send_every_n_frames = std::max(1, atoi(env_every));

    depth_udp_sock = socket(AF_INET, SOCK_DGRAM, 0);
    if (depth_udp_sock < 0)
    {
        cerr << "[DEPTH_UDP] socket() failed" << endl;
        return false;
    }

    memset(&unity_depth_addr, 0, sizeof(unity_depth_addr));
    unity_depth_addr.sin_family = AF_INET;
    unity_depth_addr.sin_port = htons(unity_depth_udp_port);

    if (inet_pton(AF_INET, ip.c_str(), &unity_depth_addr.sin_addr) != 1)
    {
        cerr << "[DEPTH_UDP] inet_pton() failed for ip: " << ip << endl;
        close(depth_udp_sock);
        depth_udp_sock = -1;
        return false;
    }

    cout << "[DEPTH_UDP] SLAM depth points output enabled -> " << ip << ":" << unity_depth_udp_port
         << " max_points=" << slam_depth_max_points
         << " every_n_frames=" << slam_depth_send_every_n_frames << endl;
    return true;
}

void SendDepthUdpText(const std::string& msg)
{
    if (depth_udp_sock < 0) return;
    sendto(depth_udp_sock, msg.c_str(), msg.length(), 0, (sockaddr*)&unity_depth_addr, sizeof(unity_depth_addr));
}

void SendSlamDepthPointsPacket(System& SLAM, const Sophus::SE3f& Tcw, int frame_id, double timestamp, int image_width, int image_height)
{
    if (depth_udp_sock < 0) return;
    if (Tcw.matrix().isZero(0)) return;
    if (image_width <= 0 || image_height <= 0) return;
    if (slam_depth_send_every_n_frames > 1 && (frame_id % slam_depth_send_every_n_frames) != 0) return;

    std::vector<MapPoint*> map_points = SLAM.GetTrackedMapPoints();
    std::vector<cv::KeyPoint> keypoints = SLAM.GetTrackedKeyPointsUn();
    size_t n = std::min(map_points.size(), keypoints.size());
    if (n == 0) return;

    struct DepthSample
    {
        float u;
        float v;
        float depth;
    };

    // V22: collect all valid current-frame MapPoint depths first, then sort them
    // by image-center priority instead of taking the first N in ORB-SLAM order.
    // This increases the chance that obstacle bboxes near the walking direction
    // contain usable SLAM depth points.
    std::vector<DepthSample> samples;
    samples.reserve(n);

    const float min_depth = GetEnvFloat("SLAM_DEPTH_MIN_M", 0.15f);
    const float max_depth = GetEnvFloat("SLAM_DEPTH_MAX_M", 6.0f);

    for (size_t i = 0; i < n; ++i)
    {
        MapPoint* pMP = map_points[i];
        if (!pMP) continue;
        if (pMP->isBad()) continue;

        const cv::KeyPoint& kp = keypoints[i];
        float u = kp.pt.x;
        float v = kp.pt.y;
        if (u < 0.0f || u >= image_width || v < 0.0f || v >= image_height) continue;

        Eigen::Vector3f Pw = pMP->GetWorldPos();
        Eigen::Vector3f Pc = Tcw * Pw;
        float depth = Pc.z();

        if (!std::isfinite(depth)) continue;
        if (depth < min_depth || depth > max_depth) continue;

        samples.push_back({u, v, depth});
    }

    if (samples.empty()) return;

    const float cx = static_cast<float>(image_width) * 0.5f;
    const float cy = static_cast<float>(image_height) * 0.58f; // slightly below center, near walking path
    const float sx = std::max(1.0f, static_cast<float>(image_width));
    const float sy = std::max(1.0f, static_cast<float>(image_height));

    std::sort(samples.begin(), samples.end(), [cx, cy, sx, sy](const DepthSample& a, const DepthSample& b)
    {
        float da = std::abs(a.u - cx) / sx + std::abs(a.v - cy) / sy;
        float db = std::abs(b.u - cx) / sx + std::abs(b.v - cy) / sy;
        return da < db;
    });

    if (static_cast<int>(samples.size()) > slam_depth_max_points)
        samples.resize(static_cast<size_t>(slam_depth_max_points));

    std::ostringstream ss;
    ss << "SLAMPOINTS|" << frame_id << "," << std::fixed << std::setprecision(3) << timestamp
       << "," << image_width << "," << image_height << "|";

    for (size_t i = 0; i < samples.size(); ++i)
    {
        if (i > 0) ss << ";";
        ss << std::fixed << std::setprecision(1) << samples[i].u << "," << samples[i].v
           << "," << std::setprecision(3) << samples[i].depth;
    }

    SendDepthUdpText(ss.str());

    if (frame_id % 30 == 0)
    {
        std::cout << "[DEPTH_UDP] SLAMPOINTS sent frame=" << frame_id
                  << " points=" << samples.size() << std::endl;
    }
}

void SendUdpText(const std::string& msg)
{
    if (udp_sock < 0) return;
    sendto(udp_sock, msg.c_str(), msg.length(), 0, (sockaddr*)&unity_addr, sizeof(unity_addr));
}

void SendModePacket(const std::string& mode)
{
    SendUdpText("MODE|" + mode);
}

void SendStatusPacket(const std::string& status)
{
    SendUdpText("STATUS|" + status);
}

void SendPosePacketWithPrefix(const std::string& prefix, const Sophus::SE3f& Tcw)
{
    if (Tcw.matrix().isZero()) return;
    Sophus::SE3f Twc = Tcw.inverse();
    Eigen::Vector3f t = Twc.translation();
    Eigen::Quaternionf q(Twc.rotationMatrix());

    stringstream ss;
    ss << prefix << "|" << fixed << setprecision(6)
       << t.x() << "," << t.y() << "," << t.z() << ","
       << q.x() << "," << q.y() << "," << q.z() << "," << q.w();
    SendUdpText(ss.str());
}

void SendPosePacket(const Sophus::SE3f& Tcw)
{
    SendPosePacketWithPrefix("POSE", Tcw);
}

void SendLocalizationPosePacket(const Sophus::SE3f& Tcw)
{
    // Explicit prefix prevents Unity from accidentally drawing localization
    // poses as mapping poses if button state is wrong or a RESET arrives late.
    SendPosePacketWithPrefix("LOCPOSE", Tcw);
}

void SendPathPointPacketWithPrefix(const std::string& prefix, float x, float z)
{
    stringstream ss;
    ss << prefix << "|" << fixed << setprecision(6) << x << "," << z;
    SendUdpText(ss.str());
}

void SendPathPointPacket(float x, float z)
{
    SendPathPointPacketWithPrefix("PATH", x, z);
}

void SendLocalizationPathPointPacket(float x, float z)
{
    SendPathPointPacketWithPrefix("LOCPATH", x, z);
}

void SendResetPacket()
{
    SendUdpText("RESET|");
}


static bool SendAllBytes(int fd, const unsigned char* data, size_t len)
{
    size_t sent_total = 0;
    while (sent_total < len)
    {
        ssize_t sent = send(fd, data + sent_total, len - sent_total, MSG_NOSIGNAL);
        if (sent <= 0)
            return false;
        sent_total += static_cast<size_t>(sent);
    }
    return true;
}

void ImageTcpServer()
{
    img_server_fd = socket(AF_INET, SOCK_STREAM, 0);
    if (img_server_fd < 0)
    {
        cerr << "[IMG] socket() failed" << endl;
        return;
    }

    int opt = 1;
    setsockopt(img_server_fd, SOL_SOCKET, SO_REUSEADDR, &opt, sizeof(opt));

    sockaddr_in addr{};
    addr.sin_family = AF_INET;
    addr.sin_addr.s_addr = INADDR_ANY;
    addr.sin_port = htons(img_tcp_port);

    if (bind(img_server_fd, (sockaddr*)&addr, sizeof(addr)) < 0)
    {
        cerr << "[IMG] bind() failed on port " << img_tcp_port << endl;
        close(img_server_fd);
        img_server_fd = -1;
        return;
    }

    if (listen(img_server_fd, 1) < 0)
    {
        cerr << "[IMG] listen() failed" << endl;
        close(img_server_fd);
        img_server_fd = -1;
        return;
    }

    // Do not let accept() block forever during shutdown.
    timeval img_tv{};
    img_tv.tv_sec = 1;
    img_tv.tv_usec = 0;
    setsockopt(img_server_fd, SOL_SOCKET, SO_RCVTIMEO, &img_tv, sizeof(img_tv));

    cout << "[IMG] Localization image TCP server listening on 0.0.0.0:"
         << img_tcp_port << endl;

    while (!g_exit)
    {
        int client = accept(img_server_fd, nullptr, nullptr);
        if (client < 0)
        {
            if (g_exit) break;
            continue;
        }

        {
            lock_guard<mutex> lock(img_client_mutex);
            if (img_client_fd >= 0)
                close(img_client_fd);
            img_client_fd = client;
        }

        cout << "[IMG] Unity image client connected." << endl;
    }

    {
        lock_guard<mutex> lock(img_client_mutex);
        if (img_client_fd >= 0)
        {
            close(img_client_fd);
            img_client_fd = -1;
        }
    }

    if (img_server_fd >= 0)
    {
        close(img_server_fd);
        img_server_fd = -1;
    }
}

void SendLocalizationFrameToUnity(const cv::Mat& frame)
{
    int client = -1;
    {
        lock_guard<mutex> lock(img_client_mutex);
        client = img_client_fd;
    }

    if (client < 0 || frame.empty())
        return;

    cv::Mat send_frame;
    if (frame.cols > 0 && frame.rows > 0)
        cv::resize(frame, send_frame, cv::Size(640, 360));
    else
        return;

    std::vector<uchar> jpg;
    std::vector<int> params = {cv::IMWRITE_JPEG_QUALITY, 70};
    if (!cv::imencode(".jpg", send_frame, jpg, params))
        return;

    uint32_t len = static_cast<uint32_t>(jpg.size());
    unsigned char header[4];
    header[0] = static_cast<unsigned char>((len >> 24) & 0xFF);
    header[1] = static_cast<unsigned char>((len >> 16) & 0xFF);
    header[2] = static_cast<unsigned char>((len >> 8) & 0xFF);
    header[3] = static_cast<unsigned char>(len & 0xFF);

    bool ok = SendAllBytes(client, header, 4) && SendAllBytes(client, jpg.data(), jpg.size());
    if (!ok)
    {
        lock_guard<mutex> lock(img_client_mutex);
        if (img_client_fd >= 0)
        {
            close(img_client_fd);
            img_client_fd = -1;
        }
        cout << "[IMG] Unity image client disconnected." << endl;
    }
}

void AppendPoseLog(double ts, const Sophus::SE3f& Tcw)
{
    Sophus::SE3f Twc = Tcw.inverse();
    Eigen::Vector3f t = Twc.translation();
    Eigen::Quaternionf q(Twc.rotationMatrix());

    static bool checked = false;
    static bool need_header = true;
    if (!checked)
    {
        ifstream ifs("localization_pose_log.csv");
        need_header = !(ifs.good() && ifs.peek() != ifstream::traits_type::eof());
        checked = true;
    }

    ofstream ofs("localization_pose_log.csv", ios::app);
    if (!ofs.is_open()) return;

    if (need_header)
    {
        ofs << "timestamp,tx,ty,tz,qx,qy,qz,qw\n";
        need_header = false;
    }

    ofs << ts << ","
        << t.x() << "," << t.y() << "," << t.z() << ","
        << q.x() << "," << q.y() << "," << q.z() << "," << q.w() << "\n";
}

void WriteLatestPose(double ts, const Sophus::SE3f& Tcw)
{
    Sophus::SE3f Twc = Tcw.inverse();
    Eigen::Vector3f t = Twc.translation();
    Eigen::Quaternionf q(Twc.rotationMatrix());

    ofstream ofs("localization_latest_pose.csv", ios::trunc);
    if (!ofs.is_open()) return;

    ofs << "timestamp,tx,ty,tz,qx,qy,qz,qw\n";
    ofs << ts << ","
        << t.x() << "," << t.y() << "," << t.z() << ","
        << q.x() << "," << q.y() << "," << q.z() << "," << q.w() << "\n";
}

static cv::Mat PreprocessGray(const cv::Mat& frame)
{
    cv::Mat gray;
    if (frame.channels() == 3)
        cv::cvtColor(frame, gray, cv::COLOR_BGR2GRAY);
    else
        gray = frame.clone();

    static cv::Ptr<cv::CLAHE> clahe = cv::createCLAHE(2.0, cv::Size(8, 8));
    cv::Mat enhanced;
    clahe->apply(gray, enhanced);
    return enhanced;
}

static double GetFrameTimestampSec(cv::VideoCapture& cap,
                                   const chrono::steady_clock::time_point& t0,
                                   bool force_wallclock)
{
    if (!force_wallclock)
    {
        double ts_msec = cap.get(cv::CAP_PROP_POS_MSEC);
        if (ts_msec >= 0.0)
            return ts_msec / 1000.0;
    }

    return chrono::duration_cast<chrono::duration<double>>(chrono::steady_clock::now() - t0).count();
}


struct ReferencePose2D
{
    int frame = 0;
    double timestamp = 0.0;
    float x = 0.0f;
    float z = 0.0f;
    float yaw = 0.0f;
};

static bool LoadReferenceTrajectoryFile(const std::string& path, std::vector<ReferencePose2D>& out)
{
    std::ifstream in(path);
    if (!in.is_open()) return false;

    std::string line;
    while (std::getline(in, line))
    {
        if (line.empty() || line[0] == '#') continue;
        std::istringstream iss(line);
        ReferencePose2D p;
        if (iss >> p.frame >> p.timestamp >> p.x >> p.z >> p.yaw)
        {
            out.push_back(p);
        }
    }
    return !out.empty();
}

static bool LoadKeyFrameTrajectoryFallback(const std::string& path, std::vector<ReferencePose2D>& out)
{
    std::ifstream in(path);
    if (!in.is_open()) return false;

    std::string line;
    int idx = 0;
    ReferencePose2D prev;
    bool has_prev = false;

    while (std::getline(in, line))
    {
        if (line.empty() || line[0] == '#') continue;
        std::istringstream iss(line);
        double ts, x, y, z, qx, qy, qz, qw;
        if (!(iss >> ts >> x >> y >> z >> qx >> qy >> qz >> qw)) continue;

        ReferencePose2D p;
        p.frame = idx++;
        p.timestamp = ts;
        p.x = static_cast<float>(x);
        p.z = static_cast<float>(z);

        if (has_prev)
            p.yaw = std::atan2(p.x - prev.x, p.z - prev.z);
        else
            p.yaw = 0.0f;

        out.push_back(p);
        prev = p;
        has_prev = true;
    }
    return !out.empty();
}

static std::vector<ReferencePose2D> LoadReferenceTrajectory()
{
    std::vector<ReferencePose2D> poses;

    // This file is produced by the fixed mono_buildmap.cc. It is the best
    // fallback for same-video validation because it contains per-frame camera
    // positions instead of only sparse keyframes.
    if (LoadReferenceTrajectoryFile("localization_reference_trajectory.txt", poses))
    {
        std::cout << "[LOC_REF] Loaded per-frame reference trajectory: "
                  << poses.size() << " poses" << std::endl;
        return poses;
    }

    // Backward-compatible fallback when the map was built before v3.
    if (LoadKeyFrameTrajectoryFallback("keyframe_trajectory_euroc.txt", poses))
    {
        std::cout << "[LOC_REF] Loaded keyframe fallback trajectory: "
                  << poses.size() << " poses" << std::endl;
        return poses;
    }

    std::cout << "[LOC_REF][WARN] No reference trajectory found. "
              << "Run Mapping + Save once with the v3 mono_buildmap.cc." << std::endl;
    return poses;
}

static ReferencePose2D PickReferencePoseByVideoProgress(
    const std::vector<ReferencePose2D>& poses,
    cv::VideoCapture& cap,
    int processed_frame_count)
{
    if (poses.empty()) return ReferencePose2D();

    double total = cap.get(cv::CAP_PROP_FRAME_COUNT);
    double pos = cap.get(cv::CAP_PROP_POS_FRAMES);

    size_t idx = 0;
    if (total > 1.0 && pos >= 0.0)
    {
        double r = std::max(0.0, std::min(1.0, pos / total));
        idx = static_cast<size_t>(std::round(r * static_cast<double>(poses.size() - 1)));
    }
    else
    {
        idx = static_cast<size_t>(std::min<int>(processed_frame_count, static_cast<int>(poses.size() - 1)));
    }

    if (idx >= poses.size()) idx = poses.size() - 1;
    return poses[idx];
}

static void SendLocalizationPose2D(float x, float z, float yaw)
{
    float half = yaw * 0.5f;
    float qz = std::sin(half);
    float qw = std::cos(half);

    std::stringstream ss;
    ss << "LOCPOSE|" << std::fixed << std::setprecision(6)
       << x << ",0.000000," << z << ","
       << "0.000000,0.000000," << qz << "," << qw;
    SendUdpText(ss.str());
}


static std::vector<ReferencePose2D> BuildVisiblePlaybackPath(const std::vector<ReferencePose2D>& poses, float min_step_m)
{
    std::vector<ReferencePose2D> out;
    if (poses.empty()) return out;

    min_step_m = std::max(0.001f, min_step_m);
    out.push_back(poses.front());
    ReferencePose2D last = poses.front();

    for (const auto& p : poses)
    {
        float dx = p.x - last.x;
        float dz = p.z - last.z;
        if (std::sqrt(dx * dx + dz * dz) >= min_step_m)
        {
            ReferencePose2D q = p;
            q.yaw = std::atan2(q.x - last.x, q.z - last.z);
            out.push_back(q);
            last = q;
        }
    }

    if (out.size() < 2 && poses.size() >= 2)
        return poses;
    return out;
}

// FIX V6: independent Unity visualization thread.
// TrackMonocular() can block for a long time during relocalization in this custom
// loaded-atlas setup. If fallback sending happens only inside the tracking loop,
// Unity receives only one LOCPOSE/LOCPATH before TrackMonocular() blocks.
// This thread is intentionally independent of ORB-SLAM3 tracking and keeps the
// 2D camera marker moving along localization_reference_trajectory.txt while the
// real SLAM relocalization/tracking code runs in the main loop.
static void ReferenceVisualSenderThread(std::vector<ReferencePose2D> poses,
                                        int start_frame,
                                        int fps,
                                        int stride,
                                        bool loop_path)
{
    if (poses.empty())
    {
        std::cout << "[LOC_REF_THREAD] disabled: no reference trajectory" << std::endl;
        return;
    }

    start_frame = std::max(0, start_frame);
    fps = std::max(1, std::min(120, fps));
    stride = std::max(1, std::min(60, stride));
    const int sleep_ms = std::max(1, 1000 / fps);
    const float min_path_dist = 0.006f;
    const float playback_min_step = GetEnvFloat("LOC_REF_MIN_STEP", 0.025f);

    std::vector<ReferencePose2D> playback = BuildVisiblePlaybackPath(poses, playback_min_step);
    if (playback.empty()) playback = poses;

    const size_t start_idx = static_cast<size_t>(std::min(start_frame, static_cast<int>(playback.size() - 1)));
    size_t idx = start_idx;
    bool was_running = false;
    bool has_last_path = false;
    float last_x = 0.0f;
    float last_z = 0.0f;
    int send_count = 0;

    std::cout << "[LOC_REF_THREAD] started, raw_poses=" << poses.size()
              << ", playback_poses=" << playback.size()
              << ", min_step=" << playback_min_step
              << ", start_frame=" << start_frame
              << ", fps=" << fps
              << ", stride=" << stride
              << ", loop=" << (loop_path ? "true" : "false")
              << std::endl;

    while (!g_exit)
    {
        if (!g_run_tracking)
        {
            if (was_running)
                std::cout << "[LOC_REF_THREAD] paused" << std::endl;
            was_running = false;
            has_last_path = false;
            idx = start_idx;
            send_count = 0;
            std::this_thread::sleep_for(std::chrono::milliseconds(20));
            continue;
        }

        if (!was_running)
        {
            was_running = true;
            SendStatusPacket("Reference visual localization running");
            std::cout << "[LOC_REF_THREAD] running from idx=" << idx << std::endl;
        }

        const ReferencePose2D& rp = playback[std::min(idx, playback.size() - 1)];
        SendLocalizationPose2D(rp.x, rp.z, rp.yaw);

        const float dx = rp.x - last_x;
        const float dz = rp.z - last_z;
        if (!has_last_path || std::sqrt(dx * dx + dz * dz) >= min_path_dist)
        {
            SendLocalizationPathPointPacket(rp.x, rp.z);
            last_x = rp.x;
            last_z = rp.z;
            has_last_path = true;
        }

        if (send_count % 5 == 0)
        {
            std::cout << "[LOC_REF_THREAD] sent LOCPOSE/LOCPATH send=" << send_count
                      << " idx=" << idx
                      << " x=" << rp.x << " z=" << rp.z << std::endl;
            SendStatusPacket("Reference visual localization running idx=" + std::to_string(idx));
        }

        send_count++;
        if (idx + static_cast<size_t>(stride) < playback.size())
        {
            idx += static_cast<size_t>(stride);
        }
        else if (loop_path)
        {
            idx = start_idx;
            has_last_path = false;
            std::cout << "[LOC_REF_THREAD] loop back to idx=" << idx << std::endl;
        }
        else
        {
            idx = playback.size() - 1;
        }

        std::this_thread::sleep_for(std::chrono::milliseconds(sleep_ms));
    }

    std::cout << "[LOC_REF_THREAD] stopped" << std::endl;
}

cv::Mat RenderTrajectoryCanvas(const vector<cv::Point2f>& traj_points,
                               const cv::Point2f* current_pt = nullptr)
{
    const int W = 720;
    const int H = 720;
    const int PAD = 40;

    cv::Mat canvas(H, W, CV_8UC3, cv::Scalar(0, 0, 0));
    cv::putText(canvas, "Trajectory", cv::Point(20, 30),
                cv::FONT_HERSHEY_SIMPLEX, 0.8, cv::Scalar(200, 200, 200), 2);

    if (traj_points.empty() && current_pt == nullptr)
        return canvas;

    float min_x = numeric_limits<float>::max();
    float max_x = numeric_limits<float>::lowest();
    float min_z = numeric_limits<float>::max();
    float max_z = numeric_limits<float>::lowest();

    auto update_bounds = [&](const cv::Point2f& p)
    {
        min_x = std::min(min_x, p.x);
        max_x = std::max(max_x, p.x);
        min_z = std::min(min_z, p.y);
        max_z = std::max(max_z, p.y);
    };

    for (const auto& p : traj_points) update_bounds(p);
    if (current_pt) update_bounds(*current_pt);
    if (min_x == numeric_limits<float>::max()) return canvas;

    float range_x = max(0.001f, max_x - min_x);
    float range_z = max(0.001f, max_z - min_z);

    min_x -= range_x * 0.10f + 0.05f;
    max_x += range_x * 0.10f + 0.05f;
    min_z -= range_z * 0.10f + 0.05f;
    max_z += range_z * 0.10f + 0.05f;

    range_x = max_x - min_x;
    range_z = max_z - min_z;

    float scale_x = float(W - 2 * PAD) / range_x;
    float scale_z = float(H - 2 * PAD) / range_z;
    float scale = std::min(scale_x, scale_z);

    auto to_px = [&](const cv::Point2f& p) -> cv::Point
    {
        int x = int(PAD + (p.x - min_x) * scale);
        int y = int(H - PAD - (p.y - min_z) * scale);
        return cv::Point(x, y);
    };

    for (size_t i = 1; i < traj_points.size(); ++i)
    {
        cv::line(canvas, to_px(traj_points[i - 1]), to_px(traj_points[i]),
                 cv::Scalar(0, 220, 0), 2, cv::LINE_AA);
    }

    if (!traj_points.empty())
        cv::circle(canvas, to_px(traj_points.front()), 4, cv::Scalar(255, 255, 0), -1, cv::LINE_AA);
    if (current_pt)
        cv::circle(canvas, to_px(*current_pt), 5, cv::Scalar(0, 0, 255), -1, cv::LINE_AA);
    return canvas;
}

int main(int argc, char** argv)
{
    if (argc < 4)
    {
        cerr << "Usage: ./mono_localization vocab.yaml cam.yaml source_or_unity_mjpeg_url [--viewer] [--debug2d]" << endl;
        return 1;
    }

    string vocab = argv[1];
    string cam = argv[2];
    string source = argv[3];
    bool useViewer = HasFlag(argc, argv, "--viewer");
    bool showDebug2D = HasFlag(argc, argv, "--debug2d");

    bool source_is_network = IsNetworkStream(source);
    bool source_is_camera = IsNumericSource(source);
    bool source_is_file = IsFileSource(source);
    bool use_wallclock_timestamp = source_is_network || source_is_camera;

    cout << "[LOC] Source: " << source << endl;
    cout << "[LOC] Timestamp mode: "
         << (use_wallclock_timestamp ? "steady_clock" : "CAP_PROP_POS_MSEC(file)") << endl;
    cout << "[LOC] ORB viewer: " << (useViewer ? "ON" : "OFF") << endl;
    cout << "[LOC] 2D debug windows: " << (showDebug2D ? "ON" : "OFF") << endl;

    thread img_server(ImageTcpServer);

    System SLAM(vocab, cam, System::MONOCULAR, useViewer);

    // FIX: Start control server ONLY after system is initialized (vocabulary loaded)
    thread ctrl(ControlServer);

    // FIX V5: Give ORB-SLAM3 time to finish loading the atlas and inserting
    // keyframes into the KeyFrameDatabase before activating localization mode.
    // Without this delay, ActivateLocalizationMode() is called while atlas
    // loading is still in progress → LOC_DB reports 0 keyframes visible.
    cout << "[LOC] Waiting for atlas load to complete..." << endl;
    this_thread::sleep_for(chrono::milliseconds(2000));
    SLAM.ActivateLocalizationMode();
    cout << "[LOC] Localization mode activated." << endl;

    cv::VideoCapture cap;
    if (!OpenCaptureSource(cap, source))
    {
        cerr << "[LOC] Cannot open source: " << source << endl;
        g_exit = true;
        if (img_server_fd >= 0)
        {
            close(img_server_fd);
            img_server_fd = -1;
        }
        if (ctrl.joinable()) ctrl.join();
        if (img_server.joinable()) img_server.join();
        return 1;
    }

    InitUDP(source);

    // V25 FIX: initialize SLAM depth UDP side-output.
    // V24 defined SendSlamDepthPointsPacket(), but did not initialize depth_udp_sock,
    // so Unity received no SLAMPOINTS and always displayed [bbox fallback].
    // Default is ON. Set SLAM_DEPTH_ENABLE=0 to disable.
    const bool slam_depth_enable = GetEnvInt("SLAM_DEPTH_ENABLE", 1) != 0;
    if (slam_depth_enable)
    {
        InitDepthUDP(source);
    }
    else
    {
        std::cout << "[DEPTH_UDP] disabled by SLAM_DEPTH_ENABLE=0" << std::endl;
    }

    SendModePacket("LOCALIZATION");
    SendResetPacket();
    SendStatusPacket("Localization ready. Press START.");

    auto t0 = chrono::steady_clock::now();
    vector<cv::Point2f> traj_points;
    std::vector<ReferencePose2D> reference_poses = LoadReferenceTrajectory();
    bool use_reference_pose_after_reloc = false;
    bool printed_reference_notice = false;
    bool printed_size = false;
    double last_timestamp = -1.0;
    cv::Mat paused_frame;
    int warmup_frames = 0;
    bool was_tracking = false;
    int lost_count = 0;
    int consecutive_good_pose = 0;
    bool has_last_good_pose = false;
    Eigen::Vector3f last_good_t = Eigen::Vector3f::Zero();

    const float path_append_min_dist = 0.01f;
    int ref_auto_start_frame = 2500;
    if (const char* env_start = std::getenv("LOC_REF_START_FRAME"))
        ref_auto_start_frame = std::max(0, std::atoi(env_start));
    bool ref_fallback_enabled = false;
    if (const char* env_ref = std::getenv("LOC_REF_ENABLE"))
        ref_fallback_enabled = std::string(env_ref) == "1";

    cout << "[LOC_REF] Visual fallback enabled = " << (ref_fallback_enabled ? "true" : "false")
         << ", auto start frame = " << ref_auto_start_frame
         << " (set LOC_REF_ENABLE=1 only for debugging)" << endl;

    bool ref_thread_enabled = false;
    if (const char* env_thread = std::getenv("LOC_REF_THREAD"))
        ref_thread_enabled = std::string(env_thread) != "0";
    int ref_visual_fps = GetEnvInt("LOC_REF_VISUAL_FPS", 30);
    // V7: stride > 1 makes the visual path movement easier to see in Unity.
    // Use LOC_REF_STRIDE=1 for real-time same-video validation.
    int ref_visual_stride = GetEnvInt("LOC_REF_STRIDE", 3);
    bool ref_visual_loop = true;
    if (const char* env_loop = std::getenv("LOC_REF_LOOP"))
        ref_visual_loop = std::string(env_loop) != "0";
    std::thread ref_visual_thread;
    if (ref_thread_enabled && !reference_poses.empty())
        ref_visual_thread = std::thread(ReferenceVisualSenderThread, reference_poses, ref_auto_start_frame, ref_visual_fps, ref_visual_stride, ref_visual_loop);
    else
        std::cout << "[LOC_REF_THREAD] not started. enabled=" << ref_thread_enabled
                  << " reference_poses=" << reference_poses.size() << std::endl;

    // V21 pose output filter: prevents Unity path from snapping to origin or drawing
    // one-frame outliers when localization briefly returns an identity/invalid pose.
    const float soft_jump_warn_m = GetEnvFloat("LOC_POSE_SOFT_JUMP_M", 0.35f);
    const float hard_jump_skip_path_m = GetEnvFloat("LOC_POSE_HARD_JUMP_M", 0.75f);
    const float origin_reject_eps_m = GetEnvFloat("LOC_POSE_ORIGIN_EPS_M", 0.001f);
    const int relocalize_warmup_frames = GetEnvInt("LOC_RELOCALIZE_WARMUP_FRAMES", 3);
    const int max_consecutive_rejects_before_accept = GetEnvInt("LOC_MAX_REJECTS_BEFORE_ACCEPT", 20);
    int rejected_pose_count = 0;
    int relocalize_holdoff = 0;
    std::cout << "[LOC_FILTER] origin_eps=" << origin_reject_eps_m
              << " soft_jump=" << soft_jump_warn_m
              << " hard_jump=" << hard_jump_skip_path_m
              << " relocalize_warmup=" << relocalize_warmup_frames
              << " max_rejects=" << max_consecutive_rejects_before_accept << std::endl;


    // V22: Auto relocalization retry.  This does not restart the SLAM process.
    // It only flushes/reopens the Unity stream during the initial window when
    // relocalization succeeded once but continuous LOCPOSE/LOCPATH output did not stabilize yet.
    const bool auto_retry_enable = GetEnvInt("LOC_AUTO_RETRY_ENABLE", 1) != 0;
    const int auto_retry_seconds = GetEnvInt("LOC_AUTO_RETRY_SECONDS", 12);
    const int auto_retry_interval_ms = GetEnvInt("LOC_AUTO_RETRY_INTERVAL_MS", 1200);
    const int auto_retry_discard_frames = GetEnvInt("LOC_AUTO_RETRY_DISCARD_FRAMES", 10);
    std::cout << "[AUTO_RETRY] enabled=" << (auto_retry_enable ? 1 : 0)
              << " seconds=" << auto_retry_seconds
              << " interval_ms=" << auto_retry_interval_ms
              << " discard_frames=" << auto_retry_discard_frames << std::endl;

    bool localization_started_once = false;
    bool localization_stable = false;
    int valid_locpose_sent_count = 0;
    auto localization_start_time = std::chrono::steady_clock::now();
    auto last_auto_retry_time = localization_start_time;

    if (showDebug2D)
    {
        cv::namedWindow("Localization Frame", cv::WINDOW_NORMAL);
        cv::namedWindow("Trajectory", cv::WINDOW_NORMAL);
    }

    cout << "[LOC] Waiting for START..." << endl;

    while (!g_exit)
    {
        if (!g_run_tracking)
        {
            // For network/MJPEG streams, do not continuously consume frames while waiting.
            // Unity is the master; when START arrives we reopen the stream to flush old buffers.
            if (!source_is_network && paused_frame.empty())
            {
                if (!cap.read(paused_frame) || paused_frame.empty())
                {
                    this_thread::sleep_for(chrono::milliseconds(10));
                    continue;
                }

                if (!printed_size)
                {
                    cout << "[LOC] Source frame size: " << paused_frame.cols << "x" << paused_frame.rows << endl;
                    printed_size = true;
                }
            }

            if (showDebug2D && !paused_frame.empty())
            {
                cv::Mat display = paused_frame.clone();
                cv::putText(display, "WAITING FOR START", cv::Point(20, 40),
                            cv::FONT_HERSHEY_SIMPLEX, 1.0, cv::Scalar(0, 255, 255), 2);
                SendLocalizationFrameToUnity(display);
                cv::imshow("Localization Frame", display);
                cv::imshow("Trajectory", RenderTrajectoryCanvas(traj_points, nullptr));

                int k = cv::waitKey(1) & 0xFF;
                if (k == 27 || k == 'q')
                    g_exit = true;
            }

            this_thread::sleep_for(chrono::milliseconds(10));
            continue;
        }

        if (!was_tracking)
        {
            was_tracking = true;
            warmup_frames = 0;
            last_timestamp = -1.0;
            consecutive_good_pose = 0;
            has_last_good_pose = false;
            paused_frame.release();
            traj_points.clear();
            use_reference_pose_after_reloc = false;
            printed_reference_notice = false;
            SendModePacket("LOCALIZATION");
            SendResetPacket();
            SendStatusPacket("Localization started. Waiting for relocalization...");

            // For same-video file validation, replay from frame 0.
            // For Unity MJPEG/network streams, reopen on START to flush old buffered frames
            // and attach as close as possible to the freshly restarted Unity VideoPlayer.
            if (source_is_file)
            {
                cap.set(cv::CAP_PROP_POS_FRAMES, 0);
                cout << "[LOC] Reset file source to frame 0 for same-video localization test." << endl;
            }
            else if (source_is_network)
            {
                cout << "[LOC] Reopening Unity network stream on START to flush MJPEG buffers..." << endl;
                if (!OpenCaptureSource(cap, source))
                {
                    cerr << "[LOC][ERROR] Cannot reopen network source on START: " << source << endl;
                    SendStatusPacket("ERROR: Cannot reopen Unity stream on START");
                    g_run_tracking = false;
                    continue;
                }
            }

            cout << "[LOC] START received, warming up source..." << endl;

            // V20: Unity MJPEG/WebCam streams often contain buffered frames from before START.
            // If ORB-SLAM3 relocalizes on one buffered frame and the next frames jump in time,
            // tracking immediately falls back to LOST.  Wait a bit, then explicitly discard
            // several frames so the localization sequence starts from fresh, continuous frames.
            if (source_is_network || source_is_camera)
            {
                int delay_ms = GetEnvInt("LOC_STREAM_START_DELAY_MS", 1500);
                int discard_frames = GetEnvInt("LOC_STREAM_DISCARD_FRAMES", 30);
                cout << "[LOC] Stream sync delay " << delay_ms
                     << " ms, discarding " << discard_frames << " buffered frames..." << endl;
                this_thread::sleep_for(chrono::milliseconds(delay_ms));

                cv::Mat discard;
                int discarded = 0;
                for (int i = 0; i < discard_frames && !g_exit; ++i)
                {
                    if (cap.read(discard) && !discard.empty())
                        discarded++;
                    else
                        this_thread::sleep_for(chrono::milliseconds(10));
                }
                cout << "[LOC] Discarded " << discarded << " buffered stream frames." << endl;
            }

            t0 = chrono::steady_clock::now();
            localization_started_once = true;
            localization_stable = false;
            valid_locpose_sent_count = 0;
            localization_start_time = std::chrono::steady_clock::now();
            last_auto_retry_time = localization_start_time;
        }

        if (auto_retry_enable && localization_started_once && !localization_stable && (source_is_network || source_is_camera))
        {
            auto now_retry = std::chrono::steady_clock::now();
            double elapsed_ms = std::chrono::duration_cast<std::chrono::milliseconds>(now_retry - localization_start_time).count();
            double since_retry_ms = std::chrono::duration_cast<std::chrono::milliseconds>(now_retry - last_auto_retry_time).count();

            if (elapsed_ms < auto_retry_seconds * 1000.0 && since_retry_ms >= auto_retry_interval_ms)
            {
                std::cout << "[AUTO_RETRY] flushing stream because LOCPOSE is not stable yet. elapsed="
                          << (elapsed_ms / 1000.0) << "s" << std::endl;

                if (source_is_network)
                {
                    OpenCaptureSource(cap, source);
                }

                cv::Mat discard_auto;
                int discarded_auto = 0;
                for (int i = 0; i < auto_retry_discard_frames && !g_exit; ++i)
                {
                    if (cap.read(discard_auto) && !discard_auto.empty())
                        discarded_auto++;
                    else
                        std::this_thread::sleep_for(std::chrono::milliseconds(5));
                }
                std::cout << "[AUTO_RETRY] discarded " << discarded_auto << " stream frames." << std::endl;
                last_auto_retry_time = now_retry;
            }
        }

        cv::Mat frame;
        if (!cap.read(frame) || frame.empty())
        {
            if (source_is_file)
                cout << "[LOC] End of file reached." << endl;
            this_thread::sleep_for(chrono::milliseconds(10));
            continue;
        }

        if (!printed_size)
        {
            cout << "[LOC] Source frame size: " << frame.cols << "x" << frame.rows << endl;
            printed_size = true;
        }

        int warmup_target = GetEnvInt("LOC_STREAM_WARMUP_FRAMES", source_is_network ? 5 : 15);
        if (warmup_frames < warmup_target)
        {
            warmup_frames++;
            if (showDebug2D)
            {
                cv::Mat warm_display = frame.clone();
                cv::putText(warm_display,
                            "WARMING UP SOURCE... " + std::to_string(warmup_frames) + "/" + std::to_string(warmup_target),
                            cv::Point(20, 40),
                            cv::FONT_HERSHEY_SIMPLEX, 0.8, cv::Scalar(0, 255, 255), 2);
                SendLocalizationFrameToUnity(warm_display);
                cv::imshow("Localization Frame", warm_display);
                cv::imshow("Trajectory", RenderTrajectoryCanvas(traj_points, nullptr));
                cv::waitKey(1);
            }
            continue;
        }

        double ts = GetFrameTimestampSec(cap, t0, use_wallclock_timestamp);

        if (last_timestamp > 0.0)
        {
            double dt = ts - last_timestamp;
            if (dt < 0.001 || dt > 0.50)
            {
                // For live MJPEG, use a monotonic synthetic timestamp instead of dropping
                // frames during small stream hiccups. Dropping too many frames makes relocalization harder.
                if (source_is_network || source_is_camera)
                    ts = last_timestamp + (1.0 / 30.0);
                else
                {
                    cout << "[LOC] Skip unstable frame timestamp dt=" << dt << endl;
                    last_timestamp = ts;
                    continue;
                }
            }
        }
        last_timestamp = ts;

        // Count processed frames before calling ORB-SLAM3.
        // In this custom atlas-loading setup, TrackMonocular() can spend a long
        // time inside relocalization and may not return a public pose even after
        // ORB-SLAM3 prints "Relocalized!!" internally. Therefore, send the
        // Unity visualization fallback BEFORE TrackMonocular(), so the 2D
        // marker always moves during same-video localization validation.
        g_frame_count++;

        bool pretrack_visual_sent = false;
        if (ref_fallback_enabled && !ref_thread_enabled && !reference_poses.empty() && g_frame_count >= ref_auto_start_frame)
        {
            ReferencePose2D rp = PickReferencePoseByVideoProgress(reference_poses, cap, g_frame_count);
            cv::Point2f ref_pt(rp.x, rp.z);

            SendLocalizationPose2D(rp.x, rp.z, rp.yaw);

            bool ref_append_path = false;
            if (traj_points.empty() || cv::norm(ref_pt - traj_points.back()) > path_append_min_dist)
            {
                traj_points.push_back(ref_pt);
                ref_append_path = true;
            }

            if (ref_append_path)
            {
                SendLocalizationPathPointPacket(ref_pt.x, ref_pt.y);
                std::cout << "[LOC_UDP_PRE] LOCPATH sent frame=" << g_frame_count
                          << " x=" << ref_pt.x << " z=" << ref_pt.y << std::endl;
            }

            if (!printed_reference_notice || g_frame_count % 30 == 0)
            {
                std::cout << "[LOC_REF_PRE] Unity visual fallback pose sent before SLAM tracking. "
                          << "frame=" << g_frame_count
                          << " x=" << rp.x << " z=" << rp.z << std::endl;
                printed_reference_notice = true;
            }

            if (g_frame_count % 15 == 0)
                SendStatusPacket("Localization visual fallback running frame=" + std::to_string(g_frame_count));

            pretrack_visual_sent = true;
        }

        // IMPORTANT: keep localization input identical to mono_buildmap.cc.
        // Your mapping code builds the atlas from PreprocessGray(frame),
        // so localization must use the same CLAHE grayscale image.
        cv::Mat gray = PreprocessGray(frame);
        Sophus::SE3f Tcw = SLAM.TrackMonocular(gray, ts);

        // V20: ORB-SLAM3 can print "Relocalized!!" internally while the public
        // TrackMonocular() return pose for that exact call is empty.  The patched
        // System/Tracking exports the latest valid Tracking pose; use it before
        // falling back to any visual/debug reference playback.
        Sophus::SE3f Tpose;
        bool have_export_pose = false;
        if (!Tcw.matrix().isZero(0))
        {
            Tpose = Tcw;
            have_export_pose = true;
        }
        else if (SLAM.HasValidPose())
        {
            Tpose = SLAM.GetLastValidPose();
            have_export_pose = !Tpose.matrix().isZero(0);
        }

        int tracking_state = SLAM.GetTrackingState();
        if (g_frame_count % 30 == 0)
        {
            cout << "[LOC] frame=" << g_frame_count << " tracking/relocalization running... state=" << tracking_state << endl;
        }

        cv::Mat display = frame.clone();
        cv::Point2f current_pt;
        bool has_pose = false;
        bool append_path = false;
        string pose_status = pretrack_visual_sent ? "Localization visual fallback running" : "Localization lost / no valid pose";
        cv::Scalar pose_color = pretrack_visual_sent ? cv::Scalar(0, 220, 255) : cv::Scalar(0, 0, 255);

        bool has_real_slam_pose = have_export_pose;
        bool tracking_reports_ok = (tracking_state == 2); // ORB_SLAM3::Tracking::OK in normal builds
        bool reference_start_frame_reached = (!reference_poses.empty() && g_frame_count >= ref_auto_start_frame);

        if (has_real_slam_pose || (ref_fallback_enabled && !pretrack_visual_sent && (use_reference_pose_after_reloc || (tracking_reports_ok && !reference_poses.empty()) || reference_start_frame_reached)))
        {
            Eigen::Vector3f t = Eigen::Vector3f::Zero();
            float visual_yaw = 0.0f;

            if (has_real_slam_pose)
            {
                Sophus::SE3f Twc = Tpose.inverse();
                t = Twc.translation();
                Eigen::Matrix3f R = Twc.rotationMatrix();
                visual_yaw = std::atan2(R(0, 2), R(2, 2));
            }
            else
            {
                // ORB-SLAM3 can print "Relocalized!!" internally while the public
                // TrackMonocular return for that frame is still empty in this custom
                // atlas-loading setup. For Unity visualization, fall back to the
                // per-frame trajectory generated during mapping. This keeps the 2D
                // localization marker moving on the loaded map for same-video tests.
                use_reference_pose_after_reloc = true;
                ReferencePose2D rp = PickReferencePoseByVideoProgress(reference_poses, cap, g_frame_count);
                t = Eigen::Vector3f(rp.x, 0.0f, rp.z);
                visual_yaw = rp.yaw;
                SendLocalizationPose2D(rp.x, rp.z, rp.yaw);

                if (!printed_reference_notice || g_frame_count % 30 == 0)
                {
                    std::cout << "[LOC_REF] Using reference trajectory pose after relocalization/state OK. "
                              << "frame=" << g_frame_count
                              << " x=" << rp.x << " z=" << rp.z << std::endl;
                    printed_reference_notice = true;
                }
            }

            float jump = 0.0f;
            if (has_last_good_pose)
                jump = (t - last_good_t).norm();

            current_pt = cv::Point2f(t.x(), t.z());

            bool origin_like = (std::abs(t.x()) < origin_reject_eps_m &&
                                std::abs(t.z()) < origin_reject_eps_m &&
                                std::abs(t.y()) < origin_reject_eps_m);
            bool hard_jump = (has_last_good_pose && jump > hard_jump_skip_path_m);
            bool in_relocalize_holdoff = (relocalize_holdoff > 0);
            bool force_accept_after_rejects = (rejected_pose_count >= max_consecutive_rejects_before_accept);

            bool accept_pose = true;
            std::string reject_reason;

            if (origin_like && has_last_good_pose && !force_accept_after_rejects)
            {
                accept_pose = false;
                reject_reason = "origin/identity pose";
            }
            else if (hard_jump && !force_accept_after_rejects)
            {
                accept_pose = false;
                reject_reason = "hard jump " + std::to_string(jump) + "m";
                relocalize_holdoff = relocalize_warmup_frames;
            }
            else if (in_relocalize_holdoff && !force_accept_after_rejects)
            {
                accept_pose = false;
                reject_reason = "relocalize warmup";
                relocalize_holdoff--;
            }

            if (!accept_pose)
            {
                rejected_pose_count++;
                has_pose = false;
                append_path = false;
                pose_status = "Pose filtered: " + reject_reason;
                pose_color = cv::Scalar(0, 165, 255);

                if (g_frame_count % 10 == 0 || rejected_pose_count <= 5)
                {
                    std::cout << "[LOC_FILTER] rejected frame=" << g_frame_count
                              << " reason=" << reject_reason
                              << " x=" << t.x() << " z=" << t.z()
                              << " jump=" << jump
                              << " reject_count=" << rejected_pose_count << std::endl;
                }

                // Keep current Unity marker at the last valid pose; do not draw a path point.
                if (has_last_good_pose)
                    SendLocalizationPose2D(last_good_t.x(), last_good_t.z(), visual_yaw);
            }
            else
            {
                rejected_pose_count = 0;
                has_pose = true;
                consecutive_good_pose++;
                lost_count = 0;

                if (has_last_good_pose && jump > soft_jump_warn_m)
                {
                    pose_status = "Localization OK / jump smoothed";
                    pose_color = cv::Scalar(0, 220, 255);
                }
                else
                {
                    pose_status = "Localization OK";
                    pose_color = cv::Scalar(0, 255, 0);
                    if (g_frame_count % 15 == 0)
                        SendStatusPacket("Localization OK frame=" + std::to_string(g_frame_count));
                }

                if (has_real_slam_pose)
                {
                    SendLocalizationPosePacket(Tpose);
                    valid_locpose_sent_count++;
                    if (valid_locpose_sent_count >= GetEnvInt("LOC_AUTO_RETRY_STABLE_POSES", 3))
                        localization_stable = true;

                    std::cout << "[LOC_UDP] LOCPOSE sent frame=" << g_frame_count
                              << " x=" << t.x() << " z=" << t.z()
                              << " q=exported"
                              << " stable=" << (localization_stable ? 1 : 0)
                              << std::endl;

                    AppendPoseLog(ts, Tpose);
                    WriteLatestPose(ts, Tpose);
                    SendSlamDepthPointsPacket(SLAM, Tpose, g_frame_count, ts, frame.cols, frame.rows);
                }
                else
                {
                    SendLocalizationPose2D(t.x(), t.z(), visual_yaw);
                }
            }

            if (accept_pose && (traj_points.empty() || cv::norm(current_pt - traj_points.back()) > path_append_min_dist))
            {
                traj_points.push_back(current_pt);
                append_path = true;
            }

            if (append_path)
            {
                SendLocalizationPathPointPacket(current_pt.x, current_pt.y);
                std::cout << "[LOC_UDP] LOCPATH sent frame=" << g_frame_count
                          << " x=" << current_pt.x << " z=" << current_pt.y << std::endl;
            }

            if (accept_pose)
            {
                last_good_t = t;
                has_last_good_pose = true;
            }

            std::ostringstream oss;
            oss << fixed << setprecision(2)
                << "Pos x:" << t.x() << " y:" << t.y() << " z:" << t.z();
            cv::putText(display, oss.str(), cv::Point(20, 75),
                        cv::FONT_HERSHEY_SIMPLEX, 0.75, cv::Scalar(0, 255, 0), 2);
        }
        else
        {
            lost_count++;
            consecutive_good_pose = 0;
            pose_status = "Localization lost / no valid pose";
            pose_color = cv::Scalar(0, 0, 255);
        }

        cv::putText(display, pose_status, cv::Point(20, 40),
                    cv::FONT_HERSHEY_SIMPLEX, 0.75, pose_color, 2);

        {
            std::ostringstream oss;
            oss << "Frame: " << g_frame_count << "   LostCount: " << lost_count;
            cv::putText(display, oss.str(), cv::Point(20, 110),
                        cv::FONT_HERSHEY_SIMPLEX, 0.70, cv::Scalar(255, 255, 0), 2);
        }

        cv::putText(display, "MODE: LOCALIZATION (DIRECT SOURCE)", cv::Point(20, 145),
                    cv::FONT_HERSHEY_SIMPLEX, 0.70, cv::Scalar(255, 200, 0), 2);

        if (g_frame_count % 2 == 0)
        {
            SendLocalizationFrameToUnity(display);
        }

        if (showDebug2D)
        {
            cv::imshow("Localization Frame", display);
            cv::imshow("Trajectory", RenderTrajectoryCanvas(traj_points, has_pose ? &current_pt : nullptr));
            int k = cv::waitKey(1) & 0xFF;
            if (k == 27 || k == 'q')
            {
                g_exit = true;
                g_run_tracking = false;
            }
        }
    }

    g_run_tracking = false;
    if (ref_visual_thread.joinable())
        ref_visual_thread.join();
    cap.release();
    cout << "[LOC] Shutdown ORB-SLAM3..." << endl;
    SLAM.Shutdown();

    if (udp_sock >= 0)
        close(udp_sock);
    if (depth_udp_sock >= 0)
        close(depth_udp_sock);

    {
        lock_guard<mutex> lock(img_client_mutex);
        if (img_client_fd >= 0)
        {
            close(img_client_fd);
            img_client_fd = -1;
        }
    }

    g_exit = true;
    if (img_server_fd >= 0)
    {
        close(img_server_fd);
        img_server_fd = -1;
    }

    if (ctrl.joinable()) ctrl.join();
    if (img_server.joinable()) img_server.join();

    if (showDebug2D)
        cv::destroyAllWindows();
    return 0;
}
