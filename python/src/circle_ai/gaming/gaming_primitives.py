# gaming_primitives.py
#
# Port of CircleAI.Gaming GamingPrimitives.cs (C# — the EXACT spec).
#
# (3.3.0) Real domain types + in-memory board for the Gaming vertical: game
# titles, play sessions, achievement unlocks. C# ConcurrentDictionary -> dict;
# the session/unlock lists are guarded by a single lock. TimeSpan -> timedelta,
# DateTimeOffset -> datetime. TotalPlayTime sums session durations; MostPlayed
# groups sessions by title, orders by total play time desc, and maps to titles.

from __future__ import annotations

import threading
from abc import ABC, abstractmethod
from collections import defaultdict
from dataclasses import dataclass
from datetime import datetime, timedelta
from typing import Dict, List, Optional


@dataclass(frozen=True, slots=True)
class GameTitle:
    """Mirrors ``CircleAI.Gaming.GameTitle``."""

    title_id: str
    name: str
    genre: str
    platform: str


@dataclass(frozen=True, slots=True)
class PlaySession:
    """Mirrors ``CircleAI.Gaming.PlaySession``."""

    session_id: str
    user_id: str
    title_id: str
    duration: timedelta
    at_utc: datetime


@dataclass(frozen=True, slots=True)
class AchievementUnlock:
    """Mirrors ``CircleAI.Gaming.AchievementUnlock``."""

    unlock_id: str
    user_id: str
    title_id: str
    achievement: str
    at_utc: datetime


class IGamingBoard(ABC):
    """In-memory board for game titles, play sessions and achievements."""

    @abstractmethod
    def add_title(self, t: GameTitle) -> None:
        ...

    @abstractmethod
    def get_title(self, id: str) -> Optional[GameTitle]:
        ...

    @abstractmethod
    def titles_by_genre(self, genre: str) -> List[GameTitle]:
        ...

    @abstractmethod
    def record_session(self, s: PlaySession) -> None:
        ...

    @abstractmethod
    def total_play_time(self, user_id: str, title_id: str) -> timedelta:
        ...

    @abstractmethod
    def unlock(self, u: AchievementUnlock) -> None:
        ...

    @abstractmethod
    def achievements_for(self, user_id: str) -> List[AchievementUnlock]:
        ...

    @abstractmethod
    def most_played(self, user_id: str, top_k: int = 5) -> List[GameTitle]:
        ...


class InMemoryGamingBoard(IGamingBoard):
    """Thread-safe in-memory :class:`IGamingBoard`."""

    def __init__(self) -> None:
        self._titles: Dict[str, GameTitle] = {}
        self._sessions: List[PlaySession] = []
        self._unlocks: List[AchievementUnlock] = []
        self._lock = threading.Lock()

    def add_title(self, t: GameTitle) -> None:
        if t is None:
            raise ValueError("game title must not be None")
        with self._lock:
            self._titles[t.title_id] = t

    def get_title(self, id: str) -> Optional[GameTitle]:
        with self._lock:
            return self._titles.get(id)

    def titles_by_genre(self, genre: str) -> List[GameTitle]:
        target = genre.casefold()
        with self._lock:
            return [
                t for t in self._titles.values() if t.genre.casefold() == target
            ]

    def record_session(self, s: PlaySession) -> None:
        if s is None:
            raise ValueError("play session must not be None")
        with self._lock:
            self._sessions.append(s)

    def total_play_time(self, user_id: str, title_id: str) -> timedelta:
        with self._lock:
            total = timedelta()
            for s in self._sessions:
                if s.user_id == user_id and s.title_id == title_id:
                    total += s.duration
            return total

    def unlock(self, u: AchievementUnlock) -> None:
        if u is None:
            raise ValueError("achievement unlock must not be None")
        with self._lock:
            self._unlocks.append(u)

    def achievements_for(self, user_id: str) -> List[AchievementUnlock]:
        with self._lock:
            items = [u for u in self._unlocks if u.user_id == user_id]
        items.sort(key=lambda u: u.at_utc, reverse=True)
        return items

    def most_played(self, user_id: str, top_k: int = 5) -> List[GameTitle]:
        if top_k <= 0:
            raise ValueError("top_k must be positive")
        with self._lock:
            totals: Dict[str, timedelta] = defaultdict(timedelta)
            for s in self._sessions:
                if s.user_id == user_id:
                    totals[s.title_id] += s.duration
            # OrderByDescending(total).Take(topK), then map title ids to titles,
            # dropping any that are missing (C# Where(t => t is not null)).
            ranked = sorted(
                totals.items(), key=lambda kv: kv[1].total_seconds(), reverse=True
            )[:top_k]
            result: List[GameTitle] = []
            for title_id, _ in ranked:
                t = self._titles.get(title_id)
                if t is not None:
                    result.append(t)
            return result
