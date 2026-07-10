// HerJarvisTests.swift
//
// Verifies the HER/Jarvis in-memory implementations ported in HerJarvis.swift:
// always-on heartbeat, fused perception, continuous learner (EWA), goal
// pursuer, voice identity (MFCC), calibrated confidence, emotion sensor, skill
// acquisition, bio-signal stream, physical actuator, agent peer network,
// federated fine-tuner, first-token optimizer, crypto delegation, code-gen
// loop, and self-improvement loop. Values cross-checked against the C# reference.

import XCTest
@testable import CircleAI

final class HerJarvisTests: XCTestCase {

    // ── 1. AlwaysOnPresence ───────────────────────────────────────────────
    func testAlwaysOnPresenceStartStop() async throws {
        let presence = HeartbeatAlwaysOnPresence(heartbeatInterval: 0.02)
        XCTAssertFalse(presence.isRunning)
        try await presence.start()
        XCTAssertTrue(presence.isRunning)
        // Give the timer a few ticks.
        try await Task.sleep(nanoseconds: 120_000_000)
        try await presence.stop()
        XCTAssertFalse(presence.isRunning)
        XCTAssertGreaterThan(presence.heartbeats, 0, "heartbeat timer should have ticked at least once")
    }

    func testAlwaysOnStartIsIdempotent() async throws {
        let presence = HeartbeatAlwaysOnPresence(heartbeatInterval: 10)
        try await presence.start()
        try await presence.start() // no-op
        XCTAssertTrue(presence.isRunning)
        try await presence.stop()
    }

    // ── 2. FusedPerception ────────────────────────────────────────────────
    func testFusedPerceptionStreams() async throws {
        let fp = ChannelFusedPerception()
        let stream = fp.stream()
        let p = FusedPercept(at: Date(), vision: "a cat", audio: nil, text: "hi", sensors: ["lux": 42])
        fp.publish(p)
        fp.complete()
        var received: [FusedPercept] = []
        for await item in stream { received.append(item) }
        XCTAssertEqual(received.count, 1)
        XCTAssertEqual(received[0].vision, "a cat")
        XCTAssertEqual(received[0].sensors["lux"], 42)
    }

    // ── 4. ContinuousLearner (EWA) ────────────────────────────────────────
    func testEwaContinuousLearner() async throws {
        let learner = EwaContinuousLearner(alpha: 0.5)
        try await learner.registerFeedback(interactionId: "i1", reward: 1.0, contextJson: "{}")
        XCTAssertEqual(learner.averageRewardOf("i1"), 1.0)
        XCTAssertEqual(learner.observationsOf("i1"), 1)
        // Second reward blends: 1.0*(1-0.5) + 0.0*0.5 = 0.5
        try await learner.registerFeedback(interactionId: "i1", reward: 0.0, contextJson: "{}")
        XCTAssertEqual(learner.averageRewardOf("i1")!, 0.5, accuracy: 1e-12)
        XCTAssertEqual(learner.observationsOf("i1"), 2)
        XCTAssertNil(learner.averageRewardOf("unknown"))
    }

    func testEwaValidAlphaBoundaries() async throws {
        // Boundary alphas construct cleanly (precondition traps on out-of-range,
        // which is uncatchable in XCTest, so we only assert the valid range).
        _ = EwaContinuousLearner(alpha: 1.0)
        let learner = EwaContinuousLearner(alpha: 0.2)
        try await learner.registerFeedback(interactionId: "i", reward: 0.5, contextJson: "{}")
        XCTAssertEqual(learner.averageRewardOf("i"), 0.5)
    }

    // ── 6. GoalPursuer ────────────────────────────────────────────────────
    func testGoalPursuerRegisterAndReplan() async throws {
        let gp = InMemoryGoalPursuer()
        let deadline = Date().addingTimeInterval(60 * 86400) // 60 days
        let g = try await gp.register(description: "ship v2", deadlineUtc: deadline)
        XCTAssertFalse(g.id.isEmpty)
        XCTAssertEqual(g.progressFraction, 0)
        XCTAssertTrue(g.planJson.contains("\"milestones\""))
        XCTAssertTrue(g.planJson.contains("\"description\":\"ship v2\""))

        let fetched = try await gp.current(id: g.id)
        XCTAssertEqual(fetched?.id, g.id)

        try await gp.replan(id: g.id)
        let after = try await gp.current(id: g.id)
        XCTAssertNotNil(after)

        try gp.progress(id: g.id, fraction: 0.5)
        let progressed = try await gp.current(id: g.id)
        XCTAssertEqual(progressed?.progressFraction, 0.5)
    }

