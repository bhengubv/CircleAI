// SpeechCloudProviders.kt
//
// Kotlin port of the CircleAI.Speech.Cloud provider recognizers + synthesizers —
// the C# reference files are the EXACT spec (OpenAi/Deepgram/AssemblyAI/Google/
// Azure/Cartesia recognizers; OpenAi/ElevenLabs/Cartesia/Deepgram/Google/Azure/
// PlayHt synthesizers) plus Options.cs. The base ASR/TTS contracts + result
// types (ISpeechRecognizer, ISpeechSynthesizer, TranscriptionResult,
// SynthesisResult, TranscribedSegment) already live in
// com.bhengubv.circleai.speech.Speech; these providers implement them.
//
// C# -> Kotlin conventions:
//   HttpClient                -> injected [ISpeechHttpTransport] seam (mirrors the
//                                VisionCloud IImageHttpTransport pattern) so the
//                                providers are deterministic-testable; a host wires
//                                a real transport, tests supply a fake.
//   ReadOnlyMemory<byte>      -> ByteArray
//   ValueTask<T>              -> suspend fun
//   System.Text.Json          -> kotlinx.serialization.json (parse) + string builders
//                                (request bodies, matching the C# anonymous objects)
//   TimeSpan / DateTimeOffset -> java.time.Duration / Instant
//   Convert.ToBase64String    -> java.util.Base64
//
// The WAV-envelope builder, multipart field ordering, base64/JSON body shapes,
// PCM-rate parsing, WAV-header stripping, and the fail-soft "empty result when
// unconfigured or non-2xx" behaviour are ported byte/shape-for-shape so audio and
// requests produced here match the C# reference.

package com.bhengubv.circleai.speechcloud

import com.bhengubv.circleai.speech.ISpeechRecognizer
import com.bhengubv.circleai.speech.ISpeechSynthesizer
import com.bhengubv.circleai.speech.SynthesisResult
import com.bhengubv.circleai.speech.TranscribedSegment
import com.bhengubv.circleai.speech.TranscriptionResult
import kotlinx.coroutines.delay
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonArray
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.contentOrNull
import kotlinx.serialization.json.doubleOrNull
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import kotlinx.serialization.json.longOrNull
import java.net.URLEncoder
import java.time.Duration
import java.util.Base64

// =====================================================================
// HTTP transport seam
// =====================================================================

/**
 * One speech HTTP response. [statusCode] is the HTTP status; on success text
 * endpoints read [bodyString] / [bodyBytes] (audio endpoints return raw PCM).
 * Mirrors the subset of `HttpResponseMessage` the C# providers consume.
 */
class SpeechHttpResponse(
    val statusCode: Int,
    val bodyBytes: ByteArray = ByteArray(0),
) {
    val isSuccess: Boolean get() = statusCode in 200..299
    val bodyString: String get() = String(bodyBytes, Charsets.UTF_8)
}

/** A single multipart form part. Either [textValue] OR a file ([fileBytes]+[fileName]+[contentType]). */
class SpeechFormPart private constructor(
    val name: String,
    val textValue: String?,
    val fileBytes: ByteArray?,
    val fileName: String?,
    val contentType: String?,
) {
    companion object {
        fun text(name: String, value: String) = SpeechFormPart(name, value, null, null, null)
        fun file(name: String, bytes: ByteArray, fileName: String, contentType: String) =
            SpeechFormPart(name, null, bytes, fileName, contentType)
    }
}

/**
 * The seam the cloud speech providers post through — the speech analogue of
 * VisionCloud's [com.bhengubv.circleai.visioncloud.IImageHttpTransport]. The C#
 * providers use a real HttpClient; the Kotlin port injects this so hosts wire a
 * real transport while tests supply a deterministic fake. [baseAddress] +
 * [path]-relative requests mirror the C# `HttpClient.BaseAddress` + relative-URI
 * pattern.
 */
interface ISpeechHttpTransport {
    /** POST a JSON body to [path] on [baseAddress]; returns the raw response. */
    suspend fun postJson(
        baseAddress: String,
        path: String,
        headers: Map<String, String>,
        jsonBody: String,
    ): SpeechHttpResponse

    /** POST raw [body] bytes with an explicit [contentType]. */
    suspend fun postBytes(
        baseAddress: String,
        path: String,
        headers: Map<String, String>,
        contentType: String,
        body: ByteArray,
    ): SpeechHttpResponse

    /** POST a multipart/form-data body ([parts] in order) to [path]. */
    suspend fun postMultipart(
        baseAddress: String,
        path: String,
        headers: Map<String, String>,
        parts: List<SpeechFormPart>,
    ): SpeechHttpResponse

    /** GET [path] on [baseAddress] (AssemblyAI transcript polling). */
    suspend fun get(
        baseAddress: String,
        path: String,
        headers: Map<String, String>,
    ): SpeechHttpResponse
}

// =====================================================================
// Shared helpers
// =====================================================================

private val SPEECH_JSON = Json { ignoreUnknownKeys = true; isLenient = true }

private fun urlEncode(s: String): String = URLEncoder.encode(s, "UTF-8")

private fun emptyTranscription(): TranscriptionResult =
    TranscriptionResult("", null, emptyList(), Duration.ZERO)

