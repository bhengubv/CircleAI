# contracts.py
#
# Port of CircleAI.Distribution Contracts.cs (C# — the EXACT spec).
#
# (2.9.0) Distribution contracts: file metadata, peers, file-sync, peer
# advertiser. C# ReadOnlyMemory<byte>? maps to Optional[bytes].

from __future__ import annotations

from abc import ABC, abstractmethod
from dataclasses import dataclass
from typing import List, Optional


@dataclass(frozen=True, slots=True)
class FileMetadata:
    """Mirrors ``CircleAI.Distribution.FileMetadata`` — ``record(string
    ContentHash, string Name, long SizeBytes)``."""

    content_hash: str
    name: str
    size_bytes: int


@dataclass(frozen=True, slots=True)
class Peer:
    """Mirrors ``CircleAI.Distribution.Peer`` — ``record(string PeerId,
    string Endpoint, IReadOnlyList<string> AvailableHashes)``."""

    peer_id: str
    endpoint: str
    available_hashes: List[str]


class IFileSync(ABC):
    """(2.9.0) Content-addressed file sync."""

    @property
    @abstractmethod
    def backend_id(self) -> str:
        ...

    @abstractmethod
    async def has_async(self, content_hash: str, ct: Optional[object] = None) -> bool:
        ...

    @abstractmethod
    async def fetch_async(self, content_hash: str, ct: Optional[object] = None) -> Optional[bytes]:
        ...

    @abstractmethod
    async def announce_async(self, metadata: FileMetadata, payload: bytes, ct: Optional[object] = None) -> None:
        ...


class IPeerAdvertiser(ABC):
    """(2.9.0) Peer discovery."""

    @property
    @abstractmethod
    def backend_id(self) -> str:
        ...

    @abstractmethod
    async def discover_async(self, ct: Optional[object] = None) -> List[Peer]:
        ...
