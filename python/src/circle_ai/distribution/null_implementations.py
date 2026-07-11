# null_implementations.py
#
# Port of CircleAI.Distribution NullImplementations.cs (C# — the EXACT spec).
#
# (2.9.0) Fail-closed distribution defaults. NullFileSync reports nothing
# present; NullPeerAdvertiser discovers no peers.

from __future__ import annotations

from typing import List, Optional

from .contracts import FileMetadata, IFileSync, IPeerAdvertiser, Peer


class NullFileSync(IFileSync):
    Instance: "NullFileSync"

    @property
    def backend_id(self) -> str:
        return "null"

    async def has_async(self, h: str, ct: Optional[object] = None) -> bool:
        return False

    async def fetch_async(self, h: str, ct: Optional[object] = None) -> Optional[bytes]:
        return None

    async def announce_async(self, m: FileMetadata, p: bytes, ct: Optional[object] = None) -> None:
        return None


class NullPeerAdvertiser(IPeerAdvertiser):
    Instance: "NullPeerAdvertiser"

    @property
    def backend_id(self) -> str:
        return "null"

    async def discover_async(self, ct: Optional[object] = None) -> List[Peer]:
        return []


NullFileSync.Instance = NullFileSync()
NullPeerAdvertiser.Instance = NullPeerAdvertiser()
