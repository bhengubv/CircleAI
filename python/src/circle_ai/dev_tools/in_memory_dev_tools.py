# in_memory_dev_tools.py
#
# Port of CircleAI.DevTools InMemoryDevTools.cs (C# — the EXACT spec).
#
# (3.3.0) Real dev-tool implementations — no host delegates required:
#   • FilesystemCodeEditor — read; apply groups edits per file and applies in
#     descending RangeStart order (so earlier offsets stay valid); save is a no-op.
#   • TokenContextInlineSuggester — completes the partial identifier at the cursor
#     from the file's own identifier vocabulary (highest frequency, then shortest).
#   • InMemoryAgentShell — turn history with a deterministic built-in executor;
#     assigns "turn-{n}" ids when the executor left TurnId empty.
#   • PatternMatchPatchPlanner — parses "rename X to Y [in P]" / "remove line N
#     from F" / "append <text> to F" into real FileEdits.
#   • RegexRefactorTool — real Rename + ExtractConstant primitives.

from __future__ import annotations

import os
import re
import threading
from dataclasses import replace
from typing import Awaitable, Callable, Dict, List, Optional, Sequence

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

_IDENTIFIER_RX = re.compile(r"[A-Za-z_][A-Za-z0-9_]*")
_RENAME_RX = re.compile(r"^rename\s+(\S+)\s+to\s+(\S+)(?:\s+in\s+(.+))?$", re.IGNORECASE)
_REMOVE_RX = re.compile(r"^remove\s+line\s+(\d+)\s+from\s+(.+)$", re.IGNORECASE)
_APPEND_RX = re.compile(r"^append\s+(.+?)\s+to\s+(.+)$", re.IGNORECASE)


def _read_text(path: str) -> str:
    with open(path, "r", encoding="utf-8") as fh:
        return fh.read()


def _write_text(path: str, text: str) -> None:
    with open(path, "w", encoding="utf-8") as fh:
        fh.write(text)


class FilesystemCodeEditor(ICodeEditor):
    """Real filesystem :class:`ICodeEditor`. Mirrors
    ``CircleAI.DevTools.FilesystemCodeEditor``."""

    @property
    def backend_id(self) -> str:
        return "filesystem"

    async def read_async(self, path: str, ct: Optional[object] = None) -> str:
        if path is None or path.strip() == "":
            raise ValueError("path required")
        return _read_text(path)

    async def apply_async(
        self, edits: Sequence[FileEdit], ct: Optional[object] = None
    ) -> None:
        if edits is None:
            raise ValueError("edits")
        # GroupBy(Path) — preserve first-seen file order.
        by_file: Dict[str, List[FileEdit]] = {}
        for e in edits:
            by_file.setdefault(e.path, []).append(e)
        for path, file_edits in by_file.items():
            text = _read_text(path)
            ordered = sorted(file_edits, key=lambda e: e.range_start, reverse=True)
            buf = list(text)
            for e in ordered:
                if e.range_start < 0 or e.range_end > len(buf) or e.range_end < e.range_start:
                    raise ValueError(
                        f"Invalid edit range {e.range_start}..{e.range_end} for {e.path}"
                    )
                buf[e.range_start:e.range_end] = list(e.replacement)
            _write_text(path, "".join(buf))

    async def save_async(self, path: str, ct: Optional[object] = None) -> None:
        return None


class TokenContextInlineSuggester(IInlineSuggester):
    """Token-vocabulary :class:`IInlineSuggester`. Mirrors
    ``CircleAI.DevTools.TokenContextInlineSuggester``."""

    @property
    def backend_id(self) -> str:
        return "token-context"

    async def suggest_async(
        self,
        path: str,
        line: int,
        column: int,
        context_before: str,
        ct: Optional[object] = None,
    ) -> Optional[InlineSuggestion]:
        if path is None or path.strip() == "":
            raise ValueError("path required")
        if context_before is None:
            raise ValueError("contextBefore")

        partial = self._extract_partial_at_cursor(context_before)
        if len(partial) < 2:
            return None

        file_text = _read_text(path) if os.path.isfile(path) else context_before
        freq: Dict[str, int] = {}
        for m in _IDENTIFIER_RX.finditer(file_text):
            v = m.group(0)
            if v.startswith(partial) and len(v) > len(partial):
                freq[v] = freq.get(v, 0) + 1
        if len(freq) == 0:
            return None
        # OrderByDescending(Value).ThenBy(Key.Length).First()
        best_key = min(
            freq.keys(),
            key=lambda k: (-freq[k], len(k)),
        )
        completion = best_key[len(partial):]
        confidence = min(1.0, freq[best_key] / 10.0)
        return InlineSuggestion(completion, float(confidence))

    @staticmethod
    def _extract_partial_at_cursor(context_before: str) -> str:
        i = len(context_before)
        while i > 0 and (context_before[i - 1].isalnum() or context_before[i - 1] == "_"):
            i -= 1
        return context_before[i:]


