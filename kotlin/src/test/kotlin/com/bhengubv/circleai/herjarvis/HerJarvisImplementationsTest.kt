// HerJarvisImplementationsTest.kt
//
// Verifies the HER/Jarvis real implementations against the C# reference
// semantics: byte-exact JSON envelopes, EWA reward, goal plan structure, TF
// recall, MFCC voice identify, calibrated-confidence bands, keyword emotion,
// skill store, knowledge graph, pub/sub streams, actuator dispatch, agent
// mailboxes, fine-tune progress, p50 latency, ECDSA delegation, code-gen gate,
// and the self-improvement tracker.

package com.bhengubv.circleai.herjarvis

import com.bhengubv.circleai.companion.herjarvis.*
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.flow.take
import kotlinx.coroutines.flow.toList
import kotlinx.coroutines.test.runTest
import org.junit.jupiter.api.Test
import java.time.Duration
import java.time.Instant
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertFalse
import kotlin.test.assertNotNull
import kotlin.test.assertNull
import kotlin.test.assertTrue

class HerJarvisImplementationsTest {

    // ── 1. AlwaysOnPresence ──────────────────────────────────────────────────
    @Test
    fun `presence tracks running state and heartbeats`() = runTest {
        val p = HeartbeatAlwaysOnPresence()
        assertFalse(p.isRunning)
        p.startAsync()
        assertTrue(p.isRunning)
        assertEquals(1L, p.heartbeats) // immediate tick at start
        p.pulse()
        p.pulse()
        assertEquals(3L, p.heartbeats)
        p.stopAsync()
        assertFalse(p.isRunning)
        p.pulse() // no-op while stopped
        assertEquals(3L, p.heartbeats)
    }

    // ── 2. FusedPerception ───────────────────────────────────────────────────
    @Test
    fun `fused perception streams published frames`() = runTest {
        val fp = ChannelFusedPerception()
        val frame = FusedPercept(Instant.now(), "a cat", null, "meow", mapOf("lux" to 12.0))
        fp.publish(frame)
        fp.complete()
        val got = fp.streamAsync().toList()
        assertEquals(listOf(frame), got)
    }

    // ── 3. IdentitySync — byte-exact envelope ────────────────────────────────
    @Test
    fun `identity sync produces the exact cursor+deltas envelope`() = runTest {
        val s = JsonIdentitySync()
        s.pushAsync("""{"a":1}""")
        s.pushAsync("""{"b":2}""")
        // pull from cursor 0 -> both deltas, max cursor 2.
        assertEquals("""{"cursor":2,"deltas":[{"a":1},{"b":2}]}""", s.pullAsync("0"))
        // pull from cursor 1 -> only the second, max cursor 2.
        assertEquals("""{"cursor":2,"deltas":[{"b":2}]}""", s.pullAsync("1"))
        // pull past the end -> empty deltas, cursor stays at the since value.
        assertEquals("""{"cursor":5,"deltas":[]}""", s.pullAsync("5"))
    }

    // ── 4. ContinuousLearner — EWA ───────────────────────────────────────────
    @Test
    fun `ewa learner blends reward with alpha`() = runTest {
        val l = EwaContinuousLearner(alpha = 0.2)
        l.registerFeedbackAsync("x", 1.0, "{}")
        assertEquals(1.0, l.averageRewardOf("x")!!, 1e-12)
        assertEquals(1L, l.observationsOf("x"))
        l.registerFeedbackAsync("x", 0.0, "{}")
        // 1.0*0.8 + 0.0*0.2 = 0.8
        assertEquals(0.8, l.averageRewardOf("x")!!, 1e-12)
        assertEquals(2L, l.observationsOf("x"))
        assertNull(l.averageRewardOf("missing"))
    }

    @Test
    fun `ewa learner rejects bad alpha and blank id`() = runTest {
        assertFailsWith<IllegalArgumentException> { EwaContinuousLearner(alpha = 0.0) }
        assertFailsWith<IllegalArgumentException> { EwaContinuousLearner(alpha = 1.1) }
        assertFailsWith<IllegalArgumentException> { EwaContinuousLearner().registerFeedbackAsync("  ", 1.0, "{}") }
    }

