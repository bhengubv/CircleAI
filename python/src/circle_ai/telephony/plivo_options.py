# plivo_options.py
#
# Port of CircleAI.Telephony.Plivo/PlivoOptions.cs (C# — the EXACT spec).
#
# (3.3.0) Plivo v1 API credentials + AnswerUrl base for media-stream XML. Empty
# AuthId/AuthToken -> fail-soft.

from __future__ import annotations

from dataclasses import dataclass
from typing import Optional


@dataclass(frozen=True, slots=True)
class PlivoOptions:
    """(3.3.0) Plivo account credentials + endpoint. Mirrors ``PlivoOptions``."""

    #: Plivo v1 API base address. Default ``https://api.plivo.com``.
    base_address: str = "https://api.plivo.com"
    #: Plivo Auth ID (starts with "MA..." or similar).
    auth_id: Optional[str] = None
    #: Plivo Auth Token.
    auth_token: Optional[str] = None
    #: (Required for dial) HTTPS URL the host serves that, given a
    #: ``?stream=<url-encoded wss://...>`` query parameter, returns Plivo XML
    #: containing the matching ``<Stream/>`` verb.
    answer_url_base: Optional[str] = None


__all__ = ["PlivoOptions"]
