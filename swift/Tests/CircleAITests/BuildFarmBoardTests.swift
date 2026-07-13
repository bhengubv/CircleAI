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
        let whenBusy = await pool.acquire(.linux)
        XCTAssertNil(whenBusy)
        // Release one → acquire succeeds again.
        await pool.release(a!.agentId)
        let afterRelease = await pool.acquire(.linux)
        XCTAssertNotNil(afterRelease)
    }

    func testAcquireWrongKindReturnsNil() async {
        let pool = InMemoryBuildAgentPool()
        pool.register(BuildAgent(agentId: "linux-1", kind: .linux, os: "ubuntu", hardware: nil))
        let wrongKind = await pool.acquire(.mac)
        XCTAssertNil(wrongKind)
    }

    func testListReturnsAllAgents() async {
        let pool = InMemoryBuildAgentPool()
        pool.register(BuildAgent(agentId: "a", kind: .linux, os: "u", hardware: nil))
        pool.register(BuildAgent(agentId: "b", kind: .windows, os: "w", hardware: "x64"))
        _ = await pool.acquire(.linux)  // busy agents still listed
        let listed = await pool.list()
        XCTAssertEqual(Set(listed.map { $0.agentId }), ["a", "b"])
    }

    // ── Job runner ─────────────────────────────────────────────────────────────

    func testStartCreatesRunningJobThenComplete() async throws {
        let runner = InMemoryBuildJobRunner()
        let job = await runner.start(agentId: "a", repo: "r", branch: "main")
        XCTAssertEqual(job.phase, .running)
        XCTAssertEqual(job.jobId, "job-1")
        try runner.complete(job.jobId, success: true)
        let completed = await runner.get(job.jobId)
        XCTAssertEqual(completed?.phase, .succeeded)
    }

    func testCompleteFailure() async throws {
        let runner = InMemoryBuildJobRunner()
        let job = await runner.start(agentId: "a", repo: "r", branch: "b")
        try runner.complete(job.jobId, success: false)
        let failed = await runner.get(job.jobId)
        XCTAssertEqual(failed?.phase, .failed)
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
        let stored = await store.get("art-1")
        XCTAssertEqual(stored, art)
        let missing = await store.get("missing")
        XCTAssertNil(missing)
    }

    func testArtifactCodableRoundTrip() throws {
        let art = BuildArtifact(artifactId: "a", jobId: "j", name: "n", payload: Data([9]))
        XCTAssertEqual(try JSONDecoder().decode(BuildArtifact.self, from: try JSONEncoder().encode(art)), art)
    }

    // ── Null ──────────────────────────────────────────────────────────────────

    func testNullBackends() async {
        let nullAcquire = await NullBuildAgentPool.instance.acquire(.linux)
        XCTAssertNil(nullAcquire)
        let nullList = await NullBuildAgentPool.instance.list()
        XCTAssertTrue(nullList.isEmpty)
        let job = await NullBuildJobRunner.instance.start(agentId: "a", repo: "r", branch: "b")
        XCTAssertEqual(job.phase, .failed)
        XCTAssertEqual(job.jobId, "00000000-0000-0000-0000-000000000000")
        await NullBuildArtifactStore.instance.save(BuildArtifact(artifactId: "x", jobId: "j", name: "n", payload: Data()))
        let nullArtifact = await NullBuildArtifactStore.instance.get("x")
        XCTAssertNil(nullArtifact)
    }
}
