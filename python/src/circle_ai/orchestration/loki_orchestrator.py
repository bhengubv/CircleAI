# loki_orchestrator.py
#
# Port of CircleAI.Orchestration LokiOrchestrator.cs (C# — the EXACT spec).
#
# Host-side orchestrator. Accepts AgentTask items, dispatches them through an
# IAgentDispatcher, enforces quality gates, and exposes results as an async
# iterator for host applications to consume.
#
# Task execution is bounded by AgentSwarmConfig.max_concurrency. After each task
# completes, the quality gate is evaluated; gate failures are re-emitted as
# Blocked results with the gate's blocker messages appended to
# SwarmResult.issues.
#
# ─ Porting notes ────────────────────────────────────────────────────────────
#   * C# SemaphoreSlim(MaxConcurrency) -> asyncio.Semaphore. Like the C#, the
#     orchestrator ACQUIRES a permit before scheduling each task (bounding the
#     start rate), and the running task RELEASES it in a finally block.
#   * Per-task timeout uses asyncio.wait_for(TaskTimeout seconds). A timeout ->
#     a Failed SwarmResult "[HIGH] Task exceeded configured timeout." A
#     dispatcher exception is likewise wrapped as a Failed SwarmResult so the
#     remaining tasks still surface (never breaks the stream mid-enumeration).
#   * Results are yielded in completion order == scheduling order (the C#
#     iterates `running` in order), one SwarmResult per task.
#   * `result with { Status = Blocked, Issues = … }` -> dataclasses.replace.

from __future__ import annotations

import asyncio
import dataclasses
from datetime import datetime, timezone
from typing import AsyncIterator, Iterable, List, Optional

from .contracts import (
    AgentStatus,
    AgentSwarmConfig,
    AgentTask,
    IAgentDispatcher,
    SwarmResult,
)


class LokiOrchestrator:
    """Host-side orchestrator that dispatches a swarm of :class:`AgentTask`
    items through an :class:`IAgentDispatcher`, bounded by a semaphore, and
    enforces quality gates. Mirrors ``CircleAI.Orchestration.LokiOrchestrator``.
    """

    def __init__(
        self, dispatcher: IAgentDispatcher, config: Optional[AgentSwarmConfig] = None
    ) -> None:
        if dispatcher is None:
            raise ValueError("dispatcher must not be None")
        self._dispatcher = dispatcher
        self._config = config if config is not None else AgentSwarmConfig.default()

    async def run_swarm_async(
        self, tasks: Iterable[AgentTask], ct: Optional[object] = None
    ) -> AsyncIterator[SwarmResult]:
        """Run a swarm of tasks concurrently up to
        :attr:`AgentSwarmConfig.max_concurrency`. For each completed task, the
        quality gate is evaluated; gate failures are yielded as
        :attr:`AgentStatus.Blocked` results.

        ``tasks`` is evaluated eagerly into a list before any dispatching begins.
        """
        semaphore = asyncio.Semaphore(self._config.max_concurrency)
        pending: List[AgentTask] = list(tasks)
        running: List["asyncio.Task[SwarmResult]"] = []

        for task in pending:
            # Acquire a permit before scheduling — bounds the start rate exactly
            # as the C# `await semaphore.WaitAsync(ct)` before `running.Add(...)`.
            await semaphore.acquire()
            running.append(asyncio.ensure_future(self._run_one_async(task, semaphore)))

        for running_task in running:
            result = await running_task
            gate = await self._dispatcher.run_quality_gate_async(result, ct)

            if (not gate.passed) and (
                self._config.require_review_pass_before_deploy
                or self._config.require_security_pass_before_deploy
            ):
                yield dataclasses.replace(
                    result,
                    status=AgentStatus.Blocked,
                    issues=list(result.issues) + list(gate.blockers),
                )
            else:
                yield result

    async def _run_one_async(
        self, task: AgentTask, semaphore: asyncio.Semaphore
    ) -> SwarmResult:
        try:
            timeout_s = self._config.task_timeout.total_seconds()
            return await asyncio.wait_for(
                self._dispatcher.dispatch_async(task, None), timeout=timeout_s
            )
        except asyncio.TimeoutError:
            return SwarmResult(
                task.id,
                task.role,
                AgentStatus.Failed,
                "Task timed out.",
                ["[HIGH] Task exceeded configured timeout."],
                datetime.now(timezone.utc),
            )
        except Exception as ex:  # noqa: BLE001
            # A dispatcher exception used to propagate out and break the swarm
            # enumeration mid-stream. Wrap it as a failed SwarmResult so the
            # remaining tasks still surface to the caller.
            type_name = type(ex).__name__
            return SwarmResult(
                task.id,
                task.role,
                AgentStatus.Failed,
                f"Dispatcher threw: {type_name}: {ex}",
                [f"[HIGH] {type_name}: {ex}"],
                datetime.now(timezone.utc),
            )
        finally:
            semaphore.release()
