using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.Web.WebView2.Core;
using OnePM.Services;
using Windows.Graphics;
using WinRT.Interop;

namespace OnePM;

/// <summary>
/// Cửa sổ chính iPhone Floating trên Windows (phong cách macOS Sequoia iPhone Mirroring).
/// Tương thích 100% với Python backend (handset.server) tại 127.0.0.1:8765.
/// </summary>
public sealed partial class MainWindow : Window
{
    private const int PillHeight = 34;
    private const int PillGap = 8;
    private const int PillWidth = 356;
    private const int PhoneWidth = 356;
    private const int PhoneHeight = 765;

    private const int PortraitWidth = PhoneWidth;
    private const int PortraitHeight = PillHeight + PillGap + PhoneHeight;
    private const int LandscapeWidth = PhoneHeight;
    private const int LandscapeHeight = PhoneWidth + PillHeight + PillGap;

    private readonly OnePMBackendService _backendService = new();
    private readonly PrerequisiteService _prerequisiteService = new();
    private readonly DispatcherTimer _statusPollTimer = new();
    private readonly Dictionary<string, string> _deviceNameCache = new();

    private AppWindow? _appWindow;
    private IntPtr _hwnd;
    private bool _isDraggingWindow;
    private POINT _dragStartCursorPos;
    private PointInt32 _dragStartWindowPos;

    private bool _isLandscape;
    private int _currentRotationDeg = 0; // 0, 90, 180, 270
    private string _audioMode = "iphone"; // "iphone" (loa iPhone) hoặc "windows" (loa máy tính Windows)
    private bool _isAlwaysOnTop = true;
    private bool _webViewInitialized;
    private bool _isStartingScreen;
    private string? _activeUdid;
    private string? _lastNavigatedUrl;
    private double _lastDpiScale = 1.0;

    public MainWindow()
    {
        InitializeComponent();
        ConfigureFloatingIphoneWindow();

        Activated += MainWindow_Activated;
        Closed += MainWindow_Closed;

        _statusPollTimer.Interval = TimeSpan.FromSeconds(2.0);
        _statusPollTimer.Tick += StatusPollTimer_Tick;
    }

    /// <summary>
    /// Cấu hình cửa sổ WinUI 3 dạng Floating không viền tiêu đề chuẩn macOS.
    /// Tự động thích ứng với DPI của mọi màn hình (100%, 125%, 150%, 175%, 200% trên 4K Windows 11).
    /// </summary>
    private void ConfigureFloatingIphoneWindow()
    {
        _hwnd = WindowNative.GetWindowHandle(this);
        WindowId windowId = Win32Interop.GetWindowIdFromWindow(_hwnd);
        _appWindow = AppWindow.GetFromWindowId(windowId);

        ExtendsContentIntoTitleBar = true;
        this.SystemBackdrop = null;

        if (_appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = true;
            presenter.IsAlwaysOnTop = _isAlwaysOnTop;
            presenter.SetBorderAndTitleBar(false, false);
        }

        // Tắt hoàn toàn style viền cửa sổ WS_BORDER, WS_CAPTION, WS_THICKFRAME
        const int GWL_STYLE = -16;
        const long WS_BORDER = 0x00800000L;
        const long WS_CAPTION = 0x00C00000L;
        const long WS_THICKFRAME = 0x00040000L;
        long style = GetWindowLongPtr(_hwnd, GWL_STYLE).ToInt64();
        style &= ~(WS_BORDER | WS_CAPTION | WS_THICKFRAME);
        SetWindowLongPtr(_hwnd, GWL_STYLE, new IntPtr(style));

        // Mở rộng frame DWM vào toàn bộ client area để DWM render trong suốt mượt mà, khử răng cưa
        MARGINS margins = new MARGINS { cxLeftWidth = -1, cxRightWidth = -1, cyTopHeight = -1, cyBottomHeight = -1 };
        DwmExtendFrameIntoClientArea(_hwnd, ref margins);

        // Tắt viền DWM của Windows 11
        ApplyWindows11BorderlessChrome(_hwnd);

        // Khởi tạo kích thước theo đúng tỷ lệ DPI của màn hình (hỗ trợ 100%, 125%, 150%, 175%, 200% trên màn 4K)
        _lastDpiScale = GetDpiScale();
        UpdatePhysicalWindowLayout(PortraitWidth, PortraitHeight);
        PositionWindowNearRightEdge();

        // Lắng nghe thay đổi DPI khi kéo cửa sổ giữa các màn hình khác nhau hoặc thay đổi Display Scaling
        RootGrid.Loaded += (s, e) =>
        {
            if (RootGrid.XamlRoot != null)
            {
                _lastDpiScale = RootGrid.XamlRoot.RasterizationScale;
                RootGrid.XamlRoot.Changed += XamlRoot_Changed;
            }
        };
    }

    private void XamlRoot_Changed(XamlRoot sender, XamlRootChangedEventArgs args)
    {
        double currentScale = sender.RasterizationScale;
        if (Math.Abs(currentScale - _lastDpiScale) > 0.001)
        {
            _lastDpiScale = currentScale;
            bool isLandscape = _currentRotationDeg == 90 || _currentRotationDeg == 270;
            int currentW = isLandscape ? LandscapeWidth : PortraitWidth;
            int currentH = isLandscape ? LandscapeHeight : PortraitHeight;
            UpdatePhysicalWindowLayout(currentW, currentH);
        }
    }

    private void PositionWindowNearRightEdge()
    {
        if (_appWindow == null) return;
        DisplayArea displayArea = DisplayArea.GetFromWindowId(_appWindow.Id, DisplayAreaFallback.Primary);

        double scale = GetDpiScale();
        int physPillH = (int)Math.Round(PillHeight * scale);
        int physPillGap = (int)Math.Round(PillGap * scale);
        int physPhoneH = (int)Math.Round(PhoneHeight * scale);
        int physPhoneW = (int)Math.Round(PhoneWidth * scale);
        int physPillW = (int)Math.Round(PillWidth * scale);

        int physWindowW = Math.Max(physPillW, physPhoneW);
        int physWindowH = physPillH + physPillGap + physPhoneH;
        int marginX = (int)Math.Round(48 * scale);

        int x = displayArea.WorkArea.Width - physWindowW - marginX;
        int y = Math.Max((int)Math.Round(40 * scale), (displayArea.WorkArea.Height - physWindowH) / 2);
        _appWindow.Move(new PointInt32(x, y));
    }

    private async void MainWindow_Activated(object sender, WindowActivatedEventArgs args)
    {
        if (_webViewInitialized) return;
        _webViewInitialized = true;

        await InitializeBackendAndMirrorAsync();
        _statusPollTimer.Start();
    }

