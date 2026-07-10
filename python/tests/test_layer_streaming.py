"""test_layer_streaming.py — layer-by-layer streaming orchestrator + discovery."""
from __future__ import annotations

import os

import pytest

from circle_ai.inference import (
    ILayerStreamingRunner,
    LayerActivations,
    LayerShardDiscovery,
    LayerStreamingOrchestrator,
    LayerStreamingPlan,
    LayerWeightShard,
    NullLayerStreamingRunner,
)


class _AddOneRunner(ILayerStreamingRunner):
    """Deterministic runner: each layer adds its index+1 to every hidden value
    and records eviction order.
    """

    def __init__(self):
        self.evicted: list[int] = []

    @property
    def backend_id(self) -> str:
        return "add-one"

    @property
    def is_available(self) -> bool:
        return True

    async def run_layer_async(self, shard, input_hidden, ct=None):
        out = [v + shard.layer_index + 1 for v in input_hidden]
        return LayerActivations(shard.layer_index, out)

    async def evict_async(self, layer_index, ct=None):
        self.evicted.append(layer_index)


async def test_forward_runs_every_layer_and_evicts():
    runner = _AddOneRunner()
    orch = LayerStreamingOrchestrator(runner)
    shards = [LayerWeightShard(i, f"/p/layer_{i}", 10) for i in range(3)]
    plan = LayerStreamingPlan("m", 3, shards, 30)

    completed: list[int] = []
    final = await orch.forward_async(plan, [0.0, 0.0], lambda a: completed.append(a.layer_index))
    # 0 adds 1, 1 adds 2, 2 adds 3 -> +6 to each starting 0.
    assert list(final.hidden) == [6.0, 6.0]
    assert final.layer_index == 2
    assert completed == [0, 1, 2]
    assert runner.evicted == [0, 1, 2]


async def test_forward_empty_plan_raises():
    orch = LayerStreamingOrchestrator(_AddOneRunner())
    plan = LayerStreamingPlan("m", 0, [], 0)
    with pytest.raises(ValueError):
        await orch.forward_async(plan, [1.0])


async def test_null_runner_raises_on_use():
    runner = NullLayerStreamingRunner.instance()
    assert runner.is_available is False
    assert runner.backend_id == "null"
    with pytest.raises(RuntimeError):
        await runner.run_layer_async(LayerWeightShard(0, "/p", 1), [0.0])
    # evict is a no-op
    await runner.evict_async(0)


def test_orchestrator_requires_runner():
    with pytest.raises(ValueError):
        LayerStreamingOrchestrator(None)


def test_discover_builds_sorted_plan(tmp_path):
    # Create layer files out of order + a non-layer file to be ignored.
    for i in [2, 0, 1]:
        (tmp_path / f"layer_{i}.safetensors").write_bytes(b"x" * (i + 1))
    (tmp_path / "config.json").write_bytes(b"{}")
    plan = LayerShardDiscovery.discover("m", str(tmp_path))
    assert plan.model_id == "m"
    assert plan.total_layers == 3
    assert [s.layer_index for s in plan.shards] == [0, 1, 2]
    assert plan.approx_parameter_bytes == 1 + 2 + 3


def test_discover_missing_dir_raises():
    with pytest.raises(NotADirectoryError):
        LayerShardDiscovery.discover("m", "/no/such/dir/here")


def test_discover_requires_model_id(tmp_path):
    with pytest.raises(ValueError):
        LayerShardDiscovery.discover("", str(tmp_path))
