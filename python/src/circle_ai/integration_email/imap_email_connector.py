# imap_email_connector.py
#
# Port of CircleAI.Integration.Email/ImapEmailConnector.cs (C# — the EXACT
# spec).
#
# (Phase B2) Generic IMAP client. The C# is backed by MailKit — a native TCP
# IMAP client — which is the injected external dependency here. To port the
# connector's domain logic (unread search, newest-first ordering + take,
# envelope -> EmailMessage mapping, Seen-flag semantics) with no real network,
# the Python port injects an :class:`IImapTransport`: a thin, MailKit-shaped
# abstraction over folders, UIDs, envelopes and flags. The in-memory
# :class:`InMemoryImapTransport` makes it fully deterministic.
#
# MailKit flag semantics preserved:
#   * ``NotSeen`` search -> unread messages.
#   * ``BodyContains(q) OR SubjectContains(q)`` search.
#   * Newest-first is ``OrderByDescending(u => u.Id)`` on the UID, then ``Take``.
#   * ``Unread`` == the ``Seen`` flag is *not* set.
#   * Labels == the set flag names (excluding ``None``).

from __future__ import annotations

from dataclasses import dataclass, field
from datetime import datetime, timezone
from enum import IntFlag
from typing import Dict, List, Optional, Sequence

from circle_ai.integration.contracts import EmailMessage, IEmailConnector


class MessageFlags(IntFlag):
    """Subset of MailKit ``MessageFlags`` used by the connector.

    Values mirror MailKit's ``[Flags]`` enum ordinals so bitwise checks match.
    """

    NONE = 0
    SEEN = 1
    ANSWERED = 2
    FLAGGED = 4
    DELETED = 8
    DRAFT = 16
    RECENT = 32


@dataclass(frozen=True, slots=True)
class ImapEnvelope:
    """Mirrors the fields of MailKit ``Envelope`` the connector reads.

    ``from_address`` / ``to_addresses`` are the mailbox address strings
    (``env.From.Mailboxes.FirstOrDefault().Address`` and
    ``env.To.Mailboxes.Select(m => m.Address)``). ``date`` is the sent date.
    """

    subject: str = ""
    from_address: Optional[str] = None
    to_addresses: Sequence[str] = field(default_factory=tuple)
    date: Optional[datetime] = None


@dataclass(frozen=True, slots=True)
class ImapSummary:
    """Mirrors the MailKit ``IMessageSummary`` fields the connector reads —
    the UID, its :class:`ImapEnvelope`, and the message flags (may be absent).
    """

    uid: int
    envelope: Optional[ImapEnvelope]
    flags: Optional[MessageFlags]


@dataclass(frozen=True, slots=True)
class ImapMessage:
    """Mirrors MailKit ``MimeMessage`` — only ``TextBody`` / ``HtmlBody``."""

    text_body: Optional[str] = None
    html_body: Optional[str] = None


class ImapSearchQuery:
    """A parsed search request handed to the transport, replacing MailKit's
    ``SearchQuery``. Either ``not_seen`` is true, or ``contains`` holds the
    body/subject substring.
    """

    __slots__ = ("not_seen", "contains")

    def __init__(self, *, not_seen: bool = False, contains: Optional[str] = None):
        self.not_seen = not_seen
        self.contains = contains

    @staticmethod
    def not_seen_query() -> "ImapSearchQuery":
        return ImapSearchQuery(not_seen=True)

    @staticmethod
    def body_or_subject(query: str) -> "ImapSearchQuery":
        return ImapSearchQuery(contains=query)


class IImapFolder:
    """MailKit ``IMailFolder``-shaped surface the connector drives."""

    async def search_async(self, query: ImapSearchQuery) -> List[int]:
        raise NotImplementedError  # pragma: no cover

    async def fetch_async(self, uids: Sequence[int]) -> List[ImapSummary]:
        raise NotImplementedError  # pragma: no cover

    async def get_message_async(self, uid: int) -> ImapMessage:
        raise NotImplementedError  # pragma: no cover

    async def add_seen_flag_async(self, uid: int) -> None:
        raise NotImplementedError  # pragma: no cover


