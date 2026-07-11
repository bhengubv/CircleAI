// EnergyBoardTests.swift
//
// Exercises the Energy records' Codable round-trips and the deterministic
// behaviour of InMemoryEnergyBoard — readings (asc), kWh delta over window,
// tariffs + cost estimate (peak rate), and active outages. Also checks the
// EnergyDomainContext constants. Mirrors CircleAI.Energy/*.cs.

import XCTest
import Foundation
@testable import CircleAI

final class EnergyBoardTests: XCTestCase {

    func testMeterReadingCodableRoundTrip() throws {
        let r = MeterReading(meterId: "m1", kwh: 1234.5, atUtc: Date(timeIntervalSince1970: 5))
        XCTAssertEqual(try JSONDecoder().decode(MeterReading.self, from: try JSONEncoder().encode(r)), r)
    }

    func testReadingsAscendingAndTotalKwhDelta() {
        let b = InMemoryEnergyBoard()
        let base = Date(timeIntervalSince1970: 1000)
        b.record(MeterReading(meterId: "m1", kwh: 100, atUtc: base.addingTimeInterval(30)))
        b.record(MeterReading(meterId: "m1", kwh: 90, atUtc: base.addingTimeInterval(10)))
        b.record(MeterReading(meterId: "m1", kwh: 95, atUtc: base.addingTimeInterval(20)))
        b.record(MeterReading(meterId: "m1", kwh: 1, atUtc: base.addingTimeInterval(-5))) // before window
        XCTAssertEqual(b.readingsFor(meterId: "m1", since: base).map { $0.kwh }, [90, 95, 100])
        XCTAssertEqual(b.totalKwhSince(meterId: "m1", since: base), 10, accuracy: 1e-9) // 100 - 90
        // Fewer than 2 readings -> 0.
        XCTAssertEqual(b.totalKwhSince(meterId: "m2", since: base), 0, accuracy: 1e-9)
    }

    func testEstimateCostUsesPeakRateAndUnknownTariffThrows() throws {
        let b = InMemoryEnergyBoard()
        let base = Date(timeIntervalSince1970: 1000)
        b.record(MeterReading(meterId: "m1", kwh: 100, atUtc: base.addingTimeInterval(1)))
        b.record(MeterReading(meterId: "m1", kwh: 150, atUtc: base.addingTimeInterval(2)))
        b.setTariff(EnergyTariff(tariffId: "t1", name: "Home", peakKwhRate: 2.5, offPeakKwhRate: 1.0, currency: "ZAR"))
        XCTAssertEqual(b.getTariff("t1")?.name, "Home")
        // (150-100) * 2.5 = 125
        XCTAssertEqual(try b.estimateCost(meterId: "m1", tariffId: "t1", since: base), Decimal(125))
        XCTAssertThrowsError(try b.estimateCost(meterId: "m1", tariffId: "ghost", since: base)) { XCTAssertEqual($0 as? EnergyError, .unknownTariff("ghost")) }
    }

    func testActiveOutages() {
        let b = InMemoryEnergyBoard()
        b.logOutage(Outage(outageId: "o1", area: "North", startUtc: Date(timeIntervalSince1970: 1), endUtc: nil, reason: "storm"))
        b.logOutage(Outage(outageId: "o2", area: "South", startUtc: Date(timeIntervalSince1970: 1), endUtc: Date(timeIntervalSince1970: 9), reason: nil)) // ended
        XCTAssertEqual(b.activeOutages().map { $0.outageId }, ["o1"])
    }

    func testDomainContext() {
        XCTAssertTrue(EnergyDomainContext.systemPromptSnippet.contains("[DOMAIN: Energy]"))
        XCTAssertEqual(EnergyDomainContext.complianceFlags, ["Electricity_Act", "NERSA", "SABS", "Municipal_Energy_By_laws", "POPIA"])
        XCTAssertEqual(EnergyDomainContext.suggestedTools, ["energy_model", "analytics", "document_editor", "web_search"])
    }
}
