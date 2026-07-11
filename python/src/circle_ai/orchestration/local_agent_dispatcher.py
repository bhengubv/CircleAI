# local_agent_dispatcher.py
#
# Port of CircleAI.Orchestration LocalAgentDispatcher.cs (C# — the EXACT spec).
#
# In-process agent dispatcher. Routes tasks to handler callables registered per
# AgentRole. No external network calls. Tasks dispatched to roles without a
# registered handler return Blocked immediately.
#
# The C# type owns an unbounded Channel<AgentTask> that Dispose() completes;
# it is never actually read (loki-mode hooks at the host level), so the port
# tracks a `_disposed` flag and raises after disposal, matching the observable
# behaviour (ObjectDisposedException -> RuntimeError). Handlers are
# Func<AgentTask, CancellationToken, Task<SwarmResult>> — async callables here.

from __future__ import annotations

from datetime import datetime, timezone
from typing import Awaitable, Callable, Dict, Optional

from .contracts import (
    AgentRole,
    AgentStatus,
    AgentTask,
    IAgentDispatcher,
    QualityGateResult,
    SwarmResult,
)

# C# Func<AgentTask, CancellationToken, Task<SwarmResult>>.
AgentHandler = Callable[[AgentTask, Optional[object]], Awaitable[SwarmResult]]


class LocalAgentDispatcher(IAgentDispatcher):
    def __init__(self) -> None:
        self._handlers: Dict[AgentRole, AgentHandler] = {}
        self._disposed = False

    def register_handler(self, role: AgentRole, handler: AgentHandler) -> None:
        """Register (or replace) an async handler for ``role``."""
        if handler is None:
            raise ValueError("handler must not be None")
        self._handlers[role] = handler

    async def dispatch_async(self, task: AgentTask, ct: Optional[object] = None) -> SwarmResult:
        if self._disposed:
            raise RuntimeError("LocalAgentDispatcher has been disposed.")

        handler = self._handlers.get(task.role)
        if handler is not None:
            return await handler(task, ct)

        # No handler registered — surface a blocked result with an actionable
        # message.
        return SwarmResult(
            task.id,
            task.role,
            AgentStatus.Blocked,
            f"No handler registered for role {task.role.name}.",
            [f"Register a handler for AgentRole.{task.role.name} before dispatching."],
            datetime.now(timezone.utc),
        )

    async def run_quality_gate_async(self, result: SwarmResult, ct: Optional[object] = None) -> QualityGateResult:
        """Deterministic gate: any issue prefixed ``[CRITICAL]`` or ``[HIGH]``
        (case-insensitive) is a blocker; all other issues are warnings. Warnings
        are computed with list-membership semantics to mirror the C#
        ``!blockers.Contains(i)``."""
        blockers = [
            i
            for i in result.issues
            if i.upper().startswith("[CRITICAL]") or i.upper().startswith("[HIGH]")
        ]
        warnings = [i for i in result.issues if i not in blockers]
        return QualityGateResult(len(blockers) == 0, blockers, warnings)

    def dispose(self) -> None:
        """Dispose the dispatcher. After disposal, :meth:`dispatch_async`
        raises."""
        self._disposed = True

    def __enter__(self) -> "LocalAgentDispatcher":
        return self

    def __exit__(self, *exc: object) -> None:
        self.dispose()