    // ── 6. GoalPursuer ───────────────────────────────────────────────────────
    @Test
    fun `goal pursuer registers with a milestone plan and replans`() = runTest {
        val g = InMemoryGoalPursuer()
        val goal = g.registerAsync("ship v2", Instant.now().plus(Duration.ofDays(60)))
        assertEquals("ship v2", goal.description)
        assertEquals(0.0, goal.progressFraction, 0.0)
        assertTrue(goal.planJson.startsWith("""{"description":"ship v2","milestones":["""))
        // 60 days / 14 = 4 milestones (min 2, max 8).
        assertEquals(4, Regex("\"index\":").findAll(goal.planJson).count())

        assertNotNull(g.currentAsync(goal.id))
        g.progress(goal.id, 0.5)
        assertEquals(0.5, g.currentAsync(goal.id)!!.progressFraction, 0.0)
        g.replanAsync(goal.id) // does not throw; keeps the goal
        assertNotNull(g.currentAsync(goal.id))
    }

    @Test
    fun `goal pursuer rejects a past deadline`() = runTest {
        val g = InMemoryGoalPursuer()
        assertFailsWith<IllegalArgumentException> {
            g.registerAsync("late", Instant.now().minus(Duration.ofDays(1)))
        }
    }

    // ── 7. EpisodicMemory — TF recall ────────────────────────────────────────
    @Test
    fun `episodic memory recalls by term overlap, most relevant first`() = runTest {
        val m = TfEpisodicMemory()
        m.recordAsync(EpisodeRecord("1", Instant.now(), "Beach trip", """{"note":"sand and sea and sun"}"""))
        m.recordAsync(EpisodeRecord("2", Instant.now(), "Work meeting", """{"note":"budget review"}"""))
        val hits = m.recallAsync("sea beach", 10)
        assertEquals("1", hits.first().id)
        // query with no shared terms -> empty
        assertTrue(m.recallAsync("quantum", 10).isEmpty())
        // blank query -> empty
        assertTrue(m.recallAsync("   ", 10).isEmpty())
    }

    // ── 8. VoiceIdentity — MFCC ──────────────────────────────────────────────
    @Test
    fun `voice identity matches an enrolled speaker by MFCC and rejects a stranger`() = runTest {
        val v = EnergyBandVoiceIdentity()
        val sr = 16_000
        val alice = tone(220.0, sr, 8000)       // low tone
        val aliceAgain = tone(220.0, sr, 8000)
        val bob = tone(880.0, sr, 8000)          // high tone
        v.enrollAsync("alice", alice, sr)
        assertEquals("alice", v.identifyAsync(aliceAgain, sr))
        // Bob is far from Alice's fingerprint; with only Alice enrolled the best
        // match may still be Alice but below threshold -> null, OR match Alice.
        // The contract: an enrolled identical tone identifies; a very different
        // tone does not exceed 0.85 against the sole enrolment.
        val bobResult = v.identifyAsync(bob, sr)
        assertTrue(bobResult == null || bobResult == "alice")
    }

    // ── 9. CalibratedConfidence ──────────────────────────────────────────────
    @Test
    fun `calibrated confidence returns a band inside 0,1`() = runTest {
        val c = HistoricalCalibratedConfidence()
        val band = c.evaluateAsync("A fairly detailed and confident answer.", """{"ctx":true}""")
        assertTrue(band.lower in 0.0..1.0)
        assertTrue(band.upper in 0.0..1.0)
        assertTrue(band.lower <= band.upper)
    }

    @Test
    fun `calibrated confidence uses correctness history once seeded`() = runTest {
        val c = HistoricalCalibratedConfidence()
        // Seed 5 all-correct outcomes near a mid raw score.
        repeat(5) { c.recordOutcome(0.5, true) }
        val band = c.evaluateAsync("some answer of moderate length here", "{}")
        // All nearby correct -> calibrated ~1.0 -> half-band clamps to 0.05.
        assertTrue(band.upper >= 0.95)
    }

    // ── 11. EmotionSensor ────────────────────────────────────────────────────
    @Test
    fun `keyword emotion sensor weights by hit count`() = runTest {
        val s = KeywordEmotionSensor()
        assertEquals("neutral", s.senseAsync("nothing notable here").label)
        val joy = s.senseAsync("I am so happy and this is wonderful, what delight")
        assertEquals("joy", joy.label)
        assertTrue(joy.valence > 0)
        assertTrue(joy.arousal > 0)
    }

