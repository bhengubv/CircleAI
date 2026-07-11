// SkillsTests.swift
//
// Exercises the Skills port: InMemorySkillStore upsert (explicit id + slug
// auto-gen), list (name-ordered summaries), search (case-insensitive over
// name/description/tags, empty query → empty), delete, GenerateSlug pipeline,
// KnownSkillPacks catalogue, SkillPackSourcesOptions.enumerateEnabled, and the
// in-memory pack downloader. Mirrors the named CircleAI.Skills/* types.

import XCTest
import Foundation
@testable import CircleAI

final class SkillsTests: XCTestCase {

    private func draft(_ name: String, _ desc: String = "d", tags: [String] = []) -> SkillDraft {
        SkillDraft(name: name, description: desc, instructions: "do it", tags: tags)
    }

    // ── DTO ─────────────────────────────────────────────────────────────────

    func testSkillDetailCodableRoundTrip() throws {
        let d = SkillDetail(id: "s", name: "n", description: "d", instructions: "i",
                            tags: ["t"], source: .remote, lastModified: Date(timeIntervalSince1970: 3))
        XCTAssertEqual(try JSONDecoder().decode(SkillDetail.self, from: try JSONEncoder().encode(d)), d)
    }

    // ── Store ─────────────────────────────────────────────────────────────────

    func testUpsertWithExplicitId() async {
        let store = InMemorySkillStore()
        let detail = await store.upsert("my-id", draft: draft("My Skill", "summarise", tags: ["prod"]))
        XCTAssertEqual(detail.id, "my-id")
        XCTAssertEqual(detail.source, .inMemory)
        XCTAssertEqual(await store.get("my-id")?.name, "My Skill")
    }

    func testUpsertAutoGeneratesSlug() async {
        let store = InMemorySkillStore()
        let detail = await store.upsert(nil, draft: draft("Calendar Summariser"))
        XCTAssertEqual(detail.id, "calendar-summariser")
    }

    func testUpsertReplacesById() async {
        let store = InMemorySkillStore()
        _ = await store.upsert("id", draft: draft("First"))
        _ = await store.upsert("id", draft: draft("Second"))
        XCTAssertEqual(await store.get("id")?.name, "Second")
        XCTAssertEqual((await store.list()).count, 1)
    }

    func testListIsNameOrdered() async {
        let store = InMemorySkillStore()
        _ = await store.upsert("z", draft: draft("Zebra"))
        _ = await store.upsert("a", draft: draft("Alpha"))
        XCTAssertEqual((await store.list()).map { $0.name }, ["Alpha", "Zebra"])
    }

    func testSearchMatchesNameDescriptionTags() async {
        let store = InMemorySkillStore()
        _ = await store.upsert("s1", draft: draft("Translator", "converts languages", tags: ["nlp"]))
        _ = await store.upsert("s2", draft: draft("Vision", "sees images", tags: ["cv"]))
        // Match on description.
        XCTAssertEqual((await store.search("languages")).map { $0.id }, ["s1"])
        // Match on tag (case-insensitive).
        XCTAssertEqual((await store.search("NLP")).map { $0.id }, ["s1"])
        // Empty query → empty.
        XCTAssertTrue((await store.search("")).isEmpty)
        XCTAssertTrue((await store.search("   ")).isEmpty)
    }

    func testDelete() async {
        let store = InMemorySkillStore()
        _ = await store.upsert("id", draft: draft("X"))
        await store.delete("id")
        XCTAssertNil(await store.get("id"))
        await store.delete("id")  // no-op, no crash
    }

    // ── Slug ──────────────────────────────────────────────────────────────────

    func testGenerateSlug() {
        XCTAssertEqual(InMemorySkillStore.generateSlug("My Skill"), "my-skill")
        XCTAssertEqual(InMemorySkillStore.generateSlug("  Hello   World!!  "), "hello-world")
        XCTAssertEqual(InMemorySkillStore.generateSlug("Café — Über"), "caf-ber")  // non-ascii stripped
        // Empty / all-symbols → 32-char hex fallback.
        let fallback = InMemorySkillStore.generateSlug("!!!")
        XCTAssertEqual(fallback.count, 32)
    }

    // ── Skill packs ─────────────────────────────────────────────────────────────

    func testKnownSkillPacksCatalogue() {
        XCTAssertEqual(KnownSkillPacks.all.count, 8)
        XCTAssertEqual(KnownSkillPacks.awesomeAgentSkills.estimatedSkillCount, 1000)
        XCTAssertFalse(KnownSkillPacks.careerOps.isDefaultEnabled)
    }

    func testEnumerateEnabled() {
        let opts = SkillPackSourcesOptions()  // defaults: all sources, default-enabled on
        let enabled = opts.enumerateEnabled()
        // 6 of 8 are default-enabled (careerOps + buildYourOwnX are not).
        XCTAssertEqual(enabled.count, 6)
        XCTAssertFalse(enabled.contains { $0.name == "career-ops" })

        // Explicitly enabling career-ops adds it.
        let opts2 = SkillPackSourcesOptions(explicitlyEnabled: ["career-ops"])
        XCTAssertTrue(opts2.enumerateEnabled().contains { $0.name == "career-ops" })
    }

    func testSkillPackSourceCodableRoundTrip() throws {
        let s = KnownSkillPacks.claudeBugHunter
        XCTAssertEqual(try JSONDecoder().decode(SkillPackSource.self, from: try JSONEncoder().encode(s)), s)
    }

    // ── Pack downloader ─────────────────────────────────────────────────────────

    func testInMemoryPackDownloader() async throws {
        let dl = InMemoryPackDownloader()
        dl.add(sourceName: "awesome-agent-skills", localPath: "awesome")
        let path = try await dl.ensure(KnownSkillPacks.awesomeAgentSkills, cacheRoot: "/cache", cacheTtl: 60)
        XCTAssertEqual(path, "/cache/awesome")
    }

    func testInMemoryPackDownloaderHonoursAbsolutePath() async throws {
        let dl = InMemoryPackDownloader()
        dl.add(sourceName: "last30days-skill", localPath: "/abs/path")
        let path = try await dl.ensure(KnownSkillPacks.last30Days, cacheRoot: "/cache", cacheTtl: 60)
        XCTAssertEqual(path, "/abs/path")
    }

    func testPackDownloaderThrowsWhenUnavailable() async {
        let dl = InMemoryPackDownloader()
        do {
            _ = try await dl.ensure(KnownSkillPacks.edubaBrand, cacheRoot: "/c", cacheTtl: 1)
            XCTFail("expected throw")
        } catch let e as SkillPackError {
            XCTAssertEqual(e, .unavailable("eduba-brand"))
        } catch { XCTFail("wrong error \(error)") }
    }
}
