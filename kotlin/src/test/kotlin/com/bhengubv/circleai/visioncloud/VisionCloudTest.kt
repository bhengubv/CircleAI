// VisionCloudTest.kt
//
// Verifies the CircleAI.Vision.Cloud Kotlin port against the C# reference: the
// OpenAI generator's JSON body + data[].url extraction, the Stability
// generator's per-image multipart loop + inline bytes, the count clamp, the
// not-configured fail-soft path, the error-status fail-soft path, the fallback
// chain's skip-unconfigured / first-non-empty semantics, and the null generator.
// The HttpClient is replaced by a deterministic fake IImageHttpTransport.

package com.bhengubv.circleai.visioncloud

import kotlinx.coroutines.runBlocking
import org.junit.jupiter.api.Test
import kotlin.test.assertContentEquals
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertNull
import kotlin.test.assertTrue

class VisionCloudTest {

    /** One recorded outbound request for assertion. */
    private data class Recorded(
        val kind: String, // "json" | "multipart"
        val baseAddress: String,
        val path: String,
        val headers: Map<String, String>,
        val jsonBody: String? = null,
        val acceptMime: String? = null,
        val fields: List<FormField>? = null,
    )

    /**
     * Deterministic transport. Returns [jsonResponse] for postJson and pops from
     * [multipartResponses] (one per call) for postMultipart. Records every call.
     */
    private class FakeTransport(
        private val jsonResponse: ImageHttpResponse = ImageHttpResponse(200, jsonBody = "{}"),
        multipartResponses: List<ImageHttpResponse> = emptyList(),
    ) : IImageHttpTransport {
        val recorded = ArrayList<Recorded>()
        private val mp = ArrayDeque(multipartResponses)

        override suspend fun postJson(
            baseAddress: String,
            path: String,
            headers: Map<String, String>,
            jsonBody: String,
        ): ImageHttpResponse {
            recorded.add(Recorded("json", baseAddress, path, headers, jsonBody = jsonBody))
            return jsonResponse
        }

        override suspend fun postMultipart(
            baseAddress: String,
            path: String,
            headers: Map<String, String>,
            acceptMime: String,
            fields: List<FormField>,
        ): ImageHttpResponse {
            recorded.add(Recorded("multipart", baseAddress, path, headers, acceptMime = acceptMime, fields = fields))
            return if (mp.isNotEmpty()) mp.removeFirst() else ImageHttpResponse(500, errorBody = "no response queued")
        }
    }

    // ── Null generator ─────────────────────────────────────────────────

    @Test
    fun `null generator is unconfigured and returns nothing`() = runBlocking {
        val g = NullImageGenerator.Instance
        assertEquals("null", g.generatorId)
        assertFalse(g.isConfigured)
        assertTrue(g.generateAsync(ImageGenerationRequest("cat")).isEmpty())
    }

    // ── OpenAI ─────────────────────────────────────────────────────────

    @Test
    fun `openai unconfigured returns empty and never calls transport`() = runBlocking {
        val transport = FakeTransport()
        val g = OpenAiImageGenerator(transport, OpenAiImageOptions(apiKey = null))
        assertFalse(g.isConfigured)
        assertTrue(g.generateAsync(ImageGenerationRequest("cat")).isEmpty())
        assertTrue(transport.recorded.isEmpty())
    }

    @Test
    fun `openai extracts every data url and builds png artifacts`() = runBlocking {
        val json = """
            {"data":[{"url":"https://img/1.png"},{"url":"https://img/2.png"},{"revised_prompt":"x"}]}
        """.trimIndent()
        val transport = FakeTransport(ImageHttpResponse(200, jsonBody = json))
        val g = OpenAiImageGenerator(transport, OpenAiImageOptions(apiKey = "sk-test", model = "dall-e-3"))

        assertTrue(g.isConfigured)
        assertEquals("OpenAI · dall-e-3", g.displayLabel)
        assertEquals("Ready · dall-e-3", g.statusMessage)

        val artifacts = g.generateAsync(ImageGenerationRequest("a fox", count = 2, size = 1024))
        assertEquals(2, artifacts.size)
        assertEquals("https://img/1.png", artifacts[0].url)
        assertEquals("https://img/2.png", artifacts[1].url)
        assertTrue(artifacts.all { it.generatorId == "openai-images" && it.mimeType == "image/png" && it.bytes == null })
        assertTrue(artifacts.all { it.prompt == "a fox" })

        // Body shape: model / prompt / n / size / response_format.
        val rec = transport.recorded.single()
        assertEquals("json", rec.kind)
        assertEquals("/v1/images/generations", rec.path)
        assertEquals("Bearer sk-test", rec.headers["Authorization"])
        val body = rec.jsonBody!!
        assertTrue(body.contains("\"model\":\"dall-e-3\""))
        assertTrue(body.contains("\"prompt\":\"a fox\""))
        assertTrue(body.contains("\"n\":2"))
        assertTrue(body.contains("\"size\":\"1024x1024\""))
        assertTrue(body.contains("\"response_format\":\"url\""))
    }

