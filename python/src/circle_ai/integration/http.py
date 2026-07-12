# http.py
#
# Injectable async HTTP abstraction for the integration connectors.
#
# The C# connectors take a ``System.Net.Http.HttpClient`` as an injected
# dependency and parse the JSON / XML / text it returns. To port that logic
# faithfully *without any real network*, we inject an :class:`IHttpFetcher`
# instead: the connectors build an :class:`HttpRequest`, call
# :meth:`IHttpFetcher.send_async`, and parse the returned :class:`HttpResponse`
# exactly as the C# reads ``HttpResponseMessage``.
#
# The in-memory :class:`InMemoryHttpFetcher` replays canned responses keyed by a
# ``(method, url-predicate)`` route and records every request it received, so
# tests are fully deterministic. This mirrors the C# design where a fake
# ``HttpMessageHandler`` is injected under test.
#
# Status-code handling mirrors ``HttpResponseMessage``:
#   * :attr:`HttpResponse.is_success` == 200..299 (C# ``IsSuccessStatusCode``).
#   * :meth:`HttpResponse.ensure_success` raises on non-2xx (C#
#     ``EnsureSuccessStatusCode``).

from __future__ import annotations

import json as _json
from dataclasses import dataclass, field
from typing import Any, Callable, List, Mapping, Optional, Tuple


class HttpError(Exception):
    """Raised by :meth:`HttpResponse.ensure_success` for a non-2xx status.

    Mirrors the ``HttpRequestException`` thrown by C#
    ``HttpResponseMessage.EnsureSuccessStatusCode``.
    """

    def __init__(self, status_code: int, reason: str = "") -> None:
        self.status_code = status_code
        super().__init__(
            f"Response status code does not indicate success: {status_code}"
            + (f" ({reason})" if reason else "")
        )


@dataclass(frozen=True, slots=True)
class HttpRequest:
    """A minimal HTTP request the connectors build and hand to a fetcher.

    ``body_json`` mirrors ``PostAsJsonAsync`` / ``JsonContent.Create`` payloads;
    ``body_text`` mirrors ``StringContent`` (XML, iCalendar). Only one is set.
    ``headers`` carries per-request headers (Authorization, Depth, X-Api-Key…).

    ``body_bytes`` carries a raw binary body (C# ``ByteArrayContent`` /
    ``MultipartFormDataContent`` / ``StringContent`` sent as bytes) — used by the
    telephony carriers (form-urlencoded) and the speech.cloud STT adapters (WAV
    upload / multipart). ``content_type`` is the ``Content-Type`` the C# set on
    that content (e.g. ``application/x-www-form-urlencoded``, ``audio/wav``,
    ``multipart/form-data; boundary=…``). Only one of ``body_json`` /
    ``body_text`` / ``body_bytes`` is set on any one request.
    """

    method: str
    url: str
    headers: Mapping[str, str] = field(default_factory=dict)
    body_json: Optional[Any] = None
    body_text: Optional[str] = None
    body_bytes: Optional[bytes] = None
    content_type: Optional[str] = None


@dataclass(frozen=True, slots=True)
class HttpResponse:
    """A minimal HTTP response — the parsed shape of ``HttpResponseMessage``.

    ``text`` is the raw body (used for XML / iCalendar). :meth:`json` parses it
    as JSON (used everywhere the C# calls ``JsonDocument.ParseAsync``).

    ``body_bytes`` is the raw binary body (C#
    ``HttpResponseMessage.Content.ReadAsByteArrayAsync``) — the speech.cloud TTS
    adapters return audio bytes here. When only ``text`` is provided,
    ``body_bytes`` defaults to that text UTF-8-encoded, so a caller reading bytes
    off a text response still gets the payload.
    """

    status_code: int
    text: str = ""
    reason: str = ""
    body_bytes: bytes = b""

    @property
    def is_success(self) -> bool:
        """C# ``HttpResponseMessage.IsSuccessStatusCode`` — 200..299."""
        return 200 <= self.status_code <= 299

    @property
    def content_bytes(self) -> bytes:
        """Raw response body as bytes (C# ``ReadAsByteArrayAsync``).

        Prefers an explicit ``body_bytes``; otherwise falls back to ``text``
        encoded as UTF-8 so a bytes-oriented caller still sees a text body.
        """
        if self.body_bytes:
            return self.body_bytes
        return self.text.encode("utf-8") if self.text else b""

    def ensure_success(self) -> "HttpResponse":
        """C# ``EnsureSuccessStatusCode`` — raise :class:`HttpError` on non-2xx."""
        if not self.is_success:
            raise HttpError(self.status_code, self.reason)
        return self

    def json(self) -> Any:
        """Parse the body as JSON (C# ``JsonDocument.ParseAsync``)."""
        return _json.loads(self.text)


class IHttpFetcher:
    """Injected async HTTP transport. Real hosts wire a network implementation;
    tests inject :class:`InMemoryHttpFetcher`.
    """

    async def send_async(self, request: HttpRequest) -> HttpResponse:
        raise NotImplementedError  # pragma: no cover - interface marker


# A route matches a request and returns the response to serve.
_Matcher = Callable[[HttpRequest], bool]


class InMemoryHttpFetcher(IHttpFetcher):
    """Deterministic, in-memory :class:`IHttpFetcher`.

    Register routes with :meth:`on` (predicate) or the ``on_*`` helpers. The
    first matching route wins; each served request is appended to
    :attr:`requests` for assertions. An unmatched request serves ``default``
    (a 404 by default), mirroring a fake handler's fall-through.
    """

    def __init__(self, default: Optional[HttpResponse] = None) -> None:
        self._routes: List[Tuple[_Matcher, HttpResponse]] = []
        self._default = default if default is not None else HttpResponse(404, "")
        self.requests: List[HttpRequest] = []

    # -- registration ------------------------------------------------------

    def on(self, matcher: _Matcher, response: HttpResponse) -> "InMemoryHttpFetcher":
        """Serve ``response`` for any request satisfying ``matcher``."""
        self._routes.append((matcher, response))
        return self

    def on_method(
        self, method: str, response: HttpResponse
    ) -> "InMemoryHttpFetcher":
        """Serve ``response`` for any request using ``method`` (case-insensitive)."""
        m = method.upper()
        return self.on(lambda r: r.method.upper() == m, response)

    def on_get(self, response: HttpResponse) -> "InMemoryHttpFetcher":
        return self.on_method("GET", response)

    def on_url_contains(
        self, needle: str, response: HttpResponse, method: Optional[str] = None
    ) -> "InMemoryHttpFetcher":
        """Serve ``response`` when the URL contains ``needle`` (and, optionally,
        the method matches).
        """
        mu = method.upper() if method else None

        def _match(r: HttpRequest) -> bool:
            if mu is not None and r.method.upper() != mu:
                return False
            return needle in r.url

        return self.on(_match, response)

    # -- dispatch ----------------------------------------------------------

    async def send_async(self, request: HttpRequest) -> HttpResponse:
        self.requests.append(request)
        for matcher, response in self._routes:
            if matcher(request):
                return response
        return self._default

    # -- convenience for assertions ---------------------------------------

    @property
    def last_request(self) -> Optional[HttpRequest]:
        return self.requests[-1] if self.requests else None
