"""Xcode's device screen, on Windows.

After Trust, iOS 17.4+ exposes CoreDeviceProxy over the USB service broker,
or RemotePairing over Wi-Fi once paired for it. Either tunnel carries a
display service. The phone then sends the screen as HEVC video. The browser
decodes it.
"""

from __future__ import annotations

import asyncio
import threading

import pymobiledevice3.remote.core_device.screen_stream as pymd3_screen_stream

# Monkey-patch: Tắt hoàn toàn bộ watchdog ngắt luồng 5s (_stall_watchdog) của pymobiledevice3.
# Trên iPhone 15/16 Pro (ProMotion), khi màn hình tĩnh (như đọc tin nhắn Zalo), encoder không sinh AU mới.
# Mặc định _STALL_RESTART_SECS = 5.0 sẽ nhận định nhầm là encoder bị treo và tự động ngắt + khởi động lại luồng!
pymd3_screen_stream._STALL_RESTART_SECS = 86400.0
pymd3_screen_stream._MAX_STALL_RESTARTS = 0

async def _dummy_stall_watchdog(self) -> None:
    try:
        while True:
            await asyncio.sleep(3600)
    except asyncio.CancelledError:
        return

pymd3_screen_stream.ScreenStreamServer._stall_watchdog = _dummy_stall_watchdog

# Giải mã âm thanh AAC-ELD chất lượng cao 48kHz Stereo của Apple trên Windows thông qua Fraunhofer FDK-AAC
import ctypes
import sys
from pathlib import Path

def _find_fdk_dll() -> str | None:
    candidates = [
        Path(__file__).parent / "libfdk-aac-2.dll",
        Path.cwd() / "libfdk-aac-2.dll",
        Path.cwd() / "handset" / "libfdk-aac-2.dll",
        Path(sys.executable).parent / "libfdk-aac-2.dll",
    ]
    for c in candidates:
        if c.is_file():
            return str(c.resolve())
    return None

