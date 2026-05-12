# identity.py
#
# Python port of Circle.AI.Identity.
#
# Covers:
#   IdentityTier      — Anonymous | Pseudonymous | Verified
#   CircleIdentity    — unified persona key travelling with the person
#   RegisteredDevice  — a device bound to an identity
#   IIdentityStore    — persistent store ABC
#   IIdentityProvider — runtime identity resolution ABC

from __future__ import annotations

from abc import ABC, abstractmethod
from dataclasses import dataclass, field
from datetime import datetime, timezone
from enum import Enum
from typing import Optional


def _utc_now() -> datetime:
    return datetime.now(timezone.utc)


# ---------------------------------------------------------------------------
# Enumerations
# ---------------------------------------------------------------------------

class IdentityTier(Enum):
    """Privacy / verification level of a Circle AI identity."""
    Anonymous     = "Anonymous"
    Pseudonymous  = "Pseudonymous"
    Verified      = "Verified"


# ---------------------------------------------------------------------------
# Data types
# ---------------------------------------------------------------------------

@dataclass(frozen=True)
class CircleIdentity:
    """A Circle AI identity — the unified persona key that travels with the person.

    Phone → Watch → Desktop → Smart Speaker → Car: same identity, same memory.
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
    platform: str               # "android" | "ios" | "windows" | "macos" | "linux" | "web" | "watch" | "iot"
    device_name: Optional[str]
    registered_at: datetime
    last_active_at: datetime


# ---------------------------------------------------------------------------
# Store / Provider ABCs
# ---------------------------------------------------------------------------

class IIdentityStore(ABC):
    """Persistent store for Circle AI identities and device registrations."""

    @abstractmethod
    async def get_async(
        self, identity_id: str, *, ct: Optional[object] = None
    ) -> Optional[CircleIdentity]:
        ...

    @abstractmethod
    async def save_async(
        self, identity: CircleIdentity, *, ct: Optional[object] = None
    ) -> None:
        ...

    @abstractmethod
    async def get_devices_async(
        self, identity_id: str, *, ct: Optional[object] = None
    ) -> list[RegisteredDevice]:
        ...

    @abstractmethod
    async def register_device_async(
        self, device: RegisteredDevice, *, ct: Optional[object] = None
    ) -> None:
        ...

    @abstractmethod
    async def get_by_device_async(
        self, device_id: str, *, ct: Optional[object] = None
    ) -> Optional[CircleIdentity]:
        ...


class IIdentityProvider(ABC):
    """Resolves the active identity for the current device/session.

    Implementations may use local storage, biometrics, or mesh-distributed keys.
    """

    @abstractmethod
    async def get_current_identity_async(
        self, *, ct: Optional[object] = None
    ) -> Optional[CircleIdentity]:
        ...

    @abstractmethod
    async def is_authenticated_async(
        self, *, ct: Optional[object] = None
    ) -> bool:
        ...

    @abstractmethod
    async def create_identity_async(
        self,
        display_name: str,
        preferred_language: Optional[str] = None,
        *,
        ct: Optional[object] = None,
    ) -> CircleIdentity:
        ...
