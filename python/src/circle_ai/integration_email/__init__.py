"""circle_ai.integration_email — port of the CircleAI.Integration.Email
assembly.

(Phase B2) Email connectors: Gmail API v1, Microsoft Graph, and a generic IMAP
client. Each is an :class:`~circle_ai.integration.contracts.IEmailConnector`.
C# is the exact spec.

The Gmail / Graph connectors take an injected
:class:`~circle_ai.integration.http.IHttpFetcher` (the C# takes an
``HttpClient``). The IMAP connector's C# is backed by MailKit (a native TCP
client); the Python port injects an :class:`IImapTransport` — a MailKit-shaped
abstraction — with a deterministic :class:`InMemoryImapTransport`. No real
network is used anywhere.

Public surface:

  * GmailEmailConnector / GmailOptions
  * MsGraphEmailConnector / MsGraphEmailOptions
  * ImapEmailConnector / ImapOptions
  * IImapTransport / IImapFolder / InMemoryImapTransport / InMemoryImapFolder
  * ImapEnvelope / ImapSummary / ImapMessage / ImapSearchQuery / MessageFlags
"""
from __future__ import annotations

from .gmail_email_connector import GmailEmailConnector, GmailOptions
from .imap_email_connector import (
    IImapFolder,
    IImapTransport,
    ImapEmailConnector,
    ImapEnvelope,
    ImapMessage,
    ImapOptions,
    ImapSearchQuery,
    ImapSummary,
    InMemoryImapFolder,
    InMemoryImapTransport,
    MessageFlags,
)
from .ms_graph_email_connector import (
    MsGraphEmailConnector,
    MsGraphEmailOptions,
)

__all__ = [
    "GmailEmailConnector",
    "GmailOptions",
    "MsGraphEmailConnector",
    "MsGraphEmailOptions",
    "ImapEmailConnector",
    "ImapOptions",
    "IImapTransport",
    "IImapFolder",
    "InMemoryImapTransport",
    "InMemoryImapFolder",
    "ImapEnvelope",
    "ImapSummary",
    "ImapMessage",
    "ImapSearchQuery",
    "MessageFlags",
]
