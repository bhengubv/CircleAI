// NeuronTest.kt
//
// The Kotlin Neuron port. Mirrors the C# CircleAI.Tests Neuron suite: the
// concierge decision table + gate, the two-slot admission gate + eviction, the
// router-gated slot selection inside AIService (specialist hot-load, generalist
// floor, best-fit == generalist), the generalist-floor session round-trip, the
// NeuronNode facade, and NullChatRuntime.

package com.bhengubv.circleai.hosting

import com.bhengubv.circleai.device.DeviceProbe
import com.bhengubv.circleai.device.DeviceTier
import com.bhengubv.circleai.inference.GenerationOptions
import com.bhengubv.circleai.inference.IChatGenerator
import com.bhengubv.circleai.models.ChatMessage
import com.bhengubv.circleai.selector.ChatCapability
import com.bhengubv.circleai.selector.IModelSelector
import com.bhengubv.circleai.selector.ModelSelection
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.flow
import kotlinx.coroutines.flow.toList
import kotlinx.coroutines.test.runTest
import org.junit.jupiter.api.Test
import java.io.File
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertNotNull
import kotlin.test.assertNull
import kotlin.test.assertTrue

class NeuronTest {

    // ── test doubles ─────────────────────────────────────────────────────────

    /** IChatGenerator with a fixed reply + a real (true) session round-trip. */
    private class NeuronGen(private val reply: String) : IChatGenerator {
        override suspend fun generateAsync(messages: List<ChatMessage>, opts: GenerationOptions): String = reply
        override fun streamAsync(messages: List<ChatMessage>, opts: GenerationOptions): Flow<String> = flow { emit(reply) }
        override suspend fun saveSessionAsync(path: String): Boolean = true
        override suspend fun loadSessionAsync(path: String): Boolean = true
        override fun close() {}
    }

    private class FakeSelector(private val selection: ModelSelection) : IModelSelector {
        override fun bestFit(probe: DeviceProbe, required: Int): ModelSelection = selection
        override fun allCandidates(probe: DeviceProbe): List<ModelSelection> = listOf(selection)
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private fun tempModel(): String {
        val f = File.createTempFile("neuron-kt", ".model")
        f.writeText("m")
        f.deleteOnExit()
        return f.absolutePath
    }

    private fun sel(id: String, bytes: Long) =
        ModelSelection(modelId = id, requiresDownload = false, estimatedBytes = bytes, tier = DeviceTier.DESKTOP)

    private fun specialist(capability: Int) =
        INeuronRouter { RouteDecision(Organ.SPECIALIST, capability, "t") }

    // ── concierge router + gate ────────────────────────────────────────────────

    @Test fun routerPlainGeneralist() {
        val d = HeuristicNeuronRouter().route(RouteContext("what's the weather today?"))
        assertEquals(Organ.GENERALIST, d.organ)
        assertEquals(ChatCapability.DEFAULT, d.capability)
    }

    @Test fun routerVision() {
        val d = HeuristicNeuronRouter().route(RouteContext("what is this?", hasImage = true))
        assertEquals(Organ.SPECIALIST, d.organ)
        assertEquals(ChatCapability.VISION, d.capability)
    }

    @Test fun routerReasoning() {
        val d = HeuristicNeuronRouter().route(RouteContext("please debug this stack trace"))
        assertEquals(Organ.SPECIALIST, d.organ)
        assertEquals(ChatCapability.REASONING, d.capability)
    }

    @Test fun routerLongContext() {
        val d = HeuristicNeuronRouter(longContextChars = 50).route(RouteContext("x".repeat(60)))
        assertEquals(Organ.SPECIALIST, d.organ)
        assertEquals(ChatCapability.LONG_CONTEXT, d.capability)
    }

    @Test fun routerGateVeto() {
        val gate = NeuronGate { false }
        val d = HeuristicNeuronRouter(gate = gate).route(RouteContext("solve this equation"))
        assertEquals(Organ.GENERALIST, d.organ)
    }

    // ── resident slot manager ───────────────────────────────────────────────────

    @Test fun slotAdmitsWithinBudget() {
        val m = ResidentSlotManager(1000) { 1_000_000L }
        val a = m.ensureSpecialist(sel("spec", 5000)) { NeuronGen("S") }
        assertEquals(SlotOutcome.ADMITTED, a.outcome)
        assertEquals("spec", m.residentSpecialistModelId)
    }

    @Test fun slotDeniesOverBudget() {
        val m = ResidentSlotManager(900_000) { 1_000_000L }
        val a = m.ensureSpecialist(sel("spec", 500_000)) { NeuronGen("S") }
        assertEquals(SlotOutcome.INSUFFICIENT_RAM, a.outcome)
        assertNull(m.residentSpecialistModelId)
    }