private fun emptySynthesis(): SynthesisResult =
    SynthesisResult(ByteArray(0), 0, Duration.ZERO)

private fun secondsToDuration(seconds: Double): Duration =
    Duration.ofNanos(Math.round(seconds * 1_000_000_000.0))

/** 44-byte WAV header for 16-bit mono PCM. Mirrors the C# `WrapPcmAsWav`. */
internal fun wrapPcmAsWav(pcm: ByteArray, sampleRate: Int): ByteArray {
    val channels = 1
    val bitsPerSample = 16
    val byteRate = sampleRate * channels * (bitsPerSample / 8)
    val blockAlign = channels * (bitsPerSample / 8)
    val dataSize = pcm.size
    val chunkSize = 36 + dataSize

    val buffer = ByteArray(44 + dataSize)
    fun putInt(off: Int, v: Int) {
        buffer[off] = (v and 0xFF).toByte()
        buffer[off + 1] = ((v ushr 8) and 0xFF).toByte()
        buffer[off + 2] = ((v ushr 16) and 0xFF).toByte()
        buffer[off + 3] = ((v ushr 24) and 0xFF).toByte()
    }
    fun putShort(off: Int, v: Int) {
        buffer[off] = (v and 0xFF).toByte()
        buffer[off + 1] = ((v ushr 8) and 0xFF).toByte()
    }
    buffer[0] = 'R'.code.toByte(); buffer[1] = 'I'.code.toByte(); buffer[2] = 'F'.code.toByte(); buffer[3] = 'F'.code.toByte()
    putInt(4, chunkSize)
    buffer[8] = 'W'.code.toByte(); buffer[9] = 'A'.code.toByte(); buffer[10] = 'V'.code.toByte(); buffer[11] = 'E'.code.toByte()
    buffer[12] = 'f'.code.toByte(); buffer[13] = 'm'.code.toByte(); buffer[14] = 't'.code.toByte(); buffer[15] = ' '.code.toByte()
    putInt(16, 16)          // Subchunk1Size
    putShort(20, 1)         // PCM = 1
    putShort(22, channels)
    putInt(24, sampleRate)
    putInt(28, byteRate)
    putShort(32, blockAlign)
    putShort(34, bitsPerSample)
    buffer[36] = 'd'.code.toByte(); buffer[37] = 'a'.code.toByte(); buffer[38] = 't'.code.toByte(); buffer[39] = 'a'.code.toByte()
    putInt(40, dataSize)
    pcm.copyInto(buffer, 44)
    return buffer
}

/** JSON-encode a string as a quoted literal (mirror `JsonSerializer.Serialize(string)`). */
private fun jsonQuote(s: String): String = JsonPrimitive(s).toString()

// =====================================================================
// Options (Options.cs)
// =====================================================================

/** (3.2.0) OpenAI Whisper + TTS options. Mirrors C# `OpenAiVoiceOptions`. */
data class OpenAiVoiceOptions(
    val baseAddress: String = "https://api.openai.com",
    val apiKey: String? = null,
    val transcriptionModel: String = "whisper-1",
    val speechModel: String = "tts-1",
    val defaultVoice: String = "alloy",
    val pcmSampleRateHz: Int = 24_000,
)

/** (3.3.0) Deepgram STT options. Mirrors C# `DeepgramOptions`. */
data class DeepgramOptions(
    val baseAddress: String = "https://api.deepgram.com",
    val apiKey: String? = null,
    val model: String = "nova-2-general",
)

/** (3.3.0) AssemblyAI STT options. Mirrors C# `AssemblyAiOptions`. */
data class AssemblyAiOptions(
    val baseAddress: String = "https://api.assemblyai.com",
    val apiKey: String? = null,
    val speechModel: String = "universal",
)

/** (3.3.0) Google Cloud Speech-to-Text options. Mirrors C# `GoogleSpeechOptions`. */
data class GoogleSpeechOptions(
    val baseAddress: String = "https://speech.googleapis.com",
    val apiKey: String? = null,
    val languageCode: String = "en-US",
)

/** (3.3.0) Microsoft Azure Speech-to-Text options. Mirrors C# `AzureSpeechOptions`. */
data class AzureSpeechOptions(
    val baseAddress: String? = null,
    val apiKey: String? = null,
    val languageCode: String = "en-US",
)

/** (3.3.0) ElevenLabs TTS options. Mirrors C# `ElevenLabsOptions`. */
data class ElevenLabsOptions(
    val baseAddress: String = "https://api.elevenlabs.io",
    val apiKey: String? = null,
    val defaultVoiceId: String = "21m00Tcm4TlvDq8ikWAM",
    val model: String = "eleven_flash_v2_5",
    val outputFormat: String = "pcm_24000",
    val pcmSampleRateHz: Int = 24_000,
)

/** (3.3.0) Cartesia Sonic TTS options. Mirrors C# `CartesiaTtsOptions`. */
data class CartesiaTtsOptions(
    val baseAddress: String = "https://api.cartesia.ai",
    val apiKey: String? = null,
    val model: String = "sonic-2",
    val defaultVoiceId: String = "a0e99841-438c-4a64-b679-ae501e7d6091",
    val outputContainer: String = "raw",
    val outputEncoding: String = "pcm_s16le",
    val pcmSampleRateHz: Int = 24_000,
    val cartesiaVersion: String = "2025-04-16",
)

