#include <iostream>
#include <thread>
#include <atomic>
#include <chrono>
#include <cstring>
#include <string>
#include <vector>
#include <sstream>
#include <iomanip>
#include <limits>
#include <cmath>
#include <cstdlib>
#include <cctype>
#include <unistd.h>
#include <sys/stat.h>
#include <sys/time.h>

#include <netinet/in.h>
#include <sys/socket.h>
#include <arpa/inet.h>

#include <opencv2/core.hpp>
#include <opencv2/highgui.hpp>
#include <opencv2/imgproc.hpp>
#include <opencv2/videoio.hpp>

#include <sophus/se3.hpp>
#include <Eigen/Core>
#include <Eigen/Geometry>
#include <Eigen/Eigenvalues>

#include "System.h"
#include "MapPoint.h"

using namespace std;

// Plane fitting helper
struct GroundPlane {
    Eigen::Vector3f normal = Eigen::Vector3f::UnitY();
    float d = 0;
    Eigen::Vector3f x_axis = Eigen::Vector3f::UnitX();
    Eigen::Vector3f z_axis = Eigen::Vector3f::UnitZ();
    bool initialized = false;

    void Update(const vector<Eigen::Vector3f>& points, const Eigen::Vector3f& cam_pos) {
        if (points.size() < 50) return;

        Eigen::Vector3f mean = Eigen::Vector3f::Zero();
        for (const auto& p : points) mean += p;
        mean /= points.size();

        Eigen::Matrix3f cov = Eigen::Matrix3f::Zero();
        for (const auto& p : points) {
            Eigen::Vector3f diff = p - mean;
            cov += diff * diff.transpose();
        }

        Eigen::SelfAdjointEigenSolver<Eigen::Matrix3f> es(cov);
        Eigen::Vector3f new_normal = es.eigenvectors().col(0);
        
        // Ensure normal points towards camera (upwards relative to floor)
        if (new_normal.dot(cam_pos - mean) < 0) new_normal = -new_normal;

        // Smooth normal to prevent jitter/flipping
        if (!initialized) {
            normal = new_normal;
        } else {
            normal = (normal * 0.9f + new_normal * 0.1f).normalized();
        }
        
        d = -normal.dot(mean);

        // Stabilize coordinate system: align x_axis with world X if possible,
        // otherwise world Z. This keeps the plot rotation fixed.
        Eigen::Vector3f world_ref = Eigen::Vector3f::UnitX();
        if (abs(normal.dot(world_ref)) > 0.9f) world_ref = Eigen::Vector3f::UnitZ();

        x_axis = (world_ref - normal * normal.dot(world_ref)).normalized();
        z_axis = normal.cross(x_axis).normalized();
        initialized = true;
    }

    cv::Point2f Project(const Eigen::Vector3f& p) const {
        // Project p onto plane
        float dist = normal.dot(p) + d;
        Eigen::Vector3f p_proj = p - normal * dist;
        return cv::Point2f(p_proj.dot(x_axis), p_proj.dot(z_axis));
    }
};

static GroundPlane g_ground;

atomic<bool> g_run_tracking(false);
atomic<bool> g_request_exit(false);
atomic<bool> g_request_save(false);

static const int CTRL_PORT = 5555;
static int g_unity_udp_port = 5006;
static int g_frame_count = 0;

int g_udp_sock = -1;
sockaddr_in g_unity_addr{};
bool g_udp_ready = false;

static long GetFileSizeBytes(const std::string& path)
{
    struct stat st{};
    if (stat(path.c_str(), &st) != 0) return -1;
    return static_cast<long>(st.st_size);
}

