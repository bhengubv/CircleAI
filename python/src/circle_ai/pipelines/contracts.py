# contracts.py
#
# Port of CircleAI.Pipelines Contracts.cs (C# — the EXACT spec).
#
# (2.8.0) Data-pipeline contracts: records, runs, source/sink/executor, and an
# in-memory database-query tool. C# records map to frozen slotted dataclasses;
# IReadOnlyDictionary<string, object?> maps to Dict[str, Any];
# IAsyncEnumerable<T> maps to an async generator (AsyncIterator[T]).

from __future__ import annotations

from abc import ABC, abstractmethod
from dataclasses import dataclass
from datetime import datetime
from typing import Any, AsyncIterator, Dict, List, Optional


@dataclass(frozen=True, slots=True)
class PipelineRecord:
    """Mirrors ``CircleAI.Pipelines.PipelineRecord`` — ``record(string Stream,
    IReadOnlyDictionary<string, object?> Values)``."""

    stream: str
    values: Dict[str, Any]


@dataclass(frozen=True, slots=True)
class PipelineRun:
    """Mirrors ``CircleAI.Pipelines.PipelineRun`` — ``record(string RunId,
    string PipelineId, DateTimeOffset StartUtc, DateTimeOffset? EndUtc,
    long RowsProcessed, string? FailureReason)``."""

    run_id: str
    pipeline_id: str
    start_utc: datetime
    end_utc: Optional[datetime]
    rows_processed: int
    failure_reason: Optional[str]


@dataclass(frozen=True, slots=True)
class DatabaseQueryResult:
    """Mirrors ``CircleAI.Pipelines.DatabaseQueryResult`` —
    ``record(IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows,
    int RowCount)``."""

    rows: List[Dict[str, Any]]
    row_count: int


class IPipelineSource(ABC):
    """(2.8.0) Streaming pipeline source."""

    @property
    @abstractmethod
    def backend_id(self) -> str:
        ...

    @abstractmethod
    def read_async(self, stream: str, ct: Optional[object] = None) -> AsyncIterator[PipelineRecord]:
        """Returns an async iterator over the records of ``stream``."""
        ...


class IPipelineSink(ABC):
    """(2.8.0) Pipeline sink."""

    @property
    @abstractmethod
    def backend_id(self) -> str:
        ...

    @abstractmethod
    async def write_async(self, record: PipelineRecord, ct: Optional[object] = None) -> None:
        ...

    @abstractmethod
    async def flush_async(self, ct: Optional[object] = None) -> None:
        ...


class IPipelineExecutor(ABC):
    """(2.8.0) Pipeline executor + run tracker."""

    @property
    @abstractmethod
    def backend_id(self) -> str:
        ...

    @abstractmethod
    async def run_async(self, pipeline_id: str, ct: Optional[object] = None) -> PipelineRun:
        ...

    @abstractmethod
    async def get_run_async(self, run_id: str, ct: Optional[object] = None) -> Optional[PipelineRun]:
        ...


class IDatabaseQueryTool(ABC):
    """(2.8.0) Read-only database query tool."""

    @property
    @abstractmethod
    def backend_id(self) -> str:
        ...

    @abstractmethod
    async def query_async(
        self, sql: str, parameters: Optional[Dict[str, Any]] = None, ct: Optional[object] = None
    ) -> DatabaseQueryResult:
        ...