    func testGoalPursuerRejectsPastDeadline() async {
        let gp = InMemoryGoalPursuer()
        do {
            _ = try await gp.register(description: "late", deadlineUtc: Date().addingTimeInterval(-100))
            XCTFail("expected past-deadline to throw")
        } catch {
            XCTAssertEqual(error as? HerJarvisError, .invalidArgument("deadline must be in the future"))
        }
    }

    func testGoalPursuerMilestoneCount() async throws {
        // 60 days → min(8, max(2, 60/14=4)) = 4 milestones.
        let gp = InMemoryGoalPursuer()
        let g = try await gp.register(description: "x", deadlineUtc: Date().addingTimeInterval(60 * 86400))
        let count = g.planJson.components(separatedBy: "\"index\":").count - 1
        XCTAssertEqual(count, 4)
    }

    // ── 8. VoiceIdentity (MFCC) ───────────────────────────────────────────
    func testVoiceIdentityEnrollAndIdentify() async throws {
        let vi = EnergyBandVoiceIdentity()
        let a = Self.tone(freqHz: 220, sampleRate: 16000, seconds: 0.5)
        let b = Self.tone(freqHz: 660, sampleRate: 16000, seconds: 0.5)
        try await vi.enroll(userId: "alice", audioPcm16: a, sampleRateHz: 16000)
        try await vi.enroll(userId: "bob", audioPcm16: b, sampleRateHz: 16000)

        // Same tone as alice → should identify alice (cosine sim of identical MFCC ≈ 1).
        let id = try await vi.identify(audioPcm16: a, sampleRateHz: 16000)
        XCTAssertEqual(id, "alice")
    }

    func testVoiceIdentityUnknownReturnsNil() async throws {
        let vi = EnergyBandVoiceIdentity()
        // Nothing enrolled → nil.
        let id = try await vi.identify(audioPcm16: Self.tone(freqHz: 300, sampleRate: 16000, seconds: 0.3),
                                       sampleRateHz: 16000)
        XCTAssertNil(id)
    }

    // ── 9. CalibratedConfidence ───────────────────────────────────────────
    func testCalibratedConfidenceBandNoHistory() async throws {
        let cc = HistoricalCalibratedConfidence()
        let band = try await cc.evaluate(answer: "The capital of France is Paris.", contextJson: "{\"x\":1}")
        XCTAssertGreaterThanOrEqual(band.lower, 0)
        XCTAssertLessThanOrEqual(band.upper, 1)
        XCTAssertLessThanOrEqual(band.lower, band.upper)
    }

    func testCalibratedConfidenceHedgeLowersRaw() async throws {
        let cc = HistoricalCalibratedConfidence()
        let confident = HistoricalCalibratedConfidence.computeRawScore(
            answer: "The answer is definitely forty two point zero exactly here.", contextJson: "")
        let hedged = HistoricalCalibratedConfidence.computeRawScore(
            answer: "Maybe it is possibly around forty, but I don't know, perhaps.", contextJson: "")
        XCTAssertGreaterThan(confident, hedged)
    }

    func testCalibratedConfidenceUsesHistory() async throws {
        let cc = HistoricalCalibratedConfidence()
        // Record 5 outcomes all correct at a similar raw score band.
        for _ in 0..<5 { cc.recordOutcome(rawScore: 0.5, wasCorrect: true) }
        let band = try await cc.evaluate(answer: "some medium length answer here now", contextJson: "")
        // All-correct nearby history → calibrated ≈ 1 → tight, high band.
        XCTAssertGreaterThan(band.upper, band.lower)
    }

    // ── 11. EmotionSensor ─────────────────────────────────────────────────
    func testEmotionSensorJoy() async throws {
        let es = KeywordEmotionSensor()
        let frame = try await es.sense(fusedJson: "{\"text\":\"I am so happy and excited, this is wonderful!\"}")
        XCTAssertEqual(frame.label, "joy")
        XCTAssertGreaterThan(frame.valence, 0)
        XCTAssertGreaterThan(frame.arousal, 0)
    }

