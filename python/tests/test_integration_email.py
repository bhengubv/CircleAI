"""test_integration_email.py

Verifies the CircleAI.Integration.Email port: the Gmail connector (list/search,
base64url body decode, nested part traversal, mark-read), the Microsoft Graph
connector, and the IMAP connector over the injected in-memory transport
(unread search, newest-first take, Seen-flag semantics). C# is the spec.
"""
from __future__ import annotations

import base64
import json
from datetime import datetime, timezone

import pytest

from circle_ai.integration import InMemoryHttpFetcher, HttpResponse
from circle_ai.integration_email import (
    GmailEmailConnector,
    GmailOptions,
    ImapEmailConnector,
    ImapOptions,
    InMemoryImapTransport,
    MessageFlags,
    MsGraphEmailConnector,
    MsGraphEmailOptions,
)


def _token(value):
    async def _provider():
        return value

    return _provider


def _b64url(s: str) -> str:
    return base64.urlsafe_b64encode(s.encode("utf-8")).decode("ascii").rstrip("=")


# -- Gmail -----------------------------------------------------------------


async def test_gmail_list_unread_fetches_full_messages() -> None:
    f = InMemoryHttpFetcher()
    # list endpoint (no /messages/<id>) -> the ids list
    f.on(
        lambda r: "messages?q=" in r.url,
        HttpResponse(200, json.dumps({"messages": [{"id": "m1"}]})),
    )
    # full message
    f.on(
        lambda r: "messages/m1" in r.url,
        HttpResponse(
            200,
            json.dumps(
                {
                    "id": "m1",
                    "labelIds": ["INBOX", "UNREAD"],
                    "internalDate": "1704196800000",
                    "payload": {
                        "headers": [
                            {"name": "From", "value": "alice@x.com"},
                            {"name": "To", "value": "me@x.com, you@x.com"},
                            {"name": "Subject", "value": "Hi"},
                        ],
                        "body": {"data": _b64url("Hello body")},
                    },
                }
            ),
        ),
    )
    conn = GmailEmailConnector(GmailOptions(_token("tok")), f)
    assert conn.provider_id == "gmail"
    assert conn.is_configured is True
    msgs = await conn.list_unread_async(10)
    assert len(msgs) == 1
    m = msgs[0]
    assert m.message_id == "m1"
    assert m.from_ == "alice@x.com"
    assert list(m.to) == ["me@x.com", "you@x.com"]
    assert m.subject == "Hi"
    assert m.body_text == "Hello body"
    assert m.unread is True
    assert list(m.labels) == ["INBOX", "UNREAD"]
    assert m.received_utc == datetime.fromtimestamp(1704196800.0, tz=timezone.utc)


async def test_gmail_body_prefers_text_plain_part() -> None:
    f = InMemoryHttpFetcher()
    f.on(
        lambda r: "messages?q=" in r.url,
        HttpResponse(200, json.dumps({"messages": [{"id": "mp"}]})),
    )
    f.on(
        lambda r: "messages/mp" in r.url,
        HttpResponse(
            200,
            json.dumps(
                {
                    "id": "mp",
                    "labelIds": [],
                    "internalDate": "0",
                    "payload": {
                        "parts": [
                            {"mimeType": "text/html", "body": {"data": _b64url("<b>x</b>")}},
                            {"mimeType": "text/plain", "body": {"data": _b64url("plain text")}},
                        ]
                    },
                }
            ),
        ),
    )
    conn = GmailEmailConnector(GmailOptions(_token("tok")), f)
    msgs = await conn.search_async("q", 5)
    assert msgs[0].body_text == "plain text"
    assert msgs[0].unread is False


async def test_gmail_mark_read_posts_modify() -> None:
    f = InMemoryHttpFetcher().on_method("POST", HttpResponse(200, "{}"))
    conn = GmailEmailConnector(GmailOptions(_token("tok")), f)
    await conn.mark_read_async("mid")
    req = f.last_request
    assert req.url.endswith("messages/mid/modify")
    assert req.body_json == {"removeLabelIds": ["UNREAD"]}


async def test_gmail_search_validates_args() -> None:
    conn = GmailEmailConnector(GmailOptions(_token("tok")), InMemoryHttpFetcher())
    with pytest.raises(ValueError):
        await conn.search_async("", 5)
    with pytest.raises(ValueError):
        await conn.search_async("q", 0)


# -- Microsoft Graph -------------------------------------------------------


async def test_msgraph_list_unread_reads_messages() -> None:
    payload = {
        "value": [
            {
                "id": "g1",
                "subject": "Report",
                "isRead": False,
                "from": {"emailAddress": {"address": "boss@x.com"}},
                "toRecipients": [{"emailAddress": {"address": "me@x.com"}}],
                "receivedDateTime": "2024-04-01T08:00:00Z",
                "categories": ["Work"],
                "body": {"content": "the body"},
            }
        ]
    }
    f = InMemoryHttpFetcher().on_get(HttpResponse(200, json.dumps(payload)))
    conn = MsGraphEmailConnector(MsGraphEmailOptions(_token("tok")), f)
    assert conn.provider_id == "ms-graph-mail"
    msgs = await conn.list_unread_async(10)
    m = msgs[0]
    assert m.message_id == "g1"
    assert m.from_ == "boss@x.com"
    assert list(m.to) == ["me@x.com"]
    assert m.subject == "Report"
    assert m.body_text == "the body"
    assert m.unread is True
    assert list(m.labels) == ["Work"]
    assert m.received_utc.isoformat() == "2024-04-01T08:00:00+00:00"


