"""Enterprise-tier contracts + records.

Port of ``CircleAI.Inference.Server.Enterprise.Contracts`` — multi-tenant
routing, batch scheduling, model sharding, and cross-tier offload contracts.
"""
from __future__ import annotations

from abc import ABC, abstractmethod
from dataclasses import dataclass, field
from datetime import datetime
from enum import IntEnum
from typing import List, Mapping, Optional

__all__ = [
    "ServerTier",
    "TenantContext",
    "TenantQuota",
    "BatchSlot",
    "ShardDescriptor",
    "OffloadDecision",
    "ITenantRouter",
    "IBatchScheduler",
    "IModelShardPlanner",
    "ICrossTierOffload",
]


class ServerTier(IntEnum):
    """Deployment tier. Mirrors ``CircleAI.Inference.Server.Enterprise.ServerTier``."""

    SINGLE_NODE = 0
    SERVER = 1
    SERVER_FARM = 2


@dataclass(frozen=True, slots=True)
class TenantContext:
    """Tenant identity for routing. Mirrors ``TenantContext``."""

    tenant_id: str
    parent_tenant_id: Optional[str] = None
    tags: Optional[Mapping[str, str]] = None


@dataclass(frozen=True, slots=True)
class TenantQuota:
    """Per-tenant capacity quota. Mirrors ``TenantQuota``."""

    tenant_id: str
    max_concurrent_requests: int
    max_models_loaded: int
    max_bytes_in_flight: int
    daily_token_budget: int


@dataclass(frozen=True, slots=True)
class BatchSlot:
    """A reserved batch slot. Mirrors ``BatchSlot``."""

    slot_id: str
    model_id: str
    tokens: int
    deadline_utc: datetime


@dataclass(frozen=True, slots=True)
class ShardDescriptor:
    """One model shard's placement. Mirrors ``ShardDescriptor``.

    The parameter-byte range this shard covers is ``[range_start, range_end)``.
    """

    shard_id: str
    range_start: int
    range_end: int
    node_id: str


@dataclass(frozen=True, slots=True)
class OffloadDecision:
    """Cross-tier offload verdict. Mirrors ``OffloadDecision``."""

    should_offload: bool
    target_node_id: Optional[str]
    reason: Optional[str]


class ITenantRouter(ABC):
    """Multi-tenant routing — pick a backend node per tenant. Mirrors ``ITenantRouter``."""

    @property
    @abstractmethod
    def backend_id(self) -> str: ...

    @abstractmethod
    async def choose_node_async(
        self, tenant: TenantContext, model_id: str, ct: object = None
    ) -> Optional[str]: ...

    @abstractmethod
    async def set_quota_async(self, quota: TenantQuota, ct: object = None) -> None: ...

    @abstractmethod
    async def get_quota_async(
        self, tenant_id: str, ct: object = None
    ) -> Optional[TenantQuota]: ...


class IBatchScheduler(ABC):
    """Batch scheduler — coalesce small requests. Mirrors ``IBatchScheduler``."""

    @property
    @abstractmethod
    def backend_id(self) -> str: ...

    @abstractmethod
    async def reserve_async(
        self, model_id: str, estimated_tokens: int, max_wait_seconds: float, ct: object = None
    ) -> BatchSlot: ...

    @abstractmethod
    async def release_async(self, slot: BatchSlot, ct: object = None) -> None: ...


class IModelShardPlanner(ABC):
    """Model-sharding plan for very-large-model deployments. Mirrors ``IModelShardPlanner``."""

    @property
    @abstractmethod
    def backend_id(self) -> str: ...

    @abstractmethod
    async def plan_async(
        self, model_id: str, param_bytes: int, ct: object = None
    ) -> List[ShardDescriptor]: ...


class ICrossTierOffload(ABC):
    """RT-12 v2 cross-tier offload — phone borrows server brain. Mirrors
    ``ICrossTierOffload``.
    """

    @property
    @abstractmethod
    def backend_id(self) -> str: ...

    @abstractmethod
    async def should_offload_async(
        self,
        model_id: str,
        prompt_tokens: int,
        caller_tier: ServerTier,
        ct: object = None,
    ) -> OffloadDecision: ...