    @Test
    fun `openai clamps count into one to four`() = runBlocking {
        val transport = FakeTransport(ImageHttpResponse(200, jsonBody = """{"data":[]}"""))
        val g = OpenAiImageGenerator(transport, OpenAiImageOptions(apiKey = "sk"))
        g.generateAsync(ImageGenerationRequest("x", count = 99))
        assertTrue(transport.recorded.single().jsonBody!!.contains("\"n\":4"))

        val t2 = FakeTransport(ImageHttpResponse(200, jsonBody = """{"data":[]}"""))
        OpenAiImageGenerator(t2, OpenAiImageOptions(apiKey = "sk")).generateAsync(ImageGenerationRequest("x", count = 0))
        assertTrue(t2.recorded.single().jsonBody!!.contains("\"n\":1"))
    }

    @Test
    fun `openai error status is fail-soft`() = runBlocking {
        val transport = FakeTransport(ImageHttpResponse(429, errorBody = "rate limited"))
        val g = OpenAiImageGenerator(transport, OpenAiImageOptions(apiKey = "sk"))
        assertTrue(g.generateAsync(ImageGenerationRequest("x")).isEmpty())
    }

    // ── Stability ──────────────────────────────────────────────────────

    @Test
    fun `stability loops per image and returns inline bytes`() = runBlocking {
        val transport = FakeTransport(
            multipartResponses = listOf(
                ImageHttpResponse(200, imageBytes = byteArrayOf(1, 2, 3)),
                ImageHttpResponse(200, imageBytes = byteArrayOf(4, 5, 6)),
            ),
        )
        val g = StabilityImageGenerator(transport, StabilityImageOptions(apiKey = "st", model = "sd3.5-large"))
        assertEquals("Stability AI · sd3.5-large", g.displayLabel)

        val artifacts = g.generateAsync(ImageGenerationRequest("a ship", count = 2))
        assertEquals(2, artifacts.size)
        assertContentEquals(byteArrayOf(1, 2, 3), artifacts[0].bytes)
        assertContentEquals(byteArrayOf(4, 5, 6), artifacts[1].bytes)
        assertTrue(artifacts.all { it.url == null && it.mimeType == "image/png" && it.generatorId == "stability" })

        // Two multipart calls, each with prompt/output_format/model, no negative.
        assertEquals(2, transport.recorded.size)
        val rec = transport.recorded[0]
        assertEquals("multipart", rec.kind)
        assertEquals("/v2beta/stable-image/generate/sd3", rec.path)
        assertEquals("image/png", rec.acceptMime)
        assertEquals("Bearer st", rec.headers["Authorization"])
        assertEquals(listOf("prompt", "output_format", "model"), rec.fields!!.map { it.name })
        assertEquals("a ship", rec.fields.first { it.name == "prompt" }.value)
    }

    @Test
    fun `stability adds a negative prompt field when present`() = runBlocking {
        val transport = FakeTransport(multipartResponses = listOf(ImageHttpResponse(200, imageBytes = byteArrayOf(9))))
        val g = StabilityImageGenerator(transport, StabilityImageOptions(apiKey = "st"))
        g.generateAsync(ImageGenerationRequest("a ship", negativePrompt = "blurry"))
        val fields = transport.recorded.single().fields!!
        assertEquals(listOf("prompt", "output_format", "model", "negative_prompt"), fields.map { it.name })
        assertEquals("blurry", fields.first { it.name == "negative_prompt" }.value)
    }

