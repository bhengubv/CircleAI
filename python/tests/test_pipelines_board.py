"""test_pipelines_board.py — CircleAI.Pipelines port.

Covers the streaming source (push/complete/read), the sink, the executor
(register + run tracking with failure capture), the tiny SELECT-only
database-query tool, and the fail-closed null defaults. C# is the exact spec.
"""
from __future__ import annotations

import pytest

from circle_ai.pipelines import (
    DatabaseQueryResult,
    IDatabaseQueryTool,
    InMemoryDatabaseQueryTool,
    InMemoryPipelineExecutor,
    InMemoryPipelineSink,
    InMemoryPipelineSource,
    IPipelineExecutor,
    IPipelineSink,
    IPipelineSource,
    NullDatabaseQueryTool,
    NullPipelineExecutor,
    NullPipelineSink,
    NullPipelineSource,
    PipelineRecord,
    PipelineRun,
)


async def test_source_streams_until_complete():
    src = InMemoryPipelineSource()
    assert isinstance(src, IPipelineSource) and src.backend_id == "in-memory"
    src.push("s", PipelineRecord("s", {"a": 1}))
    src.push("s", PipelineRecord("s", {"a": 2}))
    src.complete("s")
    got = [r.values["a"] async for r in src.read_async("s")]
    assert got == [1, 2]


async def test_source_push_guards():
    src = InMemoryPipelineSource()
    with pytest.raises(ValueError):
        src.push(" ", PipelineRecord("x", {}))
    with pytest.raises(ValueError):
        src.push("s", None)  # type: ignore[arg-type]


async def test_sink_collects_records():
    sink = InMemoryPipelineSink()
    assert isinstance(sink, IPipelineSink)
    await sink.write_async(PipelineRecord("s", {"k": "v"}))
    await sink.flush_async()
    assert len(sink.records) == 1 and sink.records[0].values == {"k": "v"}


async def test_executor_runs_and_tracks():
    src = InMemoryPipelineSource()
    sink = InMemoryPipelineSink()
    ex = InMemoryPipelineExecutor()
    assert isinstance(ex, IPipelineExecutor)

    src.push("in", PipelineRecord("in", {"n": 1}))
    src.push("in", PipelineRecord("in", {"n": 2}))
    src.complete("in")

    async def pipe(ct) -> int:
        count = 0
        async for rec in src.read_async("in"):
            await sink.write_async(rec)
            count += 1
        return count

    ex.register("copy", pipe)
    run = await ex.run_async("copy")
    assert isinstance(run, PipelineRun)
    assert run.run_id == "run-1"
    assert run.rows_processed == 2
    assert run.failure_reason is None
    assert len(sink.records) == 2
    assert (await ex.get_run_async("run-1")).rows_processed == 2


async def test_executor_captures_failure():
    ex = InMemoryPipelineExecutor()

    async def boom(ct) -> int:
        raise RuntimeError("kaboom")

    ex.register("bad", boom)
    run = await ex.run_async("bad")
    assert run.failure_reason == "kaboom" and run.rows_processed == 0


async def test_executor_unknown_pipeline_raises():
    ex = InMemoryPipelineExecutor()
    with pytest.raises(RuntimeError):
        await ex.run_async("nope")


async def test_database_query_tool_select():
    db = InMemoryDatabaseQueryTool()
    assert isinstance(db, IDatabaseQueryTool)
    db.insert("Users", {"id": 1, "name": "a"})
    db.insert("users", {"id": 2, "name": "b"})  # same table, case-insensitive
    res = await db.query_async("SELECT * FROM users")
    assert isinstance(res, DatabaseQueryResult)
    assert res.row_count == 2
    assert {r["id"] for r in res.rows} == {1, 2}
    # Trailing clause + semicolon parsing.
    res2 = await db.query_async("select * from Users;")
    assert res2.row_count == 2
    # Unknown table -> empty.
    empty = await db.query_async("SELECT * FROM ghosts")
    assert empty.row_count == 0


async def test_database_query_tool_guards():
    db = InMemoryDatabaseQueryTool()
    with pytest.raises(ValueError):
        await db.query_async("  ")
    with pytest.raises(NotImplementedError):
        await db.query_async("DELETE FROM users")


async def test_null_implementations_fail_closed():
    src = NullPipelineSource.Instance
    sink = NullPipelineSink.Instance
    ex = NullPipelineExecutor.Instance
    db = NullDatabaseQueryTool.Instance
    assert src.backend_id == "null" and sink.backend_id == "null"
    assert [r async for r in src.read_async("x")] == []
    await sink.write_async(PipelineRecord("s", {}))
    await sink.flush_async()
    run = await ex.run_async("p")
    assert run.run_id == "00000000-0000-0000-0000-000000000000"
    assert run.failure_reason == "NullPipelineExecutor"
    assert await ex.get_run_async("x") is None
    assert (await db.query_async("SELECT * FROM t")).row_count == 0
