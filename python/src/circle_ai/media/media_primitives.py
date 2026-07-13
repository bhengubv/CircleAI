# media_primitives.py
#
# Port of CircleAI.Media MediaPrimitives.cs (C# — the EXACT spec).
#
# (3.3.0) Real domain types + in-memory library for the Media vertical (audio +
# video + image asset catalog). C# ConcurrentDictionary -> a plain dict guarded
# by a threading.Lock. C# ``TimeSpan? Duration`` -> Optional[timedelta];
# ``long Bytes`` -> int. ListByKind / ByMime / Search all return newest-first
# (C# ``OrderByDescending(CreatedAtUtc)``). ByMime matches a MIME *prefix*
# case-insensitively (empty prefix yields nothing); Search matches a substring of
# the Title case-insensitively.

from __future__ import annotations

import threading
from abc import ABC, abstractmethod
from dataclasses import dataclass
from datetime import datetime, timedelta
from enum import IntEnum
from typing import Dict, List, Optional


class MediaKind(IntEnum):
    """Mirrors ``CircleAI.Media.MediaKind``. Stable ordinals match the C#
    ``enum MediaKind { Audio, Video, Image }``.
    """

    AUDIO = 0
    VIDEO = 1
    IMAGE = 2


@dataclass(frozen=True, slots=True)
class MediaAsset:
    """Mirrors ``CircleAI.Media.MediaAsset`` — ``record(string AssetId,
    string Title, MediaKind Kind, TimeSpan? Duration, long Bytes, string Mime,
    DateTimeOffset CreatedAtUtc)``. ``Duration`` maps to ``Optional[timedelta]``.
    """

    asset_id: str
    title: str
    kind: MediaKind
    duration: Optional[timedelta]
    bytes: int
    mime: str
    created_at_utc: datetime


class IMediaLibrary(ABC):
    """Mirrors ``CircleAI.Media.IMediaLibrary`` — the audio/video/image asset
    catalog contract.
    """

    @abstractmethod
    def add(self, a: MediaAsset) -> None:
        ...

    @abstractmethod
    def get(self, id: str) -> Optional[MediaAsset]:
        ...

    @abstractmethod
    def remove(self, id: str) -> bool:
        ...

    @property
    @abstractmethod
    def count(self) -> int:
        ...

    @property
    @abstractmethod
    def total_bytes(self) -> int:
        ...

    @abstractmethod
    def list_by_kind(self, kind: MediaKind) -> List[MediaAsset]:
        ...

    @abstractmethod
    def by_mime(self, mime_prefix: str) -> List[MediaAsset]:
        ...

    @abstractmethod
    def search(self, q: str, top_k: int = 20) -> List[MediaAsset]:
        ...


class InMemoryMediaLibrary(IMediaLibrary):
    """Thread-safe in-memory :class:`IMediaLibrary`. Faithful port of the C#
    ``InMemoryMediaLibrary``.
    """

    def __init__(self) -> None:
        self._items: Dict[str, MediaAsset] = {}
        self._lock = threading.Lock()

    def add(self, a: MediaAsset) -> None:
        if a is None:
            raise ValueError("asset must not be None")
        if a.asset_id is None or a.asset_id.strip() == "":
            raise ValueError("AssetId required")
        with self._lock:
            self._items[a.asset_id] = a

    def get(self, id: str) -> Optional[MediaAsset]:
        with self._lock:
            return self._items.get(id)

    def remove(self, id: str) -> bool:
        """Remove an asset by id. Returns True if it was present
        (C#: ``Remove``).
        """
        if not id:
            return False
        with self._lock:
            return self._items.pop(id, None) is not None

    @property
    def count(self) -> int:
        """Number of assets currently catalogued (C#: ``Count``)."""
        with self._lock:
            return len(self._items)

    @property
    def total_bytes(self) -> int:
        """Total on-disk footprint of every catalogued asset, in bytes
        (C#: ``TotalBytes``).
        """
        with self._lock:
            return sum(a.bytes for a in self._items.values())

    def list_by_kind(self, kind: MediaKind) -> List[MediaAsset]:
        """Assets of a given kind, newest-first (C#: ``ListByKind``)."""
        with self._lock:
            matches = [a for a in self._items.values() if a.kind == kind]
        return sorted(matches, key=lambda a: a.created_at_utc, reverse=True)

    def by_mime(self, mime_prefix: str) -> List[MediaAsset]:
        """Assets whose MIME type starts with ``mime_prefix`` (e.g. "image/",
        "audio/"), matched case-insensitively and returned newest-first. Empty
        prefix yields nothing (C#: ``ByMime``).
        """
        if not mime_prefix:
            return []
        prefix = mime_prefix.casefold()
        with self._lock:
            matches = [
                a for a in self._items.values() if a.mime.casefold().startswith(prefix)
            ]
        return sorted(matches, key=lambda a: a.created_at_utc, reverse=True)

    def search(self, q: str, top_k: int = 20) -> List[MediaAsset]:
        """Assets whose title contains ``q`` (case-insensitive), newest-first,
        capped at ``top_k`` (C#: ``Search``).
        """
        if q is None:
            raise ValueError("q must not be None")
        if top_k <= 0:
            raise ValueError("top_k must be positive")
        needle = q.casefold()
        with self._lock:
            matches = [
                a for a in self._items.values() if needle in a.title.casefold()
            ]
        matches.sort(key=lambda a: a.created_at_utc, reverse=True)
        return matches[:top_k]


__all__ = [
    "MediaKind",
    "MediaAsset",
    "IMediaLibrary",
    "InMemoryMediaLibrary",
]
