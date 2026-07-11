// PipelinesTests.swift
//
// Exercises the Pipelines port: the channel-backed source (push before/after
// subscribe + complete), the collecting sink, the executor run state machine
// (success row count + captured failure + unknown pipeline), and the tiny
// SELECT parser (case-insensitive table, SELECT-only + FROM-required guards,
// missing table → empty). Mirrors CircleAI.Pipelines/*.

import XCTest
import Foundation
@testable import CircleAI

final class PipelinesTests: XCTestCase {

    private func rec(_ stream: String, _ id: Int) -> PipelineRecord {
        PipelineRecord(stream: stream, values: ["id": AnyCodable(id)])
    }

    // ── Source ────────────────────────────────────────────────────────────────

    func testSourceDeliversBufferedThenLiveRecords() async {
        let src = InMemoryPipelineSource()
        XCTAssertEqual(src.backendId, "in-memory")
        // Push before any consumer subscribes — must be buffered.
        src.push("s", rec("s", 1))
        var received: [Int] = []
        let stream = src.read("s")
        // Push a live record + complete.
        src.push("s", rec("s", 2))
        src.complete("s")
        for await r in stream { received.append(r.intValue("id") ?? -1) }
        XCTAssertEqual(received, [1, 2])
    }

    func testSourcePushAfterCompleteIsIgnored() async {
        let src = InMemoryPipelineSource()
        src.push("s", rec("s", 1))
        src.complete("s")
        src.push("s", rec("s", 2))  // ignored
        var received: [Int] = []
        for await r in src.read("s") { received.append(r.intValue("id") ?? -1) }
        XCTAssertEqual(received, [1])
    }

    func testReadOfCompletedEmptyStreamFinishesImmediately() async {
        let src = InMemoryPipelineSource()
        src.complete("empty")
        var count = 0
        for await _ in src.read("empty") { count += 1 }
        XCTAssertEqual(count, 0)
    }

    // ── Sink ──────────────────────────────────────────────────────────────────

    func testSinkCollectsRecords() async {
        let sink = InMemoryPipelineSink()
        await sink.write(rec("s", 1))
        await sink.write(rec("s", 2))
        await sink.flush()
        XCTAssertEqual(sink.allRecords.map { $0.intValue("id") }, [1, 2])
    }

    // ── Executor ──────────────────────────────────────────────────────────────

    func testExecutorRunsRegisteredPipeline() async {
        let ex = InMemoryPipelineExecutor()
        ex.register("copy") { 42 }
        let run = await ex.run("copy")
        XCTAssertEqual(run.rowsProcessed, 42)
        XCTAssertNil(run.failureReason)
        XCTAssertEqual(run.runId, "run-1")
        let fetched = await ex.getRun("run-1")
        XCTAssertEqual(fetched?.rowsProcessed, 42)
    }

    func testExecutorCapturesThrownError() async {
        let ex = InMemoryPipelineExecutor()
        ex.register("boom") { throw PipelineError.selectRequiresFrom }
        let run = await ex.run("boom")
        XCTAssertEqual(run.rowsProcessed, 0)
        XCTAssertNotNil(run.failureReason)
    }

    func testExecutorUnknownPipelineFailsRun() async {
        let ex = InMemoryPipelineExecutor()
        let run = await ex.run("ghost")
        XCTAssertEqual(run.failureReason, "Unknown pipeline 'ghost'.")
    }

    // ── DB query tool ──────────────────────────────────────────────────────────

    func testQuerySelectStar() async throws {
        let db = InMemoryDatabaseQueryTool()
        db.insert("Users", row: ["id": AnyCodable(1), "name": AnyCodable("Ada")])
        db.insert("users", row: ["id": AnyCodable(2), "name": AnyCodable("Alan")])  // case-insensitive same table
        let result = try await db.query("SELECT * FROM users")
        XCTAssertEqual(result.rowCount, 2)
        let ids = result.rows.compactMap { $0["id"]?.value as? Int }.sorted()
        XCTAssertEqual(ids, [1, 2])
    }

    func testQueryTrailingSemicolonAndSpacing() async throws {
        let db = InMemoryDatabaseQueryTool()
        db.insert("t", row: ["x": AnyCodable(9)])
        let a = try await db.query("SELECT * FROM t;")
        XCTAssertEqual(a.rowCount, 1)
        let b = try await db.query("select * from T where 1=1")
        XCTAssertEqual(b.rowCount, 1)
    }

    func testQueryUnknownTableReturnsEmpty() async throws {
        let db = InMemoryDatabaseQueryTool()
        let result = try await db.query("SELECT * FROM missing")
        XCTAssertEqual(result.rowCount, 0)
        XCTAssertTrue(result.rows.isEmpty)
    }

    func testQueryRejectsNonSelect() async {
        let db = InMemoryDatabaseQueryTool()
        do { _ = try await db.query("DELETE FROM t"); XCTFail("expected throw") }
        catch let e as PipelineError { XCTAssertEqual(e, .onlySelectSupported) }
        catch { XCTFail("wrong error \(error)") }
    }

    func testQueryRequiresFrom() async {
        let db = InMemoryDatabaseQueryTool()
        do { _ = try await db.query("SELECT 1"); XCTFail("expected throw") }
        catch let e as PipelineError { XCTAssertEqual(e, .selectRequiresFrom) }
        catch { XCTFail("wrong error \(error)") }
    }

    // ── Null ──────────────────────────────────────────────────────────────────

    func testNullBackends() async throws {
        var streamCount = 0
        for await _ in NullPipelineSource.instance.read("x") { streamCount += 1 }
        XCTAssertEqual(streamCount, 0)

        await NullPipelineSink.instance.write(rec("x", 1))  // no crash
        let run = await NullPipelineExecutor.instance.run("p")
        XCTAssertEqual(run.runId, "00000000-0000-0000-0000-000000000000")
        XCTAssertEqual(run.failureReason, "NullPipelineExecutor")
        let q = try await NullDatabaseQueryTool.instance.query("SELECT * FROM t")
        XCTAssertEqual(q.rowCount, 0)
    }
}
