# ms_graph_email_connector.py
#
# Port of CircleAI.Integration.Email/MsGraphEmailConnector.cs (C# — the EXACT
# spec).
#
# (Phase B2) Microsoft Graph v1.0 client for Outlook / Microsoft 365 mail.
#
# The C# takes an injected ``HttpClient`` (base address
# https://graph.microsoft.com/v1.0/) and an ``AccessTokenProvider`` callback;
# the Python port takes an injected :class:`IHttpFetcher`, builds absolute URLs
# against that base, and attaches the Bearer header per request.

from __future__ import annotations

from dataclasses import dataclass
from typing import Any, Awaitable, Callable, List, Optional
from urllib.parse import quote

from circle_ai.integration._util import DATETIME_MIN, parse_utc
from circle_ai.integration.contracts import EmailMessage, IEmailConnector
from circle_ai.integration.http import HttpRequest, IHttpFetcher

_BASE_URI = "https://graph.microsoft.com/v1.0/"

AccessTokenProvider = Callable[[], Awaitable[Optional[str]]]


@dataclass(frozen=True, slots=True)
class MsGraphEmailOptions:
    """Mirrors ``CircleAI.Integration.Email.MsGraphEmailOptions`` — ``record(
    Func<CancellationToken, ValueTask<string?>> AccessTokenProvider)``.
    """

    access_token_provider: AccessTokenProvider


class MsGraphEmailConnector(IEmailConnector):
    """Port of ``CircleAI.Integration.Email.MsGraphEmailConnector``."""

    def __init__(self, opts: MsGraphEmailOptions, http: IHttpFetcher) -> None:
        if opts is None:
            raise ValueError("opts must not be None")
        if http is None:
            raise ValueError("http must not be None")
        self._opts = opts
        self._http = http

    @property
    def provider_id(self) -> str:
        return "ms-graph-mail"

    @property
    def is_configured(self) -> bool:
        return self._opts.access_token_provider is not None

    async def _ensure_auth(self) -> str:
        token = await self._opts.access_token_provider()
        if not (token and token.strip()):
            raise RuntimeError(
                "Microsoft Graph access token unavailable; refresh OAuth."
            )
        return f"Bearer {token}"

    async def list_unread_async(self, max: int) -> List[EmailMessage]:
        auth = await self._ensure_auth()
        path = (
            "me/mailFolders('Inbox')/messages?$filter=isRead+eq+false"
            f"&$top={min(max, 50)}&$orderby=receivedDateTime+desc"
        )
        resp = (
            await self._http.send_async(
                HttpRequest("GET", _BASE_URI + path, {"Authorization": auth})
            )
        ).ensure_success()
        return _read_messages(resp.json())

    async def search_async(self, query: str, max: int) -> List[EmailMessage]:
        if not (query and query.strip()):
            raise ValueError("query required")
        auth = await self._ensure_auth()
        path = (
            f"me/messages?$search={quote(query, safe='')}&$top={min(max, 50)}"
        )
        resp = (
            await self._http.send_async(
                HttpRequest("GET", _BASE_URI + path, {"Authorization": auth})
            )
        ).ensure_success()
        return _read_messages(resp.json())

    async def mark_read_async(self, message_id: str) -> None:
        if not (message_id and message_id.strip()):
            raise ValueError("messageId required")
        auth = await self._ensure_auth()
        resp = await self._http.send_async(
            HttpRequest(
                "PATCH",
                _BASE_URI + f"me/messages/{quote(message_id, safe='')}",
                {"Authorization": auth},
                body_json={"isRead": True},
            )
        )
        resp.ensure_success()


def _read_messages(root: Any) -> List[EmailMessage]:
    result: List[EmailMessage] = []
    arr = root.get("value") if isinstance(root, dict) else None
    if not isinstance(arr, list):
        return result
    for m in arr:
        if not isinstance(m, dict):
            continue
        to: List[str] = []
        rcpts = m.get("toRecipients")
        if isinstance(rcpts, list):
            for r in rcpts:
                if not isinstance(r, dict):
                    continue
                ea = r.get("emailAddress")
                if isinstance(ea, dict) and "address" in ea:
                    to.append(ea.get("address") or "")
        from_addr = ""
        fr = m.get("from")
        if isinstance(fr, dict):
            fea = fr.get("emailAddress")
            if isinstance(fea, dict) and "address" in fea:
                from_addr = fea.get("address") or ""
        received = DATETIME_MIN
        rd = m.get("receivedDateTime")
        if isinstance(rd, str):
            received = parse_utc(rd)
        labels: List[str] = []
        cats = m.get("categories")
        if isinstance(cats, list):
            for c in cats:
                labels.append(c or "")
        body = ""
        b = m.get("body")
        if isinstance(b, dict) and "content" in b:
            body = b.get("content") or ""
        elif "bodyPreview" in m:
            body = m.get("bodyPreview") or ""
        # Unread == isRead is present AND false (C# JsonValueKind.False check).
        unread = m.get("isRead") is False
        result.append(
            EmailMessage(
                message_id=m.get("id") or "",
                from_=from_addr,
                to=to,
                subject=m.get("subject") or "",
                body_text=body,
                received_utc=received,
                unread=unread,
                labels=labels,
            )
        )
    return result