/** (3.3.0) Deepgram Aura TTS options. Mirrors C# `DeepgramTtsOptions`. */
data class DeepgramTtsOptions(
    val baseAddress: String = "https://api.deepgram.com",
    val apiKey: String? = null,
    val voice: String = "aura-asteria-en",
    val pcmSampleRateHz: Int = 24_000,
)

/** (3.3.0) Microsoft Azure Speech TTS options. Mirrors C# `AzureTtsOptions`. */
data class AzureTtsOptions(
    val baseAddress: String? = null,
    val apiKey: String? = null,
    val languageCode: String = "en-US",
    val defaultVoiceName: String = "en-US-AvaMultilingualNeural",
    val pcmSampleRateHz: Int = 24_000,
)

/** (3.3.0) Google Cloud Text-to-Speech options. Mirrors C# `GoogleTtsOptions`. */
data class GoogleTtsOptions(
    val baseAddress: String = "https://texttospeech.googleapis.com",
    val apiKey: String? = null,
    val languageCode: String = "en-US",
    val defaultVoiceName: String = "en-US-Studio-O",
    val pcmSampleRateHz: Int = 24_000,
)

/** (3.3.0) PlayHT TTS options. Mirrors C# `PlayHtOptions`. */
data class PlayHtOptions(
    val baseAddress: String = "https://api.play.ht",
    val apiKey: String? = null,
    val userId: String? = null,
    val defaultVoice: String = "s3://voice-cloning-zero-shot/d9ff78ba-d016-47f6-b0ef-dd630f59414e/female-cs/manifest.json",
    val model: String = "PlayDialog",
    val pcmSampleRateHz: Int = 24_000,
)

/** (3.3.0) Cartesia STT options. Mirrors C# `CartesiaSttOptions`. */
data class CartesiaSttOptions(
    val baseAddress: String = "https://api.cartesia.ai",
    val apiKey: String? = null,
    val model: String = "ink-whisper",
    val cartesiaVersion: String = "2025-04-16",
)

// =====================================================================
// Recognizers
// =====================================================================

/** (3.2.0) [ISpeechRecognizer] backed by OpenAI Whisper. Mirrors C# `OpenAiSpeechRecognizer`. */
class OpenAiSpeechRecognizer(
    private val http: ISpeechHttpTransport,
    private val options: OpenAiVoiceOptions,
) : ISpeechRecognizer {

    override val backendId: String get() = "openai-whisper"
    val isConfigured: Boolean get() = !options.apiKey.isNullOrBlank()

    override suspend fun transcribeAsync(audioPcm16Mono: ByteArray, sampleRateHz: Int, languageHint: String?): TranscriptionResult {
        if (!isConfigured) return emptyTranscription()

        val wav = wrapPcmAsWav(audioPcm16Mono, sampleRateHz)
        val parts = ArrayList<SpeechFormPart>()
        parts.add(SpeechFormPart.file("file", wav, "audio.wav", "audio/wav"))
        parts.add(SpeechFormPart.text("model", options.transcriptionModel))
        parts.add(SpeechFormPart.text("response_format", "verbose_json"))
        if (!languageHint.isNullOrBlank()) parts.add(SpeechFormPart.text("language", languageHint))

        val resp = http.postMultipart(
            options.baseAddress,
            "/v1/audio/transcriptions",
            mapOf("Authorization" to "Bearer ${options.apiKey}"),
            parts,
        )
        if (!resp.isSuccess) return emptyTranscription()

        val root = parseObject(resp.bodyString) ?: return emptyTranscription()
        val text = root["text"]?.jsonPrimitive?.contentOrNull ?: ""
        val language = root["language"]?.jsonPrimitive?.contentOrNull
        val duration = (root["duration"]?.jsonPrimitive?.doubleOrNull)?.let { secondsToDuration(it) } ?: Duration.ZERO

        val segments = ArrayList<TranscribedSegment>()
        (root["segments"] as? JsonArray)?.forEach { s ->
            val so = s.jsonObject
            val segText = so["text"]?.jsonPrimitive?.contentOrNull ?: ""
            val segStart = so["start"]?.jsonPrimitive?.doubleOrNull ?: 0.0
            val segEnd = so["end"]?.jsonPrimitive?.doubleOrNull ?: segStart
            segments.add(
                TranscribedSegment(
                    text = segText,
                    offset = secondsToDuration(segStart),
                    duration = secondsToDuration(maxOf(0.0, segEnd - segStart)),
                    language = language,
                    confidence = 0f,
                ),
            )
        }
        return TranscriptionResult(text, language, segments, duration)
    }
}

