// InferenceBridgeTest.kt
//
// Verifies LocalProcessInferenceBridge + MockInferenceBridge against the C#
// reference: model-loaded checks, completion status classification
// (Completed/StoppedByToken/StoppedByLength/model-not-loaded Failed), streaming
// fallback when the generator yields nothing, token estimation, and the mock's
// canned behaviour.

package com.bhengubv.circleai.hosting.inferencebridge

import com.bhengubv.circleai.inference.GenerationOptions
import com.bhengubv.circleai.inference.IChatGenerator
import com.bhengubv.circleai.models.ChatMessage
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.flow
import kotlinx.coroutines.flow.toList
import kotlinx.coroutines.test.runTest
import org.junit.jupiter.api.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertTrue

class InferenceBridgeTest {

    /** Deterministic generator that returns a fixed reply and can stream it or nothing. */
    private class FakeGen(
        private val reply: String,
        private val streamNothing: Boolean = false,
    ) : IChatGenerator {
        override suspend fun generateAsync(messages: List<ChatMessage>, opts: GenerationOptions): String = reply
        override fun streamAsync(messages: List<ChatMessage>, opts: GenerationOptions): Flow<String> = flow {
            if (!streamNothing) {
                for (word in reply.split(" ")) emit(word)
            }
        }
        override fun close() {}
    }

    private fun descriptor(id: String = "m1") = ModelDescriptor(
        modelId = id, version = "1", format = ModelFormat.Gguf,
        contextWindowTokens = 4096, vocabSize = 32000, parameterCount = 0,
        quantisationLabel = null, approximateMemoryBytes = 0,
    )

    private fun request(model: String, prompt: String, maxTokens: Int = 256, stops: List<String> = emptyList()) =
        InferenceRequest(
            id = java.util.UUID.randomUUID(), modelId = model, prompt = prompt,
            maxOutputTokens = maxTokens, temperature = 0.5f, topP = 0.9f,
            stopSequences = stops, metadata = emptyMap(), requestedAt = java.time.Instant.now(),
        )

    @Test
    fun `lists loaded model and matches by id`() = runTest {
        val bridge = LocalProcessInferenceBridge(FakeGen("hi"), descriptor("m1"))
        assertEquals(listOf("m1"), bridge.listLoadedModelsAsync().map { it.modelId })
        assertTrue(bridge.isModelLoadedAsync("m1"))
        assertFalse(bridge.isModelLoadedAsync("other"))
    }

    @Test
    fun `complete returns Completed with token estimates`() = runTest {
        val bridge = LocalProcessInferenceBridge(FakeGen("hello there friend"), descriptor("m1"))
        val resp = bridge.completeAsync(request("m1", "prompt here", maxTokens = 500))
        assertEquals(InferenceStatus.Completed, resp.status)
        assertEquals("hello there friend", resp.outputText)
        assertTrue(resp.outputTokenCount >= 1)
        assertTrue(resp.promptTokenCount >= 1)
    }

    @Test
    fun `complete against wrong model fails`() = runTest {
        val bridge = LocalProcessInferenceBridge(FakeGen("x"), descriptor("m1"))
        val resp = bridge.completeAsync(request("nope", "p"))
        assertEquals(InferenceStatus.Failed, resp.status)
        assertTrue(resp.failureMessage!!.contains("not loaded"))
    }

    @Test
    fun `stop sequence classification wins`() = runTest {
        // Output contains "STOP" — status should be StoppedByToken.
        val bridge = LocalProcessInferenceBridge(FakeGen("go until STOP now"), descriptor("m1"))
        val resp = bridge.completeAsync(request("m1", "p", maxTokens = 500, stops = listOf("STOP")))
        assertEquals(InferenceStatus.StoppedByToken, resp.status)
    }

    @Test
    fun `length classification when output reaches max tokens`() = runTest {
        // maxTokens=1, output length/4 >= 1 -> StoppedByLength.
        val bridge = LocalProcessInferenceBridge(FakeGen("aaaaaaaa"), descriptor("m1"))
        val resp = bridge.completeAsync(request("m1", "p", maxTokens = 1))
        assertEquals(InferenceStatus.StoppedByLength, resp.status)
    }

    @Test
    fun `stream yields chunks, and falls back to full completion when empty`() = runTest {
        val streaming = LocalProcessInferenceBridge(FakeGen("a b c"), descriptor("m1"))
        assertEquals(listOf("a", "b", "c"), streaming.streamCompletionAsync(request("m1", "p")).toList())

        val empty = LocalProcessInferenceBridge(FakeGen("full-answer", streamNothing = true), descriptor("m1"))
        assertEquals(listOf("full-answer"), empty.streamCompletionAsync(request("m1", "p")).toList())
    }

    @Test
    fun `stream against wrong model yields nothing`() = runTest {
        val bridge = LocalProcessInferenceBridge(FakeGen("x"), descriptor("m1"))
        assertTrue(bridge.streamCompletionAsync(request("other", "p")).toList().isEmpty())
    }

    @Test
    fun `default fragments tag everything as content`() = runTest {
        val bridge = LocalProcessInferenceBridge(FakeGen("a b"), descriptor("m1"))
        val frags = bridge.streamFragmentsAsync(request("m1", "p")).toList()
        assertTrue(frags.all { it.kind == InferenceFragmentKind.Content })
        assertEquals(listOf("a", "b"), frags.map { it.text })
    }

    @Test
    fun `mock bridge returns canned output and fixed caps`() = runTest {
        val mock = MockInferenceBridge("canned", modelId = "mock-model")
        assertTrue(mock.isModelLoadedAsync("mock-model"))
        val resp = mock.completeAsync(request("anything", "p"))
        assertEquals("canned", resp.outputText)
        assertEquals(InferenceStatus.Completed, resp.status)
        val caps = mock.getDeviceCapabilitiesAsync()
        assertEquals("Mock", caps.osName)
        assertTrue(caps.hasTransportLayerEncryption)
    }

    @Test
    fun `request Create factory stamps id and defaults`() {
        val r = InferenceRequest.create("m1", "hello")
        assertEquals("m1", r.modelId)
        assertEquals(256, r.maxOutputTokens)
        assertTrue(r.stopSequences.isEmpty())
    }
}
