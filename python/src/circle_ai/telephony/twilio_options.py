# twilio_options.py
#
# Port of CircleAI.Telephony.Twilio/TwilioOptions.cs (C# — the EXACT spec).
#
# (3.3.0) Twilio REST API credentials + base address. AccountSid + AuthToken
# come from the Twilio console. Empty key -> fail-soft (carrier reports
# is_configured == False; operations raise with a helpful message).
#
# C# ``Uri BaseAddress`` maps to the base-URL string ``base_address``.

from __future__ import annotations

from dataclasses import dataclass
from typing import Optional


@dataclass(frozen=True, slots=True)
class TwilioOptions:
    """(3.3.0) Twilio account credentials + endpoint. Mirrors ``TwilioOptions``."""

    #: Twilio REST API base address. Default ``https://api.twilio.com``.
    base_address: str = "https://api.twilio.com"
    #: Twilio Account SID (starts with "AC...").
    account_sid: Optional[str] = None
    #: Twilio Auth Token.
    auth_token: Optional[str] = None


__all__ = ["TwilioOptions"]
