from __future__ import annotations

from dataclasses import dataclass
from datetime import datetime
from enum import Enum
from typing import Optional


class IdentityTier(Enum):
    """Privacy / verification level of a Circle AI identity."""
    Anonymous    = "Anonymous"
    Pseudonymous = "Pseudonymous"
    Verified     = "Verified"


@dataclass(frozen=True)
class CircleIdentity:
    """A Circle AI identity — the unified persona key that travels with the person.

    Phone -> Watch -> Desktop -> Smart Speaker -> Car: same identity, same memory.
    """

    identity_id: str            # stable GUID — never changes
    display_name: str
    preferred_language: Optional[str]
    tier: IdentityTier
    device_ids: list[str]
    created_at: datetime
    last_seen_at: datetime


@dataclass(frozen=True)
class RegisteredDevice:
    """A device registered to an identity."""

    device_id: str
    identity_id: str
    platform: str  # "android"|"ios"|"windows"|"macos"|"linux"|"web"|"watch"|"iot"
    device_name: Optional[str]
    registered_at: datetime
    last_active_at: datetime
