"""Single-node null defaults — fall back to local execution.

Port of ``CircleAI.Inference.Server.Enterprise.NullImplementations``.
"""
from __future__ import annotations

from datetime import datetime, timedelta, timezone
from typing import List, Optional

from .contracts import (
    BatchSlot,
    ICrossTierOffload,
    IBatchScheduler,
    IModelShardPlanner,
    ITenantRouter,
    OffloadDecision,
    ServerTier,
    ShardDescriptor,
    TenantContext,
    TenantQuota,
)

__all__ = [
    "NullTenantRouter",
    "NullBatchScheduler",
    "NullModelShardPlanner",
    "NullCrossTierOffload",
]


class NullTenantRouter(ITenantRouter):
    """No-op tenant router. Mirrors ``NullTenantRouter``."""

    _instance: "NullTenantRouter | None" = None

    @classmethod
    def instance(cls) -> "NullTenantRouter":
        if cls._instance is None:
            cls._instance = cls()
        return cls._instance

    @property
    def backend_id(self) -> str:
        return "null"

    async def choose_node_async(
        self, tenant: TenantContext, model_id: str, ct: object = None
    ) -> Optional[str]:
        return None

    async def set_quota_async(self, quota: TenantQuota, ct: object = None) -> None:
        return None

    async def get_quota_async(
        self, tenant_id: str, ct: object = None
    ) -> Optional[TenantQuota]:
        return None


class NullBatchScheduler(IBatchScheduler):
    """No-op batch scheduler — returns an empty-id slot. Mirrors ``NullBatchScheduler``."""

    _instance: "NullBatchScheduler | None" = None

    @classmethod
    def instance(cls) -> "NullBatchScheduler":
        if cls._instance is None:
            cls._instance = cls()
        return cls._instance

    @property
    def backend_id(self) -> str:
        return "null"

    async def reserve_async(
        self, model_id: str, estimated_tokens: int, max_wait_seconds: float, ct: object = None
    ) -> BatchSlot:
        return BatchSlot(
            slot_id="00000000-0000-0000-0000-000000000000",
            model_id=model_id,
            tokens=estimated_tokens,
            deadline_utc=datetime.now(timezone.utc) + timedelta(seconds=max_wait_seconds),
        )

    async def release_async(self, slot: BatchSlot, ct: object = None) -> None:
        return None


class NullModelShardPlanner(IModelShardPlanner):
    """No-op shard planner — returns no shards. Mirrors ``NullModelShardPlanner``."""

    _instance: "NullModelShardPlanner | None" = None

    @classmethod
    def instance(cls) -> "NullModelShardPlanner":
        if cls._instance is None:
            cls._instance = cls()
        return cls._instance

    @property
    def backend_id(self) -> str:
        return "null"

    async def plan_async(
        self, model_id: str, param_bytes: int, ct: object = None
    ) -> List[ShardDescriptor]:
        return []


class NullCrossTierOffload(ICrossTierOffload):
    """No-op cross-tier offload — always local. Mirrors ``NullCrossTierOffload``."""

    _instance: "NullCrossTierOffload | None" = None

    @classmethod
    def instance(cls) -> "NullCrossTierOffload":
        if cls._instance is None:
            cls._instance = cls()
        return cls._instance

    @property
    def backend_id(self) -> str:
        return "null"

    async def should_offload_async(
        self,
        model_id: str,
        prompt_tokens: int,
        caller_tier: ServerTier,
        ct: object = None,
    ) -> OffloadDecision:
        return OffloadDecision(
            False, None, "Local execution; no cross-tier offload configured."
        )