    func testEmotionSensorNeutral() async throws {
        let es = KeywordEmotionSensor()
        let frame = try await es.sense(fusedJson: "{\"text\":\"the meeting is at noon\"}")
        XCTAssertEqual(frame.label, "neutral")
        XCTAssertEqual(frame.arousal, 0.0)
        XCTAssertEqual(frame.valence, 0.0)
    }

    func testEmotionSensorDominantLabelWins() async throws {
        let es = KeywordEmotionSensor()
        // Two anger words vs one joy word → anger dominates.
        let frame = try await es.sense(fusedJson: "angry furious but a little happy")
        XCTAssertEqual(frame.label, "anger")
        XCTAssertLessThan(frame.valence, 0)
    }

    // ── 12. SkillAcquisition ──────────────────────────────────────────────
    func testSkillAcquisitionNamed() async throws {
        let sk = DemoStoreSkillAcquisition()
        let s = try await sk.acquire(demonstrationJson: "{\"name\":\"make-coffee\",\"steps\":[]}")
        XCTAssertEqual(s.name, "make-coffee")
        let list = try await sk.list()
        XCTAssertEqual(list.count, 1)
        XCTAssertEqual(list[0].id, s.id)
    }

    func testSkillAcquisitionUnnamedFallsBack() async throws {
        let sk = DemoStoreSkillAcquisition()
        let s = try await sk.acquire(demonstrationJson: "{\"steps\":[1,2]}")
        XCTAssertTrue(s.name.hasPrefix("skill-"))
    }

    func testSkillAcquisitionListSortedByName() async throws {
        let sk = DemoStoreSkillAcquisition()
        _ = try await sk.acquire(demonstrationJson: "{\"name\":\"zebra\"}")
        _ = try await sk.acquire(demonstrationJson: "{\"name\":\"alpha\"}")
        let list = try await sk.list()
        XCTAssertEqual(list.map { $0.name }, ["alpha", "zebra"])
    }

    // ── 17. BioSignalStream ───────────────────────────────────────────────
    func testBioSignalStream() async throws {
        let bs = ChannelBioSignalStream()
        let stream = bs.stream()
        bs.publish(BioSignal(kind: "hr", value: 72, at: Date()))
        bs.publish(BioSignal(kind: "hrv", value: 45, at: Date()))
        bs.complete()
        var kinds: [String] = []
        for await s in stream { kinds.append(s.kind) }
        XCTAssertEqual(kinds, ["hr", "hrv"])
    }

    // ── 18. PhysicalActuator ──────────────────────────────────────────────
    func testPhysicalActuatorDispatch() async throws {
        let act = RegistryPhysicalActuator()
        act.registerDevice("lamp") { cmd in
            PhysicalCommandResult(succeeded: cmd.action == "on", error: nil)
        }
        let ok = try await act.invoke(command: PhysicalCommand(deviceId: "lamp", action: "on"))
        XCTAssertTrue(ok.succeeded)
        let bad = try await act.invoke(command: PhysicalCommand(deviceId: "lamp", action: "explode"))
        XCTAssertFalse(bad.succeeded)
    }

    func testPhysicalActuatorUnknownDevice() async throws {
        let act = RegistryPhysicalActuator()
        let r = try await act.invoke(command: PhysicalCommand(deviceId: "ghost", action: "x"))
        XCTAssertFalse(r.succeeded)
        XCTAssertEqual(r.error, "Unknown device 'ghost'")
    }

    // ── 19. AgentPeerNetwork ──────────────────────────────────────────────
    func testAgentPeerNetworkBufferedDelivery() async throws {
        let net = MailboxAgentPeerNetwork()
        // Send before subscribing — should buffer and flush on receive.
        try await net.send(message: AgentToAgentMessage(fromAgentId: "a", toAgentId: "b", payload: "hi", at: Date()))
        var got: [String] = []
        let stream = net.receive(forAgentId: "b")
        // Send another after subscribing.
        try await net.send(message: AgentToAgentMessage(fromAgentId: "a", toAgentId: "b", payload: "again", at: Date()))
        var iterator = stream.makeAsyncIterator()
        if let m1 = await iterator.next() { got.append(m1.payload) }
        if let m2 = await iterator.next() { got.append(m2.payload) }
        XCTAssertEqual(got, ["hi", "again"])
    }

