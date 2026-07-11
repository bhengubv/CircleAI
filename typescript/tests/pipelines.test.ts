// pipelines.test.ts
// Verifies the CircleAI.Pipelines port: the in-memory streaming source, the sink,
// the run-tracking executor (including failure capture), the SELECT-only in-memory
// database query tool, and the Null* defaults.

import { describe, it } from "node:test";
import assert from "node:assert/strict";
import {
  InMemoryPipelineSource,
  InMemoryPipelineSink,
  InMemoryPipelineExecutor,
  InMemoryDatabaseQueryTool,
  NullPipelineExecutor,
  NullDatabaseQueryTool,
  pipelineRecord,
  type PipelineRecord,
} from "../src/pipelines/index";

describe("InMemoryPipelineSource", () => {
  it("reads pushed records and stops once completed", async () => {
    const src = new InMemoryPipelineSource();
    src.push("s", pipelineRecord("s", new Map([["a", 1]])));
    src.push("s", pipelineRecord("s", new Map([["a", 2]])));
    src.complete("s");
    const got: PipelineRecord[] = [];
    for await (const r of src.readAsync("s")) got.push(r);
    assert.equal(got.length, 2);
    assert.equal(got[0].values.get("a"), 1);
  });

  it("delivers records pushed after readAsync starts", async () => {
    const src = new InMemoryPipelineSource();
    const collected: PipelineRecord[] = [];
    const consumer = (async () => {
      for await (const r of src.readAsync("late")) collected.push(r);
    })();
    // Push after the consumer has subscribed.
    await new Promise((r) => setTimeout(r, 5));
    src.push("late", pipelineRecord("late", new Map([["x", "y"]])));
    await new Promise((r) => setTimeout(r, 5));
    src.complete("late");
    await consumer;
    assert.equal(collected.length, 1);
  });
});

describe("InMemoryPipelineSink", () => {
  it("records every written record", async () => {
    const sink = new InMemoryPipelineSink();
    await sink.writeAsync(pipelineRecord("s", new Map()));
    await sink.flushAsync();
    assert.equal(sink.records.length, 1);
  });
});

describe("InMemoryPipelineExecutor", () => {
  it("runs a registered pipeline and records rows", async () => {
    const exec = new InMemoryPipelineExecutor();
    exec.register("p", async () => 42);
    const run = await exec.runAsync("p");
    assert.equal(run.rowsProcessed, 42);
    assert.equal(run.failureReason, null);
    assert.ok(run.endUtc instanceof Date);
    assert.equal((await exec.getRunAsync(run.runId))?.runId, run.runId);
  });

  it("captures a pipeline failure as failureReason", async () => {
    const exec = new InMemoryPipelineExecutor();
    exec.register("boom", async () => {
      throw new Error("explode");
    });
    const run = await exec.runAsync("boom");
    assert.equal(run.rowsProcessed, 0);
    assert.equal(run.failureReason, "explode");
  });

  it("throws on an unknown pipeline", async () => {
    await assert.rejects(() => new InMemoryPipelineExecutor().runAsync("nope"));
  });
});

describe("InMemoryDatabaseQueryTool", () => {
  it("returns rows for SELECT * FROM <table> (case-insensitive)", async () => {
    const db = new InMemoryDatabaseQueryTool();
    db.insert("Users", new Map<string, unknown>([["id", 1], ["name", "ada"]]));
    db.insert("users", new Map<string, unknown>([["id", 2], ["name", "bob"]]));
    const res = await db.queryAsync("SELECT * FROM users");
    assert.equal(res.rowCount, 2);
    assert.equal(res.rows[0].get("name"), "ada");
  });

  it("returns empty for an unknown table", async () => {
    const res = await new InMemoryDatabaseQueryTool().queryAsync("SELECT * FROM missing");
    assert.equal(res.rowCount, 0);
  });

  it("rejects non-SELECT SQL", async () => {
    const db = new InMemoryDatabaseQueryTool();
    await assert.rejects(() => db.queryAsync("DELETE FROM users"));
  });
});

describe("Null pipeline defaults", () => {
  it("NullPipelineExecutor returns a failed run and null lookups", async () => {
    const run = await NullPipelineExecutor.instance.runAsync("p");
    assert.equal(run.failureReason, "NullPipelineExecutor");
    assert.equal(await NullPipelineExecutor.instance.getRunAsync("x"), null);
  });

  it("NullDatabaseQueryTool returns empty results", async () => {
    const res = await NullDatabaseQueryTool.instance.queryAsync("SELECT * FROM x");
    assert.equal(res.rowCount, 0);
  });
});
