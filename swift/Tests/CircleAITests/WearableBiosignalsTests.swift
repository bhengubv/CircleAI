// WearableBiosignalsTests.swift
//
// Exercises the Wearable.Biosignals module from
// CircleAI.Wearable.Biosignals/*.cs:
//   • BiosignalKind stable integer values + Codable
//   • BiosignalSample.create (fresh id, clamped confidence)
//   • NullBiosignalSource (supports nothing, emits nothing)
//   • RecordedBiosignalSource (supported kinds, replay stream)
//   • BiosignalAggregator.snapshot (per-kind min/max/mean/count, window guard)
//   • BiosignalAffectMapper.apply (rule sheet, confidence gate, clamping)

import XCTest
import Foundation
@testable import CircleAI

final class WearableBiosignalsTests: XCTestCase {

    func testBiosignalKindStableValuesAndCodable() throws {
        XCTAssertEqual(BiosignalKind.heartRate.rawValue, 0)
        XCTAssertEqual(BiosignalKind.heartRateVariability.rawValue, 1)
        XCTAssertEqual(BiosignalKind.oxygenSaturation.rawValue, 2)
        XCTAssertEqual(BiosignalKind.accelerometer.rawValue, 3)
        XCTAssertEqual(BiosignalKind.bodyTemperature.rawValue, 4)
        XCTAssertEqual(BiosignalKind.sleepStage.rawValue, 5)
        XCTAssertEqual(BiosignalKind.steps.rawValue, 6)
        XCTAssertEqual(BiosignalKind.galvanicSkinResponse.rawValue, 7)
        XCTAssertEqual(BiosignalKind.unknown.rawValue, 8)
        for k in BiosignalKind.allCases {
            XCTAssertEqual(try JSONDecoder().decode(BiosignalKind.self, from: try JSONEncoder().encode(k)), k)
        }
    }

    func testSampleCreateClampsConfidenceAndFreshId() {
        let a = BiosignalSample.create(kind: .heartRate, value: 72, unit: "bpm", confidence: 1.5)
        XCTAssertEqual(a.confidence, 1.0) // clamped
        let b = BiosignalSample.create(kind: .heartRate, value: 72, unit: "bpm", confidence: -0.5)
        XCTAssertEqual(b.confidence, 0.0) // clamped
        XCTAssertNotEqual(a.id, b.id) // fresh guids
        XCTAssertEqual(a.kind, .heartRate)
        XCTAssertFalse(a.isCumulative)
    }

    func testSampleCodableRoundTrip() throws {
        let s = BiosignalSample(id: UUID(), kind: .steps, value: 4200, unit: "count", confidence: 0.9, isCumulative: true, measuredAt: Date(timeIntervalSince1970: 5))
        XCTAssertEqual(try JSONDecoder().decode(BiosignalSample.self, from: try JSONEncoder().encode(s)), s)
    }

    func testNullSourceSupportsAndEmitsNothing() async {
        let src = NullBiosignalSource()
        XCTAssertTrue(src.supportedKinds.isEmpty)
        let supported = await src.isSupported(.heartRate)
        XCTAssertFalse(supported)
        var count = 0
        do {
            for try await _ in src.stream() { count += 1 }
        } catch { XCTFail("null source should not throw: \(error)") }
        XCTAssertEqual(count, 0)
    }

    func testRecordedSourceReplaysAndReportsKinds() async throws {
        let now = Date()
        let samples = [
            BiosignalSample(id: UUID(), kind: .heartRate, value: 60, unit: "bpm", confidence: 1, isCumulative: false, measuredAt: now),
            BiosignalSample(id: UUID(), kind: .heartRate, value: 80, unit: "bpm", confidence: 1, isCumulative: false, measuredAt: now),
            BiosignalSample(id: UUID(), kind: .oxygenSaturation, value: 98, unit: "%", confidence: 1, isCumulative: false, measuredAt: now)
        ]
        let src = RecordedBiosignalSource(samples: samples)
        XCTAssertEqual(Set(src.supportedKinds), [.heartRate, .oxygenSaturation])
        let hrSupported = await src.isSupported(.heartRate)
        let stepsSupported = await src.isSupported(.steps)
        XCTAssertTrue(hrSupported)
        XCTAssertFalse(stepsSupported)
        var replayed: [Float] = []
        for try await s in src.stream() { replayed.append(s.value) }
        XCTAssertEqual(replayed, [60, 80, 98])
    }

    func testAggregatorSnapshotComputesStats() async throws {
        let now = Date()
        let samples = [
            BiosignalSample(id: UUID(), kind: .heartRate, value: 60, unit: "bpm", confidence: 1, isCumulative: false, measuredAt: now),
            BiosignalSample(id: UUID(), kind: .heartRate, value: 80, unit: "bpm", confidence: 1, isCumulative: false, measuredAt: now),
            BiosignalSample(id: UUID(), kind: .heartRate, value: 100, unit: "bpm", confidence: 1, isCumulative: false, measuredAt: now),
            BiosignalSample(id: UUID(), kind: .oxygenSaturation, value: 97, unit: "%", confidence: 1, isCumulative: false, measuredAt: now)
        ]
        let agg = BiosignalAggregator(source: RecordedBiosignalSource(samples: samples))
        let snap = try await agg.snapshot(window: 5)
        let hr = try XCTUnwrap(snap.stats[.heartRate])
        XCTAssertEqual(hr.sampleCount, 3)
        XCTAssertEqual(hr.min, 60)
        XCTAssertEqual(hr.max, 100)
        XCTAssertEqual(hr.mean, 80, accuracy: 1e-5)
        let ox = try XCTUnwrap(snap.stats[.oxygenSaturation])
        XCTAssertEqual(ox.sampleCount, 1)
        XCTAssertEqual(ox.mean, 97)
    }

