// WakeFactory.kt
//
// Decides WHICH wake engine runs and HOW HARD its second stage judges, so a host
// does not have to know either.
//
// THERE ARE TWO ENGINES AND NOTHING CHOSE BETWEEN THEM. One runs a single-graph
// classifier trained on one phrase; the other runs three graphs and matches any
// number of phrases written as text. Both existed, both implemented the same
// interface, and every host picked by hard-coding a constructor — so the choice
// was made once, invisibly, by whoever wrote that line. Now it is made from what
// the bundle on disk actually IS.
//
// THE SECOND STAGE IS CHOSEN BY WHAT THE PHONE CAN AFFORD. The onset check costs
// nothing and removes three quarters of the false accepts; the transcript check
// removes the rest and needs a speech model resident. That is a device-tier
// decision, and the probe already knows the tier — it was simply never asked.
//
// Constructing either detector needs onnxruntime, so that stays behind: the
// DECISION crosses, the binding does not.
//
// Ported from src/CircleAI.Voice/{WakeWordFactory, ZipformerWakeWordDetector}.cs.

package com.bhengubv.circleai.voice

import java.io.File
import java.util.Locale

enum class WakeEngine { ZipformerTransducer, SingleGraphClassifier }

/**
 * What a person's own use has taught this device about its wake word.
 *
 * Advisory in both directions: a missing file is a default calibration and a
 * failed save is ignored. Losing it costs tuning, not function, and a memory
 * that refuses to start because a tuning file is unreadable is worse than one
 * that starts untuned.
 */
data class WakeCalibration(
    val threshold: Double? = null,
    val maxLeadInMs: Double? = null,
    val wakes: Int = 0,
    val vetoes: Int = 0
) {
    /** Counts alone are not tuning — they are what tuning is derived FROM. */
    val isDefault: Boolean get() = threshold == null && maxLeadInMs == null

    fun save(path: String) {
        runCatching {
            File(path).parentFile?.mkdirs()
            File(path).writeText(
                """{"threshold":${threshold ?: "null"},"maxLeadInMs":${maxLeadInMs ?: "null"},""" +
                    """"wakes":$wakes,"vetoes":$vetoes}"""
            )
        }
    }

    companion object {
        fun load(path: String): WakeCalibration {
            val text = runCatching { File(path).readText() }.getOrNull() ?: return WakeCalibration()
            fun num(key: String): Double? =
                Regex("\"$key\"\\s*:\\s*([-0-9.eE]+)").find(text)?.groupValues?.get(1)?.toDoubleOrNull()
            fun int(key: String): Int =
                Regex("\"$key\"\\s*:\\s*(-?\\d+)").find(text)?.groupValues?.get(1)?.toIntOrNull() ?: 0
            return WakeCalibration(num("threshold"), num("maxLeadInMs"), int("wakes"), int("vetoes"))
        }
    }
}

data class WakeHostCapabilities(
    val totalRamBytes: Long,
    val transcriberAvailable: Boolean
)

object WakeWordFactory {

    /**
     * Below this the transcript stage is not offered at all. A speech model
     * resident alongside everything else is what a 4 GB device cannot afford,
     * and being throttled is worse than being slightly less precise.
     */
    const val TRANSCRIPT_CONFIRMER_MIN_RAM = 4L * 1000 * 1000 * 1000

    /**
     * Which engine the BUNDLE is, not which engine a caller assumed.
     *
     * A transducer needs all three graphs; anything else is the classifier. A
     * missing directory is the classifier too — the caller then gets a clear
     * failure from the model lookup rather than a confusing one from a
     * transducer with no encoder.
     */
    fun engineFor(bundleDirectory: String): WakeEngine {
        val dir = File(bundleDirectory)
        if (!dir.isDirectory) return WakeEngine.SingleGraphClassifier

        val names = dir.walkTopDown()
            .filter { it.isFile && it.name.lowercase(Locale.ROOT).endsWith(".onnx") }
            .map { it.name.lowercase(Locale.ROOT) }
            .toList()

        val hasAll = names.any { it.contains("encoder") } &&
            names.any { it.contains("decoder") } &&
            names.any { it.contains("joiner") }
        return if (hasAll) WakeEngine.ZipformerTransducer else WakeEngine.SingleGraphClassifier
    }

