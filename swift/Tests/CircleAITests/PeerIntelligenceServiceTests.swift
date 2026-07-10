// PeerIntelligenceServiceTests.swift
//
// Validates PeerIntelligenceService (IPeerIntelligence) ported from
// AetherIntelligenceService.cs: network-health aggregation, per-peer threat
// assessment (level bands + confidence = deficit + 0.1·indicators), routing
// advice (avoid-list + direct-path gating), and the trust-score stream.

import XCTest
import Foundation
@testable import CircleAI

final class PeerIntelligenceServiceTests: XCTestCase {

    private func make() -> (PeerIntelligenceService, NodeTrustRegistry, SecurityOptions) {
        let opts = SecurityOptions()
        let reg = NodeTrustRegistry(options: opts)
        return (PeerIntelligenceService(registry: reg, options: opts), reg, opts)
    }

    private func event(node: String, kind: PeerSecurityEventKind = .authAttempt,
                       level: PeerThreatLevel = .medium, at: Date = Date(), desc: String = "e") -> PeerSecurityEvent {
        PeerSecurityEvent(nodeId: node, kind: kind, threatLevel: level,
                          description: desc, transportId: "test", occurredAt: at)
    }

    // ── network health ───────────────────────────────────────────────────────

    func testNetworkHealthWithNoPeers() async throws {
        let (svc, _, _) = make()
        let report = try await svc.getNetworkHealth()
        XCTAssertEqual(report.overallScore, 1.0, accuracy: 1e-9)
        XCTAssertEqual(report.trustedPeerCount, 0)
        XCTAssertEqual(report.suspiciousPeerCount, 0)
        XCTAssertEqual(report.summary, "No peers observed.")
    }

    func testNetworkHealthAveragesScoresAndClassifies() async throws {
        let (svc, reg, _) = make()
        // Two peers: one at 1.0 (untouched), one at 0.4 → average 0.7 → "degraded".
        reg.getOrCreate("healthy")
        _ = reg.applyDegradation(event(node: "weak"), degradationAmount: 0.6) // 0.4
        let report = try await svc.getNetworkHealth()
        XCTAssertEqual(report.overallScore, 0.7, accuracy: 1e-9)
        // trusted = scores > avoidNode(0.50): only the 1.0 peer → 1
        XCTAssertEqual(report.trustedPeerCount, 1)
        // suspicious = scores ≤ elevateMonitoring(0.75): only 0.4 → 1
        XCTAssertEqual(report.suspiciousPeerCount, 1)
        XCTAssertEqual(report.summary, "Network health is degraded; elevated monitoring active.")
    }

    func testNetworkHealthExcellentSummary() async throws {
        let (svc, reg, _) = make()
        reg.getOrCreate("a"); reg.getOrCreate("b") // both 1.0 → avg 1.0 > 0.90
        let report = try await svc.getNetworkHealth()
        XCTAssertEqual(report.summary, "Network health is excellent.")
    }

    func testNetworkHealthCriticalSummary() async throws {
        let (svc, reg, _) = make()
        _ = reg.applyDegradation(event(node: "z"), degradationAmount: 0.9) // 0.10 avg ≤ 0.25
        let report = try await svc.getNetworkHealth()
        XCTAssertEqual(report.summary, "Network health is critical; quarantine directives in effect.")
    }

    // ── threat assessment ────────────────────────────────────────────────────

    func testAssessThreatUnknownNodeIsFullyTrusted() async throws {
        let (svc, _, _) = make()
        let a = try await svc.assessThreat(nodeId: "ghost")
        XCTAssertEqual(a.threatLevel, .none)
        XCTAssertEqual(a.confidence, 0.0, accuracy: 1e-9) // deficit 0, no indicators
        XCTAssertTrue(a.indicators.isEmpty)
    }

    func testAssessThreatConfidenceFromDeficitPlusIndicators() async throws {
        let (svc, reg, _) = make()
        // Drop to 0.4 via a single degradation, but ALSO record 3 auth events so
        // the indicator "repeated-auth-attempts" fires (deficit 0.6 + 0.1 = 0.7).
        _ = reg.applyDegradation(event(node: "p", kind: .authAttempt), degradationAmount: 0.6) // 0.4, +1 auth event
        _ = reg.applyDegradation(event(node: "p", kind: .authAttempt), degradationAmount: 0.0) // still 0.4, +1 auth
        _ = reg.applyDegradation(event(node: "p", kind: .authAttempt), degradationAmount: 0.0) // +1 auth → 3 total
        let a = try await svc.assessThreat(nodeId: "p")
        XCTAssertEqual(a.threatLevel, .high) // 0.4 ≤ 0.50
        XCTAssertTrue(a.indicators.contains("repeated-auth-attempts"))
        XCTAssertEqual(a.confidence, 0.7, accuracy: 1e-9) // 0.6 deficit + 1 indicator × 0.1
    }

