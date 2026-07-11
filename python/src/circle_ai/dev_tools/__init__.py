"""circle_ai.dev_tools — port of the CircleAI.DevTools assembly.

(3.0.0 contracts / 3.3.0 in-memory) The dev-tools replacement surface: a
filesystem code editor, a token-vocabulary inline suggester, an in-memory agent
shell, a pattern-matching patch planner, a regex refactor tool (rename +
extract-constant), and fail-closed null defaults. C# is the exact spec.
"""
from __future__ import annotations

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
from .in_memory_dev_tools import (
    FilesystemCodeEditor,
    InMemoryAgentShell,
    PatternMatchPatchPlanner,
    RegexRefactorTool,
    TokenContextInlineSuggester,
)
from .null_implementations import (
    NullAgentShell,
    NullCodeEditor,
    NullInlineSuggester,
    NullPatchPlanner,
    NullRefactorTool,
)

__all__ = [
    "FileEdit",
    "InlineSuggestion",
    "AgentTurn",
    "PatchPlan",
    "RefactorRequest",
    "ICodeEditor",
    "IInlineSuggester",
    "IAgentShell",
    "IPatchPlanner",
    "IRefactorTool",
    "FilesystemCodeEditor",
    "TokenContextInlineSuggester",
    "InMemoryAgentShell",
    "PatternMatchPatchPlanner",
    "RegexRefactorTool",
    "NullCodeEditor",
    "NullInlineSuggester",
    "NullAgentShell",
    "NullPatchPlanner",
    "NullRefactorTool",
]
