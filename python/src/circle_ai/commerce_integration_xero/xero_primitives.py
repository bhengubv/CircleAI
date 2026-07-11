# xero_primitives.py
#
# Port of CircleAI.Commerce.Integration.Xero XeroPrimitives.cs (C# — the EXACT spec).
#
# (3.3.0) Xero integration primitives — token storage, tenant tracking, webhook
# recorder. HTTP plumbing is host-supplied.
#
# The token ConcurrentDictionary maps to a plain dict; the per-user tenant lists
# and the event list are guarded by a single lock (mirroring the C# `_lock`).
# AddTenant dedups by TenantId. TokensExpired is true when no tokens are stored
# or now >= ExpiresAtUtc. RecentEvents is newest-first (OrderByDescending, which
# is stable — as is Python's sorted()).

from __future__ import annotations

import threading
from abc import ABC, abstractmethod
from dataclasses import dataclass
from datetime import datetime
from typing import Dict, List, Optional


@dataclass(frozen=True, slots=True)
class XeroTokens:
    """Mirrors ``CircleAI.Commerce.Integration.Xero.XeroTokens`` —
    ``record(string AccessToken, string RefreshToken, DateTimeOffset ExpiresAtUtc,
    string IdToken)``.
    """

    access_token: str
    refresh_token: str
    expires_at_utc: datetime
    id_token: str


@dataclass(frozen=True, slots=True)
class XeroTenant:
    """Mirrors ``CircleAI.Commerce.Integration.Xero.XeroTenant`` —
    ``record(string TenantId, string TenantName, string TenantType)``.
    """

    tenant_id: str
    tenant_name: str
    tenant_type: str


@dataclass(frozen=True, slots=True)
class XeroWebhookEvent:
    """Mirrors ``CircleAI.Commerce.Integration.Xero.XeroWebhookEvent`` —
    ``record(string TenantId, string ResourceType, string ResourceId,
    DateTimeOffset AtUtc)``.
    """

    tenant_id: str
    resource_type: str
    resource_id: str
    at_utc: datetime


class IXeroBoard(ABC):
    """Xero token / tenant / webhook board."""

    @abstractmethod
    def store_tokens(self, user_id: str, t: XeroTokens) -> None:
        ...

    @abstractmethod
    def get_tokens(self, user_id: str) -> Optional[XeroTokens]:
        ...

    @abstractmethod
    def tokens_expired(self, user_id: str, now: datetime) -> bool:
        ...

    @abstractmethod
    def add_tenant(self, user_id: str, t: XeroTenant) -> None:
        ...

    @abstractmethod
    def tenants_for(self, user_id: str) -> List[XeroTenant]:
        ...

    @abstractmethod
    def record_webhook(self, e: XeroWebhookEvent) -> None:
        ...

    @abstractmethod
    def recent_events(self, limit: int = 20) -> List[XeroWebhookEvent]:
        ...


class InMemoryXeroBoard(IXeroBoard):
    """Thread-safe in-memory :class:`IXeroBoard`."""

    def __init__(self) -> None:
        self._tokens: Dict[str, XeroTokens] = {}
        self._tenants: Dict[str, List[XeroTenant]] = {}
        self._events: List[XeroWebhookEvent] = []
        self._lock = threading.Lock()

    def store_tokens(self, user_id: str, t: XeroTokens) -> None:
        if t is None:
            raise ValueError("tokens must not be None")
        with self._lock:
            self._tokens[user_id] = t

    def get_tokens(self, user_id: str) -> Optional[XeroTokens]:
        with self._lock:
            return self._tokens.get(user_id)

    def tokens_expired(self, user_id: str, now: datetime) -> bool:
        with self._lock:
            t = self._tokens.get(user_id)
            if t is None:
                return True
            return now >= t.expires_at_utc

    def add_tenant(self, user_id: str, t: XeroTenant) -> None:
        if t is None:
            raise ValueError("tenant must not be None")
        with self._lock:
            lst = self._tenants.setdefault(user_id, [])
            if not any(x.tenant_id == t.tenant_id for x in lst):
                lst.append(t)

    def tenants_for(self, user_id: str) -> List[XeroTenant]:
        with self._lock:
            lst = self._tenants.get(user_id)
            return list(lst) if lst is not None else []

    def record_webhook(self, e: XeroWebhookEvent) -> None:
        if e is None:
            raise ValueError("event must not be None")
        with self._lock:
            self._events.append(e)

    def recent_events(self, limit: int = 20) -> List[XeroWebhookEvent]:
        with self._lock:
            ordered = sorted(self._events, key=lambda e: e.at_utc, reverse=True)
        return ordered[:limit]
