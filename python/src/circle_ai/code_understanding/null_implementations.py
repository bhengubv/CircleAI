# null_implementations.py
#
# Port of CircleAI.CodeUnderstanding NullImplementations.cs (C# — the EXACT
# spec).
#
# (3.0.0) Fail-safe defaults. Each exposes a singleton `INSTANCE` mirroring the
# C# `static readonly ... Instance`.

from __future__ import annotations

from typing import List, Optional

from .contracts import CodeMatch, CodeSymbol, ICodeIndexer, ICodeSearch, ISymbolGraph, SymbolEdge


class NullCodeIndexer(ICodeIndexer):
    INSTANCE: "NullCodeIndexer"

    @property
    def backend_id(self) -> str:
        return "null"

    async def index_async(
        self, repo_path: str, ct: Optional[object] = None
    ) -> None:
        return None

    async def count_symbols_async(
        self, repo_path: str, ct: Optional[object] = None
    ) -> int:
        return 0


class NullCodeSearch(ICodeSearch):
    INSTANCE: "NullCodeSearch"

    @property
    def backend_id(self) -> str:
        return "null"

    async def search_async(
        self, query: str, top_k: int = 10, ct: Optional[object] = None
    ) -> List[CodeMatch]:
        return []

    async def semantic_search_async(
        self, query: str, top_k: int = 10, ct: Optional[object] = None
    ) -> List[CodeMatch]:
        return []


class NullSymbolGraph(ISymbolGraph):
    INSTANCE: "NullSymbolGraph"

    @property
    def backend_id(self) -> str:
        return "null"

    async def callers_of_async(
        self, s: CodeSymbol, ct: Optional[object] = None
    ) -> List[SymbolEdge]:
        return []

    async def callees_of_async(
        self, s: CodeSymbol, ct: Optional[object] = None
    ) -> List[SymbolEdge]:
        return []


NullCodeIndexer.INSTANCE = NullCodeIndexer()
NullCodeSearch.INSTANCE = NullCodeSearch()
NullSymbolGraph.INSTANCE = NullSymbolGraph()
