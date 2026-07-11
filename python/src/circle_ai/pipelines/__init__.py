"""circle_ai.pipelines — port of the CircleAI.Pipelines assembly.

(2.8.0 contracts / 3.3.0 in-memory impl) Data-pipeline domain: records, runs,
streaming source/sink, an executor that runs registered pipelines and tracks
runs, and a tiny in-memory SELECT-only database-query tool, plus fail-closed
null defaults. C# is the exact spec.

Public surface:

  * PipelineRecord / PipelineRun / DatabaseQueryResult    — domain records.
  * IPipelineSource / IPipelineSink / IPipelineExecutor / IDatabaseQueryTool.
  * InMemoryPipelineSource / InMemoryPipelineSink / InMemoryPipelineExecutor /
    InMemoryDatabaseQueryTool.
  * NullPipelineSource / NullPipelineSink / NullPipelineExecutor /
    NullDatabaseQueryTool                                 — fail-closed defaults.
"""
from __future__ import annotations

from .contracts import (
    DatabaseQueryResult,
    IDatabaseQueryTool,
    IPipelineExecutor,
    IPipelineSink,
    IPipelineSource,
    PipelineRecord,
    PipelineRun,
)
from .in_memory_pipelines import (
    InMemoryDatabaseQueryTool,
    InMemoryPipelineExecutor,
    InMemoryPipelineSink,
    InMemoryPipelineSource,
    PipelineRunner,
)
from .null_implementations import (
    NullDatabaseQueryTool,
    NullPipelineExecutor,
    NullPipelineSink,
    NullPipelineSource,
)

__all__ = [
    "PipelineRecord",
    "PipelineRun",
    "DatabaseQueryResult",
    "PipelineRunner",
    "IPipelineSource",
    "IPipelineSink",
    "IPipelineExecutor",
    "IDatabaseQueryTool",
    "InMemoryPipelineSource",
    "InMemoryPipelineSink",
    "InMemoryPipelineExecutor",
    "InMemoryDatabaseQueryTool",
    "NullPipelineSource",
    "NullPipelineSink",
    "NullPipelineExecutor",
    "NullDatabaseQueryTool",
]
