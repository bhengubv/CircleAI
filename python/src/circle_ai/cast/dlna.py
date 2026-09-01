"""Putting something the assistant made onto the television in the room.

DE-GOOGLED BY DESIGN: no Google Cast, no Chromecast SDK. The only backend is
open UPnP/DLNA, which every television in this market already speaks and which
needs nobody's account.

THE RENDERER PULLS. Nothing is ever pushed to it — a caller hands the television
a URL and the television fetches it. That single fact is why casting a local
file needs an HTTP server running on THIS device, and it is the thing most
people implementing DLNA get wrong first.

OFFLINE AND LAN-ONLY. Nothing in this module reaches the internet.
"""

from __future__ import annotations

import re
import socket
import threading
import xml.etree.ElementTree as ET
from abc import ABC, abstractmethod
from dataclasses import dataclass, field
from enum import Enum
from typing import Callable, Sequence
from urllib.parse import urljoin, urlparse
from xml.sax.saxutils import escape


class CastProtocol(Enum):
    """The wire protocol a target speaks.

    ONE member on purpose: it is the place a second one would go, and its
    absence is the de-Googling decision made visible rather than assumed.
    """

    DLNA = "dlna"


@dataclass(frozen=True)
class CastTargetId:
    """Identifies one renderer."""

    value: str


class CastContentKind(Enum):
    """What is being cast."""

    IMAGE = "image"
    AUDIO = "audio"
    VIDEO = "video"
    SLIDESHOW = "slideshow"


class CastPlaybackState(Enum):
    """What the renderer is doing."""

    UNKNOWN = "unknown"
    IDLE = "idle"
    BUFFERING = "buffering"
    PLAYING = "playing"
    PAUSED = "paused"
    STOPPED = "stopped"
    ERROR = "error"


class CastMediaSource(ABC):
    """Where the media is.

    A closed set of three, handled at exactly one place — the point where a URL
    has to be produced for the television to fetch. A file and a byte buffer
    both become a URL served by this device; only Url is already one.
    """


@dataclass(frozen=True)
class Url(CastMediaSource):
    """Media the renderer can already reach."""

    value: str


@dataclass(frozen=True)
class File(CastMediaSource):
    """Media on this device's disk."""

    path: str


@dataclass(frozen=True)
class Bytes(CastMediaSource):
    """Media held in memory."""

    data: bytes


@dataclass(frozen=True)
class CastMedia:
    """One thing to cast."""

    source: CastMediaSource
    mime_type: str
    kind: CastContentKind
    title: str = ""
    #: None when unknown. Zero is a real answer for a still image.
    duration_seconds: float | None = None


@dataclass(frozen=True)
class CastStatus:
    """The renderer's current position."""

    state: CastPlaybackState
    position_seconds: float = 0.0
    duration_seconds: float = 0.0
    current_uri: str = ""


class CastException(Exception):
    """A cast that failed."""


class CastControlException(CastException):
    """The television ANSWERED AND REFUSED.

    Separated from CastException because it is a different problem from never
    reaching it: usually an unquoted SOAPACTION or a URI the renderer will not
    accept. Retrying helps one and not the other.
    """

    def __init__(self, action: str, reason: str) -> None:
        super().__init__(f"the renderer refused {action}: {reason}")
        self.action = action
        self.reason = reason


# ─────────────────────────────────────────────────────────────────────────────
# SSDP discovery


@dataclass(frozen=True)
class SsdpResponse:
    """One M-SEARCH reply."""

    location: str
    usn: str = ""
    server: str = ""
    search_target: str = ""


def parse_ssdp_response(text: str) -> SsdpResponse | None:
    """Parses one reply.

    Header names are matched case-INSENSITIVELY: televisions disagree about
    capitalisation and a case-sensitive parser finds some of them and not
    others, which reads as a flaky network.
    """
    lines = text.replace("\r\n", "\n").split("\n")
    if not lines or not lines[0].upper().startswith("HTTP/1.1 200"):
        return None
    fields: dict[str, str] = {}
    for line in lines[1:]:
        if ":" not in line:
            continue
        name, _, value = line.partition(":")
        fields[name.strip().upper()] = value.strip()
    location = fields.get("LOCATION", "")
    if not location:
        return None
    return SsdpResponse(
        location=location,
        usn=fields.get("USN", ""),
        server=fields.get("SERVER", ""),
        search_target=fields.get("ST", ""),
    )