async def test_msgraph_body_preview_fallback_and_read_flag() -> None:
    payload = {
        "value": [
            {
                "id": "g2",
                "subject": "S",
                "isRead": True,
                "bodyPreview": "preview only",
            }
        ]
    }
    f = InMemoryHttpFetcher().on_get(HttpResponse(200, json.dumps(payload)))
    conn = MsGraphEmailConnector(MsGraphEmailOptions(_token("tok")), f)
    msgs = await conn.search_async("q", 5)
    assert msgs[0].body_text == "preview only"
    assert msgs[0].unread is False  # isRead == True


async def test_msgraph_mark_read_patches() -> None:
    f = InMemoryHttpFetcher().on_method("PATCH", HttpResponse(200, "{}"))
    conn = MsGraphEmailConnector(MsGraphEmailOptions(_token("tok")), f)
    await conn.mark_read_async("g1")
    req = f.last_request
    assert req.method == "PATCH"
    assert req.url.endswith("me/messages/g1")
    assert req.body_json == {"isRead": True}


# -- IMAP ------------------------------------------------------------------


def _imap_transport() -> InMemoryImapTransport:
    t = InMemoryImapTransport()
    t.add(
        1,
        subject="Old read",
        from_address="a@x.com",
        to_addresses=["me@x.com"],
        date=datetime(2024, 1, 1, tzinfo=timezone.utc),
        flags=MessageFlags.SEEN,
        text_body="read body",
    )
    t.add(
        2,
        subject="Unread urgent",
        from_address="b@x.com",
        to_addresses=["me@x.com"],
        date=datetime(2024, 2, 1, tzinfo=timezone.utc),
        flags=MessageFlags.NONE,
        text_body="urgent body",
    )
    t.add(
        3,
        subject="Newest unread",
        from_address="c@x.com",
        to_addresses=["me@x.com", "cc@x.com"],
        date=datetime(2024, 3, 1, tzinfo=timezone.utc),
        flags=MessageFlags.FLAGGED,
        html_body="<p>html only</p>",
    )
    return t


def _imap(t: InMemoryImapTransport) -> ImapEmailConnector:
    return ImapEmailConnector(
        ImapOptions("imap.x.com", 993, True, "user", "pass"), t
    )


async def test_imap_is_configured() -> None:
    conn = _imap(InMemoryImapTransport())
    assert conn.provider_id == "imap"
    assert conn.is_configured is True
    bad = ImapEmailConnector(
        ImapOptions("", 993, True, "u", "p"), InMemoryImapTransport()
    )
    assert bad.is_configured is False


async def test_imap_list_unread_newest_first() -> None:
    conn = _imap(_imap_transport())
    msgs = await conn.list_unread_async(10)
    # UIDs 2 and 3 are unseen; ordered newest-first by UID descending -> 3, 2.
    assert [m.message_id for m in msgs] == ["3", "2"]
    newest = msgs[0]
    assert newest.subject == "Newest unread"
    assert list(newest.to) == ["me@x.com", "cc@x.com"]
    # text_body absent -> html fallback; C# does not strip HTML here.
    assert newest.body_text == "<p>html only</p>"
    assert newest.unread is True
    assert "Flagged" in newest.labels


async def test_imap_list_unread_respects_max() -> None:
    conn = _imap(_imap_transport())
    msgs = await conn.list_unread_async(1)
    assert [m.message_id for m in msgs] == ["3"]


async def test_imap_search_body_or_subject() -> None:
    conn = _imap(_imap_transport())
    msgs = await conn.search_async("urgent", 10)
    assert [m.message_id for m in msgs] == ["2"]


async def test_imap_mark_read_sets_seen_flag() -> None:
    t = _imap_transport()
    conn = _imap(t)
    assert (t.flags_of(2) & MessageFlags.SEEN) == 0
    await conn.mark_read_async("2")
    assert (t.flags_of(2) & MessageFlags.SEEN) == MessageFlags.SEEN
    # Now UID 2 is seen, so unread search returns only UID 3.
    msgs = await conn.list_unread_async(10)
    assert [m.message_id for m in msgs] == ["3"]


async def test_imap_mark_read_rejects_non_uid() -> None:
    conn = _imap(_imap_transport())
    with pytest.raises(ValueError):
        await conn.mark_read_async("not-a-number")


async def test_imap_seen_message_reports_read() -> None:
    conn = _imap(_imap_transport())
    # A body search that matches the read message shows unread == False.
    msgs = await conn.search_async("read body", 10)
    assert msgs[0].message_id == "1"
    assert msgs[0].unread is False
    assert "Seen" in msgs[0].labels
