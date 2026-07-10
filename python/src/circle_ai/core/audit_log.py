# core/audit_log.py
#
# Port of CircleAI.Core.Auditing:
#   • ICircleAIAuditLog       — tamper-aware audit surface
#   • CircleAIAuditEntry      — immutable audit entry record
#   • CircleAIAuditQuery      — query filter
#   • NoopAuditLog            — default: silently drops entries
#   • LoggerAuditLog          — writes structured entries to a logging.Logger
#   • CircleAIAuditing        — process-wide ambient access point
#
# The C# QueryAsync is an IAsyncEnumerable; the Python contract is an async
# generator (``async def ... yield``) so ``async for`` works identically.

from __future__ import annotations

import logging
from abc import ABC, abstractmethod
from dataclasses import dataclass
from datetime import datetime
from typing import AsyncIterator, Optional


# ─────────────────────────────────────────────────────────────────────────────
# CircleAIAuditEntry — immutable audit entry emitted by the SDK.
# ─────────────────────────────────────────────────────────────────────────────


@dataclass(frozen=True, slots=True)
class CircleAIAuditEntry:
    """An immutable audit entry emitted by the CircleAI SDK.

    ``at``, ``component``, ``operation`` and ``outcome`` are required (they map
    to the C# ``required`` init-only properties); the rest are optional.
    """

    at: datetime
    component: str
    operation: str
    outcome: str
    tenant_id: Optional[str] = None
    uhid_identity_id: Optional[str] = None
    correlation_id: Optional[str] = None
    duration_ms: float = 0.0
    error_type: Optional[str] = None
    error_code: Optional[str] = None
    payload_sha256_hex: Optional[str] = None


# ─────────────────────────────────────────────────────────────────────────────
# CircleAIAuditQuery — query filter for ICircleAIAuditLog.query_async.
# ─────────────────────────────────────────────────────────────────────────────


@dataclass(frozen=True, slots=True)
class CircleAIAuditQuery:
    """Query filter for :meth:`ICircleAIAuditLog.query_async`."""

    from_utc: Optional[datetime] = None
    to_utc: Optional[datetime] = None
    component: Optional[str] = None
    tenant_id: Optional[str] = None
    uhid_identity_id: Optional[str] = None
    outcome: Optional[str] = None
    max_items: int = 1000


# ─────────────────────────────────────────────────────────────────────────────
# ICircleAIAuditLog — the audit contract.
# ─────────────────────────────────────────────────────────────────────────────


class ICircleAIAuditLog(ABC):
    """Tamper-aware audit surface for the CircleAI SDK.

    Every state-changing operation a component performs is auto-recorded here.
    Default registration is :class:`NoopAuditLog` — entries are silently
    dropped until a consumer wires :class:`LoggerAuditLog` or their own
    append-only sink.
    """

    @abstractmethod
    async def record_async(
        self, entry: CircleAIAuditEntry, ct: object = None
    ) -> None:
        """Record an audit entry. MUST NOT raise — the caller may be mid-operation
        and audit-log failure must never bring it down. Implementations should
        catch and log internally, failing open."""
        raise NotImplementedError

    @abstractmethod
    def query_async(
        self, query: CircleAIAuditQuery, ct: object = None
    ) -> AsyncIterator[CircleAIAuditEntry]:
        """Query historical entries — for compliance reporting, forensic
        investigation, debugging. Returns an async iterator."""
        raise NotImplementedError


# ─────────────────────────────────────────────────────────────────────────────
# NoopAuditLog — default sink; silently discards every entry.
# ─────────────────────────────────────────────────────────────────────────────


class NoopAuditLog(ICircleAIAuditLog):
    """Default :class:`ICircleAIAuditLog` — silently discards every entry and
    returns an empty query result."""

    _instance: Optional["NoopAuditLog"] = None

    @classmethod
    def instance(cls) -> "NoopAuditLog":
        """Shared singleton instance (mirrors C# ``NoopAuditLog.Instance``)."""
        if cls._instance is None:
            cls._instance = cls()
        return cls._instance

    async def record_async(
        self, entry: CircleAIAuditEntry, ct: object = None
    ) -> None:
        return None

    async def query_async(
        self, query: CircleAIAuditQuery, ct: object = None
    ) -> AsyncIterator[CircleAIAuditEntry]:
        return
        yield  # pragma: no cover — makes this an async generator that yields nothing


# ─────────────────────────────────────────────────────────────────────────────
# LoggerAuditLog — writes structured entries to a logging.Logger.
# ─────────────────────────────────────────────────────────────────────────────


class LoggerAuditLog(ICircleAIAuditLog):
    """:class:`ICircleAIAuditLog` that writes structured entries to a
    :class:`logging.Logger` at ``INFO`` level.

    The :meth:`query_async` implementation always returns empty — reading back
    from a logger isn't possible at the SDK layer.
    """

    def __init__(self, logger: logging.Logger) -> None:
        if logger is None:
            raise ValueError("logger")
        self._logger = logger

    async def record_async(
        self, entry: CircleAIAuditEntry, ct: object = None
    ) -> None:
        if entry is None:
            raise ValueError("entry")
        self._logger.info(
            "CircleAI audit %s.%s %s tenant=%s uhid=%s corr=%s "
            "duration_ms=%s error=%s(%s) payload_sha256=%s at=%s",
            entry.component,
            entry.operation,
            entry.outcome,
            entry.tenant_id or "-",
            entry.uhid_identity_id or "-",
            entry.correlation_id or "-",
            entry.duration_ms,
            entry.error_type or "-",
            entry.error_code or "-",
            entry.payload_sha256_hex or "-",
            entry.at.isoformat(),
        )

    async def query_async(
        self, query: CircleAIAuditQuery, ct: object = None
    ) -> AsyncIterator[CircleAIAuditEntry]:
        return
        yield  # pragma: no cover


# ─────────────────────────────────────────────────────────────────────────────
# CircleAIAuditing — process-wide ambient access point.
# ─────────────────────────────────────────────────────────────────────────────


class CircleAIAuditing:
    """Process-wide ambient access point for the audit sink.

    Initial value is :meth:`NoopAuditLog.instance`. Hosts wire the real sink
    by calling :meth:`set_default` during startup.
    """

    _default: ICircleAIAuditLog = NoopAuditLog.instance()

    @classmethod
    def default(cls) -> ICircleAIAuditLog:
        """The current ambient audit sink."""
        return cls._default

    @classmethod
    def set_default(cls, audit: ICircleAIAuditLog) -> None:
        """Replace the ambient audit sink. Idempotent."""
        if audit is None:
            raise ValueError("audit")
        cls._default = audit

    @classmethod
    def reset_to_noop(cls) -> None:
        """Restore the default to :class:`NoopAuditLog`. Test-helper."""
        cls._default = NoopAuditLog.instance()
