# vision/cloud/http_transport.py
#
# The injected HTTP seam for the CircleAI.Vision.Cloud generators. The C#
# reference drives ``System.Net.Http.HttpClient`` directly; every Python cloud
# port in this tree injects the socket/HTTP leg behind an interface (see how the
# hosting cloud-fallback + speech.cloud ports treat real providers as injected).
#
# This module declares the request/response shapes that carry EXACTLY what the C#
# HttpRequestMessage carries — method, path, headers, and either a JSON body or a
# multipart form — plus the ``IImageHttpTransport`` seam and a deterministic
# in-memory transport for tests. No real socket is opened here.

from __future__ import annotations

from abc import ABC, abstractmethod
from dataclasses import dataclass, field
from typing import Callable, Dict, List, Optional, Tuple


@dataclass(frozen=True, slots=True)
class HttpRequest:
    """One outbound request. Mirrors the pieces the C# ``HttpRequestMessage`` sets.

    :param method: HTTP verb, e.g. "POST".
    :param base_address: Base URL the client is bound to.
    :param path: Request path appended to the base address (e.g. "/v1/images/generations").
    :param headers: Flat header map (Authorization / Accept live here).
    :param json_body: JSON payload dict when the C# used ``JsonContent.Create`` — else None.
    :param form_fields: Multipart form fields (ordered) when the C# used
        ``MultipartFormDataContent`` — else None.
    """

    method: str
    base_address: str
    path: str
    headers: Dict[str, str] = field(default_factory=dict)
    json_body: Optional[Dict[str, object]] = None
    form_fields: Optional[Tuple[Tuple[str, str], ...]] = None


@dataclass(frozen=True, slots=True)
class HttpResponse:
    """One response. Mirrors the bits the C# reads back off ``HttpResponseMessage``.

    :param status_code: Numeric HTTP status.
    :param body_text: Body decoded as text (used for error logging + JSON parse).
    :param body_bytes: Raw body bytes (Stability returns the image inline as bytes).
    """

    status_code: int
    body_text: str = ""
    body_bytes: bytes = b""

    @property
    def is_success_status_code(self) -> bool:
        """Mirrors ``HttpResponseMessage.IsSuccessStatusCode`` (200-299)."""
        return 200 <= self.status_code <= 299


class IImageHttpTransport(ABC):
    """Injected HTTP seam standing in for the C# ``HttpClient.SendAsync``."""

    @abstractmethod
    async def send_async(self, request: HttpRequest, ct: object = None) -> HttpResponse:
        ...


class InMemoryImageHttpTransport(IImageHttpTransport):
    """Deterministic in-memory transport: routes each request through a supplied
    handler ``(HttpRequest) -> HttpResponse``. Records every request for
    assertions. Keeps the generators fully exercisable without a network.
    """

    def __init__(self, handler: Callable[[HttpRequest], HttpResponse]) -> None:
        if handler is None:
            raise ValueError("handler")
        self._handler = handler
        self.requests: List[HttpRequest] = []

    async def send_async(self, request: HttpRequest, ct: object = None) -> HttpResponse:
        self.requests.append(request)
        return self._handler(request)


__all__ = [
    "HttpRequest",
    "HttpResponse",
    "IImageHttpTransport",
    "InMemoryImageHttpTransport",
]
