# local_dev_tunnel.py
#
# Port of CircleAI.Telephony LocalDevTunnel.cs (C# — the EXACT spec).
#
# (3.3.0) Local-dev tunnel resolver. A voice loop needs an internet-reachable
# webhook URL even when running locally. This abstraction lets dev
# configurations route through Cloudflare Tunnel, ngrok, or a manually-pinned
# static URL — same interface, different backing.
#
# C# Uri -> str (the IsAbsoluteUri guard maps to a scheme+netloc check). C#
# Func<int, CancellationToken, ValueTask<Uri>> resolver -> an async Callable. The
# static ``Instance`` singleton on NullLocalDevTunnel maps to a module-level
# singleton. C# InvalidOperationException -> RuntimeError.

from __future__ import annotations

from abc import ABC, abstractmethod
from typing import Awaitable, Callable, Optional
from urllib.parse import urlsplit

# C# Func<int, CancellationToken, ValueTask<Uri>>.
TunnelResolver = Callable[[int, Optional[object]], Awaitable[str]]


def _is_absolute_uri(uri: str) -> bool:
    parts = urlsplit(uri)
    return bool(parts.scheme) and bool(parts.netloc)


class ILocalDevTunnel(ABC):
    """(3.3.0) Resolves a public, internet-reachable URL that maps to a local port."""

    @property
    @abstractmethod
    def provider_id(self) -> str:
        """Identifier — "cloudflare", "ngrok", "static", "null"."""

    @property
    @abstractmethod
    def is_available(self) -> bool:
        """Whether this resolver is configured/available."""

    @abstractmethod
    async def get_public_url_async(self, local_port: int, *, ct: Optional[object] = None) -> str:
        """Resolve the public URL forwarding to ``local_port``."""


class NullLocalDevTunnel(ILocalDevTunnel):
    """(3.3.0) DI-default that throws — host wires a real tunnel."""

    Instance: "NullLocalDevTunnel"

    @property
    def provider_id(self) -> str:
        return "null"

    @property
    def is_available(self) -> bool:
        return False

    async def get_public_url_async(self, local_port: int, *, ct: Optional[object] = None) -> str:
        raise RuntimeError(
            "No local-dev tunnel is configured. Register a CloudflareTunnel / NgrokTunnel / StaticTunnel."
        )


class StaticLocalDevTunnel(ILocalDevTunnel):
    """(3.3.0) Static-URL tunnel — caller supplies the public URL up front (best for CI)."""

    def __init__(self, public_url: str) -> None:
        if public_url is None:
            raise ValueError("public_url must not be None")
        if not _is_absolute_uri(public_url):
            raise ValueError("publicUrl must be absolute.")
        self._public_url = public_url

    @property
    def provider_id(self) -> str:
        return "static"

    @property
    def is_available(self) -> bool:
        return True

    async def get_public_url_async(self, local_port: int, *, ct: Optional[object] = None) -> str:
        return self._public_url


class CloudflareTunnel(ILocalDevTunnel):
    """(3.3.0) Cloudflare Tunnel resolver. Host must point at the cloudflared output URL."""

    def __init__(self, resolver: TunnelResolver) -> None:
        if resolver is None:
            raise ValueError("resolver must not be None")
        self._resolver = resolver

    @property
    def provider_id(self) -> str:
        return "cloudflare"

    @property
    def is_available(self) -> bool:
        return True

    async def get_public_url_async(self, local_port: int, *, ct: Optional[object] = None) -> str:
        return await self._resolver(local_port, ct)


class NgrokTunnel(ILocalDevTunnel):
    """(3.3.0) ngrok tunnel resolver."""

    def __init__(self, resolver: TunnelResolver) -> None:
        if resolver is None:
            raise ValueError("resolver must not be None")
        self._resolver = resolver

    @property
    def provider_id(self) -> str:
        return "ngrok"

    @property
    def is_available(self) -> bool:
        return True

    async def get_public_url_async(self, local_port: int, *, ct: Optional[object] = None) -> str:
        return await self._resolver(local_port, ct)


NullLocalDevTunnel.Instance = NullLocalDevTunnel()
