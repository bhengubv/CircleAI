// LanguageRegistryTests.swift
// Asserts KnownLanguages.all.count == 20 and validates per-tag fields
// against fixtures/language_tags.json.

import XCTest
import Foundation
@testable import CircleAI

final class LanguageRegistryTests: XCTestCase {

    private let fixturesDir: URL = {
        URL(fileURLWithPath: #file)
            .deletingLastPathComponent()   // Tests/CircleAITests/
            .deletingLastPathComponent()   // Tests/
            .deletingLastPathComponent()   // swift/
            .deletingLastPathComponent()   // CircleAI/ (repo root)
            .appendingPathComponent("fixtures")
    }()

    // ── KnownLanguages static list ───────────────────────────────────────────

    func testAllCount() {
        XCTAssertEqual(KnownLanguages.all.count, 20, "KnownLanguages.all must contain exactly 20 entries")
    }

    func testNoBcpTagDuplicates() {
        let tags = KnownLanguages.all.map(\.bcpTag)
        let unique = Set(tags)
        XCTAssertEqual(tags.count, unique.count, "BCP tags must be unique")
    }

    func testDeclarationOrder() {
        // First three: zu, st, af
        XCTAssertEqual(KnownLanguages.all[0].bcpTag, "zu")
        XCTAssertEqual(KnownLanguages.all[1].bcpTag, "st")
        XCTAssertEqual(KnownLanguages.all[2].bcpTag, "af")
        // Last two: zh, hi
        XCTAssertEqual(KnownLanguages.all[18].bcpTag, "zh")
        XCTAssertEqual(KnownLanguages.all[19].bcpTag, "hi")
    }

    func testOnlyArabicIsRtl() {
        let rtl = KnownLanguages.all.filter(\.isRtl)
        XCTAssertEqual(rtl.count, 1)
        XCTAssertEqual(rtl.first?.bcpTag, "ar")
    }

    func testWritingSystems() {
        XCTAssertEqual(KnownLanguages.amharic.writingSystem, .ethiopic)
        XCTAssertEqual(KnownLanguages.arabic.writingSystem,  .arabic)
        XCTAssertEqual(KnownLanguages.mandarin.writingSystem, .han)
        XCTAssertEqual(KnownLanguages.hindi.writingSystem,   .devanagari)
        XCTAssertEqual(KnownLanguages.isiZulu.writingSystem, .latin)
    }

    func testNativeNames() {
        XCTAssertEqual(KnownLanguages.amharic.nativeName,   "አማርኛ")
        XCTAssertEqual(KnownLanguages.arabic.nativeName,    "العربية")
        XCTAssertEqual(KnownLanguages.mandarin.nativeName,  "中文")
        XCTAssertEqual(KnownLanguages.hindi.nativeName,     "हिन्दी")
        XCTAssertEqual(KnownLanguages.swahili.nativeName,   "Kiswahili")
        XCTAssertEqual(KnownLanguages.yoruba.nativeName,    "Yorùbá")
    }

    func testPrimaryRegions() {
        XCTAssertEqual(KnownLanguages.isiZulu.primaryRegion,    "ZA")
        XCTAssertEqual(KnownLanguages.swahili.primaryRegion,    "KE")
        XCTAssertEqual(KnownLanguages.hausa.primaryRegion,      "NG")
        XCTAssertEqual(KnownLanguages.amharic.primaryRegion,    "ET")
        XCTAssertEqual(KnownLanguages.arabic.primaryRegion,     "SA")
        XCTAssertEqual(KnownLanguages.english.primaryRegion,    "GB")
        XCTAssertEqual(KnownLanguages.mandarin.primaryRegion,   "CN")
        XCTAssertEqual(KnownLanguages.hindi.primaryRegion,      "IN")
    }

    // ── LanguageTag.unknown sentinel ─────────────────────────────────────────

    func testUnknownTag() {
        let u = LanguageTag.unknown
        XCTAssertEqual(u.bcpTag,      "und")
        XCTAssertEqual(u.englishName, "Unknown")
        XCTAssertFalse(u.isRtl)
        XCTAssertEqual(u.writingSystem, .latin)
    }

    // ── Fixture-driven per-tag assertions ────────────────────────────────────

    func testAllTagsMatchFixture() throws {
        let url = fixturesDir.appendingPathComponent("language_tags.json")
        let data = try Data(contentsOf: url)
        let json = try JSONSerialization.jsonObject(with: data) as! [String: Any]
        let languages = json["languages"] as! [[String: Any]]

        XCTAssertEqual(languages.count, 20, "Fixture must have 20 language entries")

        // Build a lookup from fixture by bcpTag
        var fixtureByTag: [String: [String: Any]] = [:]
        for lang in languages {
            let tag = lang["bcpTag"] as! String
            fixtureByTag[tag] = lang
        }

        for knownTag in KnownLanguages.all {
            guard let fix = fixtureByTag[knownTag.bcpTag] else {
                XCTFail("BCP tag '\(knownTag.bcpTag)' not found in fixture")
                continue
            }
            XCTAssertEqual(knownTag.englishName,   fix["englishName"]   as? String ?? "",  "\(knownTag.bcpTag).englishName")
            XCTAssertEqual(knownTag.nativeName,    fix["nativeName"]    as? String ?? "",  "\(knownTag.bcpTag).nativeName")
            XCTAssertEqual(knownTag.isRtl,         fix["isRtl"]         as? Bool   ?? false, "\(knownTag.bcpTag).isRtl")
            XCTAssertEqual(knownTag.primaryRegion, fix["primaryRegion"] as? String ?? "",  "\(knownTag.bcpTag).primaryRegion")

            // Writing system
            let wsRaw = fix["writingSystem"] as! String
            let expectedWs = writingSystem(from: wsRaw)
            XCTAssertEqual(knownTag.writingSystem, expectedWs, "\(knownTag.bcpTag).writingSystem")
        }
    }

    // ── Fixture order matches declaration order ───────────────────────────────

    func testFixtureOrderMatchesDeclarationOrder() throws {
        let url = fixturesDir.appendingPathComponent("language_tags.json")
        let data = try Data(contentsOf: url)
        let json = try JSONSerialization.jsonObject(with: data) as! [String: Any]
        let languages = json["languages"] as! [[String: Any]]

        for (i, fix) in languages.enumerated() {
            let fixTag = fix["bcpTag"] as! String
            let knownTag = KnownLanguages.all[i].bcpTag
            XCTAssertEqual(knownTag, fixTag, "Index \(i): expected '\(fixTag)', got '\(knownTag)'")
        }
    }

    // ── African language count ────────────────────────────────────────────────

    func testAfricanLanguageCount() {
        // Africa: zu, st, af, sw, ha, am, yo, ig, xh, nso, tn, so, om = 13
        let africanRegions: Set<String> = ["ZA", "KE", "NG", "ET", "SO"]
        let count = KnownLanguages.all.filter { africanRegions.contains($0.primaryRegion) }.count
        XCTAssertEqual(count, 13)
    }

    // ── Helper ───────────────────────────────────────────────────────────────

    private func writingSystem(from raw: String) -> WritingSystem {
        switch raw {
        case "Latin":      return .latin
        case "Arabic":     return .arabic
        case "Ethiopic":   return .ethiopic
        case "Geez":       return .geez
        case "Devanagari": return .devanagari
        case "Han":        return .han
        case "Cyrillic":   return .cyrillic
        case "Hebrew":     return .hebrew
        case "Greek":      return .greek
        default:           return .other
        }
    }
}
