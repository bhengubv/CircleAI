// HerJarvisAdapters.kt
//
// Kotlin port of the two ONNX-backed HER/Jarvis adapters — the C# reference is
// the EXACT spec (OnnxSpeakerIdentityAdapter.cs, OnnxSpeechEmotionSensor.cs).
//
// In C# these wrap CircleAI.Voice's neural ECAPA-TDNN speaker embedder and a
// wav2vec2 speech-emotion ONNX model. Per the porting rules, the native/ONNX
// binding is an INJECTED dependency (ISpeakerIdentity / ISpeechEmotionDetector)
// rather than an empty stub — a host supplies the neural implementation; the
// adapter itself is real, working glue with deterministic fallbacks.

package com.bhengubv.circleai.companion.herjarvis

import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.jsonPrimitive
import java.util.Base64

// ---------------------------------------------------------------------------
// Injected native ports (host supplies the ONNX-backed neural implementation)
// ---------------------------------------------------------------------------

/**
 * Native speaker-identity port. In production this is CircleAI.Voice's
 * ECAPA-TDNN ONNX embedder; here it is an injected dependency so the neural
 * binding never leaks into this module. An in-memory MFCC baseline
 * ([EnergyBandVoiceIdentity]) can back it for tests.
 */
interface ISpeakerIdentity {
    suspend fun identifyAsync(audioPcm16: ByteArray, sampleRateHz: Int): String?
    suspend fun enrollAsync(userId: String, audioPcm16: ByteArray, sampleRateHz: Int)
}

/**
 * Native speech-emotion port. In production this is CircleAI.Voice's wav2vec2
 * ONNX emotion detector; injected here. Returns null when it cannot classify.
 */
interface ISpeechEmotionDetector {
    suspend fun senseAsync(audioPcm16: ByteArray, sampleRateHz: Int): EmotionFrame?
}

// ---------------------------------------------------------------------------
// OnnxSpeakerIdentityAdapter — exposes a neural embedder as IVoiceIdentity.
// ---------------------------------------------------------------------------

/**
 * Adapter exposing an injected neural [inner] speaker embedder through the
 * HER/Jarvis [IVoiceIdentity] contract. Kept out of the Voice module so Voice
 * needn't depend on Companion's contracts — mirrors the C# adapter exactly.
 */
class OnnxSpeakerIdentityAdapter(private val inner: ISpeakerIdentity) : IVoiceIdentity {

    override suspend fun identifyAsync(audioPcm16: ByteArray, sampleRateHz: Int): String? =
        inner.identifyAsync(audioPcm16, sampleRateHz)

    override suspend fun enrollAsync(userId: String, audioPcm16: ByteArray, sampleRateHz: Int) =
        inner.enrollAsync(userId, audioPcm16, sampleRateHz)
}

// ---------------------------------------------------------------------------
// OnnxSpeechEmotionSensor — runs a neural emotion detector on fused-JSON audio.
// ---------------------------------------------------------------------------

/**
 * Implements [IEmotionSensor] by running an injected neural [detector] on the
 * PCM16 audio embedded in the fused-signal JSON. Expects:
 *   { "audio_pcm16_b64": "<base64>", "sample_rate_hz": 16000 }
 * Falls back to a neutral frame whenever audio is missing or unparseable, so
 * callers always receive a usable [EmotionFrame] — matching the C# reference.
 */
class OnnxSpeechEmotionSensor(private val detector: ISpeechEmotionDetector) : IEmotionSensor {

    override suspend fun senseAsync(fusedJson: String): EmotionFrame {
        if (fusedJson.isBlank()) return neutral()
        return try {
            val root = JSON.parseToJsonElement(fusedJson)
            if (root !is JsonObject) return neutral()
            val audioEl = root["audio_pcm16_b64"] ?: return neutral()
            val b64 = audioEl.jsonPrimitive.contentOrNullSafe() ?: return neutral()
            if (b64.isEmpty()) return neutral()

            val sampleRateHz = (root["sample_rate_hz"])
                ?.jsonPrimitive?.contentOrNullSafe()?.toIntOrNull() ?: 16_000
            val bytes = Base64.getDecoder().decode(b64)
            detector.senseAsync(bytes, sampleRateHz) ?: neutral()
        } catch (_: Exception) {
            // JSON parse / base64 decode failures fall back to neutral (C# catches
            // JsonException + FormatException identically).
            neutral()
        }
    }

    private companion object {
        val JSON = Json { isLenient = true }
        fun neutral() = EmotionFrame("neutral", 0.0, 0.0)

        fun kotlinx.serialization.json.JsonPrimitive.contentOrNullSafe(): String? =
            if (this is kotlinx.serialization.json.JsonNull) null else content
    }
}