class FdkAacEldDecoder:
    """Apple AAC-ELD @ 48 kHz stereo 480 samples/frame via Fraunhofer FDK-AAC."""
    ASC_48K_STEREO_480 = bytes([0xF8, 0xE6, 0x50, 0x00])

    def __init__(self, magic_cookie: bytes = pymd3_screen_stream.AAC_ELD_ASC_48K_STEREO_480) -> None:
        dll_path = _find_fdk_dll()
        if not dll_path:
            raise RuntimeError("libfdk-aac-2.dll not found")
        self._fdk = ctypes.CDLL(dll_path)

        self._fdk.aacDecoder_Open.argtypes = [ctypes.c_int, ctypes.c_uint]
        self._fdk.aacDecoder_Open.restype = ctypes.c_void_p

        self._fdk.aacDecoder_ConfigRaw.argtypes = [
            ctypes.c_void_p,
            ctypes.POINTER(ctypes.POINTER(ctypes.c_uint8)),
            ctypes.POINTER(ctypes.c_uint),
        ]
        self._fdk.aacDecoder_ConfigRaw.restype = ctypes.c_int

        self._fdk.aacDecoder_Fill.argtypes = [
            ctypes.c_void_p,
            ctypes.POINTER(ctypes.POINTER(ctypes.c_uint8)),
            ctypes.POINTER(ctypes.c_uint),
            ctypes.POINTER(ctypes.c_uint),
        ]
        self._fdk.aacDecoder_Fill.restype = ctypes.c_int

        self._fdk.aacDecoder_DecodeFrame.argtypes = [
            ctypes.c_void_p,
            ctypes.POINTER(ctypes.c_int16),
            ctypes.c_int,
            ctypes.c_uint,
        ]
        self._fdk.aacDecoder_DecodeFrame.restype = ctypes.c_int

        self._fdk.aacDecoder_Close.argtypes = [ctypes.c_void_p]
        self._fdk.aacDecoder_Close.restype = None

        self._fdk.aacDecoder_SetParam.argtypes = [ctypes.c_void_p, ctypes.c_int, ctypes.c_int]
        self._fdk.aacDecoder_SetParam.restype = ctypes.c_int

        class _CStreamInfo(ctypes.Structure):
            _fields_ = [
                ("sampleRate", ctypes.c_int),
                ("frameSize", ctypes.c_int),
                ("numChannels", ctypes.c_int),
                ("pChannelType", ctypes.c_void_p),
                ("pChannelIndices", ctypes.c_void_p),
                ("aacSampleRate", ctypes.c_int),
                ("profile", ctypes.c_int),
                ("aot", ctypes.c_int),
                ("channelConfig", ctypes.c_int),
                ("bitRate", ctypes.c_int),
                ("aacSamplesPerFrame", ctypes.c_int),
            ]
        self._CStreamInfo = _CStreamInfo
        self._fdk.aacDecoder_GetStreamInfo.argtypes = [ctypes.c_void_p]
        self._fdk.aacDecoder_GetStreamInfo.restype = ctypes.POINTER(_CStreamInfo)

        handle = self._fdk.aacDecoder_Open(0, 1) # TT_MP4_RAW = 0
        if not handle:
            raise RuntimeError("aacDecoder_Open failed")
        self._handle = ctypes.c_void_p(handle)

        cookie = bytearray(magic_cookie or self.ASC_48K_STEREO_480)
        if len(cookie) >= 3:
            cookie[2] |= 0x10  # bit 19 = 1 -> 480 samples/frame for Apple DisplayService
        asc = bytes(cookie)

        asc_buf = (ctypes.c_uint8 * len(asc))(*asc)
        asc_ptr = ctypes.cast(asc_buf, ctypes.POINTER(ctypes.c_uint8))
        asc_ptr_arr = (ctypes.POINTER(ctypes.c_uint8) * 1)(asc_ptr)
        asc_len = (ctypes.c_uint * 1)(len(asc))
        err_cfg = self._fdk.aacDecoder_ConfigRaw(self._handle, asc_ptr_arr, asc_len)
        if err_cfg != 0:
            self._close()
            raise RuntimeError(f"aacDecoder_ConfigRaw failed: 0x{err_cfg:x}")

        # Tối ưu chất lượng âm thanh Hi-Fi tối đa:
        # 0x0004: AAC_PCM_LIMITER_ENABLE = 0 (tắt bộ nén limiter để âm thanh không bị méo tiếng hoặc nghẹt tiếng)
        self._fdk.aacDecoder_SetParam(self._handle, 0x0004, 0)
        # 0x0202: AAC_DRC_REFERENCE_LEVEL = -1 (tắt DRC để giữ trọn dải động dynamic range chân thực của âm thanh gốc)
        self._fdk.aacDecoder_SetParam(self._handle, 0x0202, -1)
        # 0x0100: AAC_CONCEAL_METHOD = 1 (bù khuyết âm thanh mượt mà nếu có packet trễ/mất)
        self._fdk.aacDecoder_SetParam(self._handle, 0x0100, 1)

        self._pcm_out = (ctypes.c_int16 * 4096)()

    def decode(self, au: bytes) -> bytes:
        if not au or not self._handle:
            return b""
        p_buf = (ctypes.c_uint8 * len(au))(*au)
        p_ptr = ctypes.cast(p_buf, ctypes.POINTER(ctypes.c_uint8))
        p_ptr_arr = (ctypes.POINTER(ctypes.c_uint8) * 1)(p_ptr)
        p_len = (ctypes.c_uint * 1)(len(au))
        valid = ctypes.c_uint(len(au))

        err_fill = self._fdk.aacDecoder_Fill(self._handle, p_ptr_arr, p_len, ctypes.byref(valid))
        if err_fill != 0:
            return b""

        err_dec = self._fdk.aacDecoder_DecodeFrame(self._handle, self._pcm_out, len(self._pcm_out), 0)
        if err_dec == 0 or (0x4000 <= err_dec <= 0x4FFF):
            info = self._fdk.aacDecoder_GetStreamInfo(self._handle).contents
            samples = info.frameSize * info.numChannels
            if samples > 0:
                return ctypes.string_at(ctypes.byref(self._pcm_out), samples * 2)
        return b""

    def _close(self) -> None:
        if getattr(self, "_handle", None) and self._handle.value:
            try:
                self._fdk.aacDecoder_Close(self._handle)
            except Exception:
                pass
            self._handle = None

    def __del__(self) -> None:
        self._close()