    private async Task InitializeBackendAndMirrorAsync()
    {
        // 0. Kiểm tra Onboarding: Nếu thiếu Apple Devices, WebView2 hoặc Python, hiển thị giao diện Onboarding để người dùng xem hoặc tự cài đặt
        bool onboardingNeeded = await CheckAndPromptOnboardingIfNeededAsync();
        if (onboardingNeeded)
        {
            return;
        }

        ConnectingSpinner.IsActive = true;
        OverlayTitleText.Text = "Đang khởi động Python Backend...";
        OverlaySubtitleText.Text = "Đang kết nối tới handset.server tại http://127.0.0.1:8765...";
        ConnectingOverlay.Visibility = Visibility.Visible;

        // 1. Khởi tạo WebView2 môi trường riêng với cờ tăng tốc phần cứng H.265 / HEVC WebCodecs
        try
        {
            if (MirrorWebView.CoreWebView2 == null)
            {
                await InitializeWebViewEnvironmentAsync();
            }
        }
        catch (Exception ex)
        {
            ConnectingSpinner.IsActive = false;
            OverlayTitleText.Text = "Lỗi khởi tạo WebView2";
            OverlaySubtitleText.Text = ex.Message;
            return;
        }

        // 2. Đảm bảo Python backend (handset.server) đang chạy
        bool backendUp = await _backendService.EnsureBackendRunningAsync();
        if (!backendUp)
        {
            ConnectingSpinner.IsActive = false;
            StatusDot.Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 69, 58));
            DeviceTitleText.Text = "Chưa bật Backend";
            OverlayTitleText.Text = "Python Backend (127.0.0.1:8765) chưa chạy";

