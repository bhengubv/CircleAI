# context.py
#
# Port of CircleAI.Aether.IAetherContext.cs (C# — the EXACT spec).
#
# Contract 2 — Presence and Capability.
#
# Answers: "Is Aether here, and at what level?" Apps query this at startup; the
# bootstrap acts on the result.
#
# Ships:
#   AetherInstallLevel     — None / App / OS
#   AetherVersion          — comparable version value object (mirrors C# Version)
#   IAetherContext         — the presence/version/capability query surface
#   InMemoryAetherContext  — a working, immutable implementation

from __future__ import annotations

from abc import ABC, abstractmethod
from dataclasses import dataclass
from enum import IntEnum
from typing import Optional, Union


class AetherInstallLevel(IntEnum):
    """Indicates where Aether is installed and who manages it."""

    # Aether is not present on this device.
    NONE = 0

    # Aether was installed at app level — either bundled with the app or
    # downloaded at first launch. Updated independently by the app.
    APP = 1

    # Aether is a system service managed by the OS. Always present on TGN
    # devices. Updated with OS updates. Requires biometric + device admin auth
    # to toggle on or off.
    OS = 2


@dataclass(frozen=True, slots=True, order=True)
class AetherVersion:
    """A comparable four-part version, mirroring the pieces of C# ``Version``
    used by Aether adapters. Ordered lexicographically by (major, minor, build,
    revision) so ``>=`` matches .NET ``Version`` comparison for these fields.

    Components default to 0 so ``AetherVersion(2)`` == ``2.0.0.0``.
    """

    major: int = 0
    minor: int = 0
    build: int = 0
    revision: int = 0

    def __str__(self) -> str:
        return f"{self.major}.{self.minor}.{self.build}.{self.revision}"

    @staticmethod
    def parse(text: str) -> "AetherVersion":
        """Parse a dotted version string (1 to 4 components)."""
        parts = [int(p) for p in text.strip().split(".")]
        if not 1 <= len(parts) <= 4:
            raise ValueError(f"invalid version: {text!r}")
        parts += [0] * (4 - len(parts))
        return AetherVersion(*parts)


# Accept an AetherVersion or a dotted string wherever a version is supplied.
VersionLike = Union["AetherVersion", str, None]


def _coerce_version(v: VersionLike) -> Optional[AetherVersion]:
    if v is None:
        return None
    if isinstance(v, AetherVersion):
        return v
    return AetherVersion.parse(v)


class IAetherContext(ABC):
    """Reports the presence, version, and capability of the Aether runtime on
    this device. Inject via DI; the platform adapter (MAUI, server) provides the
    concrete implementation.
    """

    @property
    @abstractmethod
    def install_level(self) -> AetherInstallLevel:
        """Where Aether is installed, if at all."""
        ...

    @property
    @abstractmethod
    def is_available(self) -> bool:
        """True when Aether is installed and enabled."""
        ...

    @property
    @abstractmethod
    def runtime_version(self) -> Optional[AetherVersion]:
        """The installed Aether runtime version, or None when Aether is absent."""
        ...

    @property
    @abstractmethod
    def minimum_required(self) -> Optional[AetherVersion]:
        """The minimum Aether version declared as required by the consuming app.
        Set this via configuration; the bootstrap checks it on startup.
        """
        ...

    @property
    @abstractmethod
    def is_sufficient(self) -> bool:
        """True when :attr:`runtime_version` satisfies :attr:`minimum_required`.
        Always true when minimum_required is None.
        """
        ...

    @property
    @abstractmethod
    def requires_auth(self) -> bool:
        """True when the install level is :attr:`AetherInstallLevel.OS`.
        OS-managed instances require biometric + device admin auth before they
        can be toggled.
        """
        ...

    @property
    @abstractmethod
    def is_enabled(self) -> bool:
        """True when Aether is installed and currently enabled. An OS-managed
        instance that has been toggled off returns false here.
        """
        ...


class InMemoryAetherContext(IAetherContext):
    """A working, immutable :class:`IAetherContext`. Reports a fixed install
    level, runtime version, and enabled state, computing :attr:`is_sufficient`,
    :attr:`requires_auth`, and :attr:`is_available` exactly as the C# adapters
    do.
    """

    def __init__(
        self,
        install_level: AetherInstallLevel = AetherInstallLevel.APP,
        runtime_version: VersionLike = None,
        minimum_required: VersionLike = None,
        is_enabled: bool = True,
    ) -> None:
        self._install_level = install_level
        self._runtime_version = _coerce_version(runtime_version)
        self._minimum_required = _coerce_version(minimum_required)
        self._is_enabled = is_enabled

    @property
    def install_level(self) -> AetherInstallLevel:
        return self._install_level

    @property
    def is_available(self) -> bool:
        # Present (installed) and enabled. NONE install level is never available.
        return self._install_level is not AetherInstallLevel.NONE and self._is_enabled

    @property
    def runtime_version(self) -> Optional[AetherVersion]:
        return self._runtime_version

    @property
    def minimum_required(self) -> Optional[AetherVersion]:
        return self._minimum_required

    @property
    def is_sufficient(self) -> bool:
        if self._minimum_required is None:
            return True
        return (
            self._runtime_version is not None
            and self._runtime_version >= self._minimum_required
        )

    @property
    def requires_auth(self) -> bool:
        return self._install_level is AetherInstallLevel.OS

    @property
    def is_enabled(self) -> bool:
        return self._is_enabled
