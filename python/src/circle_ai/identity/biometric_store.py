from __future__ import annotations

from typing import Optional, Protocol, runtime_checkable

from .biometric_profile import BiometricProfile


@runtime_checkable
class IBiometricStore(Protocol):
    """Persistent store for BiometricProfile records."""

    async def get_async(
        self, identity_id: str, *, ct: Optional[object] = None
    ) -> Optional[BiometricProfile]:
        """Return the stored profile for identity_id, or None if not enrolled."""
        ...

    async def save_async(
        self, profile: BiometricProfile, *, ct: Optional[object] = None
    ) -> None:
        """Persist or overwrite the profile for profile.identity_id."""
        ...

    async def delete_async(
        self, identity_id: str, *, ct: Optional[object] = None
    ) -> None:
        """Remove the stored profile; no-op if not found."""
        ...

    async def exists_async(
        self, identity_id: str, *, ct: Optional[object] = None
    ) -> bool:
        """Return True if a profile exists for identity_id."""
        ...
