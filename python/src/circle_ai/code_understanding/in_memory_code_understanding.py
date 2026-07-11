# in_memory_code_understanding.py
#
# Port of CircleAI.CodeUnderstanding InMemoryCodeUnderstanding.cs (C# — the
# EXACT spec).
#
# (3.3.0) Real-but-lightweight code indexer + searcher + symbol graph:
#   • FilesystemCodeIndexer — walks the repo, extracts declarations from
#     .cs/.ts/.js/.py/.go via a fast regex pass, skipping obj/bin/node_modules.
#   • IndexBackedCodeSearch — substring match on symbol names -> CodeMatch(score 1.0).
#   • InMemorySymbolGraph — host-populated adjacency list (callers/callees by name).
#
# The C# language regexes use variable-width lookbehind (e.g.
# `(?<=\b(class|interface|record|enum|struct)\s+)(\w+)`). Python's `re` forbids
# variable-width lookbehind, so each pattern is rewritten to a capturing form —
# the keyword is group 1, the identifier is group 2 — which yields the SAME
# extracted names (the C# code reads Groups[2]). The float score at the CodeMatch
# site is 1.0f.

from __future__ import annotations

import os
import re
import struct
import threading
from typing import Dict, List, Optional, Tuple

from .contracts import CodeMatch, CodeSymbol, ICodeIndexer, ICodeSearch, ISymbolGraph, SymbolEdge


def _f32(x: float) -> float:
    return struct.unpack("<f", struct.pack("<f", x))[0]


# (ext, compiled-regex, kind) — identifier is capture group 2 (mirroring the C#
# `m.Groups[2]` read). MULTILINE where the C# uses RegexOptions.Multiline.
_LANGUAGES: List[Tuple[str, "re.Pattern[str]", str]] = [
    (".cs", re.compile(r"\b(class|interface|record|enum|struct)\s+(\w+)"), "csharp"),
    (
        ".cs",
        re.compile(r"\b(public|private|internal|protected|static)\s+\w+\s+(\w+)\s*\("),
        "csharp-method",
    ),
    (".ts", re.compile(r"\b(class|interface|type|enum)\s+(\w+)"), "ts"),
    (".js", re.compile(r"\b(class|function)\s+(\w+)"), "js"),
    (".py", re.compile(r"^\s*(def|class)\s+(\w+)", re.MULTILINE), "python"),
    (".go", re.compile(r"^\s*func\s+(\(\w+\s+\*?\w+\)\s+)?(\w+)", re.MULTILINE), "go"),
]


class FilesystemCodeIndexer(ICodeIndexer):
    """Real filesystem :class:`ICodeIndexer`. Mirrors
    ``CircleAI.CodeUnderstanding.FilesystemCodeIndexer``."""

    def __init__(self) -> None:
        # Public within the package (the search backend reads it), like the C#
        # `internal ... Index`.
        self.index: Dict[str, List[CodeSymbol]] = {}
        self._lock = threading.Lock()

    @property
    def backend_id(self) -> str:
        return "filesystem"

    async def index_async(
        self, repo_path: str, ct: Optional[object] = None
    ) -> None:
        if repo_path is None or repo_path.strip() == "":
            raise ValueError("repoPath required")
        if not os.path.isdir(repo_path):
            raise NotADirectoryError(repo_path)

        symbols: List[CodeSymbol] = []
        for path in self._enumerate_source_files(repo_path):
            try:
                with open(path, "r", encoding="utf-8", errors="replace") as fh:
                    lines = fh.read().split("\n")
            except OSError:
                continue
            ext = os.path.splitext(path)[1].lower()
            for i, line in enumerate(lines):
                for (e, rx, kind) in _LANGUAGES:
                    if e != ext:
                        continue
                    for m in rx.finditer(line):
                        # Group 2 is the identifier in every rewritten pattern.
                        if m.lastindex is not None and m.lastindex >= 2 and m.group(2):
                            symbols.append(CodeSymbol(path, i + 1, m.group(2), kind))
        with self._lock:
            self.index[repo_path] = symbols

    async def count_symbols_async(
        self, repo_path: str, ct: Optional[object] = None
    ) -> int:
        with self._lock:
            got = self.index.get(repo_path)
            return len(got) if got is not None else 0

    @staticmethod
    def _enumerate_source_files(root: str) -> List[str]:
        sep = os.sep
        out: List[str] = []
        for dirpath, _dirnames, filenames in os.walk(root):
            for fn in filenames:
                file = os.path.join(dirpath, fn)
                ext = os.path.splitext(file)[1].lower()
                if ext in (".cs", ".ts", ".js", ".py", ".go"):
                    if f"{sep}obj{sep}" in file:
                        continue
                    if f"{sep}bin{sep}" in file:
                        continue
                    if f"{sep}node_modules{sep}" in file:
                        continue
                    out.append(file)
        return out


class IndexBackedCodeSearch(ICodeSearch):
    """Index-backed :class:`ICodeSearch`. Mirrors
    ``CircleAI.CodeUnderstanding.IndexBackedCodeSearch``."""

    def __init__(self, indexer: FilesystemCodeIndexer) -> None:
        if indexer is None:
            raise ValueError("indexer")
        self._indexer = indexer

    @property
    def backend_id(self) -> str:
        return "index-backed"

    async def search_async(
        self, query: str, top_k: int = 10, ct: Optional[object] = None
    ) -> List[CodeMatch]:
        if query is None:
            raise ValueError("query")
        if top_k <= 0:
            raise ValueError("topK")
        ql = query.lower()
        hits: List[CodeMatch] = []
        with self._indexer._lock:
            all_lists = list(self._indexer.index.values())
        for lst in all_lists:
            for s in lst:
                if ql in s.name.lower():
                    hits.append(CodeMatch(s.path, s.line, f"{s.kind} {s.name}", _f32(1.0)))
                    if len(hits) == top_k:
                        return hits
        return hits[:top_k]

    async def semantic_search_async(
        self, query: str, top_k: int = 10, ct: Optional[object] = None
    ) -> List[CodeMatch]:
        return await self.search_async(query, top_k, ct)  # substring fallback


class InMemorySymbolGraph(ISymbolGraph):
    """Host-populated :class:`ISymbolGraph`. Mirrors
    ``CircleAI.CodeUnderstanding.InMemorySymbolGraph``."""

    def __init__(self) -> None:
        self._edges: List[SymbolEdge] = []
        self._lock = threading.Lock()

    @property
    def backend_id(self) -> str:
        return "in-memory"

    def link(self, from_symbol: CodeSymbol, to_symbol: CodeSymbol, kind: str = "calls") -> None:
        if from_symbol is None:
            raise ValueError("from")
        if to_symbol is None:
            raise ValueError("to")
        with self._lock:
            self._edges.append(SymbolEdge(from_symbol, to_symbol, kind))

    async def callers_of_async(
        self, s: CodeSymbol, ct: Optional[object] = None
    ) -> List[SymbolEdge]:
        with self._lock:
            return [e for e in self._edges if e.to_symbol.name == s.name]

    async def callees_of_async(
        self, s: CodeSymbol, ct: Optional[object] = None
    ) -> List[SymbolEdge]:
        with self._lock:
            return [e for e in self._edges if e.from_symbol.name == s.name]
