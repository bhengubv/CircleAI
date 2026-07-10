"""circle_ai.inference_server_enterprise — port of CircleAI.Inference.Server.Enterprise.

Enterprise-tier inference-server contracts + real in-memory implementations +
single-node null defaults:
  * ServerTier enum,
  * records: TenantContext, TenantQuota, BatchSlot, ShardDescriptor,
    OffloadDecision,
  * contracts: ITenantRouter, IBatchScheduler, IModelShardPlanner,
    ICrossTierOffload,
  * real impls: RoundRobinTenantRouter, InMemoryBatchScheduler,
    EvenSplitModelShardPlanner, PolicyCrossTierOffload,
  * null impls: NullTenantRouter, NullBatchScheduler, NullModelShardPlanner,
    NullCrossTierOffload.
"""
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
from .implementations import (
    EvenSplitModelShardPlanner,
    InMemoryBatchScheduler,
    PolicyCrossTierOffload,
    RoundRobinTenantRouter,
)
from .null_implementations import (
    NullBatchScheduler,
    NullCrossTierOffload,
    NullModelShardPlanner,
    NullTenantRouter,
)

__all__ = [
    # enum
    "ServerTier",
    # records
    "TenantContext",
    "TenantQuota",
    "BatchSlot",
    "ShardDescriptor",
    "OffloadDecision",
    # contracts
    "ITenantRouter",
    "IBatchScheduler",
    "IModelShardPlanner",
    "ICrossTierOffload",
    # real impls
    "RoundRobinTenantRouter",
    "InMemoryBatchScheduler",
    "EvenSplitModelShardPlanner",
    "PolicyCrossTierOffload",
    # null impls
    "NullTenantRouter",
    "NullBatchScheduler",
    "NullModelShardPlanner",
    "NullCrossTierOffload",
]
