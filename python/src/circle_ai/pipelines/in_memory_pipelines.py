# in_memory_pipelines.py
#
# Port of CircleAI.Pipelines InMemoryPipelines.cs (C# — the EXACT spec).
#
# (3.3.0) Real in-memory pipeline source/sink/executor and an in-memory
# database-query tool that operates on a dictionary of in-memory tables.
#
# The C# source is an unbounded System.Threading.Channels.Channel per stream,
# read via WaitToReadAsync/TryRead. We model each stream with an asyncio.Queue
# plus a "completed" flag and a wake-up event, so read_async drains everything
# currently buffered, then waits for more (or exits once the stream is
# completed and drained) — matching the C# semantics. Buffering is unbounded
# (fan-out safe). The executor wires registered pipelines (a coroutine that
# returns a row count) and tracks runs; run failures are captured as the
# FailureReason rather than propagated.

from __future__ import annotations

import asyncio
import threading
from datetime import datetime, timezone
from typing import Any, AsyncIterator, Awaitable, Callable, Dict, List, Optional

from .contracts import (
    DatabaseQueryResult,
    IDatabaseQueryTool,
    IPipelineExecutor,
    IPipelineSink,
    IPipelineSource,
    PipelineRecord,
    PipelineRun,
)

# C# Func<CancellationToken, Task<long>> pipeline runner.
PipelineRunner = Callable[[Optional[object]], Awaitable[int]]


def _require(value: str, name: str) -> None:
    if value is None or value.strip() == "":
        raise ValueError(f"{name} required")


class _Stream:
    """One in-memory stream: an unbounded FIFO buffer with a completion flag and
    a condition to wake waiting readers."""

    def __init__(self) -> None:
        self._buffer: List[PipelineRecord] = []
        self._completed = False
        self._lock = threading.Lock()
        # asyncio.Event, created lazily against the running loop on first wait.
        self._waiters: List[asyncio.Future] = []

    def push(self, record: PipelineRecord) -> None:
        with self._lock:
            if self._completed:
                # TryWrite on a completed channel is a no-op (returns false).
                return
            self._buffer.append(record)
            self._wake_locked()

    def complete(self) -> None:
        with self._lock:
            self._completed = True
            self._wake_locked()

    def _wake_locked(self) -> None:
        for fut in self._waiters:
            if not fut.done():
                fut.set_result(None)
        self._waiters.clear()

    def drain(self) -> List[PipelineRecord]:
        with self._lock:
            out = self._buffer
            self._buffer = []
            return out

    async def wait_for_change(self) -> bool:
        """Block until more records are available or the stream completes.
        Returns True if the reader should keep going, False if the stream is
        completed and fully drained."""
        loop = asyncio.get_running_loop()
        with self._lock:
            if self._buffer:
                return True
            if self._completed:
                return False
            fut = loop.create_future()
            self._waiters.append(fut)
        await fut
        with self._lock:
            if self._buffer:
                return True
            return not self._completed


class InMemoryPipelineSource(IPipelineSource):
    def __init__(self) -> None:
        self._streams: Dict[str, _Stream] = {}
        self._lock = threading.Lock()

    @property
    def backend_id(self) -> str:
        return "in-memory"

    def _get_or_add(self, stream: str) -> _Stream:
        with self._lock:
            s = self._streams.get(stream)
            if s is None:
                s = _Stream()
                self._streams[stream] = s
            return s

    def push(self, stream: str, record: PipelineRecord) -> None:
        _require(stream, "stream")
        if record is None:
            raise ValueError("record must not be None")
        self._get_or_add(stream).push(record)

    def complete(self, stream: str) -> None:
        with self._lock:
            s = self._streams.get(stream)
        if s is not None:
            s.complete()

    async def read_async(self, stream: str, ct: Optional[object] = None) -> AsyncIterator[PipelineRecord]:
        _require(stream, "stream")
        s = self._get_or_add(stream)
        while True:
            for record in s.drain():
                yield record
            keep_going = await s.wait_for_change()
            if not keep_going:
                # Drain any final records that arrived alongside completion.
                for record in s.drain():
                    yield record
                return


