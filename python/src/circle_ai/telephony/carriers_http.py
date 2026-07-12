# carriers_http.py
#
# Shared HTTP plumbing for the Twilio / Telnyx / Plivo carrier ports
# (CircleAI.Telephony.Twilio / .Telnyx / .Plivo — C# is the EXACT spec).
#
# The C# carriers each take a ``System.Net.Http.HttpClient`` whose ``BaseAddress``
# is the provider host and whose ``DefaultRequestHeaders.Authorization`` is a
# Basic/Bearer credential; they then issue requests against relative paths. The
# Python ports inject the shared ``circle_ai.integration.http.IHttpFetcher``
# instead — so this module reproduces the pieces the ``HttpClient`` did for free:
#
#   * ``combine_uri(base, path)`` — join the base address + a possibly-query'd
#     relative path the way ``HttpClient`` resolves a relative request URI.
#   * ``basic_auth`` / ``bearer_auth`` — the exact ``Authorization`` header value
#     the C# built (``Convert.ToBase64String("sid:token")`` / ``"Bearer key"``).
#   * ``form_urlencoded`` — the wire body + content-type of
#     ``FormUrlEncodedContent`` (``application/x-www-form-urlencoded``,
#     ``Uri.EscapeDataString``-equivalent percent-encoding, ``&``-joined).
#   * ``parse_decimal`` — the ``JsonElement`` number/string -> ``Decimal`` reader
#     the carriers use for pricing (mirrors ``decimal.TryParse(NumberStyles.Any,
#     InvariantCulture)``), returning ``None`` on absent/unparseable.
#   * ``escape_data_string`` — ``Uri.EscapeDataString`` for building query paths.
#
# No socket is opened here; everything routes through the injected fetcher.

from __future__ import annotations

import base64 as _base64
from decimal import Decimal, InvalidOperation
from typing import Iterable, Mapping, Optional, Sequence, Tuple
from urllib.parse import quote, urlsplit, urlunsplit

FORM_CONTENT_TYPE = "application/x-www-form-urlencoded"
JSON_CONTENT_TYPE = "application/json"


def escape_data_string(value: str) -> str:
    """C# ``Uri.EscapeDataString`` — percent-encode everything but the RFC 3986
    unreserved set (``A-Z a-z 0-9 - _ . ~``)."""
    return quote(value, safe="-_.~")


def combine_uri(base_address: str, path: str) -> str:
    """Resolve a relative request path against a base address the way
    ``HttpClient`` does when ``BaseAddress`` is set and the request URI is
    relative: the base's scheme+host (and any base path prefix) is kept and the
    relative path (which here always starts with ``/`` and may carry a query)
    replaces the base path. Because every carrier path in the C# begins with
    ``/`` (an absolute path), this reduces to scheme+authority + path + query.
    """
    base = urlsplit(base_address)
    rel = urlsplit(path)
    # rel.path always starts with "/" in the ported call sites -> absolute path,
    # so it replaces the base path entirely (matching Uri resolution).
    new_path = rel.path if rel.path.startswith("/") else _merge_path(base.path, rel.path)
    return urlunsplit((base.scheme, base.netloc, new_path, rel.query, rel.fragment))


def _merge_path(base_path: str, rel_path: str) -> str:
    if not base_path:
        return "/" + rel_path
    cut = base_path.rfind("/")
    prefix = base_path[: cut + 1] if cut >= 0 else ""
    return prefix + rel_path


def basic_auth(user: str, password: str) -> str:
    """C# ``new AuthenticationHeaderValue("Basic",
    Convert.ToBase64String(UTF8("user:password")))`` -> the full header value."""
    token = _base64.b64encode(f"{user}:{password}".encode("utf-8")).decode("ascii")
    return f"Basic {token}"


def bearer_auth(key: str) -> str:
    """C# ``new AuthenticationHeaderValue("Bearer", key)`` -> the full header value."""
    return f"Bearer {key}"


def form_urlencoded(pairs: Iterable[Tuple[str, str]]) -> bytes:
    """Wire body of ``FormUrlEncodedContent``.

    ``FormUrlEncodedContent`` percent-encodes each key and value and joins
    ``key=value`` pairs with ``&``. It encodes spaces as ``+`` and escapes the
    reserved set; we reproduce that with ``quote_via`` semantics equivalent to
    ``urlencode`` (``quote_plus``). Returns UTF-8 bytes so it rides the injected
    fetcher's ``body_bytes`` seam with an explicit content-type.
    """
    from urllib.parse import quote_plus

    encoded = "&".join(f"{quote_plus(k)}={quote_plus(v)}" for k, v in pairs)
    return encoded.encode("utf-8")


def parse_decimal(element: Optional[Mapping[str, object]], prop: str) -> Optional[Decimal]:
    """Mirror the carriers' ``ParseDecimal(JsonElement, property)``: read a JSON
    number or numeric string as :class:`Decimal`; return ``None`` when the
    property is absent or not parseable. Booleans are rejected (JSON numbers only).
    """
    if not isinstance(element, dict) or prop not in element:
        return None
    raw = element.get(prop)
    if isinstance(raw, bool):
        return None
    if isinstance(raw, (int, float)):
        try:
            return Decimal(str(raw))
        except (InvalidOperation, ValueError):
            return None
    if isinstance(raw, str):
        try:
            return Decimal(raw.strip())
        except (InvalidOperation, ValueError):
            return None
    return None


def sample_rate_for_format(media_format: "object") -> int:
    """The ``Info.MediaFormat switch`` the sessions use to pick a DTMF sample
    rate: PCM16000 -> 16000, PCM24000 -> 24000, everything else -> 8000."""
    from .primitives import CallMediaFormat

    if media_format == CallMediaFormat.PCM16000:
        return 16000
    if media_format == CallMediaFormat.PCM24000:
        return 24000
    return 8000


__all__ = [
    "FORM_CONTENT_TYPE",
    "JSON_CONTENT_TYPE",
    "escape_data_string",
    "combine_uri",
    "basic_auth",
    "bearer_auth",
    "form_urlencoded",
    "parse_decimal",
    "sample_rate_for_format",
]
