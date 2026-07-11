# null_implementations.py
#
# Port of CircleAI.DevTools NullImplementations.cs (C# — the EXACT spec).
#
# (3.0.0) Fail-closed dev-tools defaults. Each exposes a singleton `INSTANCE`
# mirroring the C# `static readonly ... Instance`. Empty-Guid ids ->
# str(uuid.UUID(int=0)).

from __future__ import annotations

import uuid
from typing import List, Optional, Sequence

from .contracts import (
    AgentTurn,
    FileEdit,
    IAgentShell,
    ICodeEditor,
    IInlineSuggester,
    IPatchPlanner,
    IRefactorTool,
    InlineSuggestion,
    PatchPlan,
    RefactorRequest,
)

_EMPTY_GUID = str(uuid.UUID(int=0))


class NullCodeEditor(ICodeEditor):
    INSTANCE: "NullCodeEditor"

    @property
    def backend_id(self) -> str:
        return "null"

    async def read_async(self, path: str, ct: Optional[object] = None) -> str:
        return ""

    async def apply_async(
        self, edits: Sequence[FileEdit], ct: Optional[object] = None
    ) -> None:
        return None

    async def save_async(self, path: str, ct: Optional[object] = None) -> None:
        return None


class NullInlineSuggester(IInlineSuggester):
    INSTANCE: "NullInlineSuggester"

    @property
    def backend_id(self) -> str:
        return "null"

    async def suggest_async(
        self,
        path: str,
        line: int,
        column: int,
        context_before: str,
        ct: Optional[object] = None,
    ) -> Optional[InlineSuggestion]:
        return None


class NullAgentShell(IAgentShell):
    INSTANCE: "NullAgentShell"

    @property
    def backend_id(self) -> str:
        return "null"

    async def run_turn_async(
        self, user_prompt: str, ct: Optional[object] = None
    ) -> AgentTurn:
        return AgentTurn(_EMPTY_GUID, user_prompt, "", [])

    async def history_async(
        self, limit: int = 50, ct: Optional[object] = None
    ) -> List[AgentTurn]:
        return []


class NullPatchPlanner(IPatchPlanner):
    INSTANCE: "NullPatchPlanner"

    @property
    def backend_id(self) -> str:
        return "null"

    async def plan_async(self, goal: str, ct: Optional[object] = None) -> PatchPlan:
        return PatchPlan(goal, [], [])

    async def apply_async(self, plan: PatchPlan, ct: Optional[object] = None) -> None:
        return None


class NullRefactorTool(IRefactorTool):
    INSTANCE: "NullRefactorTool"

    @property
    def backend_id(self) -> str:
        return "null"

    async def propose_async(
        self, request: RefactorRequest, ct: Optional[object] = None
    ) -> List[FileEdit]:
        return []


NullCodeEditor.INSTANCE = NullCodeEditor()
NullInlineSuggester.INSTANCE = NullInlineSuggester()
NullAgentShell.INSTANCE = NullAgentShell()
NullPatchPlanner.INSTANCE = NullPatchPlanner()
NullRefactorTool.INSTANCE = NullRefactorTool()