pymd3_screen_stream.AACELDDecoder = FdkAacEldDecoder
pymd3_screen_stream.ScreenStreamServer._missing_audio_deps = staticmethod(
    lambda: [] if _find_fdk_dll() else ["libfdk-aac-2.dll"]
)

from handset.device import developer_mode_enabled
from handset.mux import MuxError, list_devices
from handset.wifi import wifi_paired_udids, wifi_tunnel

DEVELOPER_MODE_MESSAGE = (
    "The picture stays black because Developer Mode is off. On the iPhone open Settings, "
    "Privacy & Security, Developer Mode, turn it on, and let the phone restart. After it boots, "
    "unlock it, tap Turn On, then press Show screen again."
)

VIEW_URL = "http://127.0.0.1:8766/"
_LOCK = threading.Lock()
_STATE = {"phase": "idle", "udid": None, "url": None, "error": None, "note": None, "transport": None, "audio": "iphone"}
_CURRENT_LOOP: asyncio.AbstractEventLoop | None = None
_CURRENT_TASK: asyncio.Task | None = None
_CURRENT_SERVER = None


def screen_status() -> dict:
    with _LOCK:
        return dict(_STATE)


def set_audio_mode(mode: str) -> dict:
    """Chuyển đổi âm thanh: 'iphone' (phát loa iPhone) hoặc 'windows' (truyền lên máy tính)."""
    global _CURRENT_LOOP, _CURRENT_SERVER
    mode = "windows" if mode == "windows" else "iphone"
    with _LOCK:
        loop = _CURRENT_LOOP
        server = _CURRENT_SERVER
        _STATE["audio"] = mode
    if loop and server and not loop.is_closed():
        if mode == "windows":
            asyncio.run_coroutine_threadsafe(server._ensure_audio_stream(), loop)
        else:
            asyncio.run_coroutine_threadsafe(server._stop_audio_stream(), loop)
    return {"ok": True, "mode": mode}


def stop_screen() -> dict:
    """Cleanly cancel the running screen stream server and release port 8766."""
    global _CURRENT_LOOP, _CURRENT_TASK, _CURRENT_SERVER
    with _LOCK:
        loop = _CURRENT_LOOP
        task = _CURRENT_TASK
        _CURRENT_SERVER = None
    if loop and task and not task.done():
        loop.call_soon_threadsafe(task.cancel)
    import time
    for _ in range(25):
        with _LOCK:
            if _STATE.get("phase") in ("idle", "error"):
                break
        time.sleep(0.1)
    return screen_status()


def restart_screen(udid: str | None = None) -> dict:
    """Nudge the device encoder and keep the HTTP stream on 8766 alive."""
    import http.client

    chosen = (udid or "").strip() or _STATE.get("udid") or _only_udid()
    nudged = False
    for path in ("/restart", "/pli"):
        try:
            conn = http.client.HTTPConnection("127.0.0.1", 8766, timeout=20)
            conn.request("POST", path, body=b"", headers={})
            response = conn.getresponse()
            response.read()
            conn.close()
            nudged = True
        except OSError:
            continue
    with _LOCK:
        running = _STATE.get("phase") == "running"
    if running:
        return _set(note="Stream restarted. If the picture is still black, press Restart screen again.")
    if chosen:
        return start_screen(chosen, _STATE.get("transport") or "auto")
    return _set(error="Connect the iPhone first, then start mirroring.")