/** (3.3.0) Deepgram-backed [ISpeechRecognizer]. Mirrors C# `DeepgramSpeechRecognizer`. */
class DeepgramSpeechRecognizer(
    private val http: ISpeechHttpTransport,
    private val options: DeepgramOptions,
) : ISpeechRecognizer {

    override val backendId: String get() = "deepgram"
    val isConfigured: Boolean get() = !options.apiKey.isNullOrBlank()

    override suspend fun transcribeAsync(audioPcm16Mono: ByteArray, sampleRateHz: Int, languageHint: String?): TranscriptionResult {
        if (!isConfigured) return emptyTranscription()

        var path = "/v1/listen?model=${urlEncode(options.model)}&encoding=linear16&sample_rate=$sampleRateHz&channels=1&punctuate=true"
        if (!languageHint.isNullOrBlank()) path += "&language=${urlEncode(languageHint)}"

        val resp = http.postBytes(
            options.baseAddress,
            path,
            mapOf("Authorization" to "Token ${options.apiKey}"),
            "audio/raw",
            audioPcm16Mono,
        )
        if (!resp.isSuccess) return emptyTranscription()

        val root = parseObject(resp.bodyString) ?: return emptyTranscription()
        val results = root["results"]?.jsonObject ?: return emptyTranscription()
        val channels = results["channels"] as? JsonArray ?: return emptyTranscription()
        if (channels.isEmpty()) return emptyTranscription()
        val alts = channels[0].jsonObject["alternatives"] as? JsonArray ?: return emptyTranscription()
        if (alts.isEmpty()) return emptyTranscription()
        val firstAlt = alts[0].jsonObject

        val text = firstAlt["transcript"]?.jsonPrimitive?.contentOrNull ?: ""

        val segments = ArrayList<TranscribedSegment>()
        (firstAlt["words"] as? JsonArray)?.forEach { w ->
            val wo = w.jsonObject
            val start = wo["start"]?.jsonPrimitive?.doubleOrNull ?: 0.0
            val end = wo["end"]?.jsonPrimitive?.doubleOrNull ?: start
            segments.add(
                TranscribedSegment(
                    text = wo["word"]?.jsonPrimitive?.contentOrNull ?: "",
                    offset = secondsToDuration(start),
                    duration = secondsToDuration(end - start),
                    language = languageHint,
                    confidence = (wo["confidence"]?.jsonPrimitive?.doubleOrNull ?: 0.0).toFloat(),
                ),
            )
        }

        val duration = (root["metadata"]?.jsonObject?.get("duration")?.jsonPrimitive?.doubleOrNull)
            ?.let { secondsToDuration(it) } ?: Duration.ZERO
        return TranscriptionResult(text, languageHint, segments, duration)
    }
}

/** (3.3.0) AssemblyAI-backed [ISpeechRecognizer]. Mirrors C# `AssemblyAiSpeechRecognizer`. */
class AssemblyAiSpeechRecognizer(
    private val http: ISpeechHttpTransport,
    private val options: AssemblyAiOptions,
) : ISpeechRecognizer {

    override val backendId: String get() = "assemblyai"
    val isConfigured: Boolean get() = !options.apiKey.isNullOrBlank()

    override suspend fun transcribeAsync(audioPcm16Mono: ByteArray, sampleRateHz: Int, languageHint: String?): TranscriptionResult {
        if (!isConfigured) return emptyTranscription()
        val auth = mapOf("Authorization" to options.apiKey!!)

        // 1) Upload audio.
        val wav = wrapPcmAsWav(audioPcm16Mono, sampleRateHz)
        val uploadResp = http.postBytes(options.baseAddress, "/v2/upload", auth, "application/octet-stream", wav)
        if (!uploadResp.isSuccess) return emptyTranscription()
        val uploadUrl = parseObject(uploadResp.bodyString)?.get("upload_url")?.jsonPrimitive?.contentOrNull
        if (uploadUrl.isNullOrBlank()) return emptyTranscription()

        // 2) Submit transcript job.
        val body = StringBuilder("{")
        body.append("\"audio_url\":${jsonQuote(uploadUrl)},")
        body.append("\"speech_model\":${jsonQuote(options.speechModel)}")
        if (!languageHint.isNullOrBlank()) body.append(",\"language_code\":${jsonQuote(languageHint)}")
        body.append('}')

        val submitResp = http.postJson(options.baseAddress, "/v2/transcript", auth, body.toString())
        if (!submitResp.isSuccess) return emptyTranscription()
        val transcriptId = parseObject(submitResp.bodyString)?.get("id")?.jsonPrimitive?.contentOrNull
        if (transcriptId.isNullOrBlank()) return emptyTranscription()

        // 3) Poll until completed (max 60 attempts of 500 ms = 30 s).
        repeat(60) {
            delay(500)
            val pollResp = http.get(options.baseAddress, "/v2/transcript/$transcriptId", auth)
            if (!pollResp.isSuccess) return@repeat

            val poll = parseObject(pollResp.bodyString) ?: return@repeat
            when (poll["status"]?.jsonPrimitive?.contentOrNull) {
                "completed" -> {
                    val text = poll["text"]?.jsonPrimitive?.contentOrNull ?: ""
                    val lang = poll["language_code"]?.jsonPrimitive?.contentOrNull ?: languageHint
                    val duration = (poll["audio_duration"]?.jsonPrimitive?.doubleOrNull)
                        ?.let { secondsToDuration(it) } ?: Duration.ZERO

                    val segments = ArrayList<TranscribedSegment>()
                    (poll["words"] as? JsonArray)?.forEach { w ->
                        val wo = w.jsonObject
                        val start = (wo["start"]?.jsonPrimitive?.doubleOrNull ?: 0.0) / 1000.0
                        val end = (wo["end"]?.jsonPrimitive?.doubleOrNull)?.div(1000.0) ?: start
                        segments.add(
                            TranscribedSegment(
                                text = wo["text"]?.jsonPrimitive?.contentOrNull ?: "",
                                offset = secondsToDuration(start),
                                duration = secondsToDuration(maxOf(0.0, end - start)),
                                language = lang,
                                confidence = (wo["confidence"]?.jsonPrimitive?.doubleOrNull ?: 0.0).toFloat(),
                            ),
                        )
                    }
                    return TranscriptionResult(text, lang, segments, duration)
                }
                "error" -> return emptyTranscription()
            }
        }
        return emptyTranscription()
    }
}