class IImapTransport:
    """Injected IMAP transport. Real hosts wrap MailKit; tests inject
    :class:`InMemoryImapTransport`.
    """

    async def open_folder_async(
        self, folder: str, read_write: bool
    ) -> IImapFolder:
        raise NotImplementedError  # pragma: no cover


@dataclass(frozen=True, slots=True)
class ImapOptions:
    """Mirrors ``CircleAI.Integration.Email.ImapOptions`` — ``record(string Host,
    int Port, bool UseSsl, string Username, string Password,
    string Folder = "INBOX")``.
    """

    host: str
    port: int
    use_ssl: bool
    username: str
    password: str
    folder: str = "INBOX"


class ImapEmailConnector(IEmailConnector):
    """Port of ``CircleAI.Integration.Email.ImapEmailConnector``."""

    def __init__(self, opts: ImapOptions, transport: IImapTransport) -> None:
        if opts is None:
            raise ValueError("opts must not be None")
        if transport is None:
            raise ValueError("transport must not be None")
        self._opts = opts
        self._transport = transport

    @property
    def provider_id(self) -> str:
        return "imap"

    @property
    def is_configured(self) -> bool:
        return (
            bool(self._opts.host and self._opts.host.strip())
            and bool(self._opts.username and self._opts.username.strip())
            and bool(self._opts.password and self._opts.password.strip())
        )

    async def list_unread_async(self, max: int) -> List[EmailMessage]:
        if max <= 0:
            raise ValueError("max must be positive")
        folder = await self._transport.open_folder_async(self._opts.folder, False)
        uids = await folder.search_async(ImapSearchQuery.not_seen_query())
        slice_ = sorted(uids, reverse=True)[:max]
        return await _fetch(folder, slice_)

    async def search_async(self, query: str, max: int) -> List[EmailMessage]:
        if not (query and query.strip()):
            raise ValueError("query required")
        if max <= 0:
            raise ValueError("max must be positive")
        folder = await self._transport.open_folder_async(self._opts.folder, False)
        uids = await folder.search_async(ImapSearchQuery.body_or_subject(query))
        slice_ = sorted(uids, reverse=True)[:max]
        return await _fetch(folder, slice_)

    async def mark_read_async(self, message_id: str) -> None:
        if not (message_id and message_id.strip()):
            raise ValueError("messageId required")
        try:
            raw = int(message_id)
        except ValueError:
            raise ValueError("Expected an IMAP UID")
        if raw < 0:
            raise ValueError("Expected an IMAP UID")
        folder = await self._transport.open_folder_async(self._opts.folder, True)
        await folder.add_seen_flag_async(raw)


async def _fetch(folder: IImapFolder, uids: Sequence[int]) -> List[EmailMessage]:
    messages: List[EmailMessage] = []
    if len(uids) == 0:
        return messages
    summaries = await folder.fetch_async(uids)
    for summary in summaries:
        env = summary.envelope
        labels: List[str] = []
        if summary.flags is not None:
            for flag in MessageFlags:
                if flag != MessageFlags.NONE and (summary.flags & flag) == flag:
                    labels.append(_flag_name(flag))
        body_text = ""
        try:
            msg = await folder.get_message_async(summary.uid)
            body_text = msg.text_body or msg.html_body or ""
        except Exception:
            # Mirrors the C# try/catch that swallows body-fetch failures.
            body_text = ""
        from_addr = env.from_address if (env and env.from_address) else ""
        to = list(env.to_addresses) if (env and env.to_addresses) else []
        subject = env.subject if env else ""
        received = (
            env.date.astimezone(timezone.utc)
            if (env and env.date is not None)
            else datetime.now(timezone.utc)
        )
        unread = summary.flags is not None and (summary.flags & MessageFlags.SEEN) == 0
        messages.append(
            EmailMessage(
                message_id=str(summary.uid),
                from_=from_addr,
                to=to,
                subject=subject,
                body_text=body_text,
                received_utc=received,
                unread=unread,
                labels=labels,
            )
        )
    return messages


