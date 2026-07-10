// HostingRuntimeTests.swift
//
// Verifies ThermalThrottleService state transitions + pause semantics,
// BackgroundInferenceWorker thermal pause, ManualMemoryPressureSource
// subscription/raise, HistogramRequestPredictor forecasting, and
// PredictiveWarmupController tick decisions.

import XCTest
@testable import CircleAI

final class HostingRuntimeTests: XCTestCase {

    // ── Thermal ─────────────────────────────────────────────────────────────

    func testThermalStateComparableOrdering() {
        XCTAssertTrue(ThermalState.serious > ThermalState.fair)
        XCTAssertTrue(ThermalState.critical > ThermalState.serious)
        XCTAssertTrue(ThermalState.normal < ThermalState.serious)
    }

    func testThermalApplyStateFiresOnChangeOnly() {
        let svc = ThermalThrottleService(sampler: { .unknown })
        let box = IntBox()
        svc.onStateChanged { _ in box.increment() }

        svc.applyNewState(.normal)   // change unknown→normal → fires
        svc.applyNewState(.normal)   // no change → no fire
        svc.applyNewState(.serious)  // change → fires
        XCTAssertEqual(box.value, 2)
        XCTAssertEqual(svc.currentState, .serious)
        XCTAssertTrue(svc.shouldPauseInference)
    }

    func testThermalShouldPauseThreshold() {
        let svc = ThermalThrottleService()
        svc.applyNewState(.fair)
        XCTAssertFalse(svc.shouldPauseInference)
        svc.applyNewState(.serious)
        XCTAssertTrue(svc.shouldPauseInference)
    }

    // ── BackgroundInferenceWorker ───────────────────────────────────────────

    func testWorkerStartsButlerAndPausesOnThermal() async throws {
        let butler = FakeButler()
        let thermal = ThermalThrottleService()
        let worker = BackgroundInferenceWorker(butler: butler, thermal: thermal)
        try await worker.start()
        XCTAssertTrue(butler.isReady)
        XCTAssertFalse(worker.isPaused)

        thermal.applyNewState(.serious)
        XCTAssertTrue(worker.isPaused)
        thermal.applyNewState(.normal)
        XCTAssertFalse(worker.isPaused)

        try await worker.stop()
        XCTAssertFalse(butler.isReady)
    }

    // ── Memory pressure ─────────────────────────────────────────────────────

    func testManualMemoryPressureRaiseFiresHandlerOnTransition() async {
        let src = ManualMemoryPressureSource()
        let box = TransitionBox()
        let sub = src.subscribe { old, new in box.record(old, new) }
        await src.raise(.trim)     // normal → trim
        await src.raise(.trim)     // no transition → no fire
        await src.raise(.critical) // trim → critical
        XCTAssertEqual(box.snapshot().count, 2)
        XCTAssertEqual(src.current, .critical)
        sub.dispose()
        await src.raise(.normal)   // after unsubscribe → no fire
        XCTAssertEqual(box.snapshot().count, 2)
    }

    func testNullMemoryPressureAlwaysNormal() {
        let src = NullMemoryPressureSource.instance
        XCTAssertEqual(src.current, .normal)
    }

    // ── HistogramRequestPredictor ───────────────────────────────────────────

    func testPredictorColdStartZeroConfidence() {
        let p = HistogramRequestPredictor()
        let f = p.predict(Date(), forecastWindow: 60)
        XCTAssertEqual(f.confidence, 0)
        XCTAssertEqual(f.probabilityOfArrival, 0)
        XCTAssertEqual(p.observedArrivals, 0)
    }

    func testPredictorRaisesProbabilityAfterArrivals() {
        let p = HistogramRequestPredictor()
        var cal = Calendar(identifier: .gregorian); cal.timeZone = TimeZone(identifier: "UTC")!
        let now = cal.date(from: DateComponents(year: 2026, month: 7, day: 8, hour: 9, minute: 0))!
        // Record many arrivals at the same minute-of-day.
        for _ in 0..<30 { p.recordArrival(now) }
        XCTAssertEqual(p.observedArrivals, 30)
        let f = p.predict(now, forecastWindow: 60)
        XCTAssertGreaterThan(f.probabilityOfArrival, 0)
        XCTAssertGreaterThan(f.confidence, 0)
    }

    func testPredictorZeroWindowGivesZero() {
        let p = HistogramRequestPredictor()
        p.recordArrival(Date())
        let f = p.predict(Date(), forecastWindow: 0)
        XCTAssertEqual(f.probabilityOfArrival, 0)
    }

    // ── PredictiveWarmupController ───────────────────────────────────────────

    func testWarmupTickFiresWhenScoreAboveThreshold() async {
        let butler = FakeButler()
        // Predictor that always reports high probability + confidence.
        let predictor = FixedForecastPredictor(forecast: ArrivalForecast(probabilityOfArrival: 1.0, expectedCount: 3, confidence: 1.0))
        let opts = PredictiveWarmupOptions(enabled: true, warmupThreshold: 0.5, minTimeBetweenWarmups: 0)
        let ctrl = PredictiveWarmupController(service: butler, predictor: predictor, options: opts)
        let fired = await ctrl.tick()
        XCTAssertTrue(fired)
        XCTAssertEqual(butler.prewarmCount, 1)
    }

    func testWarmupTickSkipsBelowThreshold() async {
        let butler = FakeButler()
        let predictor = FixedForecastPredictor(forecast: ArrivalForecast(probabilityOfArrival: 0.1, expectedCount: 0.1, confidence: 0.1))
        let opts = PredictiveWarmupOptions(enabled: true, warmupThreshold: 0.5)
        let ctrl = PredictiveWarmupController(service: butler, predictor: predictor, options: opts)
        let fired = await ctrl.tick()
        XCTAssertFalse(fired)
        XCTAssertEqual(butler.prewarmCount, 0)
    }

    func testWarmupHonoursMinTimeBetweenWarmups() async {
        let butler = FakeButler()
        let predictor = FixedForecastPredictor(forecast: ArrivalForecast(probabilityOfArrival: 1.0, expectedCount: 3, confidence: 1.0))
        // Large cool-down → second tick within the window is suppressed.
        let opts = PredictiveWarmupOptions(enabled: true, warmupThreshold: 0.5, minTimeBetweenWarmups: 3600)
        let ctrl = PredictiveWarmupController(service: butler, predictor: predictor, options: opts)
        _ = await ctrl.tick()
        let second = await ctrl.tick()
        XCTAssertFalse(second)
        XCTAssertEqual(butler.prewarmCount, 1)
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    final class IntBox: @unchecked Sendable {
        private let lock = NSLock(); private var v = 0
        func increment() { lock.lock(); v += 1; lock.unlock() }
        var value: Int { lock.lock(); defer { lock.unlock() }; return v }
    }
    final class TransitionBox: @unchecked Sendable {
        private let lock = NSLock(); private var v: [(MemoryPressureLevel, MemoryPressureLevel)] = []
        func record(_ a: MemoryPressureLevel, _ b: MemoryPressureLevel) { lock.lock(); v.append((a, b)); lock.unlock() }
        func snapshot() -> [(MemoryPressureLevel, MemoryPressureLevel)] { lock.lock(); defer { lock.unlock() }; return v }
    }
    struct FixedForecastPredictor: IRequestPredictor {
        let forecast: ArrivalForecast
        func recordArrival(_ utc: Date) {}
        func predict(_ utcNow: Date, forecastWindow: TimeInterval) -> ArrivalForecast { forecast }
        var observedArrivals: Int64 { 1 }
    }
}
