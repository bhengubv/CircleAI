"""Real in-memory enterprise-tier implementations.

Port of ``CircleAI.Inference.Server.Enterprise.InMemoryInferenceServerEnterprise``:
  * RoundRobinTenantRouter — round-robin over registered nodes per model,
  * InMemoryBatchScheduler — reservation queue with deadline + release,
  * EvenSplitModelShardPlanner — even-bucket split across registered nodes,
  * PolicyCrossTierOffload — policy decision (offload if prompt exceeds ceiling).
"""
from __future__ import annotations

import threading
from datetime import datetime, timedelta, timezone
from typing import Callable, Dict, List, Optional, Sequence

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
    "RoundRobinTenantRouter",
    "InMemoryBatchScheduler",
    "EvenSplitModelShardPlanner",
    "PolicyCrossTierOffload",
]


class RoundRobinTenantRouter(ITenantRouter):
    """Round-robin node picker per model. Port of ``RoundRobinTenantRouter``.

    Register nodes per model via :meth:`register_node`; :meth:`choose_node_async`
    cycles through them. Quotas are stored/retrieved by tenant id.
    """

    __slots__ = ("_lock", "_quotas", "_nodes_by_model", "_rr")

    def __init__(self) -> None:
        self._lock = threading.Lock()
        self._quotas: Dict[str, TenantQuota] = {}
        self._nodes_by_model: Dict[str, List[str]] = {}
        self._rr: Dict[str, int] = {}

    @property
    def backend_id(self) -> str:
        return "round-robin"

    def register_node(self, model_id: str, node_id: str) -> None:
        if not model_id or not model_id.strip():
            raise ValueError("model_id required")
        if not node_id or not node_id.strip():
            raise ValueError("node_id required")
        with self._lock:
            lst = self._nodes_by_model.setdefault(model_id, [])
            if node_id not in lst:
                lst.append(node_id)

    async def choose_node_async(
        self, tenant: TenantContext, model_id: str, ct: object = None
    ) -> Optional[str]:
        if tenant is None:
            raise ValueError("tenant is required")
        if not model_id or not model_id.strip():
            raise ValueError("model_id required")
        with self._lock:
            nodes = self._nodes_by_model.get(model_id)
            if not nodes:
                return None
            idx = self._rr.get(model_id, 0)
            pick = nodes[idx % len(nodes)]
            self._rr[model_id] = idx + 1
            return pick

    async def set_quota_async(self, quota: TenantQuota, ct: object = None) -> None:
        if quota is None:
            raise ValueError("quota is required")
        with self._lock:
            self._quotas[quota.tenant_id] = quota

    async def get_quota_async(
        self, tenant_id: str, ct: object = None
    ) -> Optional[TenantQuota]:
        if not tenant_id or not tenant_id.strip():
            raise ValueError("tenant_id required")
        with self._lock:
            return self._quotas.get(tenant_id)


class InMemoryBatchScheduler(IBatchScheduler):
    """Reservation queue with deadline + release. Port of ``InMemoryBatchScheduler``.

    Each reservation gets a monotonically increasing slot id (``slot-N``) and a
    deadline of ``now + max_wait``.
    """

    __slots__ = ("_lock", "_slots", "_seq")

    def __init__(self) -> None:
        self._lock = threading.Lock()
        self._slots: Dict[str, BatchSlot] = {}
        self._seq = 0

    @property
    def backend_id(self) -> str:
        return "in-memory"

    async def reserve_async(
        self, model_id: str, estimated_tokens: int, max_wait_seconds: float, ct: object = None
    ) -> BatchSlot:
        if not model_id or not model_id.strip():
            raise ValueError("model_id required")
        if estimated_tokens <= 0:
            raise ValueError("estimated_tokens must be > 0")
        if max_wait_seconds <= 0:
            raise ValueError("max_wait must be > 0")
        with self._lock:
            self._seq += 1
            seq = self._seq
        slot = BatchSlot(
            slot_id=f"slot-{seq}",
            model_id=model_id,
            tokens=estimated_tokens,
            deadline_utc=datetime.now(timezone.utc) + timedelta(seconds=max_wait_seconds),
        )
        with self._lock:
            self._slots[slot.slot_id] = slot
        return slot

    async def release_async(self, slot: BatchSlot, ct: object = None) -> None:
        if slot is None:
            raise ValueError("slot is required")
        with self._lock:
            self._slots.pop(slot.slot_id, None)


class EvenSplitModelShardPlanner(IModelShardPlanner):
    """Even-bucket split across registered nodes. Port of ``EvenSplitModelShardPlanner``.

    ``nodes_for`` is a callable ``(model_id) -> list[str]``. The parameter byte
    space is split into contiguous ``[start, end)`` ranges, with the first
    ``param_bytes % node_count`` shards getting one extra byte — matching the
    C# bucket + remainder distribution exactly.
    """

    __slots__ = ("_nodes_for",)

    def __init__(self, nodes_for: Callable[[str], Sequence[str]]) -> None:
        if nodes_for is None:
            raise ValueError("nodes_for is required")
        self._nodes_for = nodes_for

    @property
    def backend_id(self) -> str:
        return "even-split"

    async def plan_async(
        self, model_id: str, param_bytes: int, ct: object = None
    ) -> List[ShardDescriptor]:
        if not model_id or not model_id.strip():
            raise ValueError("model_id required")
        if param_bytes <= 0:
            raise ValueError("param_bytes must be > 0")

        nodes = self._nodes_for(model_id)
        if not nodes:
            return []
        nodes = list(nodes)

        bucket = param_bytes // len(nodes)
        rem = param_bytes % len(nodes)
        shards: List[ShardDescriptor] = []
        cursor = 0
        for i in range(len(nodes)):
            size = bucket + (1 if i < rem else 0)
            shards.append(
                ShardDescriptor(f"shard-{model_id}-{i}", cursor, cursor + size, nodes[i])
            )
            cursor += size
        return shards


class PolicyCrossTierOffload(ICrossTierOffload):
    """Policy-based offload decision. Port of ``PolicyCrossTierOffload``.

    Offloads when the prompt exceeds ``local_prompt_ceiling`` and the caller
    isn't already top-tier (SERVER_FARM).
    """

    __slots__ = ("_local_prompt_ceiling", "_farm_target_node")

    def __init__(
        self, local_prompt_ceiling: int = 2048, farm_target_node: Optional[str] = None
    ) -> None:
        if local_prompt_ceiling <= 0:
            raise ValueError("local_prompt_ceiling must be > 0")
        self._local_prompt_ceiling = local_prompt_ceiling
        self._farm_target_node = farm_target_node

    @property
    def backend_id(self) -> str:
        return "policy"

    async def should_offload_async(
        self,
        model_id: str,
        prompt_tokens: int,
        caller_tier: ServerTier,
        ct: object = None,
    ) -> OffloadDecision:
        if not model_id or not model_id.strip():
            raise ValueError("model_id required")
        if prompt_tokens < 0:
            raise ValueError("prompt_tokens must be >= 0")
        if caller_tier == ServerTier.SERVER_FARM:
            return OffloadDecision(False, None, "Caller is already top-tier")
        if prompt_tokens <= self._local_prompt_ceiling:
            return OffloadDecision(False, None, "Prompt fits locally")
        return OffloadDecision(
            True,
            self._farm_target_node,
            f"Prompt exceeds local ceiling ({self._local_prompt_ceiling} tokens)",
        )
