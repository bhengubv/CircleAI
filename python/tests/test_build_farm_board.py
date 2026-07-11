"""test_build_farm_board.py — CircleAI.BuildFarm port.

Covers the enums, the agent pool (kind-filtered atomic acquire/release + list),
the job runner state machine (start -> Running, complete -> Succeeded/Failed),
the artifact store, and the fail-closed null defaults. C# is the exact spec.
"""
from __future__ import annotations

from datetime import datetime, timezone

import pytest

from circle_ai.build_farm import (
    BuildAgent,
    BuildAgentKind,
    BuildArtifact,
    BuildJob,
    BuildJobPhase,
    IBuildAgentPool,
    IBuildArtifactStore,
    IBuildJobRunner,
    InMemoryBuildAgentPool,
    InMemoryBuildArtifactStore,
    InMemoryBuildJobRunner,
    NullBuildAgentPool,
    NullBuildArtifactStore,
    NullBuildJobRunner,
)


def test_enum_ordinals():
    assert BuildAgentKind.Linux == 0 and BuildAgentKind.Ios == 4
    assert BuildJobPhase.Pending == 0 and BuildJobPhase.Failed == 3


async def test_agent_pool_acquire_release_list():
    pool = InMemoryBuildAgentPool()
    assert isinstance(pool, IBuildAgentPool)
    pool.register(BuildAgent("a1", BuildAgentKind.Linux, "ubuntu", None))
    pool.register(BuildAgent("a2", BuildAgentKind.Linux, "ubuntu", "x64"))
    pool.register(BuildAgent("m1", BuildAgentKind.Mac, "macos", "arm64"))

    first = await pool.acquire_async(BuildAgentKind.Linux)
    second = await pool.acquire_async(BuildAgentKind.Linux)
    third = await pool.acquire_async(BuildAgentKind.Linux)
    assert {first.agent_id, second.agent_id} == {"a1", "a2"}  # both linux agents
    assert third is None  # both busy
    assert await pool.acquire_async(BuildAgentKind.Windows) is None  # none registered

    await pool.release_async(first.agent_id)
    again = await pool.acquire_async(BuildAgentKind.Linux)
    assert again.agent_id == first.agent_id  # released one re-acquirable

    assert {a.agent_id for a in await pool.list_async()} == {"a1", "a2", "m1"}


async def test_job_runner_state_machine():
    runner = InMemoryBuildJobRunner()
    assert isinstance(runner, IBuildJobRunner)
    job = await runner.start_async("a1", "repo", "main")
    assert isinstance(job, BuildJob)
    assert job.job_id == "job-1" and job.phase == BuildJobPhase.Running
    runner.complete(job.job_id, True)
    assert (await runner.get_async("job-1")).phase == BuildJobPhase.Succeeded

    job2 = await runner.start_async("a1", "repo", "dev")
    assert job2.job_id == "job-2"
    runner.complete(job2.job_id, False)
    assert (await runner.get_async("job-2")).phase == BuildJobPhase.Failed
    assert await runner.get_async("nope") is None


async def test_job_runner_guards():
    runner = InMemoryBuildJobRunner()
    with pytest.raises(ValueError):
        await runner.start_async(" ", "r", "b")
    with pytest.raises(RuntimeError):
        runner.complete("missing", True)


async def test_artifact_store_roundtrip():
    store = InMemoryBuildArtifactStore()
    assert isinstance(store, IBuildArtifactStore)
    await store.save_async(BuildArtifact("art1", "job-1", "app.apk", b"\x01\x02\x03"))
    got = await store.get_async("art1")
    assert got is not None and got.payload == b"\x01\x02\x03"
    assert await store.get_async("nope") is None
    with pytest.raises(ValueError):
        await store.save_async(BuildArtifact("  ", "j", "n", b""))


async def test_null_implementations_fail_closed():
    p = NullBuildAgentPool.Instance
    r = NullBuildJobRunner.Instance
    s = NullBuildArtifactStore.Instance
    assert p.backend_id == "null" and r.backend_id == "null" and s.backend_id == "null"
    assert await p.acquire_async(BuildAgentKind.Linux) is None
    assert await p.list_async() == []
    job = await r.start_async("a", "r", "b")
    assert job.job_id == "00000000-0000-0000-0000-000000000000"
    assert job.phase == BuildJobPhase.Failed
    assert await r.get_async("x") is None
    await s.save_async(BuildArtifact("a", "j", "n", b""))  # no-op
    assert await s.get_async("a") is None