    @Test
    fun `stability skips failed images but keeps successful ones`() = runBlocking {
        val transport = FakeTransport(
            multipartResponses = listOf(
                ImageHttpResponse(500, errorBody = "boom"),
                ImageHttpResponse(200, imageBytes = byteArrayOf(7)),
            ),
        )
        val g = StabilityImageGenerator(transport, StabilityImageOptions(apiKey = "st"))
        val artifacts = g.generateAsync(ImageGenerationRequest("x", count = 2))
        assertEquals(1, artifacts.size)
        assertContentEquals(byteArrayOf(7), artifacts[0].bytes)
    }

    @Test
    fun `stability unconfigured returns empty`() = runBlocking {
        val transport = FakeTransport()
        val g = StabilityImageGenerator(transport, StabilityImageOptions(apiKey = " "))
        assertFalse(g.isConfigured)
        assertTrue(g.generateAsync(ImageGenerationRequest("x")).isEmpty())
        assertTrue(transport.recorded.isEmpty())
    }

    // ── Fallback chain ─────────────────────────────────────────────────

    @Test
    fun `fallback chain skips unconfigured and returns first non-empty`() = runBlocking {
        val openAiEmpty = OpenAiImageGenerator(
            FakeTransport(ImageHttpResponse(200, jsonBody = """{"data":[]}""")),
            OpenAiImageOptions(apiKey = "sk"),
        )
        val stability = StabilityImageGenerator(
            FakeTransport(multipartResponses = listOf(ImageHttpResponse(200, imageBytes = byteArrayOf(1)))),
            StabilityImageOptions(apiKey = "st"),
        )
        val unconfigured = OpenAiImageGenerator(FakeTransport(), OpenAiImageOptions(apiKey = null))

        val chain = ImageGeneratorFallbackChain(listOf(unconfigured, openAiEmpty, stability))
        assertEquals("fallback-chain", chain.generatorId)
        assertEquals("Fallback (3)", chain.displayLabel)
        assertTrue(chain.isConfigured)
        // openai-images is configured but yields empty; stability is next.
        assertEquals("Ready · openai-images → stability", chain.statusMessage)

        val artifacts = chain.generateAsync(ImageGenerationRequest("x"))
        assertEquals(1, artifacts.size)
        assertEquals("stability", artifacts[0].generatorId)
    }

    @Test
    fun `fallback chain returns empty when nobody is configured`() = runBlocking {
        val chain = ImageGeneratorFallbackChain(
            listOf(
                OpenAiImageGenerator(FakeTransport(), OpenAiImageOptions(apiKey = null)),
                StabilityImageGenerator(FakeTransport(), StabilityImageOptions(apiKey = null)),
            ),
        )
        assertFalse(chain.isConfigured)
        assertEquals("No configured generator in chain.", chain.statusMessage)
        assertTrue(chain.generateAsync(ImageGenerationRequest("x")).isEmpty())
    }

    @Test
    fun `fallback chain stops at first success without invoking later generators`() = runBlocking {
        val firstTransport = FakeTransport(ImageHttpResponse(200, jsonBody = """{"data":[{"url":"u"}]}"""))
        val laterTransport = FakeTransport(multipartResponses = listOf(ImageHttpResponse(200, imageBytes = byteArrayOf(1))))
        val chain = ImageGeneratorFallbackChain(
            listOf(
                OpenAiImageGenerator(firstTransport, OpenAiImageOptions(apiKey = "sk")),
                StabilityImageGenerator(laterTransport, StabilityImageOptions(apiKey = "st")),
            ),
        )
        val artifacts = chain.generateAsync(ImageGenerationRequest("x"))
        assertEquals(1, artifacts.size)
        assertEquals("openai-images", artifacts[0].generatorId)
        // Later generator was never called.
        assertTrue(laterTransport.recorded.isEmpty())
    }

    @Test
    fun `generator id constants match the wire identity`() {
        assertEquals("openai-images", VisionCloudGeneratorIds.OpenAi)
        assertEquals("stability", VisionCloudGeneratorIds.Stability)
    }

    @Test
    fun `image artifact url-vs-bytes value equality`() {
        val t = java.time.Instant.parse("2026-07-10T00:00:00Z")
        val a = ImageArtifact("g", "p", "image/png", "u", null, t)
        val b = ImageArtifact("g", "p", "image/png", "u", null, t)
        assertEquals(a, b)
        assertEquals(a.hashCode(), b.hashCode())
        val c = ImageArtifact("g", "p", "image/png", null, byteArrayOf(1), t)
        assertNull(c.url)
        assertFalse(a == c)
    }
}