static void PrintAtlasStatus()
{
    // ORB-SLAM3 expects System.SaveAtlasToFile as a BASENAME (for example "atlas").
    // It writes "./" + basename + ".osa" relative to the process cwd.
    // The Python orchestrator runs this executable with cwd=ORB_ROOT, so the
    // expected output is ORB_ROOT/atlas.osa.
    long atlasSize = GetFileSizeBytes("atlas.osa");
    long badDoubleExtSize = GetFileSizeBytes("atlas.bin.osa.osa");
    long oldBinSize = GetFileSizeBytes("atlas.bin.osa");

    cout << "[ATLAS] expected cwd atlas.osa size = " << atlasSize << " bytes" << endl;
    cout << "[ATLAS] diagnostic cwd atlas.bin.osa.osa size = " << badDoubleExtSize << " bytes" << endl;
    cout << "[ATLAS] diagnostic cwd atlas.bin.osa size = " << oldBinSize << " bytes" << endl;

    if (atlasSize < 0)
        cerr << "[ATLAS][ERROR] atlas.osa was not created. Check System.SaveAtlasToFile in YAML; it should be \"atlas\", not an absolute path and not atlas.bin.osa." << endl;
    else if (atlasSize < 10240)
        cerr << "[ATLAS][WARN] atlas.osa is very small; atlas may be invalid." << endl;
}

string ParseHostFromSource(const string& source)
{
    size_t scheme = source.find("://");
    if (scheme == string::npos) return "127.0.0.1";

    size_t start = scheme + 3;
    size_t colon = source.find(':', start);
    size_t slash = source.find('/', start);

    size_t end = string::npos;
    if (colon != string::npos && slash != string::npos)
        end = min(colon, slash);
    else if (colon != string::npos)
        end = colon;
    else
        end = slash;

    if (end == string::npos) return source.substr(start);
    return source.substr(start, end - start);
}

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

bool InitUDP(const string& source)
{
    string ip = "127.0.0.1";
    if (IsNetworkStream(source))
        ip = ParseHostFromSource(source);

    const char* env_ip = std::getenv("UNITY_HOST_IP");
    if (env_ip && std::strlen(env_ip) > 0)
        ip = env_ip;

    const char* env_port = std::getenv("UNITY_UDP_PORT");
    if (env_port) g_unity_udp_port = atoi(env_port);

    g_udp_sock = socket(AF_INET, SOCK_DGRAM, 0);
    if (g_udp_sock < 0)
    {
        cerr << "[UDP] socket() failed" << endl;
        return false;
    }

    memset(&g_unity_addr, 0, sizeof(g_unity_addr));
    g_unity_addr.sin_family = AF_INET;
    g_unity_addr.sin_port = htons(g_unity_udp_port);

    if (inet_pton(AF_INET, ip.c_str(), &g_unity_addr.sin_addr) != 1)
    {
        cerr << "[UDP] inet_pton() failed for ip: " << ip << endl;
        close(g_udp_sock);
        g_udp_sock = -1;
        return false;
    }

    cout << "[UDP] Mapping pose/path output enabled -> " << ip << ":" << g_unity_udp_port << endl;
    g_udp_ready = true;
    return true;
}

void SendUdpText(const std::string& msg)
{
    if (!g_udp_ready || g_udp_sock < 0) return;

    sendto(
        g_udp_sock,
        msg.c_str(),
        msg.length(),
        0,
        (sockaddr*)&g_unity_addr,
        sizeof(g_unity_addr)
    );
}

void SendPosePacket(const Sophus::SE3f& Tcw)
{
    if (Tcw.matrix().isZero()) return;

    Sophus::SE3f Twc = Tcw.inverse();
    Eigen::Vector3f t = Twc.translation();
    Eigen::Quaternionf q(Twc.rotationMatrix());

    stringstream ss;
    ss << "POSE|"
       << fixed << setprecision(6)
       << t.x() << "," << t.y() << "," << t.z() << ","
       << q.x() << "," << q.y() << "," << q.z() << "," << q.w();

    SendUdpText(ss.str());
}

void SendPathPointPacket(float x, float z)
{
    stringstream ss;
    ss << "PATH|"
       << fixed << setprecision(6)
       << x << "," << z;
    SendUdpText(ss.str());
}