            string detail = _backendService.LastErrorMessage;
            OverlaySubtitleText.Text = string.IsNullOrEmpty(detail)
                ? "Hãy mở PowerShell trong thư mục của 1pm và chạy: python -m handset.server"
                : detail;
            return;
        }

        await RefreshDeviceAndStreamStateAsync(forceStart: true);
    }

    /// <summary>
    /// Khởi tạo WebView2 với các cờ Chromium tối ưu cho bộ giải mã WebCodecs H.265 60fps.
    /// </summary>
    private async Task InitializeWebViewEnvironmentAsync()
    {
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string userDataFolder = Path.Combine(localAppData, "1pm", "WebView2Profile");
        Directory.CreateDirectory(userDataFolder);

        // Thiết lập biến môi trường chuẩn của Microsoft Edge WebView2 để bật tăng tốc giải mã phần cứng H.265 / HEVC
        Environment.SetEnvironmentVariable(
            "WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS",
            "--ignore-gpu-blocklist --enable-features=HardwareAcceleratedVideoDecode,PlatformHEVCDecoderSupport --disable-features=AudioServiceOutOfProcess");

        try
        {
            var envOptions = new CoreWebView2EnvironmentOptions
            {
                AdditionalBrowserArguments = "--ignore-gpu-blocklist --enable-features=HardwareAcceleratedVideoDecode,PlatformHEVCDecoderSupport --disable-features=AudioServiceOutOfProcess"
            };
            CoreWebView2Environment env = await CoreWebView2Environment.CreateWithOptionsAsync(
                null,
                userDataFolder,
                envOptions);

            await MirrorWebView.EnsureCoreWebView2Async(env);
        }
        catch
        {
            // Fallback nếu CreateAsync với options thất bại
            await MirrorWebView.EnsureCoreWebView2Async();
        }

        if (MirrorWebView.CoreWebView2 is { } core)
        {
            if (core.Settings != null)
            {
                core.Settings.AreDefaultContextMenusEnabled = false;
                core.Settings.AreDevToolsEnabled = true;
                core.Settings.IsStatusBarEnabled = false;
                core.Settings.IsZoomControlEnabled = false;
            }

            core.PermissionRequested += (s, args) =>
            {
                if (args.PermissionKind == CoreWebView2PermissionKind.ClipboardRead)
                {
                    args.State = CoreWebView2PermissionState.Allow;
                }
            };

            core.NavigationCompleted += async (s, args) =>
            {
                if (args.IsSuccess)
                {
                    if (_audioMode == "windows")
                    {
                        try
                        {
                            await core.ExecuteScriptAsync(
                                "window.postMessage({ source: 'handset', cmd: 'set-audio', enabled: true }, '*');");
                        }
                        catch { }
                    }

                    if (_currentRotationDeg != 0)
                    {
                        try
                        {
                            await core.ExecuteScriptAsync(
                                $"window.postMessage({{ source: 'handset', cmd: 'set-rotation', deg: {_currentRotationDeg} }}, '*');");
                        }
                        catch { }
                    }
                }
            };
        }
    }

    private async void StatusPollTimer_Tick(object? sender, object e)
    {
        await RefreshDeviceAndStreamStateAsync(forceStart: false);
    }

    private async Task RefreshDeviceAndStreamStateAsync(bool forceStart)
    {
        if (OnboardingOverlay.Visibility == Visibility.Visible)
        {
            return;
        }

        OnePMStatusResponse? status = await _backendService.GetStatusAsync();
        if (status == null)
        {
            StatusDot.Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 69, 58));
            DeviceTitleText.Text = "Mất kết nối Backend";
            BackendStatusLabel.Text = "Backend: Chưa phản hồi (127.0.0.1:8765)";
            return;
        }

        string repoInfo = !string.IsNullOrEmpty(_backendService.DetectedRepoRoot)
            ? $" • Root: {Path.GetFileName(_backendService.DetectedRepoRoot)}"
            : "";
        BackendStatusLabel.Text = $"Backend: Online (127.0.0.1:8765){repoInfo}";

        // 1. Tổng hợp danh sách thiết bị từ USB (status.Devices) và Wi-Fi đã ghép (status.Wifi.Paired)
        var allDevices = ConsolidateDevices(status);
        UpdateDeviceComboBox(allDevices);

        // 2. Xác định thiết bị đang chọn
        bool isNewDeviceDetected = string.IsNullOrEmpty(_activeUdid) && allDevices.Count > 0;
        if (isNewDeviceDetected)
        {
            _activeUdid = allDevices[0].Udid;
        }

        string deviceDisplayName = GetDeviceDisplayName(_activeUdid);
        string selectedTransport = GetSelectedTransportTag();

        // 3. Xử lý trạng thái luồng Mirror từ handset.screen
        OnePMScreenStatus? screen = status.Screen;
        bool isStreamRunning = screen?.Running == true;
        bool isStreamStarting = screen?.Starting == true;

        if (isStreamRunning)
        {
            // Kiểm tra xem transport đang chạy có khớp với transport người dùng đã chọn không
            bool transportMismatch = selectedTransport != "auto" &&
                                     !string.IsNullOrEmpty(screen?.Transport) &&
                                     screen.Transport != selectedTransport;

            if (!transportMismatch)
            {
                _isStartingScreen = false;
                StatusDot.Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 48, 209, 88)); // Xanh lá iOS
                ConnectingOverlay.Visibility = Visibility.Collapsed;

                string streamTransportText = screen?.Transport == "wifi" ? "Wi-Fi" : "USB";
                DeviceTitleText.Text = $"{deviceDisplayName} ({streamTransportText})";

                if (!string.IsNullOrEmpty(screen?.Audio) && screen.Audio != _audioMode)
                {
                    _audioMode = screen.Audio;
                    UpdateAudioUi(_audioMode);
                    if (MirrorWebView.CoreWebView2 != null)
                    {
                        try
                        {
                            bool enabled = _audioMode == "windows";
                            _ = MirrorWebView.CoreWebView2.ExecuteScriptAsync(
                                $"window.postMessage({{ source: 'handset', cmd: 'set-audio', enabled: {(enabled ? "true" : "false")} }}, '*');");
                        }
                        catch { }
                    }
                }

                // Điều hướng WebView2 tới mirror.html hoặc kích hoạt lại mirror.js nếu cần
                string mirrorUrl = _backendService.MirrorPageUrl;
                if (MirrorWebView.Source == null || _lastNavigatedUrl != mirrorUrl)
                {
                    _lastNavigatedUrl = mirrorUrl;
                    MirrorWebView.Source = new Uri(mirrorUrl);
                }
                else
                {
                    // Nếu trang mirror.html đã tải từ trước nhưng luồng stream vừa mới phục hồi, kích hoạt lại beginRun()
                    try
                    {
                        if (MirrorWebView.CoreWebView2 != null)
                        {
                            await MirrorWebView.CoreWebView2.ExecuteScriptAsync(
                                "if (typeof beginRun === 'function' && (typeof isStreamActive === 'function' ? !isStreamActive() : false)) { beginRun(); }");
                        }
                    }
                    catch
                    {
                        // Bỏ qua lỗi script tạm thời
                    }
                }
                return;
            }
        }

        // Luồng chưa chạy: Chỉ khởi động stream khi:
        // 1. forceStart == true (app vừa mở, người dùng nhấn nút 'Thử kết nối ngay' / 'Khởi động lại', hoặc đổi transport)
        // 2. Vừa phát hiện thiết bị mới lần đầu (isNewDeviceDetected == true)
        // Tuyệt đối KHÔNG tự ý gọi StartScreenAsync trong vòng lặp Timer định kỳ 2s nếu luồng đang kết nối hoặc tạm chập chờn,
        // để tránh việc hủy và tái kết nối liên tục làm stream Wi-Fi bị đứt quãng!
        if (!isStreamStarting && !_isStartingScreen && (forceStart || isNewDeviceDetected))
        {
            if (!string.IsNullOrEmpty(_activeUdid) || allDevices.Count > 0)
            {
                string targetUdid = !string.IsNullOrEmpty(_activeUdid) ? _activeUdid : allDevices[0].Udid!;
                _activeUdid = targetUdid;
                _isStartingScreen = true;

                ConnectingSpinner.IsActive = true;
                ConnectingOverlay.Visibility = Visibility.Visible;
                OverlayTitleText.Text = "Đang kết nối iPhone...";
                OverlaySubtitleText.Text = selectedTransport == "wifi"
                    ? "Đang tìm kiếm iPhone trên mạng Wi-Fi (Bonjour mDNS)... Hãy mở khóa màn hình iPhone."
                    : "Đang mở đường truyền USB RemoteXPC... Hãy mở khóa màn hình iPhone.";

                await _backendService.StartScreenAsync(targetUdid, selectedTransport);

                // Lấy lại trạng thái sau khi gọi start
                status = await _backendService.GetStatusAsync();
                screen = status?.Screen;
                isStreamRunning = screen?.Running == true;
                isStreamStarting = screen?.Starting == true;
            }
        }

        if (isStreamRunning)
        {
            _isStartingScreen = false;
            StatusDot.Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 48, 209, 88));
            ConnectingOverlay.Visibility = Visibility.Collapsed;
            string streamTransportText = screen?.Transport == "wifi" ? "Wi-Fi" : "USB";
            DeviceTitleText.Text = $"{deviceDisplayName} ({streamTransportText})";

            if (MirrorWebView.Source == null || _lastNavigatedUrl != _backendService.MirrorPageUrl)
            {
                _lastNavigatedUrl = _backendService.MirrorPageUrl;
                MirrorWebView.Source = new Uri(_backendService.MirrorPageUrl);
            }
            return;
        }

        if (isStreamStarting || _isStartingScreen)
        {
            StatusDot.Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 159, 10)); // Cam
            ConnectingSpinner.IsActive = true;
            ConnectingOverlay.Visibility = Visibility.Visible;
            OverlayTitleText.Text = "Đang kết nối iPhone...";

            string currentTransportText = (screen?.Transport ?? selectedTransport) == "wifi" ? "Wi-Fi" : "USB";
            DeviceTitleText.Text = $"{deviceDisplayName} ({currentTransportText})";

            string note = screen?.Note ?? (selectedTransport == "wifi"
                ? "Đang tìm kiếm iPhone trên mạng Wi-Fi... Đảm bảo iPhone và PC cùng Wi-Fi và iPhone đang mở khóa."
                : "Đang kết nối qua cáp USB... Đảm bảo đã cắm cáp và chọn 'Tin cậy máy tính'.");
            OverlaySubtitleText.Text = note;
            return;
        }

        // Trường hợp gặp lỗi từ Python backend (screen error hoặc status error)
        string? errorMsg = screen?.Error ?? status?.Error;
        if (!string.IsNullOrWhiteSpace(errorMsg))
        {
            _isStartingScreen = false;
            StatusDot.Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 69, 58)); // Đỏ
            ConnectingSpinner.IsActive = false;
            ConnectingOverlay.Visibility = Visibility.Visible;
            OverlayTitleText.Text = "Không thể kết nối iPhone";

            string currentTransportText = (screen?.Transport ?? selectedTransport) == "wifi" ? "Wi-Fi" : "USB";
            DeviceTitleText.Text = $"{deviceDisplayName} ({currentTransportText})";

            OverlaySubtitleText.Text = errorMsg;
            return;
        }

        // Không có thiết bị nào phát hiện qua USB hay Wi-Fi
        if (allDevices.Count == 0)
        {
            _isStartingScreen = false;
            StatusDot.Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 159, 10));
            ConnectingSpinner.IsActive = false;
            ConnectingOverlay.Visibility = Visibility.Visible;
            DeviceTitleText.Text = "Chờ iPhone";
            OverlayTitleText.Text = "Chưa nhận diện được iPhone";
            OverlaySubtitleText.Text = "Hãy cắm cáp USB iPhone vào máy tính hoặc kết nối cùng mạng Wi-Fi (nếu đã ghép nối). Mở khóa màn hình iPhone và nhấn 'Trust This Computer' (Tin cậy).";
        }
    }

    /// <summary>
    /// Tổng hợp danh sách thiết bị từ danh sách USB và danh sách Wi-Fi đã ghép nối.
    /// </summary>
    private List<OnePMDeviceInfo> ConsolidateDevices(OnePMStatusResponse status)
    {
        var result = new List<OnePMDeviceInfo>();
        var seenUdids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Thiết bị đang cắm cáp USB
        if (status.Devices != null)
        {
            foreach (var dev in status.Devices)
            {
                if (!string.IsNullOrEmpty(dev.Udid) && seenUdids.Add(dev.Udid))
                {
                    dev.Connection = "USB";
                    dev.FriendlyName = GetDeviceDisplayName(dev.Udid);
                    result.Add(dev);
                }
            }
        }

        // Thiết bị đã ghép nối Wi-Fi (nhưng chưa hoặc không cắm cáp USB)
        if (status.Wifi?.Paired != null)
        {
            foreach (var pairedUdid in status.Wifi.Paired)
            {
                if (!string.IsNullOrEmpty(pairedUdid) && seenUdids.Add(pairedUdid))
                {
                    result.Add(new OnePMDeviceInfo
                    {
                        Udid = pairedUdid,
                        Connection = "Wi-Fi",
                        FriendlyName = GetDeviceDisplayName(pairedUdid)
                    });
                }
            }
        }

        // Tự động truy vấn thông tin chi tiết (tên thật iPhone) nếu chưa có trong cache
        foreach (var dev in result)
        {
            if (dev.Udid != null && !_deviceNameCache.ContainsKey(dev.Udid) && dev.Connection == "USB")
            {
                _ = FetchFriendlyDeviceNameAsync(dev.Udid);
            }
        }

        return result;
    }

    private async Task FetchFriendlyDeviceNameAsync(string udid)
    {
        try
        {
            var detail = await _backendService.GetDeviceInfoAsync(udid);
            if (!string.IsNullOrWhiteSpace(detail?.DeviceName))
            {
                _deviceNameCache[udid] = detail.DeviceName;
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (_activeUdid == udid)
                    {
                        DeviceTitleText.Text = detail.DeviceName;
                    }
                });
            }
        }
        catch
        {
            // Bỏ qua lỗi nếu không đọc được lockdown
        }
    }

    private string GetDeviceDisplayName(string? udid)
    {
        if (string.IsNullOrEmpty(udid)) return "iPhone";
        if (_deviceNameCache.TryGetValue(udid, out string? cachedName) && !string.IsNullOrWhiteSpace(cachedName))
        {
            return cachedName;
        }

        string shortId = udid.Length > 8 ? $"{udid[..8]}…" : udid;
        return $"iPhone ({shortId})";
    }

    private void UpdateDeviceComboBox(IReadOnlyList<OnePMDeviceInfo> devices)
    {
        // Kiểm tra xem danh sách có thay đổi không để tránh giật UI
        if (DeviceComboBox.Items.Count == devices.Count)
        {
            bool match = true;
            for (int i = 0; i < devices.Count; i++)
            {
                if (DeviceComboBox.Items[i] is ComboBoxItem cbi && (string?)cbi.Tag != devices[i].Udid)
                {
                    match = false;
                    break;
                }
            }
            if (match) return;
        }

        DeviceComboBox.Items.Clear();
        foreach (OnePMDeviceInfo dev in devices)
        {
            string name = dev.FriendlyName ?? GetDeviceDisplayName(dev.Udid);
            string conn = dev.Connection ?? "USB";
            var item = new ComboBoxItem
            {
                Content = $"{name} • {conn}",
                Tag = dev.Udid
            };
            DeviceComboBox.Items.Add(item);
        }

        if (DeviceComboBox.Items.Count > 0)
        {
            int index = -1;
            if (!string.IsNullOrEmpty(_activeUdid))
            {
                for (int i = 0; i < DeviceComboBox.Items.Count; i++)
                {
                    if (DeviceComboBox.Items[i] is ComboBoxItem item && (string?)item.Tag == _activeUdid)
                    {
                        index = i;
                        break;
                    }
                }
            }

            DeviceComboBox.SelectedIndex = index >= 0 ? index : 0;
        }
    }

    private string GetSelectedTransportTag()
    {
        if (TransportComboBox.SelectedItem is ComboBoxItem item && item.Tag is string tag)
        {
            return tag;
        }
        return "auto";
    }

    private void SelectTransportTag(string tag)
    {
        for (int i = 0; i < TransportComboBox.Items.Count; i++)
        {
            if (TransportComboBox.Items[i] is ComboBoxItem item && (string?)item.Tag == tag)
            {
                TransportComboBox.SelectedIndex = i;
                break;
            }
        }
    }

    private async void TransportComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_webViewInitialized) return;
        string newTransport = GetSelectedTransportTag();
        if (!string.IsNullOrEmpty(_activeUdid))
        {
            ConnectingSpinner.IsActive = true;
            ConnectingOverlay.Visibility = Visibility.Visible;
            OverlayTitleText.Text = "Đang đổi phương thức kết nối...";
            OverlaySubtitleText.Text = newTransport == "wifi"
                ? "Đang tìm kiếm iPhone trên mạng Wi-Fi (Bonjour mDNS)... Hãy mở khóa màn hình iPhone."
                : "Đang mở đường truyền USB RemoteXPC... Hãy mở khóa màn hình iPhone.";
            _isStartingScreen = true;
            await _backendService.StartScreenAsync(_activeUdid, newTransport);
            await RefreshDeviceAndStreamStateAsync(forceStart: false);
        }
    }

    private async void DeviceComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DeviceComboBox.SelectedItem is ComboBoxItem item && item.Tag is string udid)
        {
            if (_activeUdid != udid)
            {
                _activeUdid = udid;
                await RefreshDeviceAndStreamStateAsync(forceStart: true);
            }
        }
    }

    // ==================== HOVER THANH PILL BAR MACOS ====================

    private void RootGrid_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        FloatingHeaderPill.Opacity = 1.0f;
    }

    private void RootGrid_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (!_isDraggingWindow)
        {
            FloatingHeaderPill.Opacity = 0.9f;
        }
    }

    // ==================== KÉO THẢ CỬA SỔ FLOATING ====================

    private void DragRegion_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_appWindow == null || sender is not UIElement element) return;

        var point = e.GetCurrentPoint(element);
        if (point.Properties.IsLeftButtonPressed)
        {
            _isDraggingWindow = true;
            element.CapturePointer(e.Pointer);
            GetCursorPos(out _dragStartCursorPos);
            _dragStartWindowPos = _appWindow.Position;
        }
    }

    private void DragRegion_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_isDraggingWindow || _appWindow == null) return;

        if (GetCursorPos(out POINT currentCursor))
        {
            int dx = currentCursor.X - _dragStartCursorPos.X;
            int dy = currentCursor.Y - _dragStartCursorPos.Y;
            _appWindow.Move(new PointInt32(_dragStartWindowPos.X + dx, _dragStartWindowPos.Y + dy));
        }
    }

    private void DragRegion_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_isDraggingWindow || sender is not UIElement element) return;
        _isDraggingWindow = false;
        element.ReleasePointerCapture(e.Pointer);
    }

    // ==================== ĐIỀU KHIỂN NÚT VẬT LÝ & CỬ CHỈ IPHONE ====================

    private void CloseWindowBtn_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void MinimizeWindowBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_appWindow?.Presenter is OverlappedPresenter presenter)
        {
            presenter.Minimize();
        }
    }

    private void AlwaysOnTopBtn_Click(object sender, RoutedEventArgs e)
    {
        _isAlwaysOnTop = !_isAlwaysOnTop;
        if (_appWindow?.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsAlwaysOnTop = _isAlwaysOnTop;
        }

        AlwaysOnTopBtn.Background = new SolidColorBrush(
            _isAlwaysOnTop
                ? Windows.UI.Color.FromArgb(255, 40, 200, 64)
                : Windows.UI.Color.FromArgb(255, 110, 110, 115));
    }

    private async void HomeButton_Click(object sender, RoutedEventArgs e)
    {
        await _backendService.SendHardwareButtonAsync("home", "press");
    }

    private async void HomeIndicator_Tapped(object sender, TappedRoutedEventArgs e)
    {
        await _backendService.SendHardwareButtonAsync("home", "press");
    }

    private async void HomeIndicator_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        await _backendService.OpenAppSwitcherGestureAsync();
    }

    private async void DynamicIsland_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        await _backendService.SendHardwareButtonAsync("home", "press");
    }

    private async void AppSwitcherButton_Click(object sender, RoutedEventArgs e)
    {
        await _backendService.OpenAppSwitcherGestureAsync();
    }

    // ==================== BỘ ĐIỀU KHIỂN ÂM THANH (IPHONE <-> WINDOWS) ====================
    private async void AudioButton_Click(object sender, RoutedEventArgs e)
    {
        string targetMode = _audioMode == "windows" ? "iphone" : "windows";
        await SetAudioModeAsync(targetMode);
    }

    private async void AudioIphone_Click(object sender, RoutedEventArgs e)
    {
        await SetAudioModeAsync("iphone");
    }

    private async void AudioWindows_Click(object sender, RoutedEventArgs e)
    {
        await SetAudioModeAsync("windows");
    }

    private async void AudioOutputComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (AudioOutputComboBox.SelectedItem is ComboBoxItem item && item.Tag is string mode)
        {
            if (_audioMode != mode)
            {
                await SetAudioModeAsync(mode);
            }
        }
    }

    private void UpdateAudioUi(string mode)
    {
        bool isWin = mode == "windows";
        AudioIcon.Glyph = isWin ? "\uE767" : "\uE74F";
        AudioIcon.Foreground = isWin
            ? new SolidColorBrush(Windows.UI.Color.FromArgb(255, 10, 132, 255))
            : new SolidColorBrush(Windows.UI.Color.FromArgb(255, 208, 208, 213));

        string tip = isWin
            ? "Âm thanh: Đang truyền lên Windows (Nhấp để trả về loa iPhone • Chuột phải để chọn)"
            : "Âm thanh: Đang phát trên iPhone (Nhấp để truyền lên Windows • Chuột phải để chọn)";
        ToolTipService.SetToolTip(PillAudioBtn, tip);

        if (AudioOutputComboBox != null)
        {
            int targetIdx = isWin ? 1 : 0;
            if (AudioOutputComboBox.SelectedIndex != targetIdx)
            {
                AudioOutputComboBox.SelectedIndex = targetIdx;
            }
        }
    }

    private async Task SetAudioModeAsync(string mode)
    {
        _audioMode = mode == "windows" ? "windows" : "iphone";
        UpdateAudioUi(_audioMode);

        try
        {
            await _backendService.SetAudioModeAsync(_audioMode);
        }
        catch
        {
            // Bỏ qua lỗi backend nếu chưa sẵn sàng
        }

        if (MirrorWebView.CoreWebView2 != null)
        {
            try
            {
                bool enabled = _audioMode == "windows";
                await MirrorWebView.CoreWebView2.ExecuteScriptAsync(
                    $"window.postMessage({{ source: 'handset', cmd: 'set-audio', enabled: {(enabled ? "true" : "false")} }}, '*');");
            }
            catch
            {
                // Bỏ qua lỗi script tạm thời
            }
        }
    }

    // ==================== BỘ ĐIỀU KHIỂN XOAY MÀN HÌNH FULL 4 HƯỚNG ====================
    private async void RotateButton_Click(object sender, RoutedEventArgs e)
    {
        // Nhấp chuột trái: Xoay vòng tuần tự qua 4 góc (0° -> 90° -> 180° -> 270° -> 0°)
        int nextDeg = (_currentRotationDeg + 90) % 360;
        await RotateToAngleAsync(nextDeg);
    }

    private async void Rotate0_Click(object sender, RoutedEventArgs e) => await RotateToAngleAsync(0);
    private async void Rotate90_Click(object sender, RoutedEventArgs e) => await RotateToAngleAsync(90);
    private async void Rotate180_Click(object sender, RoutedEventArgs e) => await RotateToAngleAsync(180);
    private async void Rotate270_Click(object sender, RoutedEventArgs e) => await RotateToAngleAsync(270);

    private async Task RotateToAngleAsync(int deg)
    {
        _currentRotationDeg = ((deg % 360) + 360) % 360;
        bool isLandscape = _currentRotationDeg == 90 || _currentRotationDeg == 270;
        _isLandscape = isLandscape;

        int currentW = isLandscape ? LandscapeWidth : PortraitWidth;
        int currentH = isLandscape ? LandscapeHeight : PortraitHeight;

        OuterTitaniumFrame.Width = isLandscape ? LandscapeWidth : PhoneWidth;
        OuterTitaniumFrame.Height = isLandscape ? PhoneWidth : PhoneHeight;

        UpdatePhysicalWindowLayout(currentW, currentH);

        string angleName = _currentRotationDeg switch
        {
            90 => "90° (Ngang phải)",
            180 => "180° (Ngược)",
            270 => "270° (Ngang trái)",
            _ => "0° (Dọc chuẩn)"
        };
        ToolTipService.SetToolTip(PillRotateBtn, $"Xoay màn hình: {angleName}\n(Nhấp: xoay tiếp 90° • Chuột phải: chọn góc)");

        // Báo cho mirror.js cập nhật WebCodecs, Canvas và chuyển vị ma trận tọa độ chạm (Touch Coordinates)
        if (MirrorWebView.CoreWebView2 != null)
        {
            try
            {
                await MirrorWebView.CoreWebView2.ExecuteScriptAsync(
                    $"window.postMessage({{ source: 'handset', cmd: 'set-rotation', deg: {_currentRotationDeg} }}, '*');");
            }
            catch
            {
                // Bỏ qua lỗi script tạm thời
            }
        }
    }

    private async void VolumeUpButton_Click(object sender, RoutedEventArgs e)
    {
        await _backendService.SendHardwareButtonAsync("volume-up", "press");
    }

    private async void VolumeDownButton_Click(object sender, RoutedEventArgs e)
    {
        await _backendService.SendHardwareButtonAsync("volume-down", "press");
    }

    private async void PowerLockButton_Click(object sender, RoutedEventArgs e)
    {
        await _backendService.SendHardwareButtonAsync("lock", "press");
    }

    private async void SendPliBtn_Click(object sender, RoutedEventArgs e)
    {
        await _backendService.RequestKeyframePliAsync();
        if (MirrorWebView.CoreWebView2 != null)
        {
            try
            {
                await MirrorWebView.CoreWebView2.ExecuteScriptAsync(
                    "fetch('/mirror/pli', { method: 'POST', cache: 'no-store' }).catch(() => {});");
            }
            catch
            {
                // Bỏ qua
            }
        }
    }

    private void RestartStreamBtn_Click(object sender, RoutedEventArgs e)
    {
        ReconnectStreamBtn_Click(sender, e);
    }

    private async void ReconnectStreamBtn_Click(object sender, RoutedEventArgs e)
    {
        ConnectingSpinner.IsActive = true;
        ConnectingOverlay.Visibility = Visibility.Visible;
        OverlayTitleText.Text = "Đang khởi tạo lại phiên phản chiếu...";
        OverlaySubtitleText.Text = "Đang khởi động lại đường truyền stream...";

        if (MirrorWebView.CoreWebView2 == null)
        {
            try
            {
                await InitializeWebViewEnvironmentAsync();
            }
            catch (Exception ex)
            {
                ConnectingSpinner.IsActive = false;
                OverlayTitleText.Text = "Lỗi khởi tạo WebView2";
                OverlaySubtitleText.Text = ex.Message;
                return;
            }
        }

        _isStartingScreen = false;
        await _backendService.RestartScreenAsync(_activeUdid);

        string transport = GetSelectedTransportTag();
        await _backendService.StartScreenAsync(_activeUdid, transport);

        await Task.Delay(600);
        await RefreshDeviceAndStreamStateAsync(forceStart: true);

        if (MirrorWebView.CoreWebView2 != null)
        {
            try
            {
                await MirrorWebView.CoreWebView2.ExecuteScriptAsync(
                    "if (typeof beginRun === 'function') { beginRun(); } else { window.location.reload(); }");
            }
            catch
            {
                // Bỏ qua
            }
        }
    }

    private async void PairWifiBtn_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_activeUdid))
        {
            BackendStatusLabel.Text = "Hãy cắm cáp USB iPhone vào máy tính trước khi ghép Wi-Fi.";
            return;
        }

        PairWifiBtn.IsEnabled = false;
        PairWifiBtn.Content = "Đang ghép...";
        BackendStatusLabel.Text = "Đang thiết lập ghép nối Wi-Fi qua cáp USB... (Vui lòng chờ)";

        var result = await _backendService.PairWifiAsync(_activeUdid);

        PairWifiBtn.IsEnabled = true;
        PairWifiBtn.Content = "Ghép Wi-Fi";

        if (result?.Ok == true)
        {
            BackendStatusLabel.Text = "✓ Ghép Wi-Fi thành công! Bạn có thể rút cáp và chọn Wi-Fi.";
            SelectTransportTag("wifi");
            await RefreshDeviceAndStreamStateAsync(forceStart: true);
        }
        else
        {
            string err = result?.Error ?? "Không thể ghép nối Wi-Fi. Đảm bảo cáp USB đã cắm và iPhone đã mở khóa.";
            BackendStatusLabel.Text = $"Lỗi ghép Wi-Fi: {err}";
        }
    }

    private void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        _statusPollTimer.Stop();
        _backendService.Dispose();
    }

    // ==================== ONBOARDING & PREREQUISITE MANAGEMENT ====================

    private async Task<bool> CheckAndPromptOnboardingIfNeededAsync()
    {
        OnePMStatusResponse? backendStatus = null;
        try { backendStatus = await _backendService.GetStatusAsync(); } catch { }

        var items = await _prerequisiteService.CheckAllAsync(backendStatus);
        bool hasMissingEssential = items.Any(i => i.IsEssential && i.Status == PrerequisiteStatus.Missing);
        bool completed = _prerequisiteService.IsOnboardingCompleted();

        if (hasMissingEssential || !completed)
        {
            ShowOnboardingOverlay(items);
            return true;
        }
        return false;
    }

    private void ShowOnboardingOverlay(List<PrerequisiteItem>? items = null)
    {
        ConnectingOverlay.Visibility = Visibility.Collapsed;
        OnboardingOverlay.Visibility = Visibility.Visible;

        if (items != null)
        {
            RenderPrerequisiteCards(items);
        }
        else
        {
            _ = RefreshOnboardingListAsync();
        }
    }

    private async Task RefreshOnboardingListAsync()
    {
        OnePMStatusResponse? backendStatus = null;
        try { backendStatus = await _backendService.GetStatusAsync(); } catch { }

        var items = await _prerequisiteService.CheckAllAsync(backendStatus);
        RenderPrerequisiteCards(items);
    }

    private bool _hasMissingInstallable;
    private bool _hasMissingEssential;

    private void RenderPrerequisiteCards(List<PrerequisiteItem> items)
    {
        PrerequisitesListPanel.Children.Clear();

        _hasMissingInstallable = false;
        _hasMissingEssential = false;

        for (int i = 0; i < items.Count; i++)
        {
            var item = items[i];
            if (item.Status == PrerequisiteStatus.Missing)
            {
                if (item.CanAutoInstall) _hasMissingInstallable = true;
                if (item.IsEssential) _hasMissingEssential = true;
            }

            // Apple Inset TableView Row (chuẩn macOS / iOS Settings)
            var rowGrid = new Grid
            {
                Padding = new Thickness(14, 11, 14, 11),
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0)),
                ColumnDefinitions =
                {
                    new ColumnDefinition { Width = GridLength.Auto },
                    new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                    new ColumnDefinition { Width = GridLength.Auto }
                }
            };

            // Squircle icon theo chuẩn Apple SF Symbols palette
            Windows.UI.Color iconBgColor;
            Windows.UI.Color iconFgColor;

            switch (item.Id)
            {
                case "apple-devices":
                    iconBgColor = Windows.UI.Color.FromArgb(45, 10, 132, 255); // Apple Blue
                    iconFgColor = Windows.UI.Color.FromArgb(255, 10, 132, 255);
                    break;
                case "webview2":
                    iconBgColor = Windows.UI.Color.FromArgb(45, 94, 92, 230); // Apple Indigo
                    iconFgColor = Windows.UI.Color.FromArgb(255, 94, 92, 230);
                    break;
                case "python-backend":
                    iconBgColor = Windows.UI.Color.FromArgb(45, 48, 209, 88); // Apple Green
                    iconFgColor = Windows.UI.Color.FromArgb(255, 48, 209, 88);
                    break;
                case "iphone-trust":
                    iconBgColor = Windows.UI.Color.FromArgb(45, 255, 159, 10); // Apple Orange
                    iconFgColor = Windows.UI.Color.FromArgb(255, 255, 159, 10);
                    break;
                default:
                    iconBgColor = Windows.UI.Color.FromArgb(45, 142, 142, 147);
                    iconFgColor = Windows.UI.Color.FromArgb(255, 142, 142, 147);
                    break;
            }

            var iconSquircle = new Border
            {
                Width = 28,
                Height = 28,
                CornerRadius = new CornerRadius(7),
                Background = new SolidColorBrush(iconBgColor),
                Margin = new Thickness(0, 0, 10, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new FontIcon
                {
                    Glyph = item.IconGlyph,
                    FontSize = 13,
                    Foreground = new SolidColorBrush(iconFgColor)
                }
            };
            Grid.SetColumn(iconSquircle, 0);
            rowGrid.Children.Add(iconSquircle);

            // Tiêu đề & phụ đề
            var textStack = new StackPanel
            {
                Spacing = 2,
                VerticalAlignment = VerticalAlignment.Center
            };
            var titleBlock = new TextBlock
            {
                Text = item.Title,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                FontSize = 12.5,
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 245, 245, 247))
            };
            var subtitleBlock = new TextBlock
            {
                Text = item.Subtitle,
                FontSize = 10.5,
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 142, 142, 147)),
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxLines = 1
            };
            textStack.Children.Add(titleBlock);
            textStack.Children.Add(subtitleBlock);
            Grid.SetColumn(textStack, 1);
            rowGrid.Children.Add(textStack);

            // Trạng thái bên phải theo chuẩn Apple
            FrameworkElement statusElement;
            if (item.Status == PrerequisiteStatus.Ready)
            {
                statusElement = new Border
                {
                    Width = 20,
                    Height = 20,
                    CornerRadius = new CornerRadius(10),
                    Background = new SolidColorBrush(Windows.UI.Color.FromArgb(40, 48, 209, 88)),
                    VerticalAlignment = VerticalAlignment.Center,
                    Child = new FontIcon
                    {
                        Glyph = "\uE73E",
                        FontSize = 10.5,
                        Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 48, 209, 88)),
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center
                    }
                };
            }
            else if (item.Status == PrerequisiteStatus.Missing)
            {
                if (item.CanAutoInstall)
                {
                    statusElement = new Border
                    {
                        CornerRadius = new CornerRadius(10),
                        Padding = new Thickness(7, 3, 7, 3),
                        Background = new SolidColorBrush(Windows.UI.Color.FromArgb(35, 10, 132, 255)),
                        VerticalAlignment = VerticalAlignment.Center,
                        Child = new TextBlock
                        {
                            Text = "Cần cài",
                            FontSize = 10.5,
                            FontWeight = Microsoft.UI.Text.FontWeights.Medium,
                            Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 10, 132, 255))
                        }
                    };
                }
                else
                {
                    statusElement = new Border
                    {
                        CornerRadius = new CornerRadius(10),
                        Padding = new Thickness(7, 3, 7, 3),
                        Background = new SolidColorBrush(Windows.UI.Color.FromArgb(35, 255, 159, 10)),
                        VerticalAlignment = VerticalAlignment.Center,
                        Child = new TextBlock
                        {
                            Text = "Cần mở",
                            FontSize = 10.5,
                            FontWeight = Microsoft.UI.Text.FontWeights.Medium,
                            Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 159, 10))
                        }
                    };
                }
            }
            else
            {
                statusElement = new ProgressRing
                {
                    Width = 16,
                    Height = 16,
                    IsActive = true,
                    Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 142, 142, 147)),
                    VerticalAlignment = VerticalAlignment.Center
                };
            }
            Grid.SetColumn(statusElement, 2);
            rowGrid.Children.Add(statusElement);

            PrerequisitesListPanel.Children.Add(rowGrid);

            // Đường phân cách Inset 0.5px kiểu iOS/macOS (trừ dòng cuối)
            if (i < items.Count - 1)
            {
                var divider = new Border
                {
                    Height = 1,
                    Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 44, 44, 46)),
                    Margin = new Thickness(48, 0, 12, 0)
                };
                PrerequisitesListPanel.Children.Add(divider);
            }
        }

        // Cập nhật nút chính Apple Capsule CTA
        if (_hasMissingInstallable)
        {
            MainActionText.Text = "Cài đặt tự động & Tiếp tục";
            MainActionIcon.Glyph = "\uE896";
            MainActionButton.IsEnabled = true;
            MainActionButton.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 10, 132, 255));
        }
        else if (_hasMissingEssential)
        {
            MainActionText.Text = "Chờ thiết lập thành phần...";
            MainActionIcon.Glyph = "\uE8EF";
            MainActionButton.IsEnabled = false;
            MainActionButton.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 58, 58, 60));
        }
        else
        {
            MainActionText.Text = "Bắt đầu sử dụng";
            MainActionIcon.Glyph = "\uE768";
            MainActionButton.IsEnabled = true;
            MainActionButton.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 10, 132, 255));
        }
    }

    private async void MainActionButton_Click(object sender, RoutedEventArgs e)
    {
        if (_hasMissingInstallable)
        {
            await RunAutoInstallSequenceAsync();
        }
        else
        {
            FinishOnboarding();
        }
    }

    private async void SecondaryActionBtn_Click(object sender, RoutedEventArgs e)
    {
        SecondaryActionBtn.IsEnabled = false;
        await RefreshOnboardingListAsync();
        SecondaryActionBtn.IsEnabled = true;
    }

    private async Task RunAutoInstallSequenceAsync()
    {
        MainActionButton.IsEnabled = false;
        SecondaryActionBtn.IsEnabled = false;
        InstallProgressBox.Visibility = Visibility.Visible;
        InstallProgressBar.IsIndeterminate = true;

        Action<string> reportProgress = (msg) =>
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                InstallStatusLog.Text = msg;
            });
        };

        OnePMStatusResponse? backendStatus = null;
        try { backendStatus = await _backendService.GetStatusAsync(); } catch { }
        var items = await _prerequisiteService.CheckAllAsync(backendStatus);

        foreach (var item in items)
        {
            if (item.CanAutoInstall && item.Status == PrerequisiteStatus.Missing)
            {
                reportProgress($"Đang tự động thiết lập {item.Title}...");
                await _prerequisiteService.InstallItemAsync(item.Id, reportProgress);
            }
        }

        reportProgress("Thiết lập hoàn tất! Đang kiểm tra lại môi trường...");
        await Task.Delay(1200);

        InstallProgressBox.Visibility = Visibility.Collapsed;
        MainActionButton.IsEnabled = true;
        SecondaryActionBtn.IsEnabled = true;

        await RefreshOnboardingListAsync();
    }

    private async void FinishOnboarding()
    {
        _prerequisiteService.SetOnboardingCompleted(true);
        OnboardingOverlay.Visibility = Visibility.Collapsed;

        if (!await _backendService.IsServerReachableAsync())
        {
            await InitializeBackendAndMirrorAsync();
        }
        else
        {
            await RefreshDeviceAndStreamStateAsync(forceStart: false);
        }
    }

    private void OpenOnboardingBtn_Click(object sender, RoutedEventArgs e)
    {
        ShowOnboardingOverlay();
    }

    // ==================== WIN32 / DWM INTEROP CHO CỬA SỔ FLOATING ====================

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MARGINS
    {
        public int cxLeftWidth;
        public int cxRightWidth;
        public int cyTopHeight;
        public int cyBottomHeight;
    }

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_LAYERED = 0x00080000;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr hWnd, ref MARGINS pMarInset);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref uint attrValue, int attrSize);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, bool bRedraw);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRoundRectRgn(int x1, int y1, int x2, int y2, int cx, int cy);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRectRgn(int x1, int y1, int x2, int y2);

    [DllImport("gdi32.dll")]
    private static extern int CombineRgn(IntPtr hrgnDst, IntPtr hrgnSrc1, IntPtr hrgnSrc2, int fnCombineMode);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    private const int RGN_OR = 2;

    /// <summary>
    /// Lấy tỷ lệ scale DPI hiện tại của cửa sổ (1.0 = 100%, 1.25 = 125%, 1.5 = 150%, 2.0 = 200% trên 4K).
    /// </summary>
    private double GetDpiScale()
    {
        if (_hwnd != IntPtr.Zero)
        {
            uint dpi = GetDpiForWindow(_hwnd);
            if (dpi > 0) return dpi / 96.0;
        }
        if (RootGrid?.XamlRoot != null)
        {
            return RootGrid.XamlRoot.RasterizationScale;
        }
        return 1.0;
    }

    /// <summary>
    /// Đồng bộ kích thước vật lý (Physical Pixels) của AppWindow và GDI Window Region
    /// theo đúng tỷ lệ DPI hiện tại của màn hình (100%, 125%, 150%, 175%, 200%, 250% trên màn 4K Windows 11).
    /// </summary>
    private void UpdatePhysicalWindowLayout(int dipWidth, int dipHeight)
    {
        if (_hwnd == IntPtr.Zero || _appWindow == null) return;

        double scale = GetDpiScale();
        bool isLandscape = _currentRotationDeg == 90 || _currentRotationDeg == 270;
        int dipPhoneW = isLandscape ? LandscapeWidth : PhoneWidth;
        int dipPhoneH = isLandscape ? PhoneWidth : PhoneHeight;

        int physPillH = (int)Math.Round(PillHeight * scale);
        int physPillGap = (int)Math.Round(PillGap * scale);
        int physPhoneH = (int)Math.Round(dipPhoneH * scale);
        int physPhoneW = (int)Math.Round(dipPhoneW * scale);
        int physPillW = (int)Math.Round(PillWidth * scale);

        int physWindowW = Math.Max(physPillW, physPhoneW);
        int physWindowH = physPillH + physPillGap + physPhoneH;

        _appWindow.Resize(new SizeInt32(physWindowW, physWindowH));
        ApplyWindowShape(physWindowW, physWindowH, physPhoneW, physPhoneH, physPillW, physPillH, physPillGap, scale);
    }

    private void ApplyWindowShape(int physWindowW, int physWindowH, int physPhoneW, int physPhoneH, int physPillW, int physPillH, int physPillGap, double scale)
    {
        if (_hwnd == IntPtr.Zero) return;
        try
        {
            int physPillX = (physWindowW - physPillW) / 2;
            int physPillY = 0;
            int physPhoneX = (physWindowW - physPhoneW) / 2;
            int physPhoneY = physPillH + physPillGap;

            int physPhoneCorner = (int)Math.Round(88 * scale);
            int physPillCorner = physPillH;

            // 1. Vùng thanh điều khiển macOS dạng Pill ở trên cùng
            IntPtr rgnPill = CreateRoundRectRgn(
                physPillX,
                physPillY,
                physPillX + physPillW + 1,
                physPillY + physPillH + 1,
                physPillCorner,
                physPillCorner
            );

            // 2. Vùng thân máy iPhone bên dưới (cách đỉnh một khoảng PillHeight + PillGap)
            IntPtr rgnPhone = CreateRoundRectRgn(
                physPhoneX,
                physPhoneY,
                physPhoneX + physPhoneW + 1,
                physPhoneY + physPhoneH + 1,
                physPhoneCorner,
                physPhoneCorner
            );

            // 3. Gộp 2 vùng thành một window region duy nhất (khoảng trống giữa và viền ngoài hoàn toàn trong suốt)
            IntPtr rgnCombined = CreateRectRgn(0, 0, 0, 0);
            _ = CombineRgn(rgnCombined, rgnPill, rgnPhone, RGN_OR);
            _ = SetWindowRgn(_hwnd, rgnCombined, true);

            DeleteObject(rgnPill);
            DeleteObject(rgnPhone);
        }
        catch { }
    }

    private static void ApplyWindows11BorderlessChrome(IntPtr hwnd)
    {
        // Tắt viền góc chữ nhật bên ngoài của Windows 11 DWM (chỉ để iPhone tự bo góc)
        const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        const uint DWMWCP_DONOTROUND = 1;
        uint cornerPref = DWMWCP_DONOTROUND;
        _ = DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref cornerPref, sizeof(uint));

        // Tắt đường viền ngoài của Windows 11 DWM
        const int DWMWA_BORDER_COLOR = 34;
        const uint DWMWA_COLOR_NONE = 0xFFFFFFFE;
        uint borderColor = DWMWA_COLOR_NONE;
        _ = DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref borderColor, sizeof(uint));
    }
}

