# ubiquity.py
#
# Port of the named CircleAI.Distribution.Ubiquity rails from UbiquityRails.cs +
# UbiquityRailsMissingDefaults.cs (C# — the EXACT spec).
#
# This unit ports the four rails the work-unit names — IAppStoreSubmitter,
# ISignedDeltaUpdater, IOemPreloadCatalog, ICarrierPreloadCatalog — with their
# records (AppStorePackage / DeltaUpdate) and default implementations. The other
# 70+ UBI rails are outside this unit's scope.
#
# DefaultSignedDeltaUpdater verifies an HMAC-SHA256 signature before applying;
# HMACSHA256 + CryptographicOperations.FixedTimeEquals map to hmac.new(...,
# sha256).digest() + hmac.compare_digest.

from __future__ import annotations

import hmac
import threading
from abc import ABC, abstractmethod
from dataclasses import dataclass
from hashlib import sha256
from typing import Dict, List, Optional


# =====================================================================
# DISTRIBUTION — app store submitter
# =====================================================================
@dataclass(frozen=True, slots=True)
class AppStorePackage:
    """Mirrors ``CircleAI.Distribution.Ubiquity.AppStorePackage`` — ``record(
    string StoreName, string PackagePath, string Version,
    IReadOnlyDictionary<string, string> Metadata)``."""

    store_name: str
    package_path: str
    version: str
    metadata: Dict[str, str]


class IAppStoreSubmitter(ABC):
    """(3.3.0) Submit a package to an app store."""

    @abstractmethod
    async def submit_async(self, package: AppStorePackage, ct: Optional[object] = None) -> bool:
        ...


class DefaultAppStoreSubmitter(IAppStoreSubmitter):
    """(3.3.0) Validates the package and records the submission. Unknown stores
    return False. Store-name matching is case-insensitive
    (StringComparer.OrdinalIgnoreCase)."""

    _KNOWN_STORES = {
        s.casefold()
        for s in ("PlayStore", "AppStore", "Galaxy Store", "Huawei AppGallery", "Microsoft Store", "F-Droid")
    }

    def __init__(self) -> None:
        self._submitted: Dict[str, AppStorePackage] = {}

    async def submit_async(self, package: AppStorePackage, ct: Optional[object] = None) -> bool:
        if package is None:
            raise ValueError("package must not be None")
        if package.store_name is None or package.store_name.strip() == "":
            raise ValueError("StoreName required")
        if package.package_path is None or package.package_path.strip() == "":
            raise ValueError("PackagePath required")
        if package.version is None or package.version.strip() == "":
            raise ValueError("Version required")
        if package.store_name.casefold() not in self._KNOWN_STORES:
            return False
        key = f"{package.store_name}/{package.version}"
        self._submitted[key] = package
        return True

    @property
    def submitted(self) -> List[AppStorePackage]:
        return list(self._submitted.values())


# =====================================================================
# DISTRIBUTION — signed delta updater
# =====================================================================
@dataclass(frozen=True, slots=True)
class DeltaUpdate:
    """Mirrors ``CircleAI.Distribution.Ubiquity.DeltaUpdate`` — ``record(string
    Channel, string FromVersion, string ToVersion, byte[] Payload,
    byte[] Signature)``."""

    channel: str
    from_version: str
    to_version: str
    payload: bytes
    signature: bytes


class ISignedDeltaUpdater(ABC):
    """(3.3.0) Apply a signed delta update."""

    @abstractmethod
    async def apply_async(self, update: DeltaUpdate, ct: Optional[object] = None) -> bool:
        ...


class DefaultSignedDeltaUpdater(ISignedDeltaUpdater):
    """(3.3.0) Verifies an HMAC-SHA256 signature over
    ``Channel|FromVersion|ToVersion|`` + Payload before applying, and enforces
    channel version ordering (FromVersion must match the channel's current
    version)."""

    def __init__(self, hmac_key: bytes) -> None:
        if hmac_key is None or len(hmac_key) < 16:
            raise ValueError("hmacKey must be at least 16 bytes")
        self._hmac_key = bytes(hmac_key)
        self._channel_version: Dict[str, str] = {}

    async def apply_async(self, update: DeltaUpdate, ct: Optional[object] = None) -> bool:
        if update is None:
            raise ValueError("update must not be None")
        if (
            update.channel is None
            or update.channel.strip() == ""
            or update.to_version is None
            or update.to_version.strip() == ""
        ):
            return False
        current = self._channel_version.get(update.channel)
        if current is not None and current != update.from_version:
            return False

        # HMAC over Channel|FromVersion|ToVersion| then the raw payload bytes.
        prefix = f"{update.channel}|{update.from_version}|{update.to_version}|".encode("utf-8")
        msg = prefix + update.payload
        expected = hmac.new(self._hmac_key, msg, sha256).digest()
        if not hmac.compare_digest(expected, update.signature):
            return False
        self._channel_version[update.channel] = update.to_version
        return True

    def current_version(self, channel: str) -> Optional[str]:
        return self._channel_version.get(channel)