def build_ssdp_search(search_target: str, mx_seconds: int = 2) -> str:
    """Builds the M-SEARCH datagram.

    The BLANK LINE at the end is required by the protocol and is the usual
    reason a hand-written M-SEARCH gets no replies at all.
    """
    return "\r\n".join([
        "M-SEARCH * HTTP/1.1",
        "HOST: 239.255.255.250:1900",
        'MAN: "ssdp:discover"',
        f"MX: {max(1, mx_seconds)}",
        f"ST: {search_target}",
        "", "",
    ])


class SsdpClient:
    """Sends M-SEARCH and collects replies."""

    def __init__(self, timeout_seconds: float = 3.0) -> None:
        self.timeout_seconds = timeout_seconds

    def search(self, search_target: str) -> list[SsdpResponse]:
        sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        sock.settimeout(self.timeout_seconds)
        try:
            sock.sendto(build_ssdp_search(search_target).encode(), ("239.255.255.250", 1900))
            seen: set[str] = set()
            out: list[SsdpResponse] = []
            while True:
                try:
                    data, _ = sock.recvfrom(4096)
                except (socket.timeout, OSError):
                    break
                reply = parse_ssdp_response(data.decode(errors="ignore"))
                # De-duplicate by USN: a renderer answers the same search
                # several times, and a caller shown one television three times
                # concludes the list is broken.
                if reply is not None and reply.usn not in seen:
                    seen.add(reply.usn)
                    out.append(reply)
            return out
        finally:
            sock.close()


# ─────────────────────────────────────────────────────────────────────────────
# Device description


@dataclass(frozen=True)
class RendererDescription:
    """One service on a device."""

    friendly_name: str
    manufacturer: str
    model_name: str
    udn: str
    #: ABSOLUTE. The description document gives a RELATIVE control URL and the
    #: base is the document's own address; resolving it late is how a caller
    #: ends up POSTing to its own host.
    control_url: str
    service_type: str


@dataclass(frozen=True)
class DeviceDescription:
    """A whole device."""

    friendly_name: str
    manufacturer: str
    model_name: str
    udn: str
    services: tuple[RendererDescription, ...] = ()


_UPNP_NS = "{urn:schemas-upnp-org:device-1-0}"


def parse_device_description(xml_text: str, base_url: str) -> DeviceDescription:
    """Parses the XML at a location."""
    root = ET.fromstring(xml_text)
    device = root.find(f"{_UPNP_NS}device")
    if device is None:
        raise CastException("no device element in the description")

    def text(tag: str) -> str:
        node = device.find(f"{_UPNP_NS}{tag}")
        return node.text or "" if node is not None else ""

    friendly = text("friendlyName")
    manufacturer = text("manufacturer")
    model = text("modelName")
    udn = text("UDN")

    services: list[RendererDescription] = []
    service_list = device.find(f"{_UPNP_NS}serviceList")
    if service_list is not None:
        for svc in service_list.findall(f"{_UPNP_NS}service"):
            stype = svc.find(f"{_UPNP_NS}serviceType")
            ctrl = svc.find(f"{_UPNP_NS}controlURL")
            if stype is None or ctrl is None:
                continue
            services.append(RendererDescription(
                friendly_name=friendly, manufacturer=manufacturer, model_name=model,
                udn=udn,
                control_url=urljoin(base_url, ctrl.text or ""),
                service_type=stype.text or "",
            ))
    return DeviceDescription(friendly, manufacturer, model, udn, tuple(services))


# ─────────────────────────────────────────────────────────────────────────────
# UPnP control


