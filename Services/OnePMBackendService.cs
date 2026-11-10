using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace OnePM.Services;

/// <summary>
/// Dịch vụ quản lý tiến trình Python Backend gốc (handset.server) và giao tiếp HTTP REST/Mirror API
/// tương thích 100% với backend trong iphone-mirror-engine.
/// </summary>
public sealed class OnePMBackendService : IDisposable
{
    public const string DefaultBaseUrl = "http://127.0.0.1:8765";

    private readonly HttpClient _httpClient;
    private Process? _pythonServerProcess;
    private readonly StringBuilder _lastPythonError = new();
    private bool _disposed;

    public string BaseUrl { get; }
    public string MirrorPageUrl => $"{BaseUrl}/mirror.html";
    public string LastErrorMessage => _lastPythonError.ToString().Trim();
    public string? DetectedRepoRoot { get; private set; }

    public OnePMBackendService(string baseUrl = DefaultBaseUrl)
    {
        BaseUrl = baseUrl.TrimEnd('/');
        _httpClient = new HttpClient
        {
            BaseAddress = new Uri(BaseUrl),
            Timeout = TimeSpan.FromSeconds(60) // Cho phép đủ thời gian cho việc ghép Wi-Fi và mở tunnel
        };
    }

    /// <summary>
    /// Kiểm tra xem Python backend (`python -m handset.server`) đã chạy tại 127.0.0.1:8765 chưa.
    /// Nếu chưa chạy, tự động tìm thư mục gốc chứa `handset/server.py` và khởi chạy ngầm.
    /// </summary>
    public async Task<bool> EnsureBackendRunningAsync(string? repoRootOverride = null, CancellationToken ct = default)
    {
        if (await IsServerReachableAsync(ct))
        {
            return true;
        }

        _lastPythonError.Clear();
        string? repoRoot = FindRepositoryRoot(repoRootOverride);
        DetectedRepoRoot = repoRoot;

        if (string.IsNullOrEmpty(repoRoot))
        {
            _lastPythonError.Append("Không tìm thấy thư mục chứa Python backend (handset/server.py).");
            return false;
        }

        string pythonExe = FindPythonExecutable(repoRoot);
        var startInfo = new ProcessStartInfo
        {
            FileName = pythonExe,
            Arguments = "-m handset.server",
            WorkingDirectory = repoRoot,
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        try
        {
            if (_pythonServerProcess is { HasExited: false })
            {
                _pythonServerProcess.Kill(entireProcessTree: true);
                _pythonServerProcess.Dispose();
            }

            _pythonServerProcess = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            _pythonServerProcess.ErrorDataReceived += (_, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data))
                {
                    _lastPythonError.AppendLine(e.Data);
                    Debug.WriteLine($"[handset.server ERR] {e.Data}");
                }
            };
            _pythonServerProcess.OutputDataReceived += (_, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data))
                {
                    Debug.WriteLine($"[handset.server] {e.Data}");
                }
            };

            _pythonServerProcess.Start();
            _pythonServerProcess.BeginErrorReadLine();
            _pythonServerProcess.BeginOutputReadLine();
        }
        catch (Exception ex)
        {
            _lastPythonError.Append($"Không thể khởi chạy '{pythonExe} -m handset.server': {ex.Message}");
            return false;
        }

        // Chờ tối đa 12 giây để ThreadingHTTPServer lắng nghe trên 127.0.0.1:8765
        for (int i = 0; i < 48; i++)
        {
            if (ct.IsCancellationRequested) break;
            if (await IsServerReachableAsync(ct))
            {
                return true;
            }
            if (_pythonServerProcess.HasExited)
            {
                break;
            }
            await Task.Delay(250, ct);
        }

        return await IsServerReachableAsync(ct);
    }

    public async Task<bool> IsServerReachableAsync(CancellationToken ct = default)
    {
        try
        {
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
            using var response = await _httpClient.GetAsync("/api/status", linkedCts.Token);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Gọi GET /api/status từ handset/server.py
    /// </summary>
    public async Task<OnePMStatusResponse?> GetStatusAsync(CancellationToken ct = default)
    {
        try
        {
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
            using var response = await _httpClient.GetAsync("/api/status", linkedCts.Token);
            if (!response.IsSuccessStatusCode) return null;

            string json = await response.Content.ReadAsStringAsync(ct);
            return JsonSerializer.Deserialize<OnePMStatusResponse>(json, JsonOptions);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[1pmBackendService] GetStatusAsync lỗi: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Gọi GET /api/devices/{udid} để lấy thông tin chi tiết (Tên máy, Model, iOS version) từ lockdown service.
    /// </summary>
    public async Task<OnePMDeviceDetailResponse?> GetDeviceInfoAsync(string udid, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(udid)) return null;

        try
        {
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
            string encoded = Uri.EscapeDataString(udid);
            using var response = await _httpClient.GetAsync($"/api/devices/{encoded}", linkedCts.Token);
            if (!response.IsSuccessStatusCode) return null;

            string json = await response.Content.ReadAsStringAsync(ct);
            return JsonSerializer.Deserialize<OnePMDeviceDetailResponse>(json, JsonOptions);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[1pmBackendService] GetDeviceInfoAsync({udid}) lỗi: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Gọi POST /api/screen để bắt đầu phiên phản chiếu màn hình iPhone (transport: "auto" | "usb" | "wifi").
    /// </summary>
    public async Task<OnePMScreenStatus?> StartScreenAsync(string? udid, string transport = "auto", CancellationToken ct = default)
    {
        var payload = new
        {
            udid = string.IsNullOrWhiteSpace(udid) ? null : udid.Trim(),
            transport = string.IsNullOrWhiteSpace(transport) ? "auto" : transport.Trim()
        };

        try
        {
            string json = JsonSerializer.Serialize(payload, JsonOptions);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var response = await _httpClient.PostAsync("/api/screen", content, ct);
            if (!response.IsSuccessStatusCode) return null;

            string respJson = await response.Content.ReadAsStringAsync(ct);
            return JsonSerializer.Deserialize<OnePMScreenStatus>(respJson, JsonOptions);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[1pmBackendService] StartScreenAsync lỗi: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Gọi POST /api/screen/restart để khởi động lại phiên stream cho thiết bị.
    /// </summary>
    public async Task<OnePMScreenStatus?> RestartScreenAsync(string? udid, CancellationToken ct = default)
    {
        var payload = new
        {
            udid = string.IsNullOrWhiteSpace(udid) ? null : udid.Trim()
        };

        try
        {
            string json = JsonSerializer.Serialize(payload, JsonOptions);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var response = await _httpClient.PostAsync("/api/screen/restart", content, ct);
            if (!response.IsSuccessStatusCode) return null;

            string respJson = await response.Content.ReadAsStringAsync(ct);
            return JsonSerializer.Deserialize<OnePMScreenStatus>(respJson, JsonOptions);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[1pmBackendService] RestartScreenAsync lỗi: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Gọi POST /api/screen/stop để dừng stream hiện tại và giải phóng cổng 8766.
    /// </summary>
    public async Task<OnePMScreenStatus?> StopScreenAsync(CancellationToken ct = default)
    {
        try
        {
            using var content = new StringContent("{}", Encoding.UTF8, "application/json");
            using var response = await _httpClient.PostAsync("/api/screen/stop", content, ct);
            if (!response.IsSuccessStatusCode) return null;

            string respJson = await response.Content.ReadAsStringAsync(ct);
            return JsonSerializer.Deserialize<OnePMScreenStatus>(respJson, JsonOptions);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[1pmBackendService] StopScreenAsync lỗi: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Gọi POST /api/wifi/pair để ghép nối thiết bị qua Wi-Fi qua cáp USB.
    /// </summary>
    public async Task<OnePMPairWifiResponse?> PairWifiAsync(string udid, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(udid)) return null;

        var payload = new { udid = udid.Trim() };
        try
        {
            // Thiết lập ghép nối Wi-Fi có thể mất 5-15 giây
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

            string json = JsonSerializer.Serialize(payload, JsonOptions);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var response = await _httpClient.PostAsync("/api/wifi/pair", content, linkedCts.Token);

            string respJson = await response.Content.ReadAsStringAsync(linkedCts.Token);
            return JsonSerializer.Deserialize<OnePMPairWifiResponse>(respJson, JsonOptions);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[1pmBackendService] PairWifiAsync({udid}) lỗi: {ex.Message}");
            return new OnePMPairWifiResponse { Ok = false, Error = ex.Message };
        }
    }

    /// <summary>
    /// Gọi POST /mirror/button. Chuẩn hóa tên nút ("volume-up", "volume-down", "lock", "home", "siri", "mute")
    /// theo đúng _NAMED_BUTTONS trong ScreenStreamServer (pymobiledevice3).
    /// </summary>
    public async Task<bool> SendHardwareButtonAsync(string buttonName, string state = "press", CancellationToken ct = default)
    {
        string normalized = (buttonName ?? "").Trim().ToLowerInvariant() switch
        {
            "volume_up" => "volume-up",
            "volume-up" => "volume-up",
            "volume_down" => "volume-down",
            "volume-down" => "volume-down",
            "lock" => "lock",
            "home" => "home",
            "siri" => "siri",
            "mute" => "mute",
            _ => buttonName?.ToLowerInvariant() ?? ""
        };

        var payload = new
        {
            name = normalized,
            state = state
        };
        return await PostJsonAsync("/mirror/button", payload, ct);
    }

    /// <summary>
    /// Gọi POST /mirror/touch với tọa độ chuẩn hóa 16-bit (0..65535).
    /// Type hợp lệ: "contact" | "release" | "tap". (Không dùng "move").
    /// </summary>
    public async Task<bool> SendTouchAsync(string type, int xNormalized, int yNormalized, CancellationToken ct = default)
    {
        string op = type.ToLowerInvariant() switch
        {
            "move" => "contact",
            "contact" => "contact",
            "release" => "release",
            "tap" => "tap",
            _ => "contact"
        };

        int clampedX = Math.Clamp(xNormalized, 0, 65535);
        int clampedY = Math.Clamp(yNormalized, 0, 65535);
        var payload = new
        {
            type = op,
            x = clampedX,
            y = clampedY
        };
        return await PostJsonAsync("/mirror/touch", payload, ct);
    }

    /// <summary>
    /// Mở App Switcher (Đa nhiệm iOS): Nhấn đúp nút Home qua HID chuẩn,
    /// phương pháp tương thích 100% trên toàn bộ các dòng iPhone iOS 17+.
    /// </summary>
    public async Task OpenAppSwitcherGestureAsync(CancellationToken ct = default)
    {
        await SendHardwareButtonAsync("home", "press", ct);
        await Task.Delay(110, ct);
        await SendHardwareButtonAsync("home", "press", ct);
    }

    /// <summary>
    /// Gọi POST /mirror/pli để yêu cầu iPhone gửi lại khung hình chính (Keyframe / IDR).
    /// </summary>
    public async Task<bool> RequestKeyframePliAsync(CancellationToken ct = default)
    {
        try
        {
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
            using var content = new StringContent("{}", Encoding.UTF8, "application/json");
            using var response = await _httpClient.PostAsync("/mirror/pli", content, linkedCts.Token);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Đồng bộ văn bản từ máy tính sang Clipboard của iPhone qua endpoint POST /mirror/clipboard.
    /// </summary>
    public async Task<bool> SetClipboardTextAsync(string text, CancellationToken ct = default)
    {
        return await PostJsonAsync("/mirror/clipboard", new { text }, ct);
    }

    /// <summary>
    /// Gửi danh sách mã phím HID (usages) tới iPhone qua endpoint POST /mirror/key.
    /// </summary>
    public async Task<bool> SendHidKeyUsagesAsync(int[] usages, CancellationToken ct = default)
    {
        return await PostJsonAsync("/mirror/key", new { usages }, ct);
    }

    /// <summary>
    /// Gửi văn bản tiếng Việt hoặc văn bản bất kỳ vào iPhone bằng cách đưa vào Clipboard và phát lệnh Cmd+V dán ngay lập tức.
    /// Khắc phục 100% tình trạng gõ dấu tiếng Việt (UniKey/EVKey) bị nuốt chữ hoặc thành "chvo".
    /// </summary>
    public async Task<bool> SendPasteTextAsync(string text, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(text)) return false;
        bool ok = await SetClipboardTextAsync(text, ct);
        if (!ok) return false;
        await Task.Delay(35, ct);
        // Cmd = 0xe3, V = 0x19
        await SendHidKeyUsagesAsync(new[] { 0xe3, 0x19 }, ct);
        await Task.Delay(40, ct);
        await SendHidKeyUsagesAsync(Array.Empty<int>(), ct);
        return true;
    }

    /// <summary>
    /// Chuyển đổi đầu ra âm thanh: "iphone" (phát qua loa iPhone) hoặc "windows" (truyền lên máy tính qua /audio.bin).
    /// </summary>
    public async Task<bool> SetAudioModeAsync(string mode, CancellationToken ct = default)
    {
        return await PostJsonAsync("/api/audio", new { mode }, ct);
    }

    private async Task<bool> PostJsonAsync<T>(string path, T payload, CancellationToken ct)
    {
        try
        {
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
            string json = JsonSerializer.Serialize(payload, JsonOptions);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var response = await _httpClient.PostAsync(path, content, linkedCts.Token);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[1pmBackendService] POST {path} lỗi: {ex.Message}");
            return false;
        }
    }

    public static string? FindRepositoryRoot(string? overridePath = null)
    {
        if (!string.IsNullOrWhiteSpace(overridePath) &&
            File.Exists(Path.Combine(overridePath, "handset", "server.py")))
        {
            return overridePath;
        }

        // 1. Dò ngược từ thư mục ứng dụng WinUI 3 lên các thư mục cha
        string? current = AppContext.BaseDirectory;
        string[] engineFolderNames =
        [
            "iphone-mirror-engine",
            "iphone-mirror-core",
            "iphone-mirror-service",
            "iphone-mirror-windows",
            "1pm-iphone-mirror-windows",
            "pane-iphone-mirror-windows"
        ];

        for (int depth = 0; depth < 8 && !string.IsNullOrEmpty(current); depth++)
        {
            // Kiểm tra trực tiếp handset/server.py
            if (File.Exists(Path.Combine(current, "handset", "server.py")))
            {
                return current;
            }
            // Kiểm tra các thư mục con engine
            foreach (string sub in engineFolderNames)
            {
                string candidate = Path.Combine(current, sub);
                if (File.Exists(Path.Combine(candidate, "handset", "server.py")))
                {
                    return candidate;
                }
            }

            current = Directory.GetParent(current)?.FullName;
        }

        // 2. Kiểm tra thư mục làm việc hiện tại (Working Directory)
        string cwd = Directory.GetCurrentDirectory();
        if (File.Exists(Path.Combine(cwd, "handset", "server.py")))
        {
            return cwd;
        }
        foreach (string sub in engineFolderNames)
        {
            string candidate = Path.Combine(cwd, sub);
            if (File.Exists(Path.Combine(candidate, "handset", "server.py")))
            {
                return candidate;
            }
        }

        // 3. Tự động tìm trong Downloads, Desktop, UserProfile
        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string[] searchBases =
        [
            Path.Combine(userProfile, "Downloads"),
            Path.Combine(userProfile, "Downloads", "1pm"),
            Path.Combine(userProfile, "Downloads", "PaneWinUI3"),
            Path.Combine(userProfile, "Downloads", "PaneWinUI3", "PaneWinUI"),
            Path.Combine(userProfile, "Desktop"),
            userProfile
        ];

        string[] folderNames =
        [
            "iphone-mirror-engine",
            "iphone-mirror-core",
            "iphone-mirror-service",
            "iphone-mirror-windows",
            "1pm-iphone-mirror-windows",
            "pane-iphone-mirror-windows",
            "pane-iphone-mirror-windows-main",
            "pane-iphone-mirror-windows-master"
        ];

        foreach (string baseDir in searchBases)
        {
            if (string.IsNullOrEmpty(baseDir) || !Directory.Exists(baseDir)) continue;

            if (File.Exists(Path.Combine(baseDir, "handset", "server.py")))
            {
                return baseDir;
            }

            foreach (string name in folderNames)
            {
                string candidate = Path.Combine(baseDir, name);
                if (File.Exists(Path.Combine(candidate, "handset", "server.py")))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    public static string FindPythonExecutable(string? repoRoot = null)
    {
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var candidatePaths = new List<string>();

        if (!string.IsNullOrEmpty(repoRoot))
        {
            candidatePaths.Add(Path.Combine(repoRoot, ".venv", "Scripts", "python.exe"));
            candidatePaths.Add(Path.Combine(repoRoot, "venv", "Scripts", "python.exe"));
        }
        else
        {
            string? detected = FindRepositoryRoot();
            if (!string.IsNullOrEmpty(detected))
            {
                candidatePaths.Add(Path.Combine(detected, ".venv", "Scripts", "python.exe"));
                candidatePaths.Add(Path.Combine(detected, "venv", "Scripts", "python.exe"));
            }
        }

        candidatePaths.Add(Path.Combine(localAppData, "Python", "bin", "python.exe"));
        candidatePaths.Add(Path.Combine(localAppData, "Python", "pythoncore-3.15-64", "python.exe"));

        foreach (string path in candidatePaths)
        {
            if (File.Exists(path)) return path;
        }

        // Tìm trong AppData\Local\Python
        string pythonDir = Path.Combine(localAppData, "Python");
        if (Directory.Exists(pythonDir))
        {
            foreach (string sub in Directory.GetDirectories(pythonDir))
            {
                string py = Path.Combine(sub, "python.exe");
                if (File.Exists(py)) return py;
            }
        }

        // Tìm trong AppData\Local\Programs\Python
        string progPy = Path.Combine(localAppData, "Programs", "Python");
        if (Directory.Exists(progPy))
        {
            foreach (string sub in Directory.GetDirectories(progPy))
            {
                string py = Path.Combine(sub, "python.exe");
                if (File.Exists(py)) return py;
            }
        }

        // Tìm từ PATH (loại bỏ WindowsApps shim)
        string? pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrEmpty(pathEnv))
        {
            foreach (string folder in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                if (folder.Contains("WindowsApps", StringComparison.OrdinalIgnoreCase)) continue;
                string candidate = Path.Combine(folder, "python.exe");
                if (File.Exists(candidate)) return candidate;
            }
        }

        return "python";
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            if (_pythonServerProcess is { HasExited: false })
            {
                _pythonServerProcess.Kill(entireProcessTree: true);
                _pythonServerProcess.Dispose();
            }
        }
        catch
        {
            // Bỏ qua lỗi khi đóng tiến trình con
        }

        _httpClient.Dispose();
    }
}

public sealed class OnePMStatusResponse
{
    [JsonPropertyName("library")]
    public OnePMLibraryInfo? Library { get; set; }

    [JsonPropertyName("driver")]
    public OnePMDriverInfo? Driver { get; set; }

    [JsonPropertyName("devices")]
    public List<OnePMDeviceInfo>? Devices { get; set; }

    [JsonPropertyName("developerMode")]
    public bool? DeveloperMode { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }

    [JsonPropertyName("screen")]
    public OnePMScreenStatus? Screen { get; set; }

    [JsonPropertyName("wifi")]
    public OnePMWifiStatus? Wifi { get; set; }
}

public sealed class OnePMLibraryInfo
{
    [JsonPropertyName("ready")]
    public bool Ready { get; set; }

    [JsonPropertyName("version")]
    public string? Version { get; set; }
}

public sealed class OnePMDriverInfo
{
    [JsonPropertyName("ready")]
    public bool Ready { get; set; }

    [JsonPropertyName("paths")]
    public List<string>? Paths { get; set; }

    [JsonPropertyName("service")]
    public string? Service { get; set; }

    [JsonPropertyName("detail")]
    public string? Detail { get; set; }
}

public sealed class OnePMDeviceInfo
{
    [JsonPropertyName("udid")]
    public string? Udid { get; set; }

    [JsonPropertyName("connection")]
    public string? Connection { get; set; }

    [JsonPropertyName("deviceId")]
    public int? DeviceId { get; set; }

    [JsonIgnore]
    public string? FriendlyName { get; set; }
}

public sealed class OnePMDeviceDetailResponse
{
    [JsonPropertyName("udid")]
    public string? Udid { get; set; }

    [JsonPropertyName("paired")]
    public bool Paired { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }

    [JsonPropertyName("info")]
    public Dictionary<string, object>? Info { get; set; }

    [JsonIgnore]
    public string? DeviceName => Info != null && Info.TryGetValue("DeviceName", out var val) ? val?.ToString() : null;

    [JsonIgnore]
    public string? ProductVersion => Info != null && Info.TryGetValue("ProductVersion", out var val) ? val?.ToString() : null;
}

public sealed class OnePMScreenStatus
{
    [JsonPropertyName("phase")]
    public string? Phase { get; set; }

    [JsonIgnore]
    public bool Running => string.Equals(Phase, "running", StringComparison.OrdinalIgnoreCase);

    [JsonIgnore]
    public bool Starting => string.Equals(Phase, "starting", StringComparison.OrdinalIgnoreCase);

    [JsonIgnore]
    public bool HasError => string.Equals(Phase, "error", StringComparison.OrdinalIgnoreCase) || !string.IsNullOrWhiteSpace(Error);

    [JsonPropertyName("udid")]
    public string? Udid { get; set; }

    [JsonPropertyName("url")]
    public string? Url { get; set; }

    [JsonPropertyName("transport")]
    public string? Transport { get; set; }

    [JsonPropertyName("note")]
    public string? Note { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }

    [JsonPropertyName("audio")]
    public string? Audio { get; set; }
}

public sealed class OnePMWifiStatus
{
    [JsonPropertyName("paired")]
    public List<string>? Paired { get; set; }
}

public sealed class OnePMPairWifiResponse
{
    [JsonPropertyName("ok")]
    public bool Ok { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }

    [JsonPropertyName("paired")]
    public List<string>? Paired { get; set; }
}

