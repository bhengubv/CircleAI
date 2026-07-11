# gmail_email_connector.py
#
# Port of CircleAI.Integration.Email/GmailEmailConnector.cs (C# — the EXACT
# spec).
#
# (Phase B2) Gmail API v1 client. Uses host-supplied OAuth tokens.
#
# The C# takes an injected ``HttpClient`` (base address
# https://gmail.googleapis.com/gmail/v1/users/me/) and an ``AccessTokenProvider``
# callback; the Python port takes an injected :class:`IHttpFetcher`, builds
# absolute URLs against that base, and attaches the Bearer header per request.
# Body decoding mirrors the C# base64url + nested-part ``text/plain`` search.

from __future__ import annotations

import base64
import binascii
from dataclasses import dataclass
from typing import Any, Awaitable, Callable, Dict, List, Optional
from urllib.parse import quote

from circle_ai.integration._util import from_unix_millis
from circle_ai.integration.contracts import EmailMessage, IEmailConnector
from circle_ai.integration.http import HttpRequest, IHttpFetcher

_BASE_URI = "https://gmail.googleapis.com/gmail/v1/users/me/"

AccessTokenProvider = Callable[[], Awaitable[Optional[str]]]


@dataclass(frozen=True, slots=True)
class GmailOptions:
    """Mirrors ``CircleAI.Integration.Email.GmailOptions`` — ``record(Func<
    CancellationToken, ValueTask<string?>> AccessTokenProvider)``.
    """

    access_token_provider: AccessTokenProvider


class GmailEmailConnector(IEmailConnector):
    """Port of ``CircleAI.Integration.Email.GmailEmailConnector``."""

    def __init__(self, opts: GmailOptions, http: IHttpFetcher) -> None:
        if opts is None:
            raise ValueError("opts must not be None")
        if http is None:
            raise ValueError("http must not be None")
        self._opts = opts
        self._http = http

    @property
    def provider_id(self) -> str:
        return "gmail"

    @property
    def is_configured(self) -> bool:
        return self._opts.access_token_provider is not None

    async def _ensure_auth(self) -> str:
        token = await self._opts.access_token_provider()
        if not (token and token.strip()):
            raise RuntimeError("Gmail access token unavailable; refresh OAuth.")
        return f"Bearer {token}"

    async def list_unread_async(self, max: int) -> List[EmailMessage]:
        return await self.search_async("is:unread", max)

    async def search_async(self, query: str, max: int) -> List[EmailMessage]:
        if not (query and query.strip()):
            raise ValueError("query required")
        if max <= 0:
            raise ValueError("max must be positive")
        auth = await self._ensure_auth()

        list_path = (
            f"messages?q={quote(query, safe='')}&maxResults={min(max, 100)}"
        )
        list_resp = (
            await self._http.send_async(
                HttpRequest("GET", _BASE_URI + list_path, {"Authorization": auth})
            )
        ).ensure_success()
        list_root = list_resp.json()

        ids: List[str] = []
        msgs = list_root.get("messages") if isinstance(list_root, dict) else None
        if isinstance(msgs, list):
            for m in msgs:
                if isinstance(m, dict) and "id" in m:
                    ids.append(m.get("id") or "")

        result: List[EmailMessage] = []
        for mid in ids:
            get_resp = await self._http.send_async(
                HttpRequest(
                    "GET",
                    _BASE_URI + f"messages/{quote(mid, safe='')}?format=full",
                    {"Authorization": auth},
                )
            )
            if not get_resp.is_success:
                continue
            result.append(_parse_gmail_message(get_resp.json()))
        return result

    async def mark_read_async(self, message_id: str) -> None:
        if not (message_id and message_id.strip()):
            raise ValueError("messageId required")
        auth = await self._ensure_auth()
        resp = await self._http.send_async(
            HttpRequest(
                "POST",
                _BASE_URI + f"messages/{quote(message_id, safe='')}/modify",
                {"Authorization": auth},
                body_json={"removeLabelIds": ["UNREAD"]},
            )
        )
        resp.ensure_success()


def _parse_gmail_message(msg: Any) -> EmailMessage:
    if not isinstance(msg, dict):
        msg = {}
    mid = msg.get("id") or ""
    labels: List[str] = []
    labs = msg.get("labelIds")
    if isinstance(labs, list):
        for lab in labs:
            labels.append(lab or "")
    unread = any(l.upper() == "UNREAD" for l in labels)
    headers: Dict[str, str] = {}
    payload = msg.get("payload")
    if isinstance(payload, dict):
        hs = payload.get("headers")
        if isinstance(hs, list):
            for h in hs:
                if isinstance(h, dict) and "name" in h and "value" in h:
                    headers[(h.get("name") or "").lower()] = h.get("value") or ""
    body_text = _extract_body(payload if isinstance(payload, dict) else None)
    received_ms = 0
    date_el = msg.get("internalDate")
    if isinstance(date_el, str):
        try:
            received_ms = int(date_el)
        except ValueError:
            received_ms = 0

    to_raw = headers.get("to")
    to = (
        [x.strip() for x in to_raw.split(",") if x.strip()]
        if to_raw
        else []
    )
    return EmailMessage(
        message_id=mid,
        from_=headers.get("from", ""),
        to=to,
        subject=headers.get("subject", ""),
        body_text=body_text,
        # C# DateTimeOffset.FromUnixTimeMilliseconds(receivedMs).UtcDateTime;
        # receivedMs defaults to 0 (epoch) when internalDate is absent.
        received_utc=from_unix_millis(received_ms),
        unread=unread,
        labels=labels,
    )


def _extract_body(payload: Optional[dict]) -> str:
    if not isinstance(payload, dict):
        return ""
    body = payload.get("body")
    if isinstance(body, dict):
        data = body.get("data")
        if isinstance(data, str):
            return _decode_base64url(data)
    parts = payload.get("parts")
    if isinstance(parts, list):
        # Prefer a text/plain part first (matches the C# two-pass search).
        for part in parts:
            if isinstance(part, dict):
                mime = part.get("mimeType")
                if isinstance(mime, str) and mime.lower() == "text/plain":
                    return _extract_body(part)
        for part in parts:
            if isinstance(part, dict):
                content = _extract_body(part)
                if content:
                    return content
    return ""


def _decode_base64url(s: str) -> str:
    if not s:
        return ""
    s = s.replace("-", "+").replace("_", "/")
    padding = len(s) % 4
    if padding > 0:
        s = s + "=" * (4 - padding)
    try:
        return base64.b64decode(s).decode("utf-8")
    except (binascii.Error, ValueError, UnicodeDecodeError):
        return ""