# =====================================================================
# DISTRIBUTION — OEM / carrier preload catalogues
# =====================================================================
class IOemPreloadCatalog(ABC):
    """(2.9.0) OEM preload partners."""

    @property
    @abstractmethod
    def partners(self) -> List[str]:
        ...


class DefaultOemPreloadCatalog(IOemPreloadCatalog):
    @property
    def partners(self) -> List[str]:
        return ["Tecno", "Itel", "Samsung mid-tier", "Xiaomi", "Huawei"]


class ICarrierPreloadCatalog(ABC):
    """(2.9.0) Carrier preload partners."""

    @property
    @abstractmethod
    def carriers(self) -> List[str]:
        ...


class DefaultCarrierPreloadCatalog(ICarrierPreloadCatalog):
    @property
    def carriers(self) -> List[str]:
        return ["MTN", "Vodacom", "Cell C", "Telkom", "Safaricom", "Airtel"]


# =====================================================================
# FAILURE MODES — abusive-environment safe mode
# =====================================================================
# FNV-1a 32-bit over UTF-8 — deterministic and identical across all language
# ports (unlike hash()/str.__hash__, which Python randomizes per process via
# PYTHONHASHSEED). This keeps the per-owner safety phrase stable across restarts
# AND byte-identical to the C# / other-language ports.
_FNV32_OFFSET_BASIS = 2166136261
_FNV32_PRIME = 16777619
_UINT32_MASK = 0xFFFFFFFF


def _fnv1a32(s: str) -> int:
    h = _FNV32_OFFSET_BASIS
    for b in s.encode("utf-8"):
        h = ((h ^ b) * _FNV32_PRIME) & _UINT32_MASK
    return h


class IAbusiveEnvironmentMode(ABC):
    """(3.3.0) Abuse-safe failure mode. Mirrors
    ``CircleAI.Distribution.Ubiquity.IAbusiveEnvironmentMode``."""

    @abstractmethod
    async def engage_async(self, owner_id: str, ct: Optional[object] = None) -> None:
        ...

    @abstractmethod
    def safety_phrase(self, owner_id: str) -> str:
        """Test phrase the user can speak to silently invoke abuse-safe mode.
        Generated per user."""
        ...

    @abstractmethod
    def is_engaged(self, owner_id: str) -> bool:
        ...


class DefaultAbusiveEnvironmentMode(IAbusiveEnvironmentMode):
    """(3.3.0) Default abuse-safe mode. The per-owner :meth:`safety_phrase` is a
    deterministic FNV-1a-32 draw from an 8-word benign vocabulary — stable across
    restarts and byte-identical across every language port."""

    _VOCAB = ("thunder", "river", "amber", "field", "rain", "stone", "harbor", "linen")

    def __init__(self) -> None:
        self._engaged: Dict[str, bool] = {}
        self._phrases: Dict[str, str] = {}
        self._lock = threading.Lock()

    async def engage_async(self, owner_id: str, ct: Optional[object] = None) -> None:
        if owner_id is None or owner_id.strip() == "":
            raise ValueError("ownerId required")
        with self._lock:
            self._engaged[owner_id] = True

    def safety_phrase(self, owner_id: str) -> str:
        if owner_id is None or owner_id.strip() == "":
            raise ValueError("ownerId required")
        with self._lock:
            existing = self._phrases.get(owner_id)
            if existing is not None:
                return existing
            # Deterministic per-owner safety phrase from an 8-word benign
            # vocabulary. FNV-1a-32 over UTF-8 so the phrase is stable across
            # restarts AND byte-identical across every language port.
            h = _fnv1a32(owner_id)
            phrase = f"the {self._VOCAB[h % 8]} {self._VOCAB[(h >> 8) % 8]} is {self._VOCAB[(h >> 16) % 8]}"
            self._phrases[owner_id] = phrase
            return phrase

    def is_engaged(self, owner_id: str) -> bool:
        with self._lock:
            return owner_id in self._engaged