void SendResetPacket()
{
    SendUdpText("RESET|");
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

    return chrono::duration_cast<chrono::duration<double>>(
        chrono::steady_clock::now() - t0
    ).count();
}

cv::Mat RenderTrajectoryCanvas(const vector<Eigen::Vector3f>& traj_points_3d,
                               const Eigen::Vector3f* current_pt = nullptr)
{
    const int W = 720;
    const int H = 720;
    const int PAD = 40;

    cv::Mat canvas(H, W, CV_8UC3, cv::Scalar(255, 255, 255));
    cv::putText(canvas, "Trajectory (Floor Plane Projection)", cv::Point(20, 30),
                cv::FONT_HERSHEY_SIMPLEX, 0.8, cv::Scalar(0, 0, 0), 2);

    if (traj_points_3d.empty() && current_pt == nullptr)
        return canvas;

    // Project all points to the CURRENT ground plane for consistent visualization
    vector<cv::Point2f> projected;
    for (const auto& p : traj_points_3d) projected.push_back(g_ground.Project(p));
    
    cv::Point2f current_proj;
    if (current_pt) {
        current_proj = g_ground.Project(*current_pt);
        projected.push_back(current_proj);
    }

    float min_x = numeric_limits<float>::max();
    float max_x = numeric_limits<float>::lowest();
    float min_z = numeric_limits<float>::max();
    float max_z = numeric_limits<float>::lowest();

    for (const auto& p : projected) {
        min_x = std::min(min_x, p.x);
        max_x = std::max(max_x, p.x);
        min_z = std::min(min_z, p.y);
        max_z = std::max(max_z, p.y);
    }

    if (min_x == numeric_limits<float>::max())
        return canvas;

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

    for (size_t i = 1; i < projected.size(); ++i)
    {
        cv::line(canvas, to_px(projected[i - 1]), to_px(projected[i]),
                 cv::Scalar(0, 150, 0), 2, cv::LINE_AA);
    }

    if (!projected.empty())
        cv::circle(canvas, to_px(projected.front()), 4, cv::Scalar(255, 0, 0), -1, cv::LINE_AA);

    if (current_pt)
        cv::circle(canvas, to_px(current_proj), 5, cv::Scalar(0, 0, 255), -1, cv::LINE_AA);

    return canvas;
}

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
    addr.sin_port = htons(CTRL_PORT);

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

    timeval tv{};
    tv.tv_sec = 1;
    tv.tv_usec = 0;
    setsockopt(server_fd, SOL_SOCKET, SO_RCVTIMEO, &tv, sizeof(tv));

    cout << "[CTRL] Listening on TCP " << CTRL_PORT << endl;

    char buffer[256];

    while (!g_request_exit)
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
        else if (cmd.find("SAVE_ATLAS") != string::npos)
            g_request_save = true;
        else if (cmd.find("EXIT") != string::npos)
        {
            g_run_tracking = false;
            g_request_exit = true;
        }

        close(client);
    }

    close(server_fd);
}

