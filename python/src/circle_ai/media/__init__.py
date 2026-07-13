"""circle_ai.media — port of the CircleAI.Media assembly (C# is the exact spec).

Real domain types + in-memory library for the Media vertical: an audio / video /
image asset catalog. ``InMemoryMediaLibrary`` is a thread-safe catalogue suitable
for offline use and tests; a host needing durability swaps in a database-backed
``IMediaLibrary`` behind the same contract.

Public surface:

  * Primitives: MediaKind (enum), MediaAsset (record).
  * Contract: IMediaLibrary.
  * Implementation: InMemoryMediaLibrary.
"""
from __future__ import annotations

from .media_primitives import (
    IMediaLibrary,
    InMemoryMediaLibrary,
    MediaAsset,
    MediaKind,
)

__all__ = [
    "MediaKind",
    "MediaAsset",
    "IMediaLibrary",
    "InMemoryMediaLibrary",
]