#: executor(prompt, ct) -> Awaitable[AgentTurn] (C# Func<string, CancellationToken, ValueTask<AgentTurn>>)
AgentExecutor = Callable[[str, Optional[object]], Awaitable[AgentTurn]]


class InMemoryAgentShell(IAgentShell):
    """In-memory :class:`IAgentShell` with a deterministic built-in executor.
    Mirrors ``CircleAI.DevTools.InMemoryAgentShell``."""

    def __init__(self, executor: Optional[AgentExecutor] = None) -> None:
        self._executor: AgentExecutor = executor if executor is not None else self._built_in_executor
        self._history: List[AgentTurn] = []
        self._lock = threading.Lock()
        self._seq = 0

    @property
    def backend_id(self) -> str:
        return "in-memory"

    async def run_turn_async(
        self, user_prompt: str, ct: Optional[object] = None
    ) -> AgentTurn:
        if user_prompt is None:
            raise ValueError("userPrompt")
        t = await self._executor(user_prompt, ct)
        if t.turn_id is None or t.turn_id == "":
            with self._lock:
                self._seq += 1
                seq = self._seq
            turn = replace(t, turn_id=f"turn-{seq}")
        else:
            turn = t
        with self._lock:
            self._history.append(turn)
        return turn

    async def history_async(
        self, limit: int = 50, ct: Optional[object] = None
    ) -> List[AgentTurn]:
        if limit <= 0:
            raise ValueError("limit")
        with self._lock:
            # Reverse().Take(limit).Reverse() -> the last `limit`, oldest-first.
            return list(self._history[-limit:])

    @staticmethod
    async def _built_in_executor(prompt: str, ct: Optional[object]) -> AgentTurn:
        trimmed = prompt.strip()
        if trimmed[:5].lower() == "read ":
            response = f"Reading {trimmed[5:]} ..."
        elif trimmed[:6].lower() == "write ":
            response = f"Writing {trimmed[6:]} ..."
        elif "?" in trimmed:
            response = "Acknowledged the question; need more context to give a useful answer."
        else:
            response = f"Acknowledged: {trimmed}."
        return AgentTurn(turn_id="", user_prompt=prompt, response=response, edits=[])