/** (3.3.0) Google-backed [ISpeechRecognizer]. Mirrors C# `GoogleSpeechRecognizer`. */
class GoogleSpeechRecognizer(
    private val http: ISpeechHttpTransport,
    private val options: GoogleSpeechOptions,
) : ISpeechRecognizer {

    override val backendId: String get() = "google-stt"
    val isConfigured: Boolean get() = !options.apiKey.isNullOrBlank()

    override suspend fun transcribeAsync(audioPcm16Mono: ByteArray, sampleRateHz: Int, languageHint: String?): TranscriptionResult {
        if (!isConfigured) return emptyTranscription()

        val lang = if (languageHint.isNullOrBlank()) options.languageCode else languageHint
        val audioB64 = Base64.getEncoder().encodeToString(audioPcm16Mono)

        val body = """
            {
              "config": {
                "encoding": "LINEAR16",
                "sampleRateHertz": $sampleRateHz,
                "languageCode": "$lang",
                "enableWordTimeOffsets": true,
                "enableWordConfidence": true
              },
              "audio": { "content": "$audioB64" }
            }
        """.trimIndent()

        val path = "/v1/speech:recognize?key=${urlEncode(options.apiKey!!)}"
        val resp = http.postJson(options.baseAddress, path, emptyMap(), body)
        if (!resp.isSuccess) return emptyTranscription()

        val root = parseObject(resp.bodyString) ?: return emptyTranscription()
        val allText = StringBuilder()
        val segments = ArrayList<TranscribedSegment>()
        (root["results"] as? JsonArray)?.forEach { r ->
            val alts = r.jsonObject["alternatives"] as? JsonArray ?: return@forEach
            if (alts.isEmpty()) return@forEach
            val alt = alts[0].jsonObject
            if (allText.isNotEmpty()) allText.append(' ')
            allText.append(alt["transcript"]?.jsonPrimitive?.contentOrNull ?: "")

            (alt["words"] as? JsonArray)?.forEach { w ->
                val wo = w.jsonObject
                val start = parseGoogleSeconds(wo, "startTime")
                val end = parseGoogleSeconds(wo, "endTime")
                segments.add(
                    TranscribedSegment(
                        text = wo["word"]?.jsonPrimitive?.contentOrNull ?: "",
                        offset = secondsToDuration(start),
                        duration = secondsToDuration(maxOf(0.0, end - start)),
                        language = lang,
                        confidence = (wo["confidence"]?.jsonPrimitive?.doubleOrNull ?: 0.0).toFloat(),
                    ),
                )
            }
        }
        return TranscriptionResult(allText.toString(), lang, segments, Duration.ZERO)
    }

    private fun parseGoogleSeconds(el: JsonObject, property: String): Double {
        // Google encodes durations as e.g. "1.500s".
        var s = el[property]?.jsonPrimitive?.contentOrNull ?: return 0.0
        if (s.isBlank()) return 0.0
        if (s.endsWith("s")) s = s.dropLast(1)
        return s.toDoubleOrNull() ?: 0.0
    }
}

/** (3.3.0) Azure-backed [ISpeechRecognizer]. Mirrors C# `AzureSpeechRecognizer`. */
class AzureSpeechRecognizer(
    private val http: ISpeechHttpTransport,
    private val options: AzureSpeechOptions,
) : ISpeechRecognizer {

    override val backendId: String get() = "azure-stt"
    val isConfigured: Boolean get() = !options.apiKey.isNullOrBlank() && options.baseAddress != null

    override suspend fun transcribeAsync(audioPcm16Mono: ByteArray, sampleRateHz: Int, languageHint: String?): TranscriptionResult {
        if (!isConfigured) return emptyTranscription()

        val lang = if (languageHint.isNullOrBlank()) options.languageCode else languageHint
        val path = "/speech/recognition/conversation/cognitiveservices/v1?language=${urlEncode(lang)}&format=detailed"

        val resp = http.postBytes(
            options.baseAddress!!,
            path,
            mapOf(
                "Ocp-Apim-Subscription-Key" to options.apiKey!!,
                "Accept" to "application/json",
            ),
            "audio/wav; codecs=audio/pcm; samplerate=$sampleRateHz",
            audioPcm16Mono,
        )
        if (!resp.isSuccess) return emptyTranscription()

        val root = parseObject(resp.bodyString) ?: return emptyTranscription()
        if (root["RecognitionStatus"]?.jsonPrimitive?.contentOrNull != "Success") return emptyTranscription()

        val text = root["DisplayText"]?.jsonPrimitive?.contentOrNull ?: ""

        // Azure returns offsets/durations in 100-nanosecond ticks (HNS).
        val offsetTicks = root["Offset"]?.jsonPrimitive?.longOrNull ?: 0L
        val durationTicks = root["Duration"]?.jsonPrimitive?.longOrNull ?: 0L
        val duration = Duration.ofNanos(durationTicks * 100)

        val confidence = (root["NBest"] as? JsonArray)
            ?.takeIf { it.isNotEmpty() }
            ?.get(0)?.jsonObject?.get("Confidence")?.jsonPrimitive?.doubleOrNull
            ?.toFloat() ?: 0f

        val segment = TranscribedSegment(
            text = text,
            offset = Duration.ofNanos(offsetTicks * 100),
            duration = duration,
            language = lang,
            confidence = confidence,
        )
        return TranscriptionResult(text, lang, listOf(segment), duration)
    }
}

