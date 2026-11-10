using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;

namespace OnePM.Services;

public enum PrerequisiteStatus
{
    Checking,
    Ready,
    Missing,
    Installing,
    Failed
}

public sealed class PrerequisiteItem
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Subtitle { get; set; } = string.Empty;
    public string IconGlyph { get; set; } = string.Empty;
    public PrerequisiteStatus Status { get; set; } = PrerequisiteStatus.Checking;
    public string StatusBadgeText { get; set; } = "Đang kiểm tra";
    public string StatusDetail { get; set; } = string.Empty;
    public bool CanAutoInstall { get; set; }
    public bool IsEssential { get; set; } = true;
}

/// <summary>
/// Dịch vụ kiểm tra và tự động cài đặt các thành phần tiên quyết (Onboarding)
/// gồm: Apple Devices (USB driver/usbmuxd), Microsoft Edge WebView2, Python Backend & CoreDevice,
/// và xác thực kết nối iPhone (Trust/Developer Mode).
/// </summary>
public sealed class PrerequisiteService
{
    private static readonly string SettingsFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "1pm");

    private static readonly string OnboardingSettingsFile = Path.Combine(SettingsFolder, "onboarding.json");

    public bool IsOnboardingCompleted()
    {
        try
        {
            if (File.Exists(OnboardingSettingsFile))
            {
                string json = File.ReadAllText(OnboardingSettingsFile);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("completed", out var comp))
                {
                    return comp.GetBoolean();
                }
            }

            // Fallback kiểm tra cấu hình cũ
            string oldSettings = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PaneWinUI", "onboarding.json");
            if (File.Exists(oldSettings))
            {
                string json = File.ReadAllText(oldSettings);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("completed", out var comp))
                {
                    bool isDone = comp.GetBoolean();
                    if (isDone) SetOnboardingCompleted(true);
                    return isDone;
                }
            }
        }
        catch { }
        return false;
    }

    public void SetOnboardingCompleted(bool completed)
    {
        try
        {
            Directory.CreateDirectory(SettingsFolder);
            string json = JsonSerializer.Serialize(new { completed, updatedAt = DateTime.UtcNow });
            File.WriteAllText(OnboardingSettingsFile, json);
        }
        catch { }
    }

    /// <summary>
    /// Kiểm tra toàn diện tất cả các thành phần tiên quyết của hệ thống.
    /// </summary>
    public async Task<List<PrerequisiteItem>> CheckAllAsync(OnePMStatusResponse? backendStatus = null)
    {
        var items = new List<PrerequisiteItem>();

        // 1. Apple Devices (Apple USB Driver, usbmuxd)
        var appleItem = await CheckAppleDevicesAsync(backendStatus);
        items.Add(appleItem);

        // 2. Microsoft Edge WebView2 Runtime
        var webViewItem = CheckWebView2();
        items.Add(webViewItem);

        // 3. Python Runtime & CoreDevice (pymobiledevice3, ifaddr)
        var pythonItem = await CheckPythonBackendAsync();
        items.Add(pythonItem);

        // 4. Mở khóa iPhone & Tin cậy máy tính (Trust)
        var trustItem = CheckIphoneConnection(backendStatus);
        items.Add(trustItem);

        // 5. Chế độ Nhà phát triển (Developer Mode trên iOS 17+)
        var devModeItem = CheckDeveloperMode(backendStatus);
        items.Add(devModeItem);

        return items;
    }

    /// <summary>
    /// Kiểm tra xem Apple Devices / Apple Mobile Device Support có trên máy không.
    /// </summary>
    public async Task<PrerequisiteItem> CheckAppleDevicesAsync(OnePMStatusResponse? status = null)
    {
        var item = new PrerequisiteItem
        {
            Id = "apple-devices",
            Title = "Dịch vụ Apple Devices",
            Subtitle = "Giao thức liên kết bảo mật và nhận diện iPhone",
            IconGlyph = "\uE7F4", // Điện thoại / Thiết bị
            CanAutoInstall = true,
            IsEssential = true
        };

        // 1. Kiểm tra nếu backend đã báo driver sẵn sàng
        if (status?.Driver?.Ready == true)
        {
            item.Status = PrerequisiteStatus.Ready;
            item.StatusBadgeText = "Đã sẵn sàng";
            item.StatusDetail = status.Driver.Detail ?? "Dịch vụ USB Apple đang hoạt động ổn định.";
            return item;
        }

        // 2. Thử kết nối tới cổng usbmuxd 127.0.0.1:27015
        bool muxListening = await ProbeTcpPortAsync("127.0.0.1", 27015, 350);
        if (muxListening)
        {
            item.Status = PrerequisiteStatus.Ready;
            item.StatusBadgeText = "Đã sẵn sàng";
            item.StatusDetail = "Dịch vụ usbmuxd đang lắng nghe trên cổng 27015.";
            return item;
        }

        // 3. Kiểm tra tiến trình AppleMobileDeviceProcess
        var procs = Process.GetProcessesByName("AppleMobileDeviceProcess");
        if (procs.Length > 0)
        {
            item.Status = PrerequisiteStatus.Ready;
            item.StatusBadgeText = "Đã sẵn sàng";
            item.StatusDetail = "Tiến trình Apple Mobile Device Process đang chạy.";
            return item;
        }

        // 4. Kiểm tra gói ứng dụng Apple Devices (Store)
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string windowsApps = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps");

        bool appxFound = false;
        try
        {
            if (Directory.Exists(windowsApps))
            {
                foreach (string d in Directory.GetDirectories(windowsApps, "*AppleDevices*"))
                {
                    appxFound = true;
                    break;
                }
            }
        }
        catch { }

        // 5. Kiểm tra thư mục Common Files của Apple
        string progFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string commonApple = Path.Combine(progFiles, "Common Files", "Apple", "Mobile Device Support");
        bool commonFound = Directory.Exists(commonApple);

        if (appxFound || commonFound)
        {
            item.Status = PrerequisiteStatus.Ready;
            item.StatusBadgeText = "Đã cài đặt";
            item.StatusDetail = "Đã phát hiện Apple Devices. Hãy cắm cáp iPhone để khởi động dịch vụ.";
            return item;
        }

        // Chưa có Apple Devices
        item.Status = PrerequisiteStatus.Missing;
        item.StatusBadgeText = "Chưa cài đặt";
        item.StatusDetail = "Cần cài đặt Apple Devices từ Microsoft Store để có driver kết nối iPhone.";
        return item;
    }

    /// <summary>
    /// Kiểm tra Microsoft Edge WebView2 Runtime.
    /// </summary>
    public PrerequisiteItem CheckWebView2()
    {
        var item = new PrerequisiteItem
        {
            Id = "webview2",
            Title = "Bộ giải mã WebCodecs",
            Subtitle = "Giải mã tăng tốc phần cứng HEVC 60fps siêu mượt",
            IconGlyph = "\uE774", // Web / Toàn cầu
            CanAutoInstall = true,
            IsEssential = true
        };

        try
        {
            string version = CoreWebView2Environment.GetAvailableBrowserVersionString();
            if (!string.IsNullOrEmpty(version))
            {
                item.Status = PrerequisiteStatus.Ready;
                item.StatusBadgeText = "Đã sẵn sàng";
                item.StatusDetail = $"Phiên bản: {version}";
                return item;
            }
        }
        catch { }

        item.Status = PrerequisiteStatus.Missing;
        item.StatusBadgeText = "Chưa có";
        item.StatusDetail = "Cần WebView2 Runtime để giải mã và hiển thị màn hình iPhone.";
        return item;
    }

    /// <summary>
    /// Kiểm tra Python Runtime và các thư viện pymobiledevice3, ifaddr.
    /// </summary>
    public async Task<PrerequisiteItem> CheckPythonBackendAsync()
    {
        var item = new PrerequisiteItem
        {
            Id = "python-backend",
            Title = "Dịch vụ phản chiếu màn hình",
            Subtitle = "Xử lý đường truyền không dây Wi-Fi Bonjour & cáp USB",
            IconGlyph = "\uE7BE", // Code / Công cụ
            CanAutoInstall = true,
            IsEssential = true
        };

        string pythonExe = OnePMBackendService.FindPythonExecutable();
        bool pythonWorks = false;
        string detail = "";

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = pythonExe,
                Arguments = "-c \"import pymobiledevice3, ifaddr; print('READY')\"",
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var p = Process.Start(psi);
            if (p != null)
            {
                string output = await p.StandardOutput.ReadToEndAsync();
                await p.WaitForExitAsync();
                if (output.Contains("READY"))
                {
                    pythonWorks = true;
                    detail = $"Đã tích hợp ({Path.GetFileName(pythonExe)} - pymobiledevice3 sẵn sàng)";
                }
            }
        }
        catch (Exception ex)
        {
            detail = ex.Message;
        }

        if (pythonWorks)
        {
            item.Status = PrerequisiteStatus.Ready;
            item.StatusBadgeText = "Đã sẵn sàng";
            item.StatusDetail = detail;
        }
        else
        {
            item.Status = PrerequisiteStatus.Missing;
            item.StatusBadgeText = "Thiếu thư viện";
            item.StatusDetail = "Cần cài đặt/cập nhật pymobiledevice3 và ifaddr cho Python.";
        }

        return item;
    }

    /// <summary>
    /// Kiểm tra kết nối iPhone và trạng thái Tin cậy (Trust).
    /// </summary>
    public PrerequisiteItem CheckIphoneConnection(OnePMStatusResponse? status)
    {
        var item = new PrerequisiteItem
        {
            Id = "iphone-trust",
            Title = "Tin cậy thiết bị iPhone",
            Subtitle = "Mở khóa màn hình và xác nhận 'Tin cậy máy tính này'",
            IconGlyph = "\uE8EF", // Liên kết / Kết nối
            CanAutoInstall = false,
            IsEssential = false
        };

        if (status?.Devices != null && status.Devices.Count > 0)
        {
            string udid = status.Devices[0].Udid ?? "";
            string conn = status.Devices[0].Connection ?? "USB";
            item.Status = PrerequisiteStatus.Ready;
            item.StatusBadgeText = "Đã kết nối";
            item.StatusDetail = $"Phát hiện iPhone ({conn}) - Đã cấp quyền tin cậy.";
        }
        else
        {
            item.Status = PrerequisiteStatus.Missing;
            item.StatusBadgeText = "Chờ iPhone";
            item.StatusDetail = "Hãy cắm cáp USB, mở khóa iPhone và bấm 'Tin cậy' khi có hộp thoại hỏi.";
        }

        return item;
    }

    /// <summary>
    /// Kiểm tra Chế độ Nhà phát triển (Developer Mode) trên iOS 17+.
    /// </summary>
    public PrerequisiteItem CheckDeveloperMode(OnePMStatusResponse? status)
    {
        var item = new PrerequisiteItem
        {
            Id = "developer-mode",
            Title = "Chế độ Nhà phát triển (iOS 17+)",
            Subtitle = "Cài đặt > Quyền riêng tư & Bảo mật > Chế độ Nhà phát triển",
            IconGlyph = "\uE839", // Bảo mật / Khiên
            CanAutoInstall = false,
            IsEssential = false
        };

        if (status?.DeveloperMode == true)
        {
            item.Status = PrerequisiteStatus.Ready;
            item.StatusBadgeText = "Đã kích hoạt";
            item.StatusDetail = "Chế độ nhà phát triển đã bật trên iPhone.";
        }
        else if (status?.DeveloperMode == false)
        {
            item.Status = PrerequisiteStatus.Missing;
            item.StatusBadgeText = "Chưa bật";
            item.StatusDetail = "Mở Cài đặt > Quyền riêng tư & Bảo mật > Chế độ Nhà phát triển trên iPhone, bật và khởi động lại máy.";
        }
        else
        {
            item.Status = PrerequisiteStatus.Checking;
            item.StatusBadgeText = "Kiểm tra sau";
            item.StatusDetail = "Sẽ tự động kiểm tra ngay khi iPhone được cắm cáp.";
        }

        return item;
    }

    /// <summary>
    /// Tự động cài đặt thành phần bị thiếu theo Id.
    /// </summary>
    public async Task<bool> InstallItemAsync(string id, Action<string>? onProgress = null)
    {
        switch (id)
        {
            case "apple-devices":
                return await InstallAppleDevicesAsync(onProgress);

            case "webview2":
                return await InstallWebView2Async(onProgress);

            case "python-backend":
                return await InstallPythonDependenciesAsync(onProgress);

            default:
                return false;
        }
    }

    /// <summary>
    /// Tự động cài đặt Apple Devices từ Microsoft Store thông qua winget (Id: 9NP83LWLPZ9K).
    /// </summary>
    private async Task<bool> InstallAppleDevicesAsync(Action<string>? onProgress)
    {
        onProgress?.Invoke("Đang tải và cài đặt Apple Devices từ Microsoft Store qua winget...");

        // Chạy lệnh winget cài đặt Apple Devices (Store ID: 9NP83LWLPZ9K)
        var psi = new ProcessStartInfo
        {
            FileName = "winget",
            Arguments = "install --id 9NP83LWLPZ9K --source msstore --accept-package-agreements --accept-source-agreements --silent",
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        try
        {
            using var proc = Process.Start(psi);
            if (proc != null)
            {
                proc.OutputDataReceived += (_, e) =>
                {
                    if (!string.IsNullOrWhiteSpace(e.Data))
                    {
                        onProgress?.Invoke(e.Data.Trim());
                    }
                };
                proc.BeginOutputReadLine();
                await proc.WaitForExitAsync();
            }
        }
        catch (Exception ex)
        {
            onProgress?.Invoke($"Lỗi winget: {ex.Message}. Đang mở Microsoft Store...");
        }

        // Kiểm tra lại xem đã có Apple Devices chưa
        var checkResult = await CheckAppleDevicesAsync();
        if (checkResult.Status == PrerequisiteStatus.Ready)
        {
            onProgress?.Invoke("Cài đặt Apple Devices thành công!");
            return true;
        }

        // Fallback: Mở trang Apple Devices trên ứng dụng Microsoft Store để người dùng bấm Get/Cài đặt
        onProgress?.Invoke("Đang mở trang Apple Devices trên Microsoft Store...");
        try
        {
            Process.Start(new ProcessStartInfo("ms-windows-store://pdp/?ProductId=9NP83LWLPZ9K")
            {
                UseShellExecute = true
            });
        }
        catch { }

        // Chờ người dùng cài đặt xong tối đa 60 giây
        for (int i = 0; i < 30; i++)
        {
            await Task.Delay(2000);
            checkResult = await CheckAppleDevicesAsync();
            if (checkResult.Status == PrerequisiteStatus.Ready)
            {
                onProgress?.Invoke("Đã phát hiện Apple Devices được cài đặt!");
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Tự động cài đặt Microsoft Edge WebView2 Runtime qua winget.
    /// </summary>
    private async Task<bool> InstallWebView2Async(Action<string>? onProgress)
    {
        onProgress?.Invoke("Đang cài đặt Microsoft Edge WebView2 Runtime qua winget...");
        var psi = new ProcessStartInfo
        {
            FileName = "winget",
            Arguments = "install Microsoft.EdgeWebView2Runtime --accept-package-agreements --accept-source-agreements --silent",
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        try
        {
            using var proc = Process.Start(psi);
            if (proc != null)
            {
                await proc.WaitForExitAsync();
            }
        }
        catch (Exception ex)
        {
            onProgress?.Invoke($"Lỗi: {ex.Message}");
        }

        var res = CheckWebView2();
        return res.Status == PrerequisiteStatus.Ready;
    }

    /// <summary>
    /// Tự động cài đặt các thư viện Python: pymobiledevice3, ifaddr.
    /// </summary>
    private async Task<bool> InstallPythonDependenciesAsync(Action<string>? onProgress)
    {
        string pythonExe = OnePMBackendService.FindPythonExecutable();
        onProgress?.Invoke($"Đang cài đặt pymobiledevice3 và ifaddr bằng pip ({Path.GetFileName(pythonExe)})...");

        var psi = new ProcessStartInfo
        {
            FileName = pythonExe,
            Arguments = "-m pip install --upgrade pymobiledevice3 ifaddr",
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        try
        {
            using var proc = Process.Start(psi);
            if (proc != null)
            {
                proc.OutputDataReceived += (_, e) =>
                {
                    if (!string.IsNullOrWhiteSpace(e.Data))
                    {
                        onProgress?.Invoke(e.Data.Trim());
                    }
                };
                proc.BeginOutputReadLine();
                await proc.WaitForExitAsync();
            }
        }
        catch (Exception ex)
        {
            onProgress?.Invoke($"Lỗi pip: {ex.Message}");
        }

        var res = await CheckPythonBackendAsync();
        return res.Status == PrerequisiteStatus.Ready;
    }

    private static async Task<bool> ProbeTcpPortAsync(string host, int port, int timeoutMs)
    {
        try
        {
            using var client = new TcpClient();
            var connectTask = client.ConnectAsync(host, port);
            var completed = await Task.WhenAny(connectTask, Task.Delay(timeoutMs));
            return completed == connectTask && client.Connected;
        }
        catch
        {
            return false;
        }
    }
}

