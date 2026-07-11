# contracts.py
#
# Port of CircleAI.CodeUnderstanding Contracts.cs (C# — the EXACT spec).
#
# (3.0.0) Code-understanding contracts: symbol / match / edge records and the
# indexer / search / symbol-graph interfaces.
#
# C# ValueTask/ValueTask<T> -> async def -> None/T. C# records -> frozen slotted
# dataclasses. float -> float.

from __future__ import annotations

from abc import ABC, abstractmethod
from dataclasses import dataclass
from typing import List, Optional


@dataclass(frozen=True, slots=True)
class CodeSymbol:
    """Mirrors ``CircleAI.CodeUnderstanding.CodeSymbol`` — ``record(string Path,
    int Line, string Name, string Kind)``.
    """

    path: str
    line: int
    name: str
    kind: str


@dataclass(frozen=True, slots=True)
class CodeMatch:
    """Mirrors ``CircleAI.CodeUnderstanding.CodeMatch`` — ``record(string Path,
    int Line, string Snippet, float Score)``.
    """

    path: str
    line: int
    snippet: str
    score: float


@dataclass(frozen=True, slots=True)
class SymbolEdge:
    """Mirrors ``CircleAI.CodeUnderstanding.SymbolEdge`` — ``record(CodeSymbol
    From, CodeSymbol To, string Kind)``.
    """

    from_symbol: CodeSymbol
    to_symbol: CodeSymbol
    kind: str


class ICodeIndexer(ABC):
    """(3.0.0) Code indexer."""

    @property
    @abstractmethod
    def backend_id(self) -> str:
        ...

    @abstractmethod
    async def index_async(
        self, repo_path: str, ct: Optional[object] = None
    ) -> None:
        ...

    @abstractmethod
    async def count_symbols_async(
        self, repo_path: str, ct: Optional[object] = None
    ) -> int:
        ...


class ICodeSearch(ABC):
    """(3.0.0) Code search."""

    @property
    @abstractmethod
    def backend_id(self) -> str:
        ...

    @abstractmethod
    async def search_async(
        self, query: str, top_k: int = 10, ct: Optional[object] = None
    ) -> List[CodeMatch]:
        ...

    @abstractmethod
    async def semantic_search_async(
        self, query: str, top_k: int = 10, ct: Optional[object] = None
    ) -> List[CodeMatch]:
        ...


class ISymbolGraph(ABC):
    """(3.0.0) Symbol call graph."""

    @property
    @abstractmethod
    def backend_id(self) -> str:
        ...

    @abstractmethod
    async def callers_of_async(
        self, s: CodeSymbol, ct: Optional[object] = None
    ) -> List[SymbolEdge]:
        ...

    @abstractmethod
    async def callees_of_async(
        self, s: CodeSymbol, ct: Optional[object] = None
    ) -> List[SymbolEdge]:
        ...