/** (3.3.0) Cartesia-backed [ISpeechRecognizer]. Mirrors C# `CartesiaSpeechRecognizer`. */
class CartesiaSpeechRecognizer(
    private val http: ISpeechHttpTransport,
    private val options: CartesiaSttOptions,
) : ISpeechRecognizer {

    override val backendId: String get() = "cartesia-stt"
    val isConfigured: Boolean get() = !options.apiKey.isNullOrBlank()

    override suspend fun transcribeAsync(audioPcm16Mono: ByteArray, sampleRateHz: Int, languageHint: String?): TranscriptionResult {
        if (!isConfigured) return emptyTranscription()

        val wav = wrapPcmAsWav(audioPcm16Mono, sampleRateHz)
        val parts = ArrayList<SpeechFormPart>()
        parts.add(SpeechFormPart.file("file", wav, "audio.wav", "audio/wav"))
        parts.add(SpeechFormPart.text("model", options.model))
        if (!languageHint.isNullOrBlank()) parts.add(SpeechFormPart.text("language", languageHint))

        val resp = http.postMultipart(
            options.baseAddress,
            "/v1/transcribe",
            mapOf(
                "Authorization" to "Bearer ${options.apiKey}",
                "Cartesia-Version" to options.cartesiaVersion,
            ),
            parts,
        )
        if (!resp.isSuccess) return emptyTranscription()

        val root = parseObject(resp.bodyString) ?: return emptyTranscription()
        val text = root["text"]?.jsonPrimitive?.contentOrNull ?: ""
        val lang = root["language"]?.jsonPrimitive?.contentOrNull ?: languageHint
        val duration = (root["duration"]?.jsonPrimitive?.doubleOrNull)?.let { secondsToDuration(it) } ?: Duration.ZERO
        return TranscriptionResult(text, lang, emptyList(), duration)
    }
}

// =====================================================================
// Synthesizers
// =====================================================================

/** (3.2.0) [ISpeechSynthesizer] backed by OpenAI TTS. Mirrors C# `OpenAiSpeechSynthesizer`. */
class OpenAiSpeechSynthesizer(
    private val http: ISpeechHttpTransport,
    private val options: OpenAiVoiceOptions,
) : ISpeechSynthesizer {

    override val backendId: String get() = "openai-tts"
    val isConfigured: Boolean get() = !options.apiKey.isNullOrBlank()

    override suspend fun synthesizeAsync(text: String, voiceId: String?, languageHint: String?): SynthesisResult {
        if (!isConfigured) return emptySynthesis()

        val resolvedVoice = if (voiceId.isNullOrBlank()) options.defaultVoice else voiceId
        val body = buildJson(
            "model" to JsonPrimitive(options.speechModel),
            "input" to JsonPrimitive(text),
            "voice" to JsonPrimitive(resolvedVoice),
            "response_format" to JsonPrimitive("pcm"),
        )

        val resp = http.postJson(
            options.baseAddress,
            "/v1/audio/speech",
            mapOf("Authorization" to "Bearer ${options.apiKey}"),
            body,
        )
        if (!resp.isSuccess) return emptySynthesis()
        return pcmResult(resp.bodyBytes, options.pcmSampleRateHz)
    }
}

/** (3.3.0) ElevenLabs-backed [ISpeechSynthesizer]. Mirrors C# `ElevenLabsSpeechSynthesizer`. */
class ElevenLabsSpeechSynthesizer(
    private val http: ISpeechHttpTransport,
    private val options: ElevenLabsOptions,
) : ISpeechSynthesizer {

    override val backendId: String get() = "elevenlabs"
    val isConfigured: Boolean get() = !options.apiKey.isNullOrBlank()

    override suspend fun synthesizeAsync(text: String, voiceId: String?, languageHint: String?): SynthesisResult {
        if (!isConfigured) return emptySynthesis()

        val voice = if (voiceId.isNullOrBlank()) options.defaultVoiceId else voiceId
        val rate = parsePcmRate(options.outputFormat, options.pcmSampleRateHz)

        val body = buildJson(
            "text" to JsonPrimitive(text),
            "model_id" to JsonPrimitive(options.model),
        )
        val resp = http.postJson(
            options.baseAddress,
            "/v1/text-to-speech/${urlEncode(voice)}?output_format=${options.outputFormat}",
            mapOf("xi-api-key" to options.apiKey!!),
            body,
        )
        if (!resp.isSuccess) return emptySynthesis()
        return pcmResult(resp.bodyBytes, rate)
    }

    private fun parsePcmRate(outputFormat: String, fallback: Int): Int {
        // Format: pcm_22050 / pcm_24000 / pcm_44100 / pcm_16000
        val m = Regex("""pcm_(\d+)""").find(outputFormat) ?: return fallback
        return m.groupValues[1].toIntOrNull() ?: fallback
    }
}