class PatternMatchPatchPlanner(IPatchPlanner):
    """Pattern-matching :class:`IPatchPlanner`. Mirrors
    ``CircleAI.DevTools.PatternMatchPatchPlanner``."""

    def __init__(self, editor: ICodeEditor) -> None:
        if editor is None:
            raise ValueError("editor")
        self._editor = editor

    @property
    def backend_id(self) -> str:
        return "pattern-match"

    async def plan_async(self, goal: str, ct: Optional[object] = None) -> PatchPlan:
        if goal is None or goal.strip() == "":
            raise ValueError("goal required")
        rename = _RENAME_RX.match(goal)
        if rename is not None:
            old_name = rename.group(1)
            new_name = rename.group(2)
            scope = rename.group(3) if rename.group(3) is not None else os.getcwd()
            edits = await self._compute_rename_edits(scope, old_name, new_name, ct)
            return PatchPlan(
                goal,
                [f"Rename '{old_name}' -> '{new_name}' across {len(edits)} location(s)"],
                edits,
            )
        remove = _REMOVE_RX.match(goal)
        if remove is not None:
            line_no = int(remove.group(1))
            path = remove.group(2).strip()
            edits = await self._compute_remove_line_edits(path, line_no, ct)
            return PatchPlan(goal, [f"Remove line {line_no} from {path}"], edits)
        append = _APPEND_RX.match(goal)
        if append is not None:
            text = append.group(1).strip().strip('"')
            path = append.group(2).strip()
            length = len(_read_text(path)) if os.path.isfile(path) else 0
            edits = [FileEdit(path, length, length, text)]
            return PatchPlan(goal, [f"Append to {path}"], edits)
        return PatchPlan(goal, ["no recognised intent"], [])

    async def apply_async(self, plan: PatchPlan, ct: Optional[object] = None) -> None:
        if plan is None:
            raise ValueError("plan")
        await self._editor.apply_async(plan.proposed_edits, ct)

    @staticmethod
    async def _compute_rename_edits(
        scope: str, old_name: str, new_name: str, ct: Optional[object]
    ) -> List[FileEdit]:
        if not os.path.isdir(scope) and not os.path.isfile(scope):
            raise NotADirectoryError(scope)
        if os.path.isfile(scope):
            files = [scope]
        else:
            files = []
            for dirpath, _dn, filenames in os.walk(scope):
                for fn in filenames:
                    if fn.endswith(".cs"):
                        files.append(os.path.join(dirpath, fn))
        sep = os.sep
        edits: List[FileEdit] = []
        rx = re.compile(r"\b" + re.escape(old_name) + r"\b")
        for f in files:
            if f"{sep}obj{sep}" in f:
                continue
            if f"{sep}bin{sep}" in f:
                continue
            text = _read_text(f)
            for m in rx.finditer(text):
                edits.append(FileEdit(f, m.start(), m.start() + len(m.group(0)), new_name))
        return edits

    @staticmethod
    async def _compute_remove_line_edits(
        path: str, line_no: int, ct: Optional[object]
    ) -> List[FileEdit]:
        if not os.path.isfile(path):
            raise FileNotFoundError(path)
        text = _read_text(path)
        current = 1
        for i in range(len(text)):
            if current == line_no:
                offset = i
                end = text.find("\n", i)
                range_end = len(text) if end < 0 else end + 1
                return [FileEdit(path, offset, range_end, "")]
            if text[i] == "\n":
                current += 1
        return []


class RegexRefactorTool(IRefactorTool):
    """Regex :class:`IRefactorTool` (Rename + ExtractConstant). Mirrors
    ``CircleAI.DevTools.RegexRefactorTool``."""

    @property
    def backend_id(self) -> str:
        return "regex"

    async def propose_async(
        self, request: RefactorRequest, ct: Optional[object] = None
    ) -> List[FileEdit]:
        if request is None:
            raise ValueError("request")
        if request.target_paths is None:
            raise ValueError("request.TargetPaths")
        description = (request.description or "").strip()
        if description[:7].lower() == "rename ":
            m = re.match(r"^rename\s+(\S+)\s+to\s+(\S+)", description, re.IGNORECASE)
            if m is None:
                return []
            return await self._rename_in_files(request.target_paths, m.group(1), m.group(2), ct)
        if description[:8].lower() == "extract ":
            m = re.match(
                r'^extract\s+constant\s+from\s+"([^"]+)"\s+as\s+(\S+)',
                description,
                re.IGNORECASE,
            )
            if m is None:
                return []
            return await self._extract_constant(request.target_paths, m.group(1), m.group(2), ct)
        return []

    @staticmethod
    async def _rename_in_files(
        paths: Sequence[str], old_name: str, new_name: str, ct: Optional[object]
    ) -> List[FileEdit]:
        edits: List[FileEdit] = []
        rx = re.compile(r"\b" + re.escape(old_name) + r"\b")
        for p in paths:
            if not os.path.isfile(p):
                continue
            text = _read_text(p)
            for m in rx.finditer(text):
                edits.append(FileEdit(p, m.start(), m.start() + len(m.group(0)), new_name))
        return edits

    @staticmethod
    async def _extract_constant(
        paths: Sequence[str], literal: str, constant_name: str, ct: Optional[object]
    ) -> List[FileEdit]:
        edits: List[FileEdit] = []
        quoted = '"' + literal + '"'
        for p in paths:
            if not os.path.isfile(p):
                continue
            text = _read_text(p)
            first = text.find(quoted)
            if first < 0:
                continue
            class_idx = text.find("class ")
            if class_idx < 0:
                continue
            brace = text.find("{", class_idx)
            if brace < 0:
                continue
            insertion = f"\n    private const string {constant_name} = {quoted};\n"
            edits.append(FileEdit(p, brace + 1, brace + 1, insertion))
            idx = first
            while idx >= 0:
                edits.append(FileEdit(p, idx, idx + len(quoted), constant_name))
                idx = text.find(quoted, idx + 1)
        return edits
