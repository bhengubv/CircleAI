"""circle_ai.distribution — port of the CircleAI.Distribution assembly.

Distribution domain: the content-addressed file-sync + peer-advertiser
contracts with fail-closed null defaults, plus the four named Ubiquity rails —
IAppStoreSubmitter / ISignedDeltaUpdater / IOemPreloadCatalog /
ICarrierPreloadCatalog — with their records and default implementations
(DefaultAppStoreSubmitter validates + records; DefaultSignedDeltaUpdater
verifies an HMAC-SHA256 signature; the preload catalogues return their partner
lists). C# is the exact spec.

Public surface:

  * FileMetadata / Peer                                   — domain records.
  * IFileSync / IPeerAdvertiser + NullFileSync / NullPeerAdvertiser.
  * AppStorePackage / DeltaUpdate                         — Ubiquity records.
  * IAppStoreSubmitter / ISignedDeltaUpdater / IOemPreloadCatalog /
    ICarrierPreloadCatalog + their Default* implementations.
  * IAbusiveEnvironmentMode + DefaultAbusiveEnvironmentMode — abuse-safe mode
    whose per-owner safety phrase is a deterministic FNV-1a-32 draw.
"""
from __future__ import annotations

from .contracts import FileMetadata, IFileSync, IPeerAdvertiser, Peer
from .null_implementations import NullFileSync, NullPeerAdvertiser
from .ubiquity import (
    AppStorePackage,
    DefaultAbusiveEnvironmentMode,
    DefaultAppStoreSubmitter,
    DefaultCarrierPreloadCatalog,
    DefaultOemPreloadCatalog,
    DefaultSignedDeltaUpdater,
    DeltaUpdate,
    IAbusiveEnvironmentMode,
    IAppStoreSubmitter,
    ICarrierPreloadCatalog,
    IOemPreloadCatalog,
    ISignedDeltaUpdater,
)

__all__ = [
    "FileMetadata",
    "Peer",
    "IFileSync",
    "IPeerAdvertiser",
    "NullFileSync",
    "NullPeerAdvertiser",
    "AppStorePackage",
    "DeltaUpdate",
    "IAppStoreSubmitter",
    "ISignedDeltaUpdater",
    "IOemPreloadCatalog",
    "ICarrierPreloadCatalog",
    "DefaultAppStoreSubmitter",
    "DefaultSignedDeltaUpdater",
    "DefaultOemPreloadCatalog",
    "DefaultCarrierPreloadCatalog",
    "IAbusiveEnvironmentMode",
    "DefaultAbusiveEnvironmentMode",
]