/** (3.3.0) Cartesia Sonic-backed [ISpeechSynthesizer]. Mirrors C# `CartesiaSpeechSynthesizer`. */
class CartesiaSpeechSynthesizer(
    private val http: ISpeechHttpTransport,
    private val options: CartesiaTtsOptions,
) : ISpeechSynthesizer {

    override val backendId: String get() = "cartesia-tts"
    val isConfigured: Boolean get() = !options.apiKey.isNullOrBlank()

    override suspend fun synthesizeAsync(text: String, voiceId: String?, languageHint: String?): SynthesisResult {
        if (!isConfigured) return emptySynthesis()

        val voice = if (voiceId.isNullOrBlank()) options.defaultVoiceId else voiceId
        val body = buildJson(
            "model_id" to JsonPrimitive(options.model),
            "transcript" to JsonPrimitive(text),
            "voice" to buildJsonObject(
                "mode" to JsonPrimitive("id"),
                "id" to JsonPrimitive(voice),
            ),
            "output_format" to buildJsonObject(
                "container" to JsonPrimitive(options.outputContainer),
                "encoding" to JsonPrimitive(options.outputEncoding),
                "sample_rate" to JsonPrimitive(options.pcmSampleRateHz),
            ),
            "language" to JsonPrimitive(languageHint ?: "en"),
        )
        val resp = http.postJson(
            options.baseAddress,
            "/v1/tts/bytes",
            mapOf(
                "Authorization" to "Bearer ${options.apiKey}",
                "Cartesia-Version" to options.cartesiaVersion,
            ),
            body,
        )
        if (!resp.isSuccess) return emptySynthesis()
        return pcmResult(resp.bodyBytes, options.pcmSampleRateHz)
    }
}

/** (3.3.0) Deepgram Aura-backed [ISpeechSynthesizer]. Mirrors C# `DeepgramSpeechSynthesizer`. */
class DeepgramSpeechSynthesizer(
    private val http: ISpeechHttpTransport,
    private val options: DeepgramTtsOptions,
) : ISpeechSynthesizer {

    override val backendId: String get() = "deepgram-aura"
    val isConfigured: Boolean get() = !options.apiKey.isNullOrBlank()

    override suspend fun synthesizeAsync(text: String, voiceId: String?, languageHint: String?): SynthesisResult {
        if (!isConfigured) return emptySynthesis()

        val voice = if (voiceId.isNullOrBlank()) options.voice else voiceId
        val path = "/v1/speak?model=${urlEncode(voice)}&encoding=linear16&sample_rate=${options.pcmSampleRateHz}"

        val resp = http.postJson(
            options.baseAddress,
            path,
            mapOf("Authorization" to "Token ${options.apiKey}"),
            buildJson("text" to JsonPrimitive(text)),
        )
        if (!resp.isSuccess) return emptySynthesis()
        return pcmResult(resp.bodyBytes, options.pcmSampleRateHz)
    }
}

/** (3.3.0) Google-backed [ISpeechSynthesizer]. Mirrors C# `GoogleSpeechSynthesizer`. */
class GoogleSpeechSynthesizer(
    private val http: ISpeechHttpTransport,
    private val options: GoogleTtsOptions,
) : ISpeechSynthesizer {

    override val backendId: String get() = "google-tts"
    val isConfigured: Boolean get() = !options.apiKey.isNullOrBlank()

    override suspend fun synthesizeAsync(text: String, voiceId: String?, languageHint: String?): SynthesisResult {
        if (!isConfigured) return emptySynthesis()

        val voice = if (voiceId.isNullOrBlank()) options.defaultVoiceName else voiceId
        val lang = if (languageHint.isNullOrBlank()) options.languageCode else languageHint

        val body = """
            {
              "input": { "text": ${jsonQuote(text)} },
              "voice": {
                "languageCode": "$lang",
                "name": "$voice"
              },
              "audioConfig": {
                "audioEncoding": "LINEAR16",
                "sampleRateHertz": ${options.pcmSampleRateHz}
              }
            }
        """.trimIndent()

        val path = "/v1/text:synthesize?key=${urlEncode(options.apiKey!!)}"
        val resp = http.postJson(options.baseAddress, path, emptyMap(), body)
        if (!resp.isSuccess) return emptySynthesis()

        val ac = parseObject(resp.bodyString)?.get("audioContent")?.jsonPrimitive?.contentOrNull
        if (ac.isNullOrEmpty()) return emptySynthesis()

        val bytes = Base64.getDecoder().decode(ac)
        // Google returns a WAV envelope — strip it.
        val pcm = stripWavHeader(bytes)
        return pcmResult(pcm, options.pcmSampleRateHz)
    }

    /** Strip a 44-byte WAV header if present. Mirrors C# `StripWavHeader`. */
    private fun stripWavHeader(data: ByteArray): ByteArray {
        if (data.size > 44 &&
            data[0] == 'R'.code.toByte() && data[1] == 'I'.code.toByte() &&
            data[2] == 'F'.code.toByte() && data[3] == 'F'.code.toByte()
        ) {
            return data.copyOfRange(44, data.size)
        }
        return data
    }
}