    func testAggregatorRejectsNonPositiveWindow() async {
        let agg = BiosignalAggregator(source: NullBiosignalSource())
        do {
            _ = try await agg.snapshot(window: 0)
            XCTFail("expected windowMustBePositive")
        } catch { XCTAssertEqual(error as? BiosignalError, .windowMustBePositive) }
    }

    func testAggregatorExcludesSamplesBeforeCutoff() async throws {
        let now = Date()
        let old = now.addingTimeInterval(-3600) // an hour ago, outside a 5s window
        let samples = [
            BiosignalSample(id: UUID(), kind: .heartRate, value: 70, unit: "bpm", confidence: 1, isCumulative: false, measuredAt: now),
            BiosignalSample(id: UUID(), kind: .heartRate, value: 999, unit: "bpm", confidence: 1, isCumulative: false, measuredAt: old)
        ]
        let agg = BiosignalAggregator(source: RecordedBiosignalSource(samples: samples))
        let snap = try await agg.snapshot(window: 5)
        let hr = try XCTUnwrap(snap.stats[.heartRate])
        XCTAssertEqual(hr.sampleCount, 1) // the old sample is excluded
        XCTAssertEqual(hr.max, 70)
    }

    // MARK: - Affect mapper

    func testAffectMapperHighHeartRate() {
        let a = AffectState(userId: "u")
        let e0 = a.energy, u0 = a.uncertainty
        BiosignalAffectMapper.apply(BiosignalSample(id: UUID(), kind: .heartRate, value: 140, unit: "bpm", confidence: 1, isCumulative: false, measuredAt: Date()), to: a)
        XCTAssertEqual(a.energy, min(1, e0 + 0.10), accuracy: 1e-5)
        XCTAssertEqual(a.uncertainty, min(1, u0 + 0.05), accuracy: 1e-5)
    }

    func testAffectMapperModerateAndLowHeartRate() {
        let a = AffectState(userId: "u")
        let e0 = a.energy
        BiosignalAffectMapper.apply(BiosignalSample(id: UUID(), kind: .heartRate, value: 110, unit: "bpm", confidence: 1, isCumulative: false, measuredAt: Date()), to: a)
        XCTAssertEqual(a.energy, min(1, e0 + 0.05), accuracy: 1e-5)

        let b = AffectState(userId: "u")
        let e1 = b.energy
        BiosignalAffectMapper.apply(BiosignalSample(id: UUID(), kind: .heartRate, value: 45, unit: "bpm", confidence: 1, isCumulative: false, measuredAt: Date()), to: b)
        XCTAssertEqual(b.energy, max(0, e1 - 0.05), accuracy: 1e-5)
    }

    func testAffectMapperHrvAndSpO2() {
        let a = AffectState(userId: "u")
        let u0 = a.uncertainty, r0 = a.rapport
        BiosignalAffectMapper.apply(BiosignalSample(id: UUID(), kind: .heartRateVariability, value: 15, unit: "ms", confidence: 1, isCumulative: false, measuredAt: Date()), to: a)
        XCTAssertEqual(a.uncertainty, min(1, u0 + 0.05), accuracy: 1e-5)
        XCTAssertEqual(a.rapport, max(0, r0 - 0.02), accuracy: 1e-5)

        let b = AffectState(userId: "u")
        let en0 = b.engagement
        BiosignalAffectMapper.apply(BiosignalSample(id: UUID(), kind: .heartRateVariability, value: 70, unit: "ms", confidence: 1, isCumulative: false, measuredAt: Date()), to: b)
        XCTAssertEqual(b.engagement, min(1, en0 + 0.02), accuracy: 1e-5)

        let c = AffectState(userId: "u")
        let cu0 = c.uncertainty
        BiosignalAffectMapper.apply(BiosignalSample(id: UUID(), kind: .oxygenSaturation, value: 88, unit: "%", confidence: 1, isCumulative: false, measuredAt: Date()), to: c)
        XCTAssertEqual(c.uncertainty, min(1, cu0 + 0.10), accuracy: 1e-5)
    }

    func testAffectMapperLowConfidenceIsIgnored() {
        let a = AffectState(userId: "u")
        let e0 = a.energy, u0 = a.uncertainty
        BiosignalAffectMapper.apply(BiosignalSample(id: UUID(), kind: .heartRate, value: 140, unit: "bpm", confidence: 0.4, isCumulative: false, measuredAt: Date()), to: a)
        XCTAssertEqual(a.energy, e0)
        XCTAssertEqual(a.uncertainty, u0)
    }

    func testAffectMapperSleepStageAndUnknownDoNothing() {
        let a = AffectState(userId: "u")
        let e0 = a.energy, u0 = a.uncertainty, r0 = a.rapport, en0 = a.engagement
        BiosignalAffectMapper.apply(BiosignalSample(id: UUID(), kind: .sleepStage, value: 2, unit: "stage", confidence: 1, isCumulative: false, measuredAt: Date()), to: a)
        BiosignalAffectMapper.apply(BiosignalSample(id: UUID(), kind: .steps, value: 5000, unit: "count", confidence: 1, isCumulative: true, measuredAt: Date()), to: a)
        XCTAssertEqual(a.energy, e0)
        XCTAssertEqual(a.uncertainty, u0)
        XCTAssertEqual(a.rapport, r0)
        XCTAssertEqual(a.engagement, en0)
    }
}