class InMemoryPipelineSink(IPipelineSink):
    def __init__(self) -> None:
        self._records: List[PipelineRecord] = []
        self._lock = threading.Lock()

    @property
    def backend_id(self) -> str:
        return "in-memory"

    async def write_async(self, record: PipelineRecord, ct: Optional[object] = None) -> None:
        if record is None:
            raise ValueError("record must not be None")
        with self._lock:
            self._records.append(record)

    async def flush_async(self, ct: Optional[object] = None) -> None:
        return None

    @property
    def records(self) -> List[PipelineRecord]:
        with self._lock:
            return list(self._records)


class InMemoryPipelineExecutor(IPipelineExecutor):
    def __init__(self) -> None:
        self._pipelines: Dict[str, PipelineRunner] = {}
        self._runs: Dict[str, PipelineRun] = {}
        self._run_seq = 0
        self._lock = threading.Lock()

    @property
    def backend_id(self) -> str:
        return "in-memory"

    def register(self, pipeline_id: str, runner: PipelineRunner) -> None:
        _require(pipeline_id, "pipelineId")
        if runner is None:
            raise ValueError("runner must not be None")
        with self._lock:
            self._pipelines[pipeline_id] = runner

    async def run_async(self, pipeline_id: str, ct: Optional[object] = None) -> PipelineRun:
        _require(pipeline_id, "pipelineId")
        with self._lock:
            runner = self._pipelines.get(pipeline_id)
            if runner is None:
                raise RuntimeError(f"Unknown pipeline '{pipeline_id}'.")
            self._run_seq += 1
            run_id = f"run-{self._run_seq}"
        start = datetime.now(timezone.utc)
        rows = 0
        err: Optional[str] = None
        try:
            rows = await runner(ct)
        except Exception as ex:  # noqa: BLE001 — mirror C# catch (Exception ex)
            err = str(ex)
        run = PipelineRun(run_id, pipeline_id, start, datetime.now(timezone.utc), rows, err)
        with self._lock:
            self._runs[run_id] = run
        return run

    async def get_run_async(self, run_id: str, ct: Optional[object] = None) -> Optional[PipelineRun]:
        _require(run_id, "runId")
        with self._lock:
            return self._runs.get(run_id)


class InMemoryDatabaseQueryTool(IDatabaseQueryTool):
    """(3.3.0) Tiny in-memory database — supports simple SELECTs against
    registered tables. Table names are matched case-insensitively (the C#
    dictionary uses StringComparer.OrdinalIgnoreCase)."""

    def __init__(self) -> None:
        # Keys stored casefolded to emulate the OrdinalIgnoreCase dictionary.
        self._tables: Dict[str, List[Dict[str, Any]]] = {}
        self._lock = threading.Lock()

    @property
    def backend_id(self) -> str:
        return "in-memory"

    def insert(self, table_name: str, row: Dict[str, Any]) -> None:
        _require(table_name, "tableName")
        if row is None:
            raise ValueError("row must not be None")
        key = table_name.casefold()
        with self._lock:
            self._tables.setdefault(key, []).append(dict(row))

    async def query_async(
        self, sql: str, parameters: Optional[Dict[str, Any]] = None, ct: Optional[object] = None
    ) -> DatabaseQueryResult:
        _require(sql, "sql")
        trimmed = sql.strip()
        # C#: !trimmed.StartsWith("SELECT ", OrdinalIgnoreCase) — case-insensitive.
        if not trimmed[:7].upper() == "SELECT ":
            raise NotImplementedError("Only SELECT queries are supported by InMemoryDatabaseQueryTool.")

        # "SELECT * FROM <table>" — extremely simple parser (sufficient for
        # in-memory use). Locate "FROM " case-insensitively.
        upper = trimmed.upper()
        from_idx = upper.find("FROM ")
        if from_idx < 0:
            raise RuntimeError("SELECT requires a FROM clause.")
        rest = trimmed[from_idx + 5:].strip()
        # IndexOfAny(new[] { ' ', ';' }) — first whitespace or semicolon.
        space_idx = -1
        for i, ch in enumerate(rest):
            if ch == " " or ch == ";":
                space_idx = i
                break
        table_name = rest[:space_idx] if space_idx > 0 else rest

        key = table_name.casefold()
        with self._lock:
            lst = self._tables.get(key)
            if lst is None:
                return DatabaseQueryResult([], 0)
            rows = [dict(r) for r in lst]
        return DatabaseQueryResult(rows, len(rows))
