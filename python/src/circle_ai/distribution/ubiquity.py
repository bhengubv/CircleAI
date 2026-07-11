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