    /**
     * The smallest .onnx in the bundle, which is the classifier's single graph.
     *
     * Smallest rather than first: a bundle can carry a spare or a quantised
     * variant alongside, and picking by directory order loads whichever the
     * filesystem happened to hand back.
     */
    fun singleGraphModel(bundleDirectory: String): String? =
        File(bundleDirectory).takeIf { it.isDirectory }
            ?.walkTopDown()
            ?.filter { it.isFile && it.name.lowercase(Locale.ROOT).endsWith(".onnx") }
            ?.minByOrNull { it.length() }
            ?.path

    /**
     * The default threshold per engine. They differ because the two score
     * entirely different things: a transducer's mean acoustic probability and a
     * classifier's single output are not comparable numbers.
     */
    fun defaultThreshold(engine: WakeEngine): Double = when (engine) {
        WakeEngine.ZipformerTransducer -> 0.5
        WakeEngine.SingleGraphClassifier -> 0.7
    }

    /**
     * The second stage, chosen by what the device can afford.
     *
     * BOTH, IN ORDER, when it can pay: the cheap one first so the expensive one
     * is never asked about a wake it would have let through anyway. On the
     * measured corpus that is 27 of 30 clips never reaching the transcriber at
     * all, which is most of the battery the precise tier would otherwise cost.
     */
    fun confirmerFor(
        host: WakeHostCapabilities,
        calibration: WakeCalibration,
        transcribe: (suspend (ByteArray) -> String)? = null
    ): IWakeConfirmer {
        val onset = UtteranceOnsetConfirmer(
            maxLeadInMs = calibration.maxLeadInMs ?: UtteranceOnsetConfirmer().maxLeadInMs
        )
        if (transcribe == null ||
            !host.transcriberAvailable ||
            host.totalRamBytes < TRANSCRIPT_CONFIRMER_MIN_RAM
        ) return onset

        return EitherConfirmer(onset, TranscriptConfirmer(transcribe))
    }
}

/** Which wake model to use for a language, and what to say about it. */
data class WakeLanguageChoice(
    val modelName: String?,
    val isNative: Boolean,
    /** Said to a PERSON. Empty when there is nothing they need to know. */
    val note: String
)

object WakeLanguages {

    data class Model(val name: String, val language: String?, val quality: Int)

    /**
     * A native model if there is one, else English, else the best of whatever
     * there is — and it SAYS SO when it falls back.
     *
     * The note is the point. Falling back silently leaves somebody repeating a
     * phrase in their own language at a device listening for it in English, with
     * nothing on screen to explain why it never answers.
     */
    fun choose(available: List<Model>, languageCode: String): WakeLanguageChoice {
        if (available.isEmpty()) {
            return WakeLanguageChoice(
                null, false,
                "No wake word is available yet, so it cannot listen for a phrase."
            )
        }

        val wanted = base(languageCode)

        available.filter { base(it.language).equals(wanted, ignoreCase = true) }
            .maxByOrNull { it.quality }
            ?.let { return WakeLanguageChoice(it.name, true, "") }

        val fallback = available.filter { base(it.language).equals("en", ignoreCase = true) }
            .maxByOrNull { it.quality }
            ?: available.maxByOrNull { it.quality }!!

        return WakeLanguageChoice(
            fallback.name, false,
            "There is no wake word for this language yet, so an English one is being used. " +
                "It will still hear you, but the phrase has to be said the English way."
        )
    }

    /** en-ZA and en are the same language for this purpose; the region never
     *  changes which acoustic model can hear a phrase. */
    internal fun base(code: String?): String =
        code?.trim()?.split('-', '_')?.firstOrNull()?.trim().orEmpty()
}

/**
 * Everything the zipformer wake detector needs to be built.
 *
 * Separated from the detector so the CHOICES — which bundle, which phrases, how
 * hard the second stage judges, how close together two wakes may fire — can be
 * made, stored and tested on a host with no onnxruntime at all.
 */
data class ZipformerWakeConfig(
    val bundleDirectory: String,
    /**
     * Phrases as TEXT, one per line. This is what the transducer can do that the
     * classifier cannot: any number of phrases, each matched independently, so a
     * household can give each permitted person their own.
     */
    val keywordsFile: String? = null,
    val threshold: Double = 0.5,
    /** null is the onset check, which is what [WakeWordFactory.confirmerFor]
     *  picks for a device that cannot pay for more. */
    val confirmer: IWakeConfirmer? = null,
    /**
     * How close together two wakes may fire.
     *
     * The decoder emits a detection per frame while the phrase is still under
     * the microphone, so one spoken "Hey B" is several detections. Without this
     * the loop is woken three or four times by one utterance and starts three or
     * four conversations.
     */
    val minIntervalBetweenFiresMs: Long = 1200
)