def _flag_name(flag: MessageFlags) -> str:
    """Mirror MailKit ``MessageFlags.ToString()`` — the PascalCase member name."""
    return {
        MessageFlags.SEEN: "Seen",
        MessageFlags.ANSWERED: "Answered",
        MessageFlags.FLAGGED: "Flagged",
        MessageFlags.DELETED: "Deleted",
        MessageFlags.DRAFT: "Draft",
        MessageFlags.RECENT: "Recent",
    }.get(flag, flag.name or "")


# -- In-memory transport ---------------------------------------------------


@dataclass
class _StoredMessage:
    uid: int
    envelope: ImapEnvelope
    flags: MessageFlags
    text_body: Optional[str] = None
    html_body: Optional[str] = None


class InMemoryImapFolder(IImapFolder):
    """Deterministic in-memory :class:`IImapFolder`. Searches match MailKit
    semantics: ``NotSeen`` -> messages without the Seen flag; ``contains`` ->
    case-insensitive substring in subject or body.
    """

    def __init__(self, messages: Dict[int, _StoredMessage], read_write: bool):
        self._messages = messages
        self._read_write = read_write

    async def search_async(self, query: ImapSearchQuery) -> List[int]:
        out: List[int] = []
        for uid, m in self._messages.items():
            if query.not_seen:
                if (m.flags & MessageFlags.SEEN) == 0:
                    out.append(uid)
            elif query.contains is not None:
                q = query.contains.lower()
                body = (m.text_body or m.html_body or "")
                if q in (m.envelope.subject or "").lower() or q in body.lower():
                    out.append(uid)
        return out

    async def fetch_async(self, uids: Sequence[int]) -> List[ImapSummary]:
        out: List[ImapSummary] = []
        for uid in uids:
            m = self._messages.get(uid)
            if m is None:
                continue
            out.append(ImapSummary(uid=uid, envelope=m.envelope, flags=m.flags))
        return out

    async def get_message_async(self, uid: int) -> ImapMessage:
        m = self._messages.get(uid)
        if m is None:
            raise KeyError(uid)
        return ImapMessage(text_body=m.text_body, html_body=m.html_body)

    async def add_seen_flag_async(self, uid: int) -> None:
        if not self._read_write:
            raise RuntimeError("folder opened read-only")
        m = self._messages.get(uid)
        if m is None:
            raise KeyError(uid)
        m.flags = m.flags | MessageFlags.SEEN


class InMemoryImapTransport(IImapTransport):
    """Deterministic in-memory :class:`IImapTransport` seeded with messages."""

    def __init__(self) -> None:
        self._messages: Dict[int, _StoredMessage] = {}

    def add(
        self,
        uid: int,
        *,
        subject: str = "",
        from_address: Optional[str] = None,
        to_addresses: Sequence[str] = (),
        date: Optional[datetime] = None,
        flags: MessageFlags = MessageFlags.NONE,
        text_body: Optional[str] = None,
        html_body: Optional[str] = None,
    ) -> "InMemoryImapTransport":
        self._messages[uid] = _StoredMessage(
            uid=uid,
            envelope=ImapEnvelope(
                subject=subject,
                from_address=from_address,
                to_addresses=tuple(to_addresses),
                date=date,
            ),
            flags=flags,
            text_body=text_body,
            html_body=html_body,
        )
        return self

    def flags_of(self, uid: int) -> MessageFlags:
        return self._messages[uid].flags

    async def open_folder_async(
        self, folder: str, read_write: bool
    ) -> IImapFolder:
        return InMemoryImapFolder(self._messages, read_write)
