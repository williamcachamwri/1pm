"""Cable-free connection to the iPhone over the local network.

iOS 17+ announces a RemotePairing service over Bonjour on Wi-Fi. A host that
holds a RemotePairing record for the phone can open the same no-admin
userspace tunnel used for USB, just over the network. The record is created
once over USB, where the existing Trust makes it promptless.
"""

from __future__ import annotations

import asyncio
import contextlib
import logging
import socket
import struct
from typing import Collection, Optional

logger = logging.getLogger(__name__)

BONJOUR_TIMEOUT = 10.0
_MDNS_GROUP = "224.0.0.251"
_MDNS_PORT = 5353
_bonjour_patched = False


def _local_ipv4() -> list[str]:
    try:
        import ifaddr
    except ImportError:
        return []
    found = []
    for adapter in ifaddr.get_adapters():
        for ip in adapter.ips:
            if isinstance(ip.ip, str) and not ip.ip.startswith("127.") and not ip.ip.startswith("169.254."):
                found.append(ip.ip)
    # Ưu tiên các dải IP mạng LAN cục bộ (192.168.x.x, 10.x.x.x, 172.16-31.x.x)
    found.sort(key=lambda x: (not (x.startswith("192.168.") or x.startswith("10.") or x.startswith("172.")), x))
    return found


def _patch_bonjour() -> None:
    """Browse mDNS on every IPv4 adapter, not just Windows' default one,
    and prevent Windows IDNA/IPv6 crashes."""
    global _bonjour_patched
    if _bonjour_patched:
        return
    from pymobiledevice3 import bonjour
    from pymobiledevice3.remote import tunnel_service

    # 1. Khắc phục lỗi Windows IDNA UnicodeEncodeError:
    # Trên Windows, Address.full_ip không được nối tên adapter có dấu cách/dấu ngoặc
    # (ví dụ: "%Microsoft Wi-Fi Direct Virtual Adapter #3") vì hàm socket getaddrinfo
    # sẽ cố encode IDNA và crash với lỗi 'label too long' (>63 chars).
    def _safe_full_ip(self):
        if ":" not in self.ip:
            return self.ip
        # Với IPv6, chỉ dùng interface index dạng số nguyên nếu có
        if self.iface and self.iface.isdigit():
            return f"{self.ip}%{self.iface}"
        return self.ip

    bonjour.Address.full_ip = property(_safe_full_ip)

    # 2. Patch socket mDNS IPv4 để gửi & nhận trên tất cả card mạng LAN
    original_bind = bonjour._bind_ipv4
    original_send = bonjour._send_query_all

    async def bind_ipv4(queue):
        transport, sock = await original_bind(queue)
        group = socket.inet_aton(_MDNS_GROUP)
        for ip in _local_ipv4():
            with contextlib.suppress(OSError):
                sock.setsockopt(socket.IPPROTO_IP, socket.IP_ADD_MEMBERSHIP, struct.pack("=4s4s", group, socket.inet_aton(ip)))
        return transport, sock

    async def send_query_all(transports, pkt, *args, **kwargs):
        sent = await original_send(transports, pkt, *args, **kwargs)
        for _, sock in transports:
            if sock.family != socket.AF_INET:
                continue
            for ip in _local_ipv4():
                with contextlib.suppress(OSError):
                    sock.setsockopt(socket.IPPROTO_IP, socket.IP_MULTICAST_IF, socket.inet_aton(ip))
                    sock.sendto(pkt, (_MDNS_GROUP, _MDNS_PORT))
                    sent += 1
        return sent

    bonjour._bind_ipv4 = bind_ipv4
    bonjour._send_query_all = send_query_all

    # 3. Patch get_remote_pairing_tunnel_services để ưu tiên thử địa chỉ IPv4 trước
    # và bắt mọi Exception cục bộ, không để 1 địa chỉ IPv6 lỗi làm sập toàn bộ quá trình tìm kiếm.
    async def robust_get_remote_pairing_tunnel_services(
        bonjour_timeout: float = BONJOUR_TIMEOUT,
        udid: Optional[str] = None,
        excluded_identifiers: Collection[str] = (),
    ) -> list[tunnel_service.RemotePairingTunnelService]:
        alt_irks: dict[str, bytes] = {}
        for identifier, path, pair_record in tunnel_service.iter_remote_pair_records_by_identifier():
            if (udid is not None and identifier != udid) or identifier in excluded_identifiers:
                continue
            alt_irk = pair_record.get(tunnel_service.PEER_ALT_IRK_KEY)
            if alt_irk is None:
                continue
            alt_irks[identifier] = alt_irk
        if not alt_irks:
            return []

        result: list[tunnel_service.RemotePairingTunnelService] = []
        answers = tunnel_service.iter_browse_remotepairing(timeout=bonjour_timeout)
        try:
            async for answer in answers:
                identifier = tunnel_service._match_remote_pair_record(answer, alt_irks)
                if identifier is None:
                    continue
                # Ưu tiên kết nối IPv4 (LAN ổn định và nhanh trên Windows) trước IPv6
                sorted_addrs = sorted(answer.addresses, key=lambda a: (":" in a.ip, a.ip))
                for address in sorted_addrs:
                    try:
                        srv = await tunnel_service.create_core_device_tunnel_service_using_remotepairing(
                            identifier, address.full_ip, answer.port
                        )
                        result.append(srv)
                        break  # Đã kết nối thành công với thiết bị này
                    except Exception as e:
                        logger.debug("Bỏ qua ứng viên Wi-Fi %s@%s:%s: %r", identifier, address.full_ip, answer.port, e)
                if udid is not None and result:
                    break
        finally:
            with contextlib.suppress(Exception):
                await answers.aclose()
        return result

    tunnel_service.get_remote_pairing_tunnel_services = robust_get_remote_pairing_tunnel_services
    _bonjour_patched = True


