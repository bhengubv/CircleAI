# contracts.py
#
# Port of CircleAI.DevTools Contracts.cs (C# — the EXACT spec).
#
# (3.0.0) The Western-dev-tools replacement surface: file-edit / inline-suggestion
# / agent-turn / patch-plan / refactor-request records and the code-editor /
# inline-suggester / agent-shell / patch-planner / refactor-tool interfaces.
#
# C# ValueTask/ValueTask<T> -> async def -> None/T. C# records -> frozen slotted
# dataclasses. float -> float.

from __future__ import annotations

from abc import ABC, abstractmethod
from dataclasses import dataclass
from typing import List, Optional, Sequence


@dataclass(frozen=True, slots=True)
class FileEdit:
    """Mirrors ``CircleAI.DevTools.FileEdit`` — ``record(string Path,
    int RangeStart, int RangeEnd, string Replacement)``.
    """

    path: str
    range_start: int
    range_end: int
    replacement: str


@dataclass(frozen=True, slots=True)
class InlineSuggestion:
    """Mirrors ``CircleAI.DevTools.InlineSuggestion`` — ``record(string Text,
    float Confidence)``.
    """

    text: str
    confidence: float


@dataclass(frozen=True, slots=True)
class AgentTurn:
    """Mirrors ``CircleAI.DevTools.AgentTurn`` — ``record(string TurnId,
    string UserPrompt, string Response, IReadOnlyList<FileEdit> Edits)``.
    """

    turn_id: str
    user_prompt: str
    response: str
    edits: Sequence[FileEdit]


@dataclass(frozen=True, slots=True)
class PatchPlan:
    """Mirrors ``CircleAI.DevTools.PatchPlan`` — ``record(string Goal,
    IReadOnlyList<string> Steps, IReadOnlyList<FileEdit> ProposedEdits)``.
    """

    goal: str
    steps: Sequence[str]
    proposed_edits: Sequence[FileEdit]


@dataclass(frozen=True, slots=True)
class RefactorRequest:
    """Mirrors ``CircleAI.DevTools.RefactorRequest`` — ``record(string Description,
    IReadOnlyList<string> TargetPaths)``.
    """

    description: str
    target_paths: Sequence[str]


class ICodeEditor(ABC):
    """(3.0.0) Read / write text buffers in an editor session."""

    @property
    @abstractmethod
    def backend_id(self) -> str:
        ...

    @abstractmethod
    async def read_async(self, path: str, ct: Optional[object] = None) -> str:
        ...

    @abstractmethod
    async def apply_async(
        self, edits: Sequence[FileEdit], ct: Optional[object] = None
    ) -> None:
        ...

    @abstractmethod
    async def save_async(self, path: str, ct: Optional[object] = None) -> None:
        ...


class IInlineSuggester(ABC):
    """(3.0.0) Tab-completion / ghost-text suggester."""

    @property
    @abstractmethod
    def backend_id(self) -> str:
        ...

    @abstractmethod
    async def suggest_async(
        self,
        path: str,
        line: int,
        column: int,
        context_before: str,
        ct: Optional[object] = None,
    ) -> Optional[InlineSuggestion]:
        ...


class IAgentShell(ABC):
    """(3.0.0) Agent-shell loop — accept user prompt -> reason -> turn record."""

    @property
    @abstractmethod
    def backend_id(self) -> str:
        ...

    @abstractmethod
    async def run_turn_async(
        self, user_prompt: str, ct: Optional[object] = None
    ) -> AgentTurn:
        ...

    @abstractmethod
    async def history_async(
        self, limit: int = 50, ct: Optional[object] = None
    ) -> List[AgentTurn]:
        ...


class IPatchPlanner(ABC):
    """(3.0.0) Propose a multi-file patch plan before applying."""

    @property
    @abstractmethod
    def backend_id(self) -> str:
        ...

    @abstractmethod
    async def plan_async(self, goal: str, ct: Optional[object] = None) -> PatchPlan:
        ...

    @abstractmethod
    async def apply_async(self, plan: PatchPlan, ct: Optional[object] = None) -> None:
        ...


class IRefactorTool(ABC):
    """(3.0.0) Cross-file refactor primitives."""

    @property
    @abstractmethod
    def backend_id(self) -> str:
        ...

    @abstractmethod
    async def propose_async(
        self, request: RefactorRequest, ct: Optional[object] = None
    ) -> List[FileEdit]:
        ...