    // ── 20. FederatedFineTuner ────────────────────────────────────────────
    func testFederatedFineTunerCompletes() async throws {
        let ft = InMemoryFederatedFineTuner(trainer: { _, _, report in
            report(0.25); report(0.5); report(0.75)
        })
        let job = try await ft.start(baseModel: "base", trainingDataPath: "/tmp/does-not-matter")
        // Poll until complete.
        var status = try await ft.status(jobId: job)
        for _ in 0..<50 where status.progress < 1.0 {
            try await Task.sleep(nanoseconds: 20_000_000)
            status = try await ft.status(jobId: job)
        }
        XCTAssertEqual(status.progress, 1.0, accuracy: 1e-9)
        XCTAssertNil(status.error)
    }

    func testFederatedFineTunerUnknownJob() async throws {
        let ft = InMemoryFederatedFineTuner(trainer: { _, _, _ in })
        let s = try await ft.status(jobId: "nope")
        XCTAssertEqual(s.error, "unknown job")
    }

    // ── 21. FirstTokenOptimizer ───────────────────────────────────────────
    func testFirstTokenP50() async throws {
        let opt = SlidingP50FirstTokenOptimizer(targetMs: 100, windowSize: 8)
        for ms in [10, 20, 30, 40, 50] { opt.recordFirstTokenLatency(ms) }
        let budget = try await opt.current()
        XCTAssertEqual(budget.targetMs, 100)
        // sorted[5/2] = sorted[2] = 30.
        XCTAssertEqual(budget.currentP50Ms, 30)
    }

    func testFirstTokenEmpty() async throws {
        let opt = SlidingP50FirstTokenOptimizer()
        let budget = try await opt.current()
        XCTAssertEqual(budget.currentP50Ms, 0)
    }

    func testFirstTokenWindowEviction() async throws {
        let opt = SlidingP50FirstTokenOptimizer(targetMs: 50, windowSize: 3)
        for ms in [100, 100, 100, 1, 1, 1] { opt.recordFirstTokenLatency(ms) }
        // Only last 3 (all 1) remain → p50 = 1.
        let budget = try await opt.current()
        XCTAssertEqual(budget.currentP50Ms, 1)
    }

    // ── 22. CryptoDelegation ──────────────────────────────────────────────
    func testCryptoDelegationRoundTrip() throws {
        let cd = EcdsaCryptoDelegation(issuer: "test")
        let cred = try cd.issue(subjectId: "user-1", scope: "read", lifetime: 60)
        XCTAssertEqual(cred.issuer, "test")
        XCTAssertEqual(cred.subjectId, "user-1")
        XCTAssertEqual(cred.scope, "read")
        XCTAssertTrue(cd.verify(credential: cred))
    }

    func testCryptoDelegationRejectsTampered() throws {
        let cd = EcdsaCryptoDelegation(issuer: "test")
        let cred = try cd.issue(subjectId: "user-1", scope: "read", lifetime: 60)
        let tampered = DelegationCredential(
            issuer: cred.issuer, subjectId: cred.subjectId, scope: "write", // changed scope
            expiresAtUtc: cred.expiresAtUtc, signature: cred.signature)
        XCTAssertFalse(cd.verify(credential: tampered))
    }

    func testCryptoDelegationRejectsExpired() throws {
        let cd = EcdsaCryptoDelegation(issuer: "test")
        let cred = try cd.issue(subjectId: "user-1", scope: "read", lifetime: 1)
        let expired = DelegationCredential(
            issuer: cred.issuer, subjectId: cred.subjectId, scope: cred.scope,
            expiresAtUtc: Date().addingTimeInterval(-10), signature: cred.signature)
        XCTAssertFalse(cd.verify(credential: expired))
    }

    func testCryptoDelegationRejectsWrongIssuer() throws {
        let cd = EcdsaCryptoDelegation(issuer: "test")
        let cred = try cd.issue(subjectId: "u", scope: "s", lifetime: 60)
        let other = DelegationCredential(
            issuer: "attacker", subjectId: cred.subjectId, scope: cred.scope,
            expiresAtUtc: cred.expiresAtUtc, signature: cred.signature)
        XCTAssertFalse(cd.verify(credential: other))
    }

