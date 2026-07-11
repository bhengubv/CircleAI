// BuildFarmBoardTests.swift
//
// Exercises the BuildFarm port: agent-pool acquire/release/list (busy tracking,
// none-available), job runner start→complete state machine (+ unknown-job
// throw), artifact save/get, and the null backends. Mirrors CircleAI.BuildFarm/*.

import XCTest
import Foundation
@testable import CircleAI

final class BuildFarmBoardTests: XCTestCase {

    // ── Agent pool ─────────────────────────────────────────────────────────────

    func testAcquireMarksBusyAndReleaseFrees() async {
        let pool = InMemoryBuildAgentPool()
        XCTAssertEqual(pool.backendId, "in-memory")
        pool.register(BuildAgent(agentId: "linux-1", kind: .linux, os: "ubuntu", hardware: nil))
        pool.register(BuildAgent(agentId: "linux-2", kind: .linux, os: "ubuntu", hardware: nil))

        let a = await pool.acquire(.linux)
        let b = await pool.acquire(.linux)
        XCTAssertNotNil(a)
        XCTAssertNotNil(b)
        XCTAssertNotEqual(a?.agentId, b?.agentId)
        // Both busy → next acquire returns nil.
        XCTAssertNil(await pool.acquire(.linux))
        // Release one → acquire succeeds again.
        await pool.release(a!.agentId)
        XCTAssertNotNil(await pool.acquire(.linux))
    }

    func testAcquireWrongKindReturnsNil() async {
        let pool = InMemoryBuildAgentPool()
        pool.register(BuildAgent(agentId: "linux-1", kind: .linux, os: "ubuntu", hardware: nil))
        XCTAssertNil(await pool.acquire(.mac))
    }

    func testListReturnsAllAgents() async {
        let pool = InMemoryBuildAgentPool()
        pool.register(BuildAgent(agentId: "a", kind: .linux, os: "u", hardware: nil))
        pool.register(BuildAgent(agentId: "b", kind: .windows, os: "w", hardware: "x64"))
        _ = await pool.acquire(.linux)  // busy agents still listed
        XCTAssertEqual(Set((await pool.list()).map { $0.agentId }), ["a", "b"])
    }

    // ── Job runner ─────────────────────────────────────────────────────────────

    func testStartCreatesRunningJobThenComplete() async throws {
        let runner = InMemoryBuildJobRunner()
        let job = await runner.start(agentId: "a", repo: "r", branch: "main")
        XCTAssertEqual(job.phase, .running)
        XCTAssertEqual(job.jobId, "job-1")
        try runner.complete(job.jobId, success: true)
        XCTAssertEqual(await runner.get(job.jobId)?.phase, .succeeded)
    }

    func testCompleteFailure() async throws {
        let runner = InMemoryBuildJobRunner()
        let job = await runner.start(agentId: "a", repo: "r", branch: "b")
        try runner.complete(job.jobId, success: false)
        XCTAssertEqual(await runner.get(job.jobId)?.phase, .failed)
    }

    func testCompleteUnknownJobThrows() {
        let runner = InMemoryBuildJobRunner()
        XCTAssertThrowsError(try runner.complete("ghost", success: true)) { err in
            XCTAssertEqual(err as? BuildFarmError, .unknownJob("ghost"))
        }
    }

    func testJobIdsAreMonotonic() async {
        let runner = InMemoryBuildJobRunner()
        let j1 = await runner.start(agentId: "a", repo: "r", branch: "b")
        let j2 = await runner.start(agentId: "a", repo: "r", branch: "b")
        XCTAssertEqual([j1.jobId, j2.jobId], ["job-1", "job-2"])
    }

    // ── Artifact store ─────────────────────────────────────────────────────────

    func testArtifactSaveAndGet() async {
        let store = InMemoryBuildArtifactStore()
        let art = BuildArtifact(artifactId: "art-1", jobId: "job-1", name: "out.zip", payload: Data([1, 2, 3]))
        await store.save(art)
        XCTAssertEqual(await store.get("art-1"), art)
        XCTAssertNil(await store.get("missing"))
    }

    func testArtifactCodableRoundTrip() throws {
        let art = BuildArtifact(artifactId: "a", jobId: "j", name: "n", payload: Data([9]))
        XCTAssertEqual(try JSONDecoder().decode(BuildArtifact.self, from: try JSONEncoder().encode(art)), art)
    }

    // ── Null ──────────────────────────────────────────────────────────────────

    func testNullBackends() async {
        XCTAssertNil(await NullBuildAgentPool.instance.acquire(.linux))
        XCTAssertTrue(await NullBuildAgentPool.instance.list().isEmpty)
        let job = await NullBuildJobRunner.instance.start(agentId: "a", repo: "r", branch: "b")
        XCTAssertEqual(job.phase, .failed)
        XCTAssertEqual(job.jobId, "00000000-0000-0000-0000-000000000000")
        await NullBuildArtifactStore.instance.save(BuildArtifact(artifactId: "x", jobId: "j", name: "n", payload: Data()))
        XCTAssertNil(await NullBuildArtifactStore.instance.get("x"))
    }
}
