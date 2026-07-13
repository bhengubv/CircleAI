# creative_primitives.py
#
# Port of CircleAI.Creative CreativePrimitives.cs (C# — the EXACT spec).
#
# (3.3.0) Real domain types + in-memory board for the Creative vertical: works,
# inspiration, critiques. C# ConcurrentDictionary -> dict; the inspiration /
# critique lists are guarded by a single lock. DateTimeOffset -> datetime.
# WorksByTag matches any tag case-insensitively; RecentInspiration returns the
# newest `limit`; AvgScore averages a work's critique scores, returning 0.0 when
# there are none (C# ``DefaultIfEmpty(0).Average()``).

from __future__ import annotations

import threading
from abc import ABC, abstractmethod
from dataclasses import dataclass
from datetime import datetime
from typing import Dict, List, Optional, Sequence


@dataclass(frozen=True, slots=True)
class CreativeWork:
    """Mirrors ``CircleAI.Creative.CreativeWork``."""

    work_id: str
    title: str
    medium: str
    author: str
    created_utc: datetime
    tags: Sequence[str]


@dataclass(frozen=True, slots=True)
class Inspiration:
    """Mirrors ``CircleAI.Creative.Inspiration``."""

    inspiration_id: str
    prompt_text: str
    source_url: str
    seen_utc: datetime


@dataclass(frozen=True, slots=True)
class Critique:
    """Mirrors ``CircleAI.Creative.Critique``."""

    critique_id: str
    work_id: str
    reviewer: str
    body: str
    score: int


class ICreativeBoard(ABC):
    """In-memory board for creative works, inspiration and critiques."""

    @abstractmethod
    def add_work(self, w: CreativeWork) -> None:
        ...

    @abstractmethod
    def get_work(self, id: str) -> Optional[CreativeWork]:
        ...

    @abstractmethod
    def works_by_tag(self, tag: str) -> List[CreativeWork]:
        ...

    @abstractmethod
    def record_inspiration(self, i: Inspiration) -> None:
        ...

    @abstractmethod
    def recent_inspiration(self, limit: int = 20) -> List[Inspiration]:
        ...

    @abstractmethod
    def add_critique(self, c: Critique) -> None:
        ...

    @abstractmethod
    def avg_score(self, work_id: str) -> float:
        ...


class InMemoryCreativeBoard(ICreativeBoard):
    """Thread-safe in-memory :class:`ICreativeBoard`."""

    def __init__(self) -> None:
        self._works: Dict[str, CreativeWork] = {}
        self._inspiration: List[Inspiration] = []
        self._critiques: List[Critique] = []
        self._lock = threading.Lock()

    def add_work(self, w: CreativeWork) -> None:
        if w is None:
            raise ValueError("creative work must not be None")
        with self._lock:
            self._works[w.work_id] = w

    def get_work(self, id: str) -> Optional[CreativeWork]:
        with self._lock:
            return self._works.get(id)

    def works_by_tag(self, tag: str) -> List[CreativeWork]:
        target = tag.casefold()
        with self._lock:
            return [
                w
                for w in self._works.values()
                if any(t.casefold() == target for t in w.tags)
            ]

    def record_inspiration(self, i: Inspiration) -> None:
        if i is None:
            raise ValueError("inspiration must not be None")
        with self._lock:
            self._inspiration.append(i)

    def recent_inspiration(self, limit: int = 20) -> List[Inspiration]:
        with self._lock:
            items = list(self._inspiration)
        items.sort(key=lambda i: i.seen_utc, reverse=True)
        return items[:limit]

    def add_critique(self, c: Critique) -> None:
        if c is None:
            raise ValueError("critique must not be None")
        with self._lock:
            self._critiques.append(c)

    def avg_score(self, work_id: str) -> float:
        with self._lock:
            scores = [
                float(c.score) for c in self._critiques if c.work_id == work_id
            ]
        if not scores:
            return 0.0
        return sum(scores) / len(scores)

    @property
    def work_count(self) -> int:
        """Number of catalogued works (C#: ``WorkCount``)."""
        with self._lock:
            return len(self._works)

    def remove_work(self, work_id: str) -> bool:
        """Remove a work and cascade-delete its critiques. Returns True if the
        work was present (C#: ``RemoveWork``).
        """
        with self._lock:
            removed = self._works.pop(work_id, None) is not None
            if removed:
                self._critiques = [
                    c for c in self._critiques if c.work_id != work_id
                ]
            return removed

    def works_by_author(self, author: str) -> List[CreativeWork]:
        """Works by a given author (case-insensitive), newest-first
        (C#: ``WorksByAuthor``).
        """
        target = author.casefold()
        with self._lock:
            matches = [
                w for w in self._works.values() if w.author.casefold() == target
            ]
        return sorted(matches, key=lambda w: w.created_utc, reverse=True)

    def works_by_medium(self, medium: str) -> List[CreativeWork]:
        """Works in a given medium (case-insensitive), newest-first
        (C#: ``WorksByMedium``).
        """
        target = medium.casefold()
        with self._lock:
            matches = [
                w for w in self._works.values() if w.medium.casefold() == target
            ]
        return sorted(matches, key=lambda w: w.created_utc, reverse=True)

    def top_rated_work(self) -> Optional[CreativeWork]:
        """The work with the highest average critique score whose record still
        exists, or None (C#: ``TopRatedWork`` — critiques grouped by work id
        (ordinal); ties keep first-seen order).
        """
        with self._lock:
            averages: Dict[str, List] = {}  # work_id -> [sum, count]
            for c in self._critiques:
                agg = averages.get(c.work_id)
                if agg is None:
                    averages[c.work_id] = [float(c.score), 1]
                else:
                    agg[0] += float(c.score)
                    agg[1] += 1
            ranked = sorted(
                averages.items(), key=lambda kv: kv[1][0] / kv[1][1], reverse=True
            )
            for work_id, _ in ranked:
                w = self._works.get(work_id)
                if w is not None:
                    return w
            return None

    def all_tags(self) -> List[str]:
        """Every distinct tag (case-insensitive), ordered case-insensitively
        (C#: ``AllTags`` — first-seen casing is kept per distinct tag).
        """
        with self._lock:
            seen: Dict[str, str] = {}  # casefold -> first-seen display
            for w in self._works.values():
                for t in w.tags:
                    key = t.casefold()
                    if key not in seen:
                        seen[key] = t
        return sorted(seen.values(), key=lambda t: t.casefold())