class DidlLite:
    """Builds the metadata a renderer wants alongside a URI.

    NOT optional in practice: a television handed a URI with no metadata will
    often play it and show nothing, or refuse outright.
    """

    _CLASSES = {
        CastContentKind.AUDIO: "object.item.audioItem.musicTrack",
        CastContentKind.VIDEO: "object.item.videoItem",
        CastContentKind.IMAGE: "object.item.imageItem",
        CastContentKind.SLIDESHOW: "object.item.imageItem",
    }

    @classmethod
    def build(cls, title: str, mime_type: str, kind: CastContentKind, uri: str) -> str:
        klass = cls._CLASSES.get(kind, "object.item.imageItem")
        return (
            '<DIDL-Lite xmlns="urn:schemas-upnp-org:metadata-1-0/DIDL-Lite/"'
            ' xmlns:dc="http://purl.org/dc/elements/1.1/"'
            ' xmlns:upnp="urn:schemas-upnp-org:metadata-1-0/upnp/">'
            '<item id="0" parentID="-1" restricted="1">'
            f"<dc:title>{escape(title)}</dc:title>"
            f"<upnp:class>{klass}</upnp:class>"
            f'<res protocolInfo="http-get:*:{escape(mime_type)}:*">{escape(uri)}</res>'
            "</item></DIDL-Lite>"
        )


_AV_TRANSPORT = "urn:schemas-upnp-org:service:AVTransport:1"


def _format_upnp_time(seconds: float) -> str:
    """H:MM:SS. Renderers reject a bare number of seconds."""
    total = max(0, int(seconds))
    return f"{total // 3600}:{(total % 3600) // 60:02d}:{total % 60:02d}"


def _parse_upnp_time(text: str) -> float:
    parts = text.strip().split(":")
    if len(parts) != 3:
        return -1.0
    try:
        return int(parts[0]) * 3600 + int(parts[1]) * 60 + float(parts[2])
    except ValueError:
        return -1.0


def _between_tags(body: str, tag: str) -> str:
    match = re.search(rf"<{tag}>(.*?)</{tag}>", body, re.DOTALL)
    return match.group(1) if match else ""


class UpnpControlPoint:
    """Performs SOAP actions against a renderer.

    `post` is the host's HTTP — this module owns no client and opens no socket,
    which keeps the transport, and therefore the proxy and the timeout policy,
    the host's decision.
    """

    def __init__(self, post: Callable[[str, str, str], str] | None = None) -> None:
        self._post = post

    def _soap(self, control_url: str, action: str, inner: str) -> str:
        if self._post is None:
            raise CastException("no transport configured")
        body = (
            '<?xml version="1.0"?>'
            '<s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/"'
            ' s:encodingStyle="http://schemas.xmlsoap.org/soap/encoding/"><s:Body>'
            f'<u:{action} xmlns:u="{_AV_TRANSPORT}">{inner}</u:{action}>'
            "</s:Body></s:Envelope>"
        )
        # SOAPACTION must be QUOTED. An unquoted action is rejected by most
        # renderers with a 500 and no explanation, and it is the single most
        # common reason a first DLNA implementation does not work.
        quoted = f'"{_AV_TRANSPORT}#{action}"'
        try:
            return self._post(control_url, quoted, body)
        except Exception as exc:  # noqa: BLE001 — the reason is what matters
            raise CastControlException(action, str(exc)) from exc

    def set_av_transport_uri(self, control_url: str, uri: str, metadata: str) -> None:
        self._soap(
            control_url, "SetAVTransportURI",
            f"<InstanceID>0</InstanceID><CurrentURI>{escape(uri)}</CurrentURI>"
            f"<CurrentURIMetaData>{escape(metadata)}</CurrentURIMetaData>",
        )

    def play(self, control_url: str) -> None:
        self._soap(control_url, "Play", "<InstanceID>0</InstanceID><Speed>1</Speed>")

    def pause(self, control_url: str) -> None:
        self._soap(control_url, "Pause", "<InstanceID>0</InstanceID>")

    def stop(self, control_url: str) -> None:
        self._soap(control_url, "Stop", "<InstanceID>0</InstanceID>")

    def seek(self, control_url: str, position_seconds: float) -> None:
        self._soap(
            control_url, "Seek",
            "<InstanceID>0</InstanceID><Unit>REL_TIME</Unit>"
            f"<Target>{_format_upnp_time(position_seconds)}</Target>",
        )

    def get_position_info(self, control_url: str) -> CastStatus:
        body = self._soap(control_url, "GetPositionInfo", "<InstanceID>0</InstanceID>")
        return CastStatus(
            state=CastPlaybackState.PLAYING,
            position_seconds=_parse_upnp_time(_between_tags(body, "RelTime")),
            duration_seconds=_parse_upnp_time(_between_tags(body, "TrackDuration")),
            current_uri=_between_tags(body, "TrackURI"),
        )


# ─────────────────────────────────────────────────────────────────────────────
# Targets, discovery and sessions