    func testAssessThreatConfidenceCapsAtOne() async throws {
        let (svc, reg, _) = make()
        // Fully lost (deficit 1.0) with any indicator → capped at 1.0.
        _ = reg.applyDegradation(event(node: "p", kind: .intrusionSignal, level: .critical), degradationAmount: 1.0)
        let a = try await svc.assessThreat(nodeId: "p")
        XCTAssertEqual(a.confidence, 1.0, accuracy: 1e-9)
        XCTAssertEqual(a.threatLevel, .critical)
    }

    func testAssessThreatLevelBands() async throws {
        let (svc, reg, _) = make()
        // Score bands: >0.90 none; ≤0.90 low; ≤0.75 medium; ≤0.50 high; ≤0.25 critical.
        _ = reg.applyDegradation(event(node: "low"), degradationAmount: 0.05)   // 0.95 → none
        _ = reg.applyDegradation(event(node: "lo2"), degradationAmount: 0.15)   // 0.85 → low
        _ = reg.applyDegradation(event(node: "med"), degradationAmount: 0.30)   // 0.70 → medium
        _ = reg.applyDegradation(event(node: "hi"), degradationAmount: 0.55)    // 0.45 → high
        _ = reg.applyDegradation(event(node: "crit"), degradationAmount: 0.80)  // 0.20 → critical
        let none = try await svc.assessThreat(nodeId: "low")
        let low = try await svc.assessThreat(nodeId: "lo2")
        let med = try await svc.assessThreat(nodeId: "med")
        let hi = try await svc.assessThreat(nodeId: "hi")
        let crit = try await svc.assessThreat(nodeId: "crit")
        XCTAssertEqual(none.threatLevel, .none)
        XCTAssertEqual(low.threatLevel, .low)
        XCTAssertEqual(med.threatLevel, .medium)
        XCTAssertEqual(hi.threatLevel, .high)
        XCTAssertEqual(crit.threatLevel, .critical)
    }

    // ── routing advice ───────────────────────────────────────────────────────

    func testRoutingAdviceDirectPathWhenTrusted() async throws {
        let (svc, reg, _) = make()
        reg.getOrCreate("dest") // 1.0
        let advice = try await svc.getRoutingAdvice(destinationNodeId: "dest")
        XCTAssertEqual(advice.recommendedPath, ["dest"])
        XCTAssertEqual(advice.confidence, 1.0, accuracy: 1e-9)
        XCTAssertTrue(advice.avoidNodeIds.isEmpty)
        XCTAssertTrue(advice.reasoning.contains("trusted"))
        XCTAssertTrue(advice.reasoning.contains("1.00"))
    }

    func testRoutingAdviceEmptyPathWhenBelowAvoidThreshold() async throws {
        let (svc, reg, _) = make()
        _ = reg.applyDegradation(event(node: "dest"), degradationAmount: 0.6) // 0.40 ≤ 0.50
        let advice = try await svc.getRoutingAdvice(destinationNodeId: "dest")
        XCTAssertTrue(advice.recommendedPath.isEmpty)
        XCTAssertTrue(advice.avoidNodeIds.contains("dest"))
        XCTAssertTrue(advice.reasoning.contains("degraded trust"))
    }

    func testRoutingAdviceQuarantinedReasoning() async throws {
        let (svc, reg, _) = make()
        _ = reg.applyDegradation(event(node: "dest"), degradationAmount: 0.9) // 0.10 ≤ 0.25
        let advice = try await svc.getRoutingAdvice(destinationNodeId: "dest")
        XCTAssertTrue(advice.reasoning.contains("quarantined"))
        XCTAssertTrue(advice.recommendedPath.isEmpty)
    }

    func testRoutingAdviceCollectsAllAvoidNodes() async throws {
        let (svc, reg, _) = make()
        _ = reg.applyDegradation(event(node: "bad1"), degradationAmount: 0.7) // 0.30 ≤ 0.50
        _ = reg.applyDegradation(event(node: "bad2"), degradationAmount: 0.8) // 0.20 ≤ 0.50
        reg.getOrCreate("dest") // 1.0
        let advice = try await svc.getRoutingAdvice(destinationNodeId: "dest")
        XCTAssertEqual(Set(advice.avoidNodeIds), ["bad1", "bad2"])
        XCTAssertEqual(advice.recommendedPath, ["dest"])
    }

    // ── stream ───────────────────────────────────────────────────────────────

    func testStreamTrustScoresDeliversUpdates() async throws {
        let (svc, reg, _) = make()
        var iterator = svc.streamTrustScores().makeAsyncIterator()
        _ = reg.applyDegradation(event(node: "p", desc: "streamed"), degradationAmount: 0.2)
        let update = await iterator.next()
        XCTAssertEqual(update?.nodeId, "p")
        XCTAssertEqual(update?.reason, "streamed")
    }
}
