# telnyx_options.py
#
# Port of CircleAI.Telephony.Telnyx/TelnyxOptions.cs (C# — the EXACT spec).
#
# (3.3.0) Telnyx v2 API credentials + Call Control application id. Empty key ->
# fail-soft (carrier reports is_configured == False; operations raise a helpful
# message).

from __future__ import annotations

from dataclasses import dataclass
from typing import Optional


@dataclass(frozen=True, slots=True)
class TelnyxOptions:
    """(3.3.0) Telnyx account credentials + endpoint. Mirrors ``TelnyxOptions``."""

    #: Telnyx v2 API base address. Default ``https://api.telnyx.com``.
    base_address: str = "https://api.telnyx.com"
    #: Telnyx v2 API key (Bearer). Found in the portal under "API Keys".
    api_key: Optional[str] = None
    #: (Optional) Telnyx Call Control Application id used as the Connection for
    #: outbound calls and as the webhook owner for inbound calls. Required to dial.
    call_control_connection_id: Optional[str] = None


__all__ = ["TelnyxOptions"]