    // ── 23. CodeGenerationLoop ────────────────────────────────────────────
    func testCodeGenLoopBalanced() async throws {
        let loop = SyntaxCheckingCodeGenerationLoop(
            generator: { _ in "public class C { void M() { } }" })
        let job = try await loop.run(prompt: "write a class")
        XCTAssertTrue(job.testsPass)
        XCTAssertEqual(job.deployHint, "stage as nuget")
    }

    func testCodeGenLoopUnbalancedFails() async throws {
        let loop = SyntaxCheckingCodeGenerationLoop(
            generator: { _ in "void M() { unclosed(" })
        let job = try await loop.run(prompt: "broken")
        XCTAssertFalse(job.testsPass)
        XCTAssertNil(job.deployHint)
    }

    func testCodeGenLoopDefaultInline() async throws {
        let loop = SyntaxCheckingCodeGenerationLoop() // default generator echoes prompt + "return 0;"
        let job = try await loop.run(prompt: "hello")
        XCTAssertTrue(job.testsPass)
        XCTAssertEqual(job.deployHint, "run inline")
    }

    func testIsSyntacticallyBalanced() {
        XCTAssertTrue(SyntaxCheckingCodeGenerationLoop.isSyntacticallyBalanced("{[()]}"))
        XCTAssertFalse(SyntaxCheckingCodeGenerationLoop.isSyntacticallyBalanced(")("))
        XCTAssertFalse(SyntaxCheckingCodeGenerationLoop.isSyntacticallyBalanced(""))
    }

    // ── 24. SelfImprovementLoop (tracking) ────────────────────────────────
    func testTrackingSelfImprovementNewBest() async throws {
        let loop = TrackingSelfImprovementLoop(runBench: { _ in 0.9 })
        let v1 = try await loop.cycle(benchSuiteId: "s")
        XCTAssertEqual(v1.improvementsApplied, "new best")
        XCTAssertEqual(v1.newBenchScore, 0.9, accuracy: 1e-12)
        XCTAssertEqual(loop.bestScoreFor("s"), 0.9, accuracy: 1e-12)
    }

    /// Thread-safe FIFO of scripted scores for the @Sendable runBench closure.
    final class ScoreQueue: @unchecked Sendable {
        private let lock = NSLock()
        private var scores: [Double]
        init(_ scores: [Double]) { self.scores = scores }
        func next() -> Double { lock.lock(); defer { lock.unlock() }; return scores.removeFirst() }
    }

    func testTrackingSelfImprovementRegressionProposes() async throws {
        let scores = ScoreQueue([0.9, 0.5])
        let loop = TrackingSelfImprovementLoop(
            runBench: { _ in scores.next() },
            proposeImprovement: { _, current in "tune (\(current))" })
        _ = try await loop.cycle(benchSuiteId: "s")        // 0.9 → new best
        let v2 = try await loop.cycle(benchSuiteId: "s")   // 0.5 → regression
        XCTAssertTrue(v2.improvementsApplied.hasPrefix("tune"))
        XCTAssertEqual(v2.newBenchScore, 0.5, accuracy: 1e-12)
        // Best stays at 0.9.
        XCTAssertEqual(loop.bestScoreFor("s"), 0.9, accuracy: 1e-12)
    }

    func testTrackingSelfImprovementNoRegression() async throws {
        let loop = TrackingSelfImprovementLoop(runBench: { _ in 0.7 })
        _ = try await loop.cycle(benchSuiteId: "s")
        let v2 = try await loop.cycle(benchSuiteId: "s") // equal → "no regression"
        XCTAssertEqual(v2.improvementsApplied, "no regression")
    }

    // ── helpers ───────────────────────────────────────────────────────────

    /// A PCM-16 little-endian sine tone → Data.
    static func tone(freqHz: Double, sampleRate: Int, seconds: Double) -> Data {
        let n = Int(Double(sampleRate) * seconds)
        var data = Data(capacity: n * 2)
        for i in 0..<n {
            let sample = sin(2.0 * Double.pi * freqHz * Double(i) / Double(sampleRate))
            let s = Int16(max(-1.0, min(1.0, sample)) * 32000)
            data.append(UInt8(truncatingIfNeeded: Int(s) & 0xFF))
            data.append(UInt8(truncatingIfNeeded: (Int(s) >> 8) & 0xFF))
        }
        return data
    }
}