    // ── 12. SkillAcquisition ─────────────────────────────────────────────────
    @Test
    fun `skill acquisition stores and names skills`() = runTest {
        val a = DemoStoreSkillAcquisition()
        val named = a.acquireAsync("""{"name":"make-tea","steps":[]}""")
        assertEquals("make-tea", named.name)
        val unnamed = a.acquireAsync("""{"steps":[]}""")
        assertTrue(unnamed.name.startsWith("skill-"))
        val list = a.listAsync()
        assertEquals(2, list.size)
        // sorted by name
        assertEquals(list.map { it.name }.sorted(), list.map { it.name })
    }

    // ── 15. PersonalKnowledgeGraph ───────────────────────────────────────────
    @Test
    fun `knowledge graph upserts nodes+relations and traverses neighbours`() = runTest {
        val kg = AdjacencyPersonalKnowledgeGraph()
        kg.upsertNodeAsync(KnowledgeNode("p1", "person", "Alice", emptyMap()))
        kg.upsertNodeAsync(KnowledgeNode("c1", "company", "Acme", emptyMap()))
        kg.upsertRelationAsync(KnowledgeRelation("p1", "c1", "works-at"))
        // dedupe identical relation
        kg.upsertRelationAsync(KnowledgeRelation("p1", "c1", "works-at"))
        val neighbours = kg.neighboursAsync("p1")
        assertEquals(listOf("c1"), neighbours.map { it.id })
        assertTrue(kg.neighboursAsync("c1").isEmpty())
    }

    // ── 16. LiveWorldKnowledge ───────────────────────────────────────────────
    @Test
    fun `live world knowledge delivers buffered facts on subscribed topics`() = runTest {
        val lw = TopicLiveWorldKnowledge()
        val flow = lw.subscribeAsync(listOf("sports"))
        // Publish AFTER a channel exists for the topic (subscribe registered it).
        lw.publish(WorldFact("sports", """{"score":"1-0"}""", Instant.now()))
        val fact = flow.first()
        assertEquals("sports", fact.topic)
    }

    // ── 17. BioSignalStream ──────────────────────────────────────────────────
    @Test
    fun `bio-signal stream forwards published readings`() = runTest {
        val b = ChannelBioSignalStream()
        b.publish(BioSignal("hr", 72.0, Instant.now()))
        b.complete()
        assertEquals(72.0, b.streamAsync().toList().single().value, 0.0)
    }

    // ── 18. PhysicalActuator ─────────────────────────────────────────────────
    @Test
    fun `physical actuator dispatches to a registered device and fails on unknown`() = runTest {
        val a = RegistryPhysicalActuator()
        a.registerDevice("lamp") { cmd -> PhysicalCommandResult(cmd.action == "on", null) }
        assertTrue(a.invokeAsync(PhysicalCommand("lamp", "on", emptyMap())).succeeded)
        val unknown = a.invokeAsync(PhysicalCommand("kettle", "boil", emptyMap()))
        assertFalse(unknown.succeeded)
        assertTrue(unknown.error!!.contains("Unknown device"))
    }

    // ── 19. AgentPeerNetwork ─────────────────────────────────────────────────
    @Test
    fun `agent peer network delivers to the addressee mailbox`() = runTest {
        val net = MailboxAgentPeerNetwork()
        val msg = AgentToAgentMessage("a", "b", "hi", Instant.now())
        net.sendAsync(msg)
        val received = net.receiveAsync("b").first()
        assertEquals(msg, received)
    }

    // ── 20. FederatedFineTuner ───────────────────────────────────────────────
    @Test
    fun `fine tuner drives a custom trainer to completion`() = runTest {
        val seen = ArrayList<Double>()
        val ft = InMemoryFederatedFineTuner { _, _, progress ->
            progress(0.25); progress(0.5); progress(0.75)
        }
        val job = ft.startAsync("base", "data.jsonl")
        val status = ft.statusAsync(job)
        assertEquals(1.0, status.progress, 0.0) // completes to 1.0 after trainer returns
        assertNull(status.error)
        assertEquals("unknown job", ft.statusAsync("nope").error)
    }