/** (3.3.0) Azure-backed [ISpeechSynthesizer]. Mirrors C# `AzureSpeechSynthesizer`. */
class AzureSpeechSynthesizer(
    private val http: ISpeechHttpTransport,
    private val options: AzureTtsOptions,
) : ISpeechSynthesizer {

    override val backendId: String get() = "azure-tts"
    val isConfigured: Boolean get() = !options.apiKey.isNullOrBlank() && options.baseAddress != null

    override suspend fun synthesizeAsync(text: String, voiceId: String?, languageHint: String?): SynthesisResult {
        if (!isConfigured) return emptySynthesis()

        val voice = if (voiceId.isNullOrBlank()) options.defaultVoiceName else voiceId
        val lang = if (languageHint.isNullOrBlank()) options.languageCode else languageHint
        val rate = options.pcmSampleRateHz

        val ssml = """
            <speak version='1.0' xml:lang='$lang'>
              <voice name='$voice'>${htmlEncode(text)}</voice>
            </speak>
        """.trimIndent()

        val resp = http.postBytes(
            options.baseAddress!!,
            "/cognitiveservices/v1",
            mapOf(
                "Ocp-Apim-Subscription-Key" to options.apiKey!!,
                "X-Microsoft-OutputFormat" to "raw-${rate / 1000}khz-16bit-mono-pcm",
                "User-Agent" to "CircleAI",
            ),
            "application/ssml+xml",
            ssml.toByteArray(Charsets.UTF_8),
        )
        if (!resp.isSuccess) return emptySynthesis()
        return pcmResult(resp.bodyBytes, rate)
    }

    /** Minimal XML/HTML text escaping (mirror `WebUtility.HtmlEncode` for SSML content). */
    private fun htmlEncode(s: String): String = buildString {
        for (c in s) {
            when (c) {
                '&' -> append("&amp;")
                '<' -> append("&lt;")
                '>' -> append("&gt;")
                '"' -> append("&quot;")
                '\'' -> append("&#39;")
                else -> append(c)
            }
        }
    }
}

/** (3.3.0) Play.HT-backed [ISpeechSynthesizer]. Mirrors C# `PlayHtSpeechSynthesizer`. */
class PlayHtSpeechSynthesizer(
    private val http: ISpeechHttpTransport,
    private val options: PlayHtOptions,
) : ISpeechSynthesizer {

    override val backendId: String get() = "playht"
    val isConfigured: Boolean get() = !options.apiKey.isNullOrBlank() && !options.userId.isNullOrBlank()

    override suspend fun synthesizeAsync(text: String, voiceId: String?, languageHint: String?): SynthesisResult {
        if (!isConfigured) return emptySynthesis()

        val voice = if (voiceId.isNullOrBlank()) options.defaultVoice else voiceId
        val body = buildJson(
            "text" to JsonPrimitive(text),
            "voice" to JsonPrimitive(voice),
            "voice_engine" to JsonPrimitive(options.model),
            "output_format" to JsonPrimitive("raw"),
            "sample_rate" to JsonPrimitive(options.pcmSampleRateHz),
            "language" to JsonPrimitive(languageHint ?: "english"),
        )
        val resp = http.postJson(
            options.baseAddress,
            "/api/v2/tts/stream",
            mapOf(
                "Authorization" to "Bearer ${options.apiKey}",
                "X-USER-ID" to options.userId!!,
                "Accept" to "audio/raw",
            ),
            body,
        )
        if (!resp.isSuccess) return emptySynthesis()
        return pcmResult(resp.bodyBytes, options.pcmSampleRateHz)
    }
}

// =====================================================================
// JSON helpers
// =====================================================================

private fun parseObject(json: String): JsonObject? =
    try {
        SPEECH_JSON.parseToJsonElement(json) as? JsonObject
    } catch (_: Exception) {
        null
    }

private fun buildJson(vararg pairs: Pair<String, kotlinx.serialization.json.JsonElement>): String =
    JsonObject(linkedMapOf(*pairs)).toString()

private fun buildJsonObject(vararg pairs: Pair<String, kotlinx.serialization.json.JsonElement>): JsonObject =
    JsonObject(linkedMapOf(*pairs))

/** PCM-16 mono: 2 bytes/sample; duration = samples / rate. Mirrors the shared C# tail. */
private fun pcmResult(bytes: ByteArray, sampleRateHz: Int): SynthesisResult {
    val samples = bytes.size / 2
    val duration = if (sampleRateHz > 0) secondsToDuration(samples.toDouble() / sampleRateHz) else Duration.ZERO
    return SynthesisResult(bytes, sampleRateHz, duration)
}
