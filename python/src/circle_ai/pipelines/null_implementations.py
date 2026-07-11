# null_implementations.py
#
# Port of CircleAI.Pipelines NullImplementations.cs (C# — the EXACT spec).
#
# (2.8.0) Fail-closed pipeline defaults. NullPipelineSource yields nothing;
# NullPipelineExecutor returns a Failed run stamped with Guid.Empty (dashed) +
# DateTimeOffset.MinValue; NullDatabaseQueryTool returns an empty result set.

from __future__ import annotations

from datetime import datetime, timezone
from typing import Any, AsyncIterator, Dict, Optional

from .contracts import (
    DatabaseQueryResult,
    IDatabaseQueryTool,
    IPipelineExecutor,
    IPipelineSink,
    IPipelineSource,
    PipelineRecord,
    PipelineRun,
)

_GUID_EMPTY = "00000000-0000-0000-0000-000000000000"
_MIN_UTC = datetime(1, 1, 1, tzinfo=timezone.utc)


class NullPipelineSource(IPipelineSource):
    Instance: "NullPipelineSource"

    @property
    def backend_id(self) -> str:
        return "null"

    async def read_async(self, stream: str, ct: Optional[object] = None) -> AsyncIterator[PipelineRecord]:
        return
        yield  # pragma: no cover — makes this an (empty) async generator


class NullPipelineSink(IPipelineSink):
    Instance: "NullPipelineSink"

    @property
    def backend_id(self) -> str:
        return "null"

    async def write_async(self, r: PipelineRecord, ct: Optional[object] = None) -> None:
        return None

    async def flush_async(self, ct: Optional[object] = None) -> None:
        return None


class NullPipelineExecutor(IPipelineExecutor):
    Instance: "NullPipelineExecutor"

    @property
    def backend_id(self) -> str:
        return "null"

    async def run_async(self, id: str, ct: Optional[object] = None) -> PipelineRun:
        return PipelineRun(_GUID_EMPTY, id, _MIN_UTC, _MIN_UTC, 0, "NullPipelineExecutor")

    async def get_run_async(self, run_id: str, ct: Optional[object] = None) -> Optional[PipelineRun]:
        return None


class NullDatabaseQueryTool(IDatabaseQueryTool):
    Instance: "NullDatabaseQueryTool"

    @property
    def backend_id(self) -> str:
        return "null"

    async def query_async(
        self, sql: str, parameters: Optional[Dict[str, Any]] = None, ct: Optional[object] = None
    ) -> DatabaseQueryResult:
        return DatabaseQueryResult([], 0)


NullPipelineSource.Instance = NullPipelineSource()
NullPipelineSink.Instance = NullPipelineSink()
NullPipelineExecutor.Instance = NullPipelineExecutor()
NullDatabaseQueryTool.Instance = NullDatabaseQueryTool()