class ICastTarget(ABC):
    """Something that can be cast to."""

    @property
    @abstractmethod
    def id(self) -> CastTargetId: ...

    @property
    @abstractmethod
    def friendly_name(self) -> str: ...

    @property
    @abstractmethod
    def protocol(self) -> CastProtocol: ...


@dataclass(frozen=True)
class DlnaCastTarget(ICastTarget):
    """One renderer on the LAN."""

    target_id: CastTargetId
    name: str
    control_url: str

    @property
    def id(self) -> CastTargetId:
        return self.target_id

    @property
    def friendly_name(self) -> str:
        return self.name

    @property
    def protocol(self) -> CastProtocol:
        return CastProtocol.DLNA


class ICastDiscovery(ABC):
    """Finds targets."""

    @abstractmethod
    def discover(self) -> Sequence[ICastTarget]: ...


class NullCastDiscovery(ICastDiscovery):
    """Finds nobody.

    The default, so a host with no discovery wired gets an empty list rather
    than a crash — and so no test sends multicast on somebody's network.
    """

    def discover(self) -> Sequence[ICastTarget]:
        return ()


class DlnaCastDiscovery(ICastDiscovery):
    """Finds renderers by SSDP."""

    def __init__(self, ssdp: SsdpClient, get: Callable[[str], str] | None = None) -> None:
        self._ssdp = ssdp
        self._get = get

    def discover(self) -> Sequence[ICastTarget]:
        if self._get is None:
            return ()
        out: list[ICastTarget] = []
        for reply in self._ssdp.search("urn:schemas-upnp-org:device:MediaRenderer:1"):
            try:
                body = self._get(reply.location)
                desc = parse_device_description(body, reply.location)
            except Exception:
                # One unreachable renderer must not fail the whole scan: a
                # television that answered and then went to sleep is normal.
                continue
            for svc in desc.services:
                if "AVTransport" in svc.service_type:
                    out.append(DlnaCastTarget(
                        CastTargetId(desc.udn), desc.friendly_name, svc.control_url))
                    break
        return out


class ILocalMediaHost(ABC):
    """Publishes bytes at a URL the television can fetch.

    This exists because THE RENDERER PULLS — which is the whole reason casting a
    file already on the device needs an HTTP server running on it.
    """

    @abstractmethod
    def publish(self, data: bytes, mime_type: str) -> str: ...

    @abstractmethod
    def publish_file(self, path: str, mime_type: str) -> str: ...

    @abstractmethod
    def unpublish(self, url: str) -> None: ...


class ICastSession(ABC):
    """Controls playback on one target."""

    @abstractmethod
    def load(self, media: CastMedia) -> None: ...

    @abstractmethod
    def play(self) -> None: ...

    @abstractmethod
    def pause(self) -> None: ...

    @abstractmethod
    def stop(self) -> None: ...

    @abstractmethod
    def seek(self, position_seconds: float) -> None: ...

    @abstractmethod
    def status(self) -> CastStatus: ...

    @abstractmethod
    def close(self) -> None: ...


class DlnaCastSession(ICastSession):
    """A session against one DLNA renderer."""

    def __init__(
        self,
        target: DlnaCastTarget,
        control: UpnpControlPoint,
        media_host: ILocalMediaHost | None = None,
    ) -> None:
        self._target = target
        self._control = control
        self._media_host = media_host
        self._lock = threading.Lock()
        self._published: list[str] = []

    def _uri_for(self, media: CastMedia) -> str:
        source = media.source
        if isinstance(source, Url):
            return source.value
        if self._media_host is None:
            raise CastException(
                "no local media host: the renderer pulls, so a file must be "
                "served from this device"
            )
        if isinstance(source, File):
            url = self._media_host.publish_file(source.path, media.mime_type)
        elif isinstance(source, Bytes):
            url = self._media_host.publish(source.data, media.mime_type)
        else:
            raise CastException("unknown media source")
        with self._lock:
            self._published.append(url)
        return url

    def load(self, media: CastMedia) -> None:
        uri = self._uri_for(media)
        metadata = DidlLite.build(media.title, media.mime_type, media.kind, uri)
        self._control.set_av_transport_uri(self._target.control_url, uri, metadata)

    def play(self) -> None:
        self._control.play(self._target.control_url)

    def pause(self) -> None:
        self._control.pause(self._target.control_url)

    def stop(self) -> None:
        self._control.stop(self._target.control_url)

    def seek(self, position_seconds: float) -> None:
        self._control.seek(self._target.control_url, position_seconds)

    def status(self) -> CastStatus:
        return self._control.get_position_info(self._target.control_url)

    def close(self) -> None:
        """Unpublishes anything this session served.

        A media host that keeps serving after the session ended is a file server
        for the whole network with no expiry.
        """
        with self._lock:
            published, self._published = self._published, []
        if self._media_host is not None:
            for url in published:
                try:
                    self._media_host.unpublish(url)
                except Exception:
                    continue


