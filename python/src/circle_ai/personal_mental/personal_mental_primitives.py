# personal_mental_primitives.py
#
# Port of CircleAI.Personal.Mental PersonalMentalPrimitives.cs (C# — the EXACT spec).
#
# (3.3.0) Real domain types + in-memory board for the mental-health vertical:
# mood logs, journal entries, coping-strategy library, 7-day trend. Privacy:
# per-user instance only.
#
# The C# enum Mood maps to an IntEnum with the C# declaration order as its
# stable ordinals. The mood List<> is guarded by a single lock; the journal /
# strategy ConcurrentDictionaries map to plain dicts. Last7Days uses a live
# UtcNow-7d cutoff. AvgMood7Day averages the integer mood ordinals and returns
# NaN (float('nan')) for an empty window, mirroring C#'s double.NaN. Tag
# matching is ordinal-ignore-case (str.casefold()).

from __future__ import annotations

import math
import threading
from abc import ABC, abstractmethod
from dataclasses import dataclass
from datetime import datetime, timedelta, timezone
from enum import IntEnum
from typing import Dict, List, Optional, Tuple


class Mood(IntEnum):
    """Mirrors ``CircleAI.Personal.Mental.Mood``. Ordinals are the C#
    declaration order and are stable across languages.
    """

    VERY_LOW = 0
    LOW = 1
    NEUTRAL = 2
    GOOD = 3
    GREAT = 4


@dataclass(frozen=True, slots=True)
class MoodLog:
    """Mirrors ``CircleAI.Personal.Mental.MoodLog`` —
    ``record(Mood Mood, DateTimeOffset AtUtc, string? Note)``.
    """

    mood: Mood
    at_utc: datetime
    note: Optional[str]


@dataclass(frozen=True, slots=True)
class JournalEntry:
    """Mirrors ``CircleAI.Personal.Mental.JournalEntry`` — ``record(string EntryId,
    string Title, string Body, DateTimeOffset AtUtc)``.
    """

    entry_id: str
    title: str
    body: str
    at_utc: datetime


@dataclass(frozen=True, slots=True)
class CopingStrategy:
    """Mirrors ``CircleAI.Personal.Mental.CopingStrategy`` — ``record(string
    StrategyId, string Title, string Description, IReadOnlyList<string> Tags)``.
    """

    strategy_id: str
    title: str
    description: str
    tags: Tuple[str, ...]


class IMentalHealthBoard(ABC):
    """In-memory board for mood logs, journal entries and coping strategies."""

    @abstractmethod
    def log_mood(self, m: MoodLog) -> None:
        ...

    @abstractmethod
    def last_7_days(self) -> List[MoodLog]:
        ...

    @abstractmethod
    def add_entry(self, e: JournalEntry) -> None:
        ...

    @property
    @abstractmethod
    def entries(self) -> List[JournalEntry]:
        ...

    @abstractmethod
    def register_strategy(self, s: CopingStrategy) -> None:
        ...

    @abstractmethod
    def strategies_by_tag(self, tag: str) -> List[CopingStrategy]:
        ...

    @abstractmethod
    def avg_mood_7_day(self) -> float:
        ...


class InMemoryMentalHealthBoard(IMentalHealthBoard):
    """Thread-safe in-memory :class:`IMentalHealthBoard`."""

    def __init__(self) -> None:
        self._moods: List[MoodLog] = []
        self._entries: Dict[str, JournalEntry] = {}
        self._strats: Dict[str, CopingStrategy] = {}
        self._lock = threading.Lock()

    def log_mood(self, m: MoodLog) -> None:
        if m is None:
            raise ValueError("mood log must not be None")
        with self._lock:
            self._moods.append(m)

    def last_7_days(self) -> List[MoodLog]:
        cutoff = datetime.now(timezone.utc) - timedelta(days=7)
        with self._lock:
            rows = [m for m in self._moods if m.at_utc >= cutoff]
        return sorted(rows, key=lambda m: m.at_utc)

    def add_entry(self, e: JournalEntry) -> None:
        if e is None:
            raise ValueError("entry must not be None")
        if e.entry_id is None or e.entry_id.strip() == "":
            raise ValueError("EntryId required")
        with self._lock:
            self._entries[e.entry_id] = e

    @property
    def entries(self) -> List[JournalEntry]:
        with self._lock:
            values = list(self._entries.values())
        return sorted(values, key=lambda e: e.at_utc, reverse=True)

    def register_strategy(self, s: CopingStrategy) -> None:
        if s is None:
            raise ValueError("strategy must not be None")
        with self._lock:
            self._strats[s.strategy_id] = s

    def strategies_by_tag(self, tag: str) -> List[CopingStrategy]:
        if tag is None or tag.strip() == "":
            raise ValueError("tag required")
        needle = tag.casefold()
        with self._lock:
            return [
                s for s in self._strats.values()
                if any(t.casefold() == needle for t in s.tags)
            ]

    def avg_mood_7_day(self) -> float:
        items = self.last_7_days()
        if len(items) == 0:
            return math.nan
        return sum(int(m.mood) for m in items) / len(items)
