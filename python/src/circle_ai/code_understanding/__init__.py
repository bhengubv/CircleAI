"""circle_ai.code_understanding — port of the CircleAI.CodeUnderstanding assembly.

(3.0.0 contracts / 3.3.0 in-memory) Code-understanding surface: a filesystem
symbol indexer (regex declaration extraction across .cs/.ts/.js/.py/.go), an
index-backed substring search, a host-populated symbol call graph, and fail-safe
null defaults. C# is the exact spec.
"""
from __future__ import annotations

from .contracts import (
    CodeMatch,
    CodeSymbol,
    ICodeIndexer,
    ICodeSearch,
    ISymbolGraph,
    SymbolEdge,
)
from .in_memory_code_understanding import (
    FilesystemCodeIndexer,
    IndexBackedCodeSearch,
    InMemorySymbolGraph,
)
from .null_implementations import (
    NullCodeIndexer,
    NullCodeSearch,
    NullSymbolGraph,
)

__all__ = [
    "CodeSymbol",
    "CodeMatch",
    "SymbolEdge",
    "ICodeIndexer",
    "ICodeSearch",
    "ISymbolGraph",
    "FilesystemCodeIndexer",
    "IndexBackedCodeSearch",
    "InMemorySymbolGraph",
    "NullCodeIndexer",
    "NullCodeSearch",
    "NullSymbolGraph",
]