def start_screen(udid: str | None, transport: str = "auto") -> dict:
    usb = _usb_udids()
    wifi = wifi_paired_udids()
    chosen = (udid or "").strip() or _only_udid(usb, wifi, transport)
    if not chosen:
        if transport == "wifi":
            return _set(phase="idle", udid=None, url=None, error="Chưa ghép nối Wi-Fi. Hãy cắm cáp một lần và nhấn 'Ghép Wi-Fi' trong Cài đặt.")
        return _set(phase="idle", udid=None, url=None, error="Hãy cắm cáp kết nối iPhone trước.")

    if transport == "auto":
        transport = "usb" if chosen in usb else "wifi"
    if transport == "usb" and chosen not in usb:
        return _set(phase="error", udid=chosen, url=None, error="iPhone chưa kết nối qua cáp USB. Hãy cắm lại cáp hoặc chọn kết nối Wi-Fi.")
    if transport == "wifi" and chosen not in wifi:
        return _set(phase="error", udid=chosen, url=None, error="Chưa ghép nối Wi-Fi cho thiết bị này. Hãy cắm cáp một lần và nhấn 'Ghép Wi-Fi' trong Cài đặt.")
    if transport == "usb" and developer_mode_enabled(chosen) is False:
        return _set(phase="error", udid=chosen, url=None, error=DEVELOPER_MODE_MESSAGE)

    with _LOCK:
        if _STATE["phase"] in {"starting", "running"} and _STATE["udid"] == chosen:
            if _STATE.get("transport") == transport:
                return dict(_STATE)

    # Nếu người dùng đổi transport (USB <-> Wi-Fi), dừng luồng cũ trước để giải phóng cổng 8766
    stop_screen()

    with _LOCK:
        note = "Đang tìm kiếm iPhone trên mạng Wi-Fi... Hãy mở khóa iPhone." if transport == "wifi" else None
        _STATE.update(phase="starting", udid=chosen, url=None, error=None, note=note, transport=transport)
    threading.Thread(target=_run, args=(chosen, transport), name="handset-screen", daemon=True).start()
    return screen_status()


def _run(udid: str, transport: str) -> None:
    global _CURRENT_LOOP, _CURRENT_TASK
    loop = asyncio.new_event_loop()
    asyncio.set_event_loop(loop)
    with _LOCK:
        _CURRENT_LOOP = loop
    try:
        task = loop.create_task(_serve(udid, transport))
        with _LOCK:
            _CURRENT_TASK = task
        loop.run_until_complete(task)
        _set(phase="idle", udid=udid, url=None, error="Luồng phản chiếu màn hình đã dừng.", transport=transport)
    except (asyncio.CancelledError, KeyboardInterrupt):
        _set(phase="idle", udid=udid, url=None, error=None, transport=transport)
    except Exception as error:
        _set(phase="error", udid=udid, url=None, error=_plain(error), transport=transport)
    finally:
        with _LOCK:
            _CURRENT_TASK = None
            _CURRENT_LOOP = None
        try:
            loop.close()
        except Exception:
            pass


DISPLAY_SERVICE = "com.apple.coredevice.displayservice"


def _open_tunnel(udid: str, transport: str):
    if transport == "wifi":
        return wifi_tunnel(udid)
    from pymobiledevice3.remote.rsd_tunnel import PreferredRsdTunnel

    return PreferredRsdTunnel(serial=udid, autopair=True)


async def _serve(udid: str, transport: str) -> None:
    from pymobiledevice3.remote.core_device.screen_stream import ScreenStreamServer
    from pymobiledevice3.services.mobile_image_mounter import auto_mount

    for attempt in range(2):
        async with _open_tunnel(udid, transport) as rsd:
            if DISPLAY_SERVICE in (rsd.peer_info or {}).get("Services", {}):
                await _stream(rsd, udid, transport, ScreenStreamServer)
                return
            if attempt:
                raise RuntimeError(
                    "The developer image mounted, but the iPhone still offers no display service. "
                    "Unplug it, plug it back in, and press Show screen again."
                )
            _set(note="Mounting the developer image on the iPhone. This downloads it the first time.", transport=transport)
            await auto_mount(rsd)