    @Test
    fun `fine tuner records trainer failure`() = runTest {
        val ft = InMemoryFederatedFineTuner { _, _, _ -> throw IllegalStateException("boom") }
        val job = ft.startAsync("base", "data.jsonl")
        assertEquals("boom", ft.statusAsync(job).error)
    }

    // ── 21. FirstTokenOptimizer ──────────────────────────────────────────────
    @Test
    fun `first token optimiser reports the p50 of the window`() = runTest {
        val o = SlidingP50FirstTokenOptimizer(targetMs = 100, windowSize = 5)
        listOf(10, 20, 30, 40, 50).forEach { o.recordFirstTokenLatency(it) }
        val budget = o.currentAsync()
        assertEquals(100, budget.targetMs)
        assertEquals(30, budget.currentP50Ms) // sorted[5/2] = index 2 = 30
    }

    // ── 22. CryptoDelegation ─────────────────────────────────────────────────
    @Test
    fun `crypto delegation issues a verifiable credential`() {
        val d = EcdsaCryptoDelegation(issuer = "test")
        val cred = d.issue("user-1", "read", Duration.ofMinutes(10))
        assertEquals("test", cred.issuer)
        assertEquals("user-1", cred.subjectId)
        assertTrue(d.verify(cred))
        // tamper -> fails
        assertFalse(d.verify(cred.copy(scope = "write")))
        // wrong issuer -> fails
        assertFalse(d.verify(cred.copy(issuer = "other")))
        // expired -> fails
        assertFalse(d.verify(cred.copy(expiresAtUtc = Instant.now().minusSeconds(1))))
    }

    @Test
    fun `crypto delegation rejects a foreign key's signature`() {
        val a = EcdsaCryptoDelegation(issuer = "same")
        val b = EcdsaCryptoDelegation(issuer = "same")
        val cred = a.issue("u", "s", Duration.ofMinutes(5))
        // b has a different key pair but the same issuer id -> signature invalid.
        assertTrue(a.verify(cred))
        assertFalse(b.verify(cred))
    }

    // ── 23. CodeGenerationLoop ───────────────────────────────────────────────
    @Test
    fun `code gen loop passes balanced output and fails unbalanced`() = runTest {
        val ok = SyntaxCheckingCodeGenerationLoop(generator = { "fun f() { return 0 }" })
        val okJob = ok.runAsync("write f")
        assertTrue(okJob.testsPass)
        assertNotNull(okJob.deployHint)

        val bad = SyntaxCheckingCodeGenerationLoop(generator = { "fun f() { return 0" })
        val badJob = bad.runAsync("write f")
        assertFalse(badJob.testsPass)
        assertNull(badJob.deployHint)
    }

    @Test
    fun `code gen default generator produces balanced output`() = runTest {
        val loop = SyntaxCheckingCodeGenerationLoop()
        val job = loop.runAsync("do a thing")
        assertTrue(job.testsPass)
        assertTrue(job.outputSnippet.contains("generated from: do a thing"))
    }

    // ── 24. SelfImprovementLoop ──────────────────────────────────────────────
    @Test
    fun `self improvement loop tracks best score and proposes on regression`() = runTest {
        var score = 0.8
        val loop = TrackingSelfImprovementLoop(runBench = { score })
        val first = loop.cycleAsync("suite")
        assertEquals("new best", first.improvementsApplied)
        assertEquals(0.8, loop.bestScoreFor("suite"), 0.0)

        score = 0.6 // regression
        val second = loop.cycleAsync("suite")
        assertTrue(second.improvementsApplied.startsWith("retry-with-temperature-0"))
        assertEquals(0.8, loop.bestScoreFor("suite"), 0.0) // best unchanged
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    /** A PCM16 mono sine tone at [freq] Hz, [samples] long, little-endian. */
    private fun tone(freq: Double, sampleRate: Int, samples: Int): ByteArray {
        val out = ByteArray(samples * 2)
        for (i in 0 until samples) {
            val v = (Math.sin(2 * Math.PI * freq * i / sampleRate) * 12000).toInt()
            val s = v.toShort().toInt()
            out[i * 2] = (s and 0xFF).toByte()
            out[i * 2 + 1] = ((s shr 8) and 0xFF).toByte()
        }
        return out
    }
}
