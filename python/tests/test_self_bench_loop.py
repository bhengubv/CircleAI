"""test_self_bench_loop.py

Verifies SelfBenchSelfImprovementLoop ported from CircleAI.Companion
(SelfBenchSelfImprovementLoop.cs): blank-suite defaulting, empty-suite skip,
promote path (callback fired + best score recorded), and reject path.
"""
from __future__ import annotations

from typing import List, Optional, Sequence

import pytest

from circle_ai.companion.herjarvis_contracts import ISelfImprovementLoop
from circle_ai.companion.self_bench_loop import (
    AbVerdict,
    BenchSummary,
    IAbBenchRunner,
    IBenchSuiteRegistry,
    RegressionGateConfig,
    SelfBenchSelfImprovementLoop,
)


class FakeRegistry(IBenchSuiteRegistry):
    def __init__(self, tasks_by_suite: dict) -> None:
        self._tasks = tasks_by_suite

    def get(self, suite_id: str) -> Sequence[object]:
        return self._tasks.get(suite_id, [])


class FakeRunner(IAbBenchRunner):
    def __init__(self, verdict: AbVerdict) -> None:
        self._verdict = verdict
        self.compared = False
        self.seen_gate: Optional[RegressionGateConfig] = None

    async def compare_async(self, suite_id, tasks, baseline, candidate, gate, *, ct=None) -> AbVerdict:
        self.compared = True
        self.seen_gate = gate
        return self._verdict


async def _make_ai(ct) -> object:
    return object()


def _loop(registry, runner, on_promote=None, gate=None) -> SelfBenchSelfImprovementLoop:
    return SelfBenchSelfImprovementLoop(
        registry, runner, _make_ai, _make_ai, on_promote=on_promote, gate=gate
    )


def test_implements_interface() -> None:
    loop = _loop(FakeRegistry({}), FakeRunner(AbVerdict(False, BenchSummary(0.0), "x")))
    assert isinstance(loop, ISelfImprovementLoop)


async def test_empty_suite_is_skipped() -> None:
    loop = _loop(FakeRegistry({"s": []}), FakeRunner(AbVerdict(True, BenchSummary(1.0), "y")))
    v = await loop.cycle_async("s")
    assert v.improvements_applied == "skipped: no tasks in suite"
    assert v.new_bench_score == 0.0


async def test_blank_suite_defaults_to_default() -> None:
    reg = FakeRegistry({"default": [object()]})
    runner = FakeRunner(AbVerdict(False, BenchSummary(0.7), "no gain"))
    loop = _loop(reg, runner)
    v = await loop.cycle_async("   ")
    assert runner.compared is True
    assert v.improvements_applied == "rejected (no gain)"


async def test_promote_fires_callback_and_records_best() -> None:
    reg = FakeRegistry({"s": [object(), object()]})
    verdict = AbVerdict(True, BenchSummary(0.92), "mean +0.05")
    runner = FakeRunner(verdict)
    promoted: List[AbVerdict] = []

    async def on_promote(v, ct) -> None:
        promoted.append(v)

    loop = _loop(reg, runner, on_promote=on_promote)
    result = await loop.cycle_async("s")
    assert result.improvements_applied == "promoted candidate (mean +0.05)"
    assert result.new_bench_score == pytest.approx(0.92)
    assert promoted == [verdict]
    assert loop.best_score_for("s") == pytest.approx(0.92)


async def test_reject_does_not_record_best() -> None:
    reg = FakeRegistry({"s": [object()]})
    runner = FakeRunner(AbVerdict(False, BenchSummary(0.4), "below threshold"))
    loop = _loop(reg, runner)
    result = await loop.cycle_async("s")
    assert result.improvements_applied == "rejected (below threshold)"
    assert loop.best_score_for("s") == 0.0


async def test_default_gate_used_when_none() -> None:
    reg = FakeRegistry({"s": [object()]})
    runner = FakeRunner(AbVerdict(False, BenchSummary(0.5), "r"))
    loop = _loop(reg, runner)
    await loop.cycle_async("s")
    assert isinstance(runner.seen_gate, RegressionGateConfig)
    assert runner.seen_gate.min_mean_score_improvement == pytest.approx(0.01)


async def test_best_score_takes_max_across_cycles() -> None:
    reg = FakeRegistry({"s": [object()]})
    runner = FakeRunner(AbVerdict(True, BenchSummary(0.9), "up"))
    loop = _loop(reg, runner)
    await loop.cycle_async("s")  # records 0.9
    # Next promote with a lower score keeps the higher best.
    runner._verdict = AbVerdict(True, BenchSummary(0.6), "up-again")
    await loop.cycle_async("s")
    assert loop.best_score_for("s") == pytest.approx(0.9)


def test_rejects_none_deps() -> None:
    runner = FakeRunner(AbVerdict(False, BenchSummary(0.0), "x"))
    with pytest.raises(ValueError):
        SelfBenchSelfImprovementLoop(None, runner, _make_ai, _make_ai)  # type: ignore[arg-type]
    with pytest.raises(ValueError):
        SelfBenchSelfImprovementLoop(FakeRegistry({}), None, _make_ai, _make_ai)  # type: ignore[arg-type]
    with pytest.raises(ValueError):
        SelfBenchSelfImprovementLoop(FakeRegistry({}), runner, None, _make_ai)  # type: ignore[arg-type]
    with pytest.raises(ValueError):
        SelfBenchSelfImprovementLoop(FakeRegistry({}), runner, _make_ai, None)  # type: ignore[arg-type]