async def _stream(rsd, udid: str, transport: str, ScreenStreamServer) -> None:
    global _CURRENT_SERVER
    from pymobiledevice3.remote.core_device.display_service import is_media_in_use_error

    server = ScreenStreamServer(
        rsd,
        bind="127.0.0.1",
        http_port=8766,
        audio_default_on=False,
    )
    with _LOCK:
        _CURRENT_SERVER = server

    async def _eager_stream_start() -> None:
        with _LOCK:
            audio_mode = _STATE.get("audio", "iphone")
        if audio_mode == "windows":
            try:
                await server._ensure_audio_stream()
            except Exception as e:
                import logging
                logging.getLogger(__name__).warning("Eager audio start failed: %s", e)
        try:
            await server._ensure_fresh_stream(force=False)
        except Exception as error:
            if is_media_in_use_error(error):
                raise
            import logging
            logging.getLogger(__name__).warning("Eager video start failed, retrying with force=True...", exc_info=True)
            try:
                await server._ensure_fresh_stream(force=True)
            except Exception:
                raise

    server._eager_stream_start = _eager_stream_start

    # Chạy eager stream start ngay
    await _eager_stream_start()

    # Chờ stream_ready trước khi công bố running để WebView2 không bao giờ nhận 503 trên Wi-Fi / USB
    try:
        await asyncio.wait_for(server._stream_ready.wait(), timeout=15.0)
    except asyncio.TimeoutError:
        import logging
        logging.getLogger(__name__).warning("Stream ready wait timed out, continuing...")

    _set(phase="running", udid=udid, url=VIEW_URL, error=None, note=None, transport=transport)
    await server.serve()


def _usb_udids() -> list[str]:
    try:
        return [device["udid"] for device in list_devices()]
    except MuxError:
        return []


def _only_udid(usb: list[str] | None = None, wifi: list[str] | None = None, transport: str = "auto") -> str | None:
    usb = _usb_udids() if usb is None else usb
    wifi = wifi_paired_udids() if wifi is None else wifi
    if transport != "wifi" and len(usb) == 1:
        return usb[0]
    if transport != "usb" and len(wifi) == 1:
        return wifi[0]
    return None


def _set(**values) -> dict:
    with _LOCK:
        _STATE.update(values)
        return dict(_STATE)


def _plain(error: Exception) -> str:
    text = str(error).strip() or type(error).__name__
    name = type(error).__name__
    if name == "WifiNotFoundError":
        return text
    if name in {"ConnectionFailedToUsbmuxdError", "MuxError"} or "27015" in text or "usbmux" in text.lower():
        return "Dịch vụ Apple Mobile Device Service chưa chạy. Hãy cài đặt Apple Devices hoặc iTunes và cắm lại cáp."
    if name == "UserspaceTunnelUnavailableError":
        return "iPhone không mở được đường truyền phản chiếu. Cần iOS 17.4 trở lên, chọn 'Tin cậy máy tính' và bật Chế độ Nhà phát triển."
    if "CoreDeviceProxy" in text or name == "InvalidServiceError":
        return "iPhone không cung cấp dịch vụ hiển thị. Hãy bật Chế độ Nhà phát triển, mở khóa màn hình và chọn 'Tin cậy'."
    if name == "DeviceNotFoundError":
        return "Không tìm thấy iPhone trên cổng USB. Hãy cắm lại cáp, mở khóa màn hình và nhấn Thử lại."
    if name in {"DeveloperModeIsNotEnabledError", "DeveloperModeError"}:
        return DEVELOPER_MODE_MESSAGE
    if "PasswordProtected" in text or "locked" in text.lower():
        return "Vui lòng mở khóa màn hình iPhone và giữ màn hình luôn sáng, sau đó thử lại."
    if "PairingDialog" in text or "Trust" in text:
        return "Vui lòng mở khóa iPhone và chọn 'Tin cậy máy tính này', sau đó thử lại."
    return text