    @Test fun slotAlreadyResident() {
        val m = ResidentSlotManager(0) { 1_000_000L }
        var builds = 0
        val build: (String) -> IChatGenerator = { builds++; NeuronGen("S") }
        m.ensureSpecialist(sel("spec", 1), build)
        val second = m.ensureSpecialist(sel("spec", 1), build)
        assertEquals(SlotOutcome.ALREADY_RESIDENT, second.outcome)
        assertEquals(1, builds)
    }

    @Test fun slotSwapEvicts() {
        val m = ResidentSlotManager(0) { 1_000_000L }
        m.ensureSpecialist(sel("A", 1)) { NeuronGen("A") }
        m.ensureSpecialist(sel("B", 1)) { NeuronGen("B") }
        assertEquals("B", m.residentSpecialistModelId)
    }

    @Test fun slotBuildFailure() {
        val m = ResidentSlotManager(0) { 1_000_000L }
        val a = m.ensureSpecialist(sel("spec", 1)) { null }
        assertEquals(SlotOutcome.BUILD_FAILED, a.outcome)
        assertNull(m.residentSpecialistModelId)
    }

    @Test fun slotEvict() {
        val m = ResidentSlotManager(0) { 1_000_000L }
        m.ensureSpecialist(sel("spec", 1)) { NeuronGen("S") }
        m.evictSpecialist()
        assertNull(m.residentSpecialistModelId)
    }

    // ── AIService two-slot residency ─────────────────────────────────────────────

    @Test fun routerNullUsesGeneralist() = runTest {
        val svc = AIService(AIOptions(modelPath = tempModel(), warmOnStart = false), { NeuronGen("GEN") })
        svc.startAsync()
        assertEquals("GEN", svc.askAsync("solve this equation")) // reasoning cue, but no router
    }

    @Test fun hotLoadsSpecialist() = runTest {
        val gen = NeuronGen("GEN")
        val spec = NeuronGen("SPEC")
        val svc = AIService(
            AIOptions(
                modelId = "gen-model",
                modelPath = tempModel(),
                warmOnStart = false,
                router = specialist(ChatCapability.REASONING),
                neuronSelector = FakeSelector(sel("spec-model", 1024)),
                specialistFactory = { spec },
            ),
            { gen },
        )
        svc.startAsync()
        assertEquals("SPEC", svc.askAsync("anything"))
    }

    @Test fun bestFitEqualsGeneralist() = runTest {
        val gen = NeuronGen("GEN")
        val svc = AIService(
            AIOptions(
                modelId = "gen-model",
                modelPath = tempModel(),
                warmOnStart = false,
                router = specialist(ChatCapability.REASONING),
                neuronSelector = FakeSelector(sel("gen-model", 1024)), // best-fit == generalist
                specialistFactory = { NeuronGen("SPEC") },
            ),
            { gen },
        )
        svc.startAsync()
        assertEquals("GEN", svc.askAsync("anything"))
    }

    @Test fun sessionRoundTrip() = runTest {
        val svc = AIService(AIOptions(modelPath = tempModel(), warmOnStart = false), { NeuronGen("GEN") })
        svc.startAsync()
        val snap = tempModel()
        assertTrue(svc.saveSessionAsync(snap))
        assertTrue(svc.loadSessionAsync(snap))
    }

    // ── NeuronNode facade + NullChatRuntime ───────────────────────────────────────

    @Test fun neuronNodeOverBrain() = runTest {
        val svc = AIService(AIOptions(modelId = "qwen-x", modelPath = tempModel(), warmOnStart = false), { NeuronGen("hello") })
        val node = NeuronNode(svc)

        assertEquals("circleai-neuron", node.id)
        assertFalse(node.isReady)
        assertEquals("loading model…", node.statusMessage)

        svc.startAsync()
        assertTrue(node.isReady)
        assertEquals("ready", node.statusMessage)
        assertTrue(node.engineLabel.contains("qwen-x"))

        val out = node.streamAsync(listOf(ChatTurn("user", "hi"))).toList().joinToString("")
        assertEquals("hello", out)
        assertNotNull(node.sessionSnapshotPath)
    }

    @Test fun nullRuntime() = runTest {
        val nul = NullChatRuntime()
        assertFalse(nul.isReady)
        val out = nul.streamAsync(listOf(ChatTurn("user", "hi"))).toList().joinToString("")
        assertTrue(out.contains("No chat engine"))
    }
}