class ICastEngine(ABC):
    """Discovery plus control, assembled."""

    @abstractmethod
    def discover(self) -> Sequence[ICastTarget]: ...

    @abstractmethod
    def open(self, target: ICastTarget) -> ICastSession: ...


class DlnaCastEngine(ICastEngine):
    """The one entry point a host needs."""

    def __init__(
        self,
        discovery: ICastDiscovery,
        control: UpnpControlPoint,
        media_host: ILocalMediaHost | None = None,
    ) -> None:
        self._discovery = discovery
        self._control = control
        self._media_host = media_host

    def discover(self) -> Sequence[ICastTarget]:
        return self._discovery.discover()

    def open(self, target: ICastTarget) -> ICastSession:
        if not isinstance(target, DlnaCastTarget):
            raise CastException("not a DLNA target")
        return DlnaCastSession(target, self._control, self._media_host)


class LocalAddress:
    """Finds the LAN address this device is reachable at."""

    @staticmethod
    def best() -> str:
        """The first non-loopback IPv4 address, or "".

        IPv4 specifically: DLNA renderers in this market are overwhelmingly
        IPv4-only, and handing one an IPv6 URL produces a television that
        accepts the command and plays nothing.
        """
        sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        try:
            # Connecting a UDP socket sends nothing; it just picks the interface
            # the routing table would use, which is the one a renderer on the
            # same network can reach.
            sock.connect(("192.0.2.1", 9))
            return sock.getsockname()[0]
        except OSError:
            return ""
        finally:
            sock.close()


class TcpMediaHost(ILocalMediaHost):
    """A minimal HTTP host for cast media.

    Serves ONLY what has been published through it — no path is derived from a
    request. A media host that resolved paths from the URL is a file server for
    the whole network, reachable by anything on the same wifi.
    """

    def __init__(self, port: int, serve: Callable[[str, bytes, str], None] | None = None) -> None:
        address = LocalAddress.best()
        if not address:
            raise CastException("no LAN address: a renderer cannot fetch from this device")
        self._base = f"http://{address}:{port}"
        self._lock = threading.Lock()
        self._items: dict[str, tuple[bytes | None, str | None, str]] = {}
        self._next = 0
        self._serve = serve

    def _publish(self, data: bytes | None, path: str | None, mime_type: str) -> str:
        with self._lock:
            self._next += 1
            route = f"/media/{self._next}"
            self._items[route] = (data, path, mime_type)
        return self._base + route

    def publish(self, data: bytes, mime_type: str) -> str:
        return self._publish(data, None, mime_type)

    def publish_file(self, path: str, mime_type: str) -> str:
        return self._publish(None, path, mime_type)

    def unpublish(self, url: str) -> None:
        route = urlparse(url).path
        with self._lock:
            self._items.pop(route, None)

    @property
    def published_count(self) -> int:
        with self._lock:
            return len(self._items)


# ─────────────────────────────────────────────────────────────────────────────
# Documents


@dataclass(frozen=True)
class CastDocument:
    """A document on its way to a screen."""

    title: str
    data: bytes
    mime_type: str
    page_count: int = 1


class IDocumentCastAdapter(ABC):
    """Turns a document into something a television can show.

    Usually images, one per page, because televisions render almost no document
    format and the ones they do render inconsistently.
    """

    @abstractmethod
    def to_media(self, document: CastDocument, page_index: int) -> CastMedia: ...


class NullDocumentCastAdapter(IDocumentCastAdapter):
    """Converts nothing."""

    def to_media(self, document: CastDocument, page_index: int) -> CastMedia:
        raise CastException("no document adapter configured")
