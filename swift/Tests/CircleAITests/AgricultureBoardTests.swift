// AgricultureBoardTests.swift
//
// Exercises the Agriculture records' Codable round-trips and the deterministic
// behaviour of InMemoryFarmBoard — fields, crops (per field, asc by plantedOn),
// yields, and average yield of a variety (case-insensitive). Also checks the
// AgricultureDomainContext constants. Mirrors CircleAI.Agriculture/*.cs.

import XCTest
import Foundation
@testable import CircleAI

final class AgricultureBoardTests: XCTestCase {

    func testCropCodableRoundTrip() throws {
        let c = Crop(cropId: "c1", fieldId: "f1", variety: "Maize", plantedOn: Date(timeIntervalSince1970: 10), expectedHarvest: Date(timeIntervalSince1970: 100))
        XCTAssertEqual(try JSONDecoder().decode(Crop.self, from: try JSONEncoder().encode(c)), c)
        let noHarvest = Crop(cropId: "c2", fieldId: "f1", variety: "Wheat", plantedOn: Date(timeIntervalSince1970: 5), expectedHarvest: nil)
        XCTAssertEqual(try JSONDecoder().decode(Crop.self, from: try JSONEncoder().encode(noHarvest)), noHarvest)
    }

    func testCropsForFieldAscending() {
        let b = InMemoryFarmBoard()
        b.addField(Field(fieldId: "f1", areaHa: 10, soilType: "loam", irrigationKind: "drip"))
        b.plant(Crop(cropId: "c2", fieldId: "f1", variety: "Maize", plantedOn: Date(timeIntervalSince1970: 30), expectedHarvest: nil))
        b.plant(Crop(cropId: "c1", fieldId: "f1", variety: "Maize", plantedOn: Date(timeIntervalSince1970: 10), expectedHarvest: nil))
        b.plant(Crop(cropId: "c3", fieldId: "f2", variety: "Maize", plantedOn: Date(timeIntervalSince1970: 5), expectedHarvest: nil))
        XCTAssertEqual(b.getField("f1")?.areaHa, 10)
        XCTAssertEqual(b.cropsForField(fieldId: "f1").map { $0.cropId }, ["c1", "c2"])
    }

    func testAvgYieldOfVariety() {
        let b = InMemoryFarmBoard()
        b.plant(Crop(cropId: "c1", fieldId: "f1", variety: "Maize", plantedOn: Date(timeIntervalSince1970: 1), expectedHarvest: nil))
        b.plant(Crop(cropId: "c2", fieldId: "f1", variety: "maize", plantedOn: Date(timeIntervalSince1970: 1), expectedHarvest: nil))
        b.plant(Crop(cropId: "c3", fieldId: "f1", variety: "Wheat", plantedOn: Date(timeIntervalSince1970: 1), expectedHarvest: nil))
        b.recordYield(YieldRecord(cropId: "c1", tonsPerHa: 6, harvestedOn: Date(timeIntervalSince1970: 1)))
        b.recordYield(YieldRecord(cropId: "c2", tonsPerHa: 8, harvestedOn: Date(timeIntervalSince1970: 1)))
        b.recordYield(YieldRecord(cropId: "c3", tonsPerHa: 100, harvestedOn: Date(timeIntervalSince1970: 1)))
        XCTAssertEqual(b.avgYieldOfVariety("MAIZE"), 7, accuracy: 1e-9) // case-insensitive, averages c1+c2
        XCTAssertEqual(b.avgYieldOfVariety("Sorghum"), 0, accuracy: 1e-9) // none
    }

    func testDomainContext() {
        XCTAssertTrue(AgricultureDomainContext.systemPromptSnippet.contains("[DOMAIN: Agriculture]"))
        XCTAssertEqual(AgricultureDomainContext.complianceFlags, ["DAFF_regs", "CARA", "Fertilizer_Act", "POPIA"])
        XCTAssertEqual(AgricultureDomainContext.suggestedTools, ["weather_api", "market_prices", "soil_data", "document_editor"])
    }
}