int main(int argc, char **argv)
{
    if (argc < 4)
    {
        cerr << "Usage: ./mono_buildmap vocab_file settings_file source [--viewer] [--debug2d]" << endl;
        return 1;
    }

    string vocabFile = argv[1];
    string settingsFile = argv[2];
    string source = argv[3];

    bool useViewer = HasFlag(argc, argv, "--viewer");
    bool showDebug2D = HasFlag(argc, argv, "--debug2d");

    bool source_is_network = IsNetworkStream(source);
    bool source_is_camera = IsNumericSource(source);
    bool source_is_file = IsFileSource(source);
    bool use_wallclock_timestamp = source_is_network || source_is_camera;

    cout << "[MAP] Source: " << source << endl;
    cout << "[MAP] Timestamp mode: "
         << (use_wallclock_timestamp ? "steady_clock" : "CAP_PROP_POS_MSEC(file)")
         << endl;
    cout << "[MAP] ORB viewer: " << (useViewer ? "ON" : "OFF") << endl;
    cout << "[MAP] 2D debug windows: " << (showDebug2D ? "ON" : "OFF") << endl;

    ORB_SLAM3::System SLAM(
        vocabFile,
        settingsFile,
        ORB_SLAM3::System::MONOCULAR,
        useViewer
    );

    // FIX: Start control server ONLY after system is initialized (vocabulary loaded)
    thread ctrl(ControlServer);

    cv::VideoCapture cap;
    if (source_is_camera) cap.open(std::stoi(source));
    else cap.open(source);

    if (!cap.isOpened())
    {
        cerr << "[MAIN] Cannot open source: " << source << endl;
        g_request_exit = true;
        if (ctrl.joinable()) ctrl.join();
        return 1;
    }

    InitUDP(source);
    SendResetPacket();

    auto t0 = chrono::steady_clock::now();
    vector<Eigen::Vector3f> traj_points_3d;
    int lost_count = 0;

    int warmup_frames = 0;
    bool was_tracking = false;
    bool printed_size = false;
    double last_timestamp = -1.0;

    cv::Mat paused_frame;

    cv::namedWindow("Mapping Frame", cv::WINDOW_NORMAL);
    cv::namedWindow("Trajectory", cv::WINDOW_NORMAL);

    cout << "[MAIN] Waiting for START..." << endl;

    while (!g_request_exit)
    {
        if (!g_run_tracking)
        {
            // Critical: Unity workflow is START -> STOP -> SAVE.
            // After STOP, tracking is false, so SAVE_ATLAS must be handled here,
            // otherwise the main loop waits forever and never reaches SLAM.Shutdown().
            if (g_request_save)
            {
                g_request_save = false;
                cout << "[MAIN] SAVE_ATLAS requested while stopped. Exiting cleanly so atlas can be written..." << endl;
                g_request_exit = true;
                break;
            }

            if (paused_frame.empty())
            {
                if (!cap.read(paused_frame) || paused_frame.empty())
                {
                    this_thread::sleep_for(chrono::milliseconds(10));
                    continue;
                }
                if (!printed_size)
                {
                    cout << "[MAP] Source frame size: " << paused_frame.cols << "x" << paused_frame.rows << endl;
                    printed_size = true;
                }
            }

            cv::Mat display = paused_frame.clone();
            cv::putText(display, "WAITING FOR START", cv::Point(20, 40),
                        cv::FONT_HERSHEY_SIMPLEX, 1.0, cv::Scalar(0, 255, 255), 2);

            cv::imshow("Mapping Frame", display);
            cv::imshow("Trajectory", RenderTrajectoryCanvas(traj_points_3d, nullptr));

            int k = cv::waitKey(1) & 0xFF;
            if (k == 27 || k == 'q')
                g_request_exit = true;

            this_thread::sleep_for(chrono::milliseconds(10));
            continue;
        }

        if (!was_tracking)
        {
            was_tracking = true;
            warmup_frames = 0;
            last_timestamp = -1.0;
            paused_frame.release();
            SendResetPacket();

            if (source_is_file)
            {
                cap.set(cv::CAP_PROP_POS_FRAMES, 0);
                cout << "[MAP] Reset file source to frame 0 for sync start." << endl;
            }

            t0 = chrono::steady_clock::now();
            cout << "[MAP] START received, warming up source..." << endl;
        }

        cv::Mat frame;
        if (!cap.read(frame) || frame.empty())
        {
            if (source_is_file)
            {
                cout << "[MAP] End of file reached." << endl;
            }
            this_thread::sleep_for(chrono::milliseconds(10));
            continue;
        }

        if (!printed_size)
        {
            cout << "[MAP] Source frame size: " << frame.cols << "x" << frame.rows << endl;
            printed_size = true;
        }

        if (warmup_frames < 15)
        {
            warmup_frames++;

            cv::Mat warm_display = frame.clone();
            cv::putText(warm_display,
                        "WARMING UP SOURCE... " + std::to_string(warmup_frames) + "/15",
                        cv::Point(20, 40),
                        cv::FONT_HERSHEY_SIMPLEX, 0.8, cv::Scalar(0, 255, 255), 2);

            cv::imshow("Mapping Frame", warm_display);
            cv::imshow("Trajectory", RenderTrajectoryCanvas(traj_points_3d, nullptr));
            cv::waitKey(1);
            continue;
        }

        cv::Mat gray = PreprocessGray(frame);
        double timestamp = GetFrameTimestampSec(cap, t0, use_wallclock_timestamp);

        if (last_timestamp > 0.0)
        {
            double dt = timestamp - last_timestamp;
            if (dt < 0.001 || dt > 0.50)
            {
                cout << "[MAP] Skip unstable frame timestamp dt=" << dt << endl;
                last_timestamp = timestamp;
                continue;
            }
        }
        last_timestamp = timestamp;

        Sophus::SE3f Tcw = SLAM.TrackMonocular(gray, timestamp);
        g_frame_count++;

        cv::Mat display = frame.clone();

        // --- Efficient Feature Point & Map Diagnostic ---
        vector<cv::KeyPoint> vKeys = SLAM.GetTrackedKeyPointsUn();
        vector<ORB_SLAM3::MapPoint*> vMPs = SLAM.GetTrackedMapPoints();
        int trackingState = SLAM.GetTrackingState();
        int nMatchedMPs = 0;

        vector<Eigen::Vector3f> tracked_pts;
        // Drawing ALL feature points for maximum visual feedback.
        for (size_t i = 0; i < vKeys.size(); i++)
        {
            if (vMPs.size() > i && vMPs[i]) {
                cv::circle(display, vKeys[i].pt, 1, cv::Scalar(0, 255, 0), -1);
                nMatchedMPs++;
                tracked_pts.push_back(vMPs[i]->GetWorldPos());
            } else {
                cv::circle(display, vKeys[i].pt, 1, cv::Scalar(0, 0, 255), -1);
            }
        }
        // ------------------------------------------------

        cv::Point2f current_pt;
        bool has_pose = false;
        Eigen::Vector3f raw_t(0,0,0);

        if (!Tcw.matrix().isZero())
        {
            Sophus::SE3f Twc = Tcw.inverse();
            Eigen::Vector3f t = Twc.translation();
            raw_t = t;

            // Update ground plane estimate every 10 frames
            if (g_frame_count % 10 == 0 && tracked_pts.size() > 50) {
                g_ground.Update(tracked_pts, t);
            }

            // Project current camera position onto the estimated ground plane
            current_pt = g_ground.Project(t);
            
            // Draw normal vector for diagnostic
            if (g_ground.initialized) {
                // Draw a small 3D-like normal vector on the top-left of the display
                cv::Point origin(100, 100);
                int length = 50;
                // Project 3D normal to 2D screen space (simplified)
                // We use raw_t relative orientation or just show the vector components
                cv::line(display, origin, 
                         cv::Point(origin.x + g_ground.normal.x()*length, origin.y - g_ground.normal.y()*length),
                         cv::Scalar(255, 0, 255), 2, cv::LINE_AA);
                cv::circle(display, origin, 3, cv::Scalar(255, 0, 255), -1);
                cv::putText(display, "Normal", cv::Point(origin.x - 20, origin.y + 20),
                            cv::FONT_HERSHEY_SIMPLEX, 0.5, cv::Scalar(255, 0, 255), 1);
                
                std::ostringstream nss;
                nss << fixed << setprecision(3) 
                    << "Nx:" << g_ground.normal.x() << " Ny:" << g_ground.normal.y() << " Nz:" << g_ground.normal.z();
                cv::putText(display, nss.str(), cv::Point(origin.x - 20, origin.y + 40),
                            cv::FONT_HERSHEY_SIMPLEX, 0.5, cv::Scalar(255, 0, 255), 1);
            }

            // 3D distance diagnostic
            static Eigen::Vector3f g_last_t(0,0,0);
            static bool g_has_last_t = false;
            float dist_3d = 0.0f;
            if (g_has_last_t) dist_3d = (t - g_last_t).norm();
            g_last_t = t;
            g_has_last_t = true;

            // Store the RAW 3D position
            traj_points_3d.push_back(t);

            has_pose = true;
            lost_count = 0;

            SendPosePacket(Tcw);
            // Send the projected ground coordinates to Unity
            SendPathPointPacket(current_pt.x, current_pt.y);

            std::ostringstream oss;
            oss << fixed << setprecision(4)
                << "Pos X:" << current_pt.x << " Y:" << current_pt.y << " D3D:" << dist_3d;
            cv::putText(display, oss.str(), cv::Point(20, 40),
                        cv::FONT_HERSHEY_SIMPLEX, 0.75, cv::Scalar(0, 255, 0), 2);
        }
        else
        {
            lost_count++;
            cv::putText(display, "Tracking lost / no valid pose", cv::Point(20, 40),
                        cv::FONT_HERSHEY_SIMPLEX, 0.75, cv::Scalar(0, 0, 255), 2);
        }

        {
            std::ostringstream oss;
            oss << "Frame: " << g_frame_count << "  Lost: " << lost_count << "  Pts: " << traj_points_3d.size();
            cv::putText(display, oss.str(), cv::Point(20, 75),
                        cv::FONT_HERSHEY_SIMPLEX, 0.70, cv::Scalar(255, 255, 0), 2);
        }

        {
            // Tracking State Diagnostic
            string stateStr = "LOST";
            if (trackingState == 1) stateStr = "INITIALIZING";
            else if (trackingState == 2 || trackingState == 5) stateStr = "OK";
            
            std::ostringstream oss;
            oss << "State: " << stateStr << "  Tracked MPs: " << nMatchedMPs << (has_pose ? " (POSE)" : " (NO POSE)");
            cv::putText(display, oss.str(), cv::Point(20, 110),
                        cv::FONT_HERSHEY_SIMPLEX, 0.70, cv::Scalar(255, 255, 255), 2);
        }

        cv::putText(display, "MODE: MAPPING (DIRECT SOURCE)", cv::Point(20, 145),
                    cv::FONT_HERSHEY_SIMPLEX, 0.70, cv::Scalar(255, 200, 0), 2);

        cv::imshow("Mapping Frame", display);
        cv::imshow("Trajectory", RenderTrajectoryCanvas(traj_points_3d, has_pose ? &raw_t : nullptr));

        if (g_request_save)
        {
            g_request_save = false;
            cout << "[MAIN] SAVE_ATLAS requested. Stopping mapping and exiting cleanly so atlas can be written..." << endl;
            g_run_tracking = false;
            g_request_exit = true;
            break;
        }

        int k = cv::waitKey(1) & 0xFF;
        if (k == 27 || k == 'q')
        {
            g_request_exit = true;
            g_run_tracking = false;
        }
    }

    g_run_tracking = false;
    cap.release();

    cout << "[MAIN] Waiting briefly before shutdown so mapping/loop-closing can finish..." << endl;
    this_thread::sleep_for(chrono::milliseconds(1500));

    cout << "[MAIN] Shutdown ORB-SLAM3..." << endl;
    cout << "[MAIN] Expected atlas output: ./atlas.osa (cwd should be ORB_ROOT)." << endl;
    SLAM.Shutdown();

    // SaveAtlas() is private in ORB-SLAM3. The atlas is written by SLAM.Shutdown()
    // when System.SaveAtlasToFile is set in the YAML.
    PrintAtlasStatus();

    cout << "[MAIN] Saving final trajectories..." << endl;
    SLAM.SaveTrajectoryEuRoC("trajectory_euroc.txt");
    SLAM.SaveKeyFrameTrajectoryEuRoC("keyframe_trajectory_euroc.txt");

    if (g_udp_sock >= 0)
        close(g_udp_sock);

    g_request_exit = true;
    if (ctrl.joinable()) ctrl.join();

    cv::destroyAllWindows();
    return 0;
}
