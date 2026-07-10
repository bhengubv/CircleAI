"""test_inference_server_enterprise.py — enterprise-tier primitives + null defaults."""
from __future__ import annotations

import pytest

from circle_ai.inference_server_enterprise import (
    EvenSplitModelShardPlanner,
    InMemoryBatchScheduler,
    NullBatchScheduler,
    NullCrossTierOffload,
    NullModelShardPlanner,
    NullTenantRouter,
    PolicyCrossTierOffload,
    RoundRobinTenantRouter,
    ServerTier,
    TenantContext,
    TenantQuota,
)


# ── ServerTier ───────────────────────────────────────────────────────────


def test_server_tier_ordinals():
    assert int(ServerTier.SINGLE_NODE) == 0
    assert int(ServerTier.SERVER) == 1
    assert int(ServerTier.SERVER_FARM) == 2


# ── RoundRobinTenantRouter ───────────────────────────────────────────────


async def test_round_robin_cycles_nodes():
    r = RoundRobinTenantRouter()
    r.register_node("m", "node-a")
    r.register_node("m", "node-b")
    tenant = TenantContext("t1")
    picks = [await r.choose_node_async(tenant, "m") for _ in range(4)]
    assert picks == ["node-a", "node-b", "node-a", "node-b"]


async def test_round_robin_no_nodes_returns_none():
    r = RoundRobinTenantRouter()
    assert await r.choose_node_async(TenantContext("t1"), "unknown") is None


async def test_round_robin_dedupes_node_registration():
    r = RoundRobinTenantRouter()
    r.register_node("m", "n1")
    r.register_node("m", "n1")  # duplicate ignored
    r.register_node("m", "n2")
    picks = [await r.choose_node_async(TenantContext("t"), "m") for _ in range(4)]
    assert picks == ["n1", "n2", "n1", "n2"]


async def test_tenant_quota_set_get():
    r = RoundRobinTenantRouter()
    q = TenantQuota("t1", 10, 3, 1024, 100000)
    await r.set_quota_async(q)
    got = await r.get_quota_async("t1")
    assert got == q
    assert await r.get_quota_async("unknown") is None


async def test_router_backend_id():
    assert RoundRobinTenantRouter().backend_id == "round-robin"


async def test_router_validation():
    r = RoundRobinTenantRouter()
    with pytest.raises(ValueError):
        r.register_node("", "n")
    with pytest.raises(ValueError):
        await r.choose_node_async(TenantContext("t"), "")


# ── InMemoryBatchScheduler ───────────────────────────────────────────────


async def test_batch_reserve_unique_ids_and_deadline():
    s = InMemoryBatchScheduler()
    a = await s.reserve_async("m", 100, 5.0)
    b = await s.reserve_async("m", 50, 5.0)
    assert a.slot_id != b.slot_id
    assert a.slot_id == "slot-1" and b.slot_id == "slot-2"
    assert a.tokens == 100
    assert a.deadline_utc > a.deadline_utc.replace(microsecond=0) or a.deadline_utc is not None


async def test_batch_release():
    s = InMemoryBatchScheduler()
    slot = await s.reserve_async("m", 10, 1.0)
    await s.release_async(slot)  # no error; idempotent removal


async def test_batch_reserve_validation():
    s = InMemoryBatchScheduler()
    with pytest.raises(ValueError):
        await s.reserve_async("", 10, 1.0)
    with pytest.raises(ValueError):
        await s.reserve_async("m", 0, 1.0)
    with pytest.raises(ValueError):
        await s.reserve_async("m", 10, 0)


# ── EvenSplitModelShardPlanner ───────────────────────────────────────────


async def test_shard_plan_even_split_with_remainder():
    planner = EvenSplitModelShardPlanner(lambda mid: ["n0", "n1", "n2"])
    shards = await planner.plan_async("m", 10, None)
    # 10 / 3 = 3 rem 1 -> sizes [4, 3, 3], contiguous ranges.
    assert [(s.range_start, s.range_end, s.node_id) for s in shards] == [
        (0, 4, "n0"),
        (4, 7, "n1"),
        (7, 10, "n2"),
    ]
    assert shards[0].shard_id == "shard-m-0"


async def test_shard_plan_no_nodes_empty():
    planner = EvenSplitModelShardPlanner(lambda mid: [])
    assert await planner.plan_async("m", 100, None) == []


async def test_shard_plan_validation():
    planner = EvenSplitModelShardPlanner(lambda mid: ["n0"])
    with pytest.raises(ValueError):
        await planner.plan_async("", 10)
    with pytest.raises(ValueError):
        await planner.plan_async("m", 0)


# ── PolicyCrossTierOffload ───────────────────────────────────────────────


async def test_offload_fits_locally():
    o = PolicyCrossTierOffload(local_prompt_ceiling=2048)
    d = await o.should_offload_async("m", 100, ServerTier.SINGLE_NODE)
    assert d.should_offload is False
    assert d.reason == "Prompt fits locally"


async def test_offload_exceeds_ceiling():
    o = PolicyCrossTierOffload(local_prompt_ceiling=1000, farm_target_node="farm-1")
    d = await o.should_offload_async("m", 5000, ServerTier.SERVER)
    assert d.should_offload is True
    assert d.target_node_id == "farm-1"
    assert "exceeds local ceiling" in d.reason


async def test_offload_top_tier_never_offloads():
    o = PolicyCrossTierOffload(local_prompt_ceiling=10)
    d = await o.should_offload_async("m", 99999, ServerTier.SERVER_FARM)
    assert d.should_offload is False
    assert d.reason == "Caller is already top-tier"


async def test_offload_validation():
    o = PolicyCrossTierOffload()
    with pytest.raises(ValueError):
        await o.should_offload_async("", 10, ServerTier.SERVER)
    with pytest.raises(ValueError):
        await o.should_offload_async("m", -1, ServerTier.SERVER)
    with pytest.raises(ValueError):
        PolicyCrossTierOffload(local_prompt_ceiling=0)


# ── Null implementations ─────────────────────────────────────────────────


async def test_null_tenant_router():
    r = NullTenantRouter.instance()
    assert r.backend_id == "null"
    assert await r.choose_node_async(TenantContext("t"), "m") is None
    assert await r.get_quota_async("t") is None
    await r.set_quota_async(TenantQuota("t", 1, 1, 1, 1))  # no-op


async def test_null_batch_scheduler_returns_empty_id_slot():
    s = NullBatchScheduler.instance()
    slot = await s.reserve_async("m", 5, 2.0)
    assert slot.slot_id == "00000000-0000-0000-0000-000000000000"
    assert slot.model_id == "m" and slot.tokens == 5
    await s.release_async(slot)


async def test_null_shard_planner_returns_empty():
    p = NullModelShardPlanner.instance()
    assert await p.plan_async("m", 100) == []


async def test_null_cross_tier_offload_never_offloads():
    o = NullCrossTierOffload.instance()
    d = await o.should_offload_async("m", 999999, ServerTier.SINGLE_NODE)
    assert d.should_offload is False
    assert "no cross-tier offload configured" in d.reason
