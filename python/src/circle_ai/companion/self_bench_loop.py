# companion/self_bench_loop.py
#
# SelfBench-backed ISelfImprovementLoop. Ported from CircleAI.Companion
# (SelfBenchSelfImprovementLoop.cs) — the C# reference.
#
# Implements HER/Jarvis ISelfImprovementLoop by orchestrating CircleAI.SelfBench:
# run the named suite against the current AI service as baseline, ask the host
# for a candidate AI service (e.g. one with a freshly-trained LoRA adapter), A/B
# compare, and only "apply" the candidate if the regression gate passes.
#
# The SelfBench pieces (BenchSuiteRegistry, AbBenchRunner, RegressionGateConfig,
# AbVerdict, BenchSummary) live in CircleAI.SelfBench — a separate project, out of
# this module's scope — so they are modelled here as the minimal injected seams
# this loop consumes. The "apply candidate" step is a host-supplied callback so
# this class stays free of adapter-management plumbing — it just runs the gate.

from __future__ import annotations

import threading
from abc import ABC, abstractmethod
from dataclasses import dataclass
from typing import Awaitable, Callable, Dict, Optional, Sequence

from .herjarvis_contracts import ISelfImprovementLoop, SelfImprovementVerdict


# ── SelfBench seams (subset consumed by the loop) ─────────────────────────────
# These mirror the CircleAI.SelfBench surface the loop touches.


@dataclass(frozen=True, slots=True)
class RegressionGateConfig:
    """Regression-gate thresholds. Mirrors ``CircleAI.SelfBench.RegressionGateConfig``."""

    min_mean_score_improvement: float = 0.01
    max_p95_latency_regression_ms: float = 250.0
    allow_critical_regressions: bool = False


@dataclass(frozen=True, slots=True)
class BenchSummary:
    """A bench run summary. Mirrors ``CircleAI.SelfBench.BenchSummary`` (score field)."""

    mean_score: float


@dataclass(frozen=True, slots=True)
class AbVerdict:
    """An A/B comparison verdict. Mirrors ``CircleAI.SelfBench.AbVerdict``.

    Only the fields the loop reads are required; the rest carry sensible
    defaults so callers/tests can construct a verdict minimally.
    """

    should_promote: bool
    candidate_summary: BenchSummary
    reason: str
    baseline_summary: Optional[BenchSummary] = None
    mean_score_delta: float = 0.0
    p95_latency_delta_ms: float = 0.0
    critical_regressions: Sequence[str] = ()


# The host's AI service is opaque to this loop — it just hands it to the runner.
AIService = object


class IBenchSuiteRegistry(ABC):
    """Suite registry seam — mirrors ``CircleAI.SelfBench.BenchSuiteRegistry.Get``."""

    @abstractmethod
    def get(self, suite_id: str) -> Sequence[object]:
        """Return the tasks registered for a suite (empty if none)."""
        ...


class IAbBenchRunner(ABC):
    """A/B runner seam — mirrors ``CircleAI.SelfBench.AbBenchRunner.CompareAsync``."""

    @abstractmethod
    async def compare_async(
        self,
        suite_id: str,
        tasks: Sequence[object],
        baseline: AIService,
        candidate: AIService,
        gate: RegressionGateConfig,
        *,
        ct: Optional[object] = None,
    ) -> AbVerdict:
        """Run the suite against baseline + candidate and return an A/B verdict."""
        ...


# Host factories + promote callback.
AIServiceFactory = Callable[[Optional[object]], Awaitable[AIService]]
PromoteCallback = Callable[[AbVerdict, Optional[object]], Awaitable[None]]


class SelfBenchSelfImprovementLoop(ISelfImprovementLoop):
    """SelfBench-orchestrating self-improvement loop.

    Mirrors ``CircleAI.Companion.SelfBenchSelfImprovementLoop``. A blank suite id
    defaults to ``"default"``; an empty suite short-circuits to a "skipped"
    verdict. Otherwise it builds baseline + candidate AI services, A/B compares
    under the gate, and — only when the verdict says promote — invokes the
    promote callback and records the new best score.
    """

    __slots__ = (
        "_registry",
        "_runner",
        "_baseline_factory",
        "_candidate_factory",
        "_on_promote",
        "_gate",
        "_best_scores",
        "_lock",
    )

    def __init__(
        self,
        registry: IBenchSuiteRegistry,
        runner: IAbBenchRunner,
        baseline_factory: AIServiceFactory,
        candidate_factory: AIServiceFactory,
        on_promote: Optional[PromoteCallback] = None,
        gate: Optional[RegressionGateConfig] = None,
    ) -> None:
        if registry is None:
            raise ValueError("registry required")
        if runner is None:
            raise ValueError("runner required")
        if baseline_factory is None:
            raise ValueError("baseline_factory required")
        if candidate_factory is None:
            raise ValueError("candidate_factory required")
        self._registry = registry
        self._runner = runner
        self._baseline_factory = baseline_factory
        self._candidate_factory = candidate_factory
        self._on_promote: PromoteCallback = on_promote or self._noop_promote
        self._gate = gate or RegressionGateConfig()
        self._best_scores: Dict[str, float] = {}
        self._lock = threading.Lock()

    async def cycle_async(
        self, bench_suite_id: str, *, ct: Optional[object] = None
    ) -> SelfImprovementVerdict:
        if bench_suite_id is None or len(bench_suite_id.strip()) == 0:
            bench_suite_id = "default"
        tasks = self._registry.get(bench_suite_id)
        if len(tasks) == 0:
            return SelfImprovementVerdict("skipped: no tasks in suite", 0.0)

        baseline = await self._baseline_factory(ct)
        candidate = await self._candidate_factory(ct)

        verdict = await self._runner.compare_async(
            bench_suite_id, tasks, baseline, candidate, self._gate, ct=ct
        )

        new_score = verdict.candidate_summary.mean_score
        if verdict.should_promote:
            await self._on_promote(verdict, ct)
            with self._lock:
                prev = self._best_scores.get(bench_suite_id)
                self._best_scores[bench_suite_id] = (
                    new_score if prev is None else max(prev, new_score)
                )
            applied = f"promoted candidate ({verdict.reason})"
        else:
            applied = f"rejected ({verdict.reason})"
        return SelfImprovementVerdict(applied, new_score)

    def best_score_for(self, bench_suite_id: str) -> float:
        with self._lock:
            return self._best_scores.get(bench_suite_id, 0.0)

    @staticmethod
    async def _noop_promote(verdict: AbVerdict, ct: Optional[object]) -> None:
        return None


__all__ = [
    "RegressionGateConfig",
    "BenchSummary",
    "AbVerdict",
    "IBenchSuiteRegistry",
    "IAbBenchRunner",
    "SelfBenchSelfImprovementLoop",
]
