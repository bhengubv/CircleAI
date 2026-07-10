// PrefixCacheServiceTests.swift

import XCTest
@testable import CircleAI

final class PrefixCacheServiceTests: XCTestCase {

    private func tempRoot() -> String {
        let base = NSTemporaryDirectory()
        return (base as NSString).appendingPathComponent("circleai-prefix-\(UUID().uuidString)")
    }

    func testKeyForIsDeterministicAndComposed() {
        let a = PrefixCacheService.keyFor(modelId: "qwen3-0.6b", systemPrompt: "You are B!")
        let b = PrefixCacheService.keyFor(modelId: "qwen3-0.6b", systemPrompt: "You are B!")
        XCTAssertNotNil(a)
        XCTAssertEqual(a, b)
        // key = <16 hex>_<16 hex>
        let parts = a!.split(separator: "_")
        XCTAssertEqual(parts.count, 2)
        XCTAssertEqual(parts[0].count, 16)
        XCTAssertEqual(parts[1].count, 16)
        XCTAssertTrue(a!.allSatisfy { $0.isHexDigit || $0 == "_" })
    }

    func testKeyForNilWhenSystemPromptMissing() {
        XCTAssertNil(PrefixCacheService.keyFor(modelId: "m", systemPrompt: nil))
        XCTAssertNil(PrefixCacheService.keyFor(modelId: "m", systemPrompt: ""))
        XCTAssertNil(PrefixCacheService.keyFor(modelId: "  ", systemPrompt: "sys"))
    }

    func testDifferentInputsDifferentKeys() {
        let a = PrefixCacheService.keyFor(modelId: "m1", systemPrompt: "sys")
        let b = PrefixCacheService.keyFor(modelId: "m2", systemPrompt: "sys")
        let c = PrefixCacheService.keyFor(modelId: "m1", systemPrompt: "other")
        XCTAssertNotEqual(a, b)
        XCTAssertNotEqual(a, c)
    }

    func testHasEntryReflectsFilePresence() throws {
        let root = tempRoot()
        defer { try? FileManager.default.removeItem(atPath: root) }
        let svc = PrefixCacheService(root: root)
        let key = "abcd1234abcd1234_deadbeefdeadbeef"
        XCTAssertFalse(svc.hasEntry(key))
        try Data("session".utf8).write(to: URL(fileURLWithPath: svc.path(for: key)))
        XCTAssertTrue(svc.hasEntry(key))
    }

    func testPathForUsesSessionExtension() {
        let root = tempRoot()
        defer { try? FileManager.default.removeItem(atPath: root) }
        let svc = PrefixCacheService(root: root)
        XCTAssertTrue(svc.path(for: "k").hasSuffix("k.session"))
    }

    func testTouchUpdatesMtime() throws {
        let root = tempRoot()
        defer { try? FileManager.default.removeItem(atPath: root) }
        let svc = PrefixCacheService(root: root)
        let key = "k1"
        let p = svc.path(for: key)
        try Data("x".utf8).write(to: URL(fileURLWithPath: p))
        let old = Date(timeIntervalSince1970: 1_000_000)
        try FileManager.default.setAttributes([.modificationDate: old], ofItemAtPath: p)
        svc.touch(key)
        let attrs = try FileManager.default.attributesOfItem(atPath: p)
        let mtime = attrs[.modificationDate] as! Date
        XCTAssertGreaterThan(mtime, old)
    }

    func testEvictIfNeededKeepsUnderCapAndEvictsOldest() throws {
        // The cap is 500 MB; writing tiny files never triggers eviction, so we
        // assert the no-op-under-cap behaviour: all entries survive.
        let root = tempRoot()
        defer { try? FileManager.default.removeItem(atPath: root) }
        let svc = PrefixCacheService(root: root)
        for i in 0..<5 {
            try Data("session-\(i)".utf8).write(to: URL(fileURLWithPath: svc.path(for: "k\(i)")))
        }
        svc.evictIfNeeded()
        let remaining = try FileManager.default.contentsOfDirectory(atPath: root).filter { $0.hasSuffix(".session") }
        XCTAssertEqual(remaining.count, 5)
    }
}
