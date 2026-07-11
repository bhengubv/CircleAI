# social_primitives.py
#
# Port of CircleAI.Social SocialPrimitives.cs (C# — the EXACT spec).
#
# (3.3.0) Real domain types + in-memory board for the Social vertical: posts,
# reactions, follows, a simple follow-graph feed. C# ConcurrentDictionary ->
# dict; the reaction/follow lists are guarded by a single lock. DateTimeOffset
# -> datetime. ReactionCount matches Kind case-insensitively; Follow rejects
# self-follow; FeedFor returns the newest `limit` posts authored by anyone the
# user follows; Followers lists follower ids of a user.

from __future__ import annotations

import threading
from abc import ABC, abstractmethod
from dataclasses import dataclass
from datetime import datetime
from typing import Dict, List, Optional, Sequence


@dataclass(frozen=True, slots=True)
class SocialPost:
    """Mirrors ``CircleAI.Social.SocialPost``."""

    post_id: str
    author_id: str
    body: str
    at_utc: datetime
    tags: Sequence[str]


@dataclass(frozen=True, slots=True)
class Reaction:
    """Mirrors ``CircleAI.Social.Reaction``."""

    post_id: str
    user_id: str
    kind: str
    at_utc: datetime


@dataclass(frozen=True, slots=True)
class Follow:
    """Mirrors ``CircleAI.Social.Follow``."""

    follower_id: str
    followee_id: str
    at_utc: datetime


class ISocialBoard(ABC):
    """In-memory board for posts, reactions and a follow graph."""

    @abstractmethod
    def post(self, p: SocialPost) -> None:
        ...

    @abstractmethod
    def get_post(self, id: str) -> Optional[SocialPost]:
        ...

    @abstractmethod
    def react(self, r: Reaction) -> None:
        ...

    @abstractmethod
    def reaction_count(self, post_id: str, kind: str) -> int:
        ...

    @abstractmethod
    def follow(self, f: Follow) -> None:
        ...

    @abstractmethod
    def unfollow(self, follower_id: str, followee_id: str) -> None:
        ...

    @abstractmethod
    def feed_for(self, user_id: str, limit: int = 20) -> List[SocialPost]:
        ...

    @abstractmethod
    def followers(self, user_id: str) -> List[str]:
        ...


class InMemorySocialBoard(ISocialBoard):
    """Thread-safe in-memory :class:`ISocialBoard`."""

    def __init__(self) -> None:
        self._posts: Dict[str, SocialPost] = {}
        self._reacts: List[Reaction] = []
        self._follows: List[Follow] = []
        self._lock = threading.Lock()

    def post(self, p: SocialPost) -> None:
        if p is None:
            raise ValueError("social post must not be None")
        with self._lock:
            self._posts[p.post_id] = p

    def get_post(self, id: str) -> Optional[SocialPost]:
        with self._lock:
            return self._posts.get(id)

    def react(self, r: Reaction) -> None:
        if r is None:
            raise ValueError("reaction must not be None")
        with self._lock:
            self._reacts.append(r)

    def reaction_count(self, post_id: str, kind: str) -> int:
        target = kind.casefold()
        with self._lock:
            return sum(
                1
                for r in self._reacts
                if r.post_id == post_id and r.kind.casefold() == target
            )

    def follow(self, f: Follow) -> None:
        if f is None:
            raise ValueError("follow must not be None")
        if f.follower_id == f.followee_id:
            raise RuntimeError("Cannot follow yourself.")
        with self._lock:
            self._follows.append(f)

    def unfollow(self, follower_id: str, followee_id: str) -> None:
        with self._lock:
            self._follows = [
                f
                for f in self._follows
                if not (f.follower_id == follower_id and f.followee_id == followee_id)
            ]

    def feed_for(self, user_id: str, limit: int = 20) -> List[SocialPost]:
        if limit <= 0:
            raise ValueError("limit must be positive")
        with self._lock:
            following = {
                f.followee_id for f in self._follows if f.follower_id == user_id
            }
            items = [p for p in self._posts.values() if p.author_id in following]
        items.sort(key=lambda p: p.at_utc, reverse=True)
        return items[:limit]

    def followers(self, user_id: str) -> List[str]:
        with self._lock:
            return [
                f.follower_id for f in self._follows if f.followee_id == user_id
            ]