def wifi_paired_udids() -> list[str]:
    try:
        from pymobiledevice3.pair_records import iter_remote_paired_identifiers

        return sorted(set(iter_remote_paired_identifiers()))
    except Exception:
        return []


def pair_for_wifi(udid: str) -> dict:
    """Write the RemotePairing record over the trusted USB link."""

    async def pair() -> None:
        from pymobiledevice3.exceptions import RemotePairingCompletedError
        from pymobiledevice3.lockdown import create_using_usbmux
        from pymobiledevice3.remote.tunnel_service import RemotePairingLockdownService

        lockdown = await create_using_usbmux(serial=udid)
        service = await RemotePairingLockdownService.create(lockdown)
        try:
            await service.connect(autopair=True)
        except RemotePairingCompletedError:
            pass
        finally:
            await service.close()
            await lockdown.close()

    try:
        asyncio.run(pair())
    except Exception as error:
        return {"ok": False, "error": f"Wi-Fi setup needs the cable once: {error}"}
    return {"ok": udid in wifi_paired_udids(), "paired": wifi_paired_udids()}


async def _wifi_provider(serial: Optional[str], autopair: bool, remotepairing_fallback: bool = True):
    from pymobiledevice3.remote import tunnel_service

    _patch_bonjour()
    services = await tunnel_service.get_remote_pairing_tunnel_services(
        udid=serial, bonjour_timeout=BONJOUR_TIMEOUT
    )
    # Nếu lần quét đầu chưa thấy (do mDNS packet rơi rớt hoặc iPhone vừa tỉnh dậy), thử quét thêm 1 lần nhanh
    if not services:
        services = await tunnel_service.get_remote_pairing_tunnel_services(
            udid=serial, bonjour_timeout=5.0
        )
    if not services:
        raise WifiNotFoundError(
            "Không tìm thấy iPhone trên mạng Wi-Fi.\n"
            "Vui lòng kiểm tra:\n"
            "1. iPhone và PC đang cùng kết nối chung một mạng Wi-Fi.\n"
            "2. Màn hình iPhone đang mở khóa.\n"
            "3. Đã cắm cáp một lần và bấm 'Ghép Wi-Fi' trong Cài đặt.\n"
            "4. Mạng Wi-Fi trên Windows đã được đặt thành 'Private network' (Mạng riêng tư)."
        )
    for extra in services[1:]:
        await extra.close()
    return services[0], None


class WifiNotFoundError(RuntimeError):
    pass


def wifi_tunnel(udid: str):
    """An RSD tunnel to ``udid`` over Wi-Fi, used like ``PreferredRsdTunnel``."""
    from pymobiledevice3.remote import userspace_tunnel

    class WifiRsdTunnel(userspace_tunnel.UserspaceRsdTunnel):
        # The base class always starts from USB (usbmux); swap in the network
        # provider for the duration of this tunnel's setup only.
        async def _aopen_locked(self):
            original = userspace_tunnel._create_no_root_tunnel_provider
            userspace_tunnel._create_no_root_tunnel_provider = _wifi_provider
            try:
                return await super()._aopen_locked()
            finally:
                userspace_tunnel._create_no_root_tunnel_provider = original

    return WifiRsdTunnel(serial=udid, autopair=False)
