// SpeechSelector.kt
//
// Device-aware selection for SPEECH models — ASR, TTS, VAD, wake word —
// alongside the chat selector rather than inside it.
//
// A speech model is a different kind of thing from a chat LLM: a different
// runtime and a different selection axis (modality, not capability flags).
// Folding them into one query would have let a TTS model compete to be the
// reasoning core. So the two selectors share the device-fit MATHS and not the
// question.
//
// AND THE FIT-VS-FUNCTION VERDICT MATTERS MORE HERE. A chat model below the
// quality floor gives a worse answer. An ASR model below the intelligibility
// floor acts on the WRONG WORDS — worse than none, because the assistant then
// does something confidently incorrect. That is why this returns a quality
// alongside the pick rather than just the pick.
//
// Ported from src/CircleAI.Inference/{SpeechModelSelector, IModelSelector}.cs.

package com.bhengubv.circleai.inference

import com.bhengubv.circleai.core.ModelModality
import java.util.Locale

/** How good a selection actually is, as opposed to whether one was made. */
enum class SelectionQuality {
    /** Satisfied the capability flags AND the device gates. */
    Good,

    /** Fits the device, but below the caller's requested quality floor. */
    BelowFloor,

    /** Nothing in the catalogue clears this device. */
    NothingFits,

    /** No model at all: a built-in heuristic is standing in. */
    HeuristicFallback,

    /** The feature is off by design on this device. */
    Unavailable
}

/** The outcome of planning one modality: a quality, an optional model, and the
 *  reason in words a person can read. */
data class ModalityPlan(
    val quality: SelectionQuality,
    val model: SpeechModelSelection?,
    val reason: String
) {
    val isAvailable: Boolean get() = quality != SelectionQuality.Unavailable
    val usesBuiltIn: Boolean get() = quality == SelectionQuality.HeuristicFallback
}

data class SpeechModelSelection(
    val modelId: String,
    val requiresDownload: Boolean,
    val estimatedBytes: Long,
    val quality: SelectionQuality
)

/** One catalogue row, as far as selection is concerned. */
data class SpeechCatalogueEntry(
    val name: String,
    val modality: ModelModality?,
    val minRamGb: Double,
    val minStorageGb: Double,
    val qualityRank: Int,
    val totalBytes: Long
)

interface ISpeechModelSelector {
    fun bestFor(
        ramGb: Double, storageGb: Double, modality: ModelModality, minQualityRank: Int = 0
    ): SpeechModelSelection?

    fun candidatesFor(modality: ModelModality): List<SpeechModelSelection>

    /**
     * Language-aware selection is OPTIONAL for an implementation, and the
     * default is null rather than "any model": handing back an English model for
     * a Zulu request is the failure this whole path exists to avoid.
     */
    fun bestFor(
        ramGb: Double, storageGb: Double, modality: ModelModality,
        language: String, minQualityRank: Int = 0
    ): SpeechModelSelection? = null
}

class SpeechModelSelector(
    private val entries: () -> List<SpeechCatalogueEntry>
) : ISpeechModelSelector {

    override fun bestFor(
        ramGb: Double, storageGb: Double, modality: ModelModality, minQualityRank: Int
    ): SpeechModelSelection? {
        require(modality != ModelModality.Chat) {
            "Chat selection goes through the chat selector, not the speech selector."
        }

        val ofModality = entries().filter { it.modality == modality }
        // NOT CATALOGUED IS AN HONEST NULL, distinct from "catalogued and does
        // not fit" — the first needs a different build, the second a different
        // phone, and one answer for both sends people to the wrong fix.
        if (ofModality.isEmpty()) return null

        val deviceOk = ofModality.filter {
            it.minRamGb <= ramGb + 0.0001 &&
                (storageGb <= 0 || it.minStorageGb <= storageGb + 0.0001)
        }
        val somethingFits = deviceOk.isNotEmpty()

        // Same rule as chat: the best quality that FITS; failing that the
        // smallest thing there is, so the caller has something to show and a
        // quality that says it will not run well.
        val winner = if (somethingFits) {
            deviceOk.sortedWith(compareByDescending<SpeechCatalogueEntry> { it.qualityRank }
                .thenBy { it.minRamGb }).first()
        } else {
            ofModality.sortedWith(compareBy<SpeechCatalogueEntry> { it.minRamGb }
                .thenBy { it.totalBytes }).first()
        }

        val quality = when {
            !somethingFits -> SelectionQuality.NothingFits
            winner.qualityRank < minQualityRank -> SelectionQuality.BelowFloor
            else -> SelectionQuality.Good
        }

        return SpeechModelSelection(winner.name, true, winner.totalBytes, quality)
    }

    override fun candidatesFor(modality: ModelModality): List<SpeechModelSelection> =
        entries().filter { it.modality == modality }
            .sortedWith(compareByDescending<SpeechCatalogueEntry> { it.qualityRank }
                .thenBy { it.name })
            .map { SpeechModelSelection(it.name, true, it.totalBytes, SelectionQuality.Good) }

    /**
     * The verdict with its reason.
     *
     * A "NO" BUILT ON A GUESSED MEMORY FIGURE HAS TO SAY SO. Without this a
     * mobile head that never set the platform probe gets a confident, specific,
     * wrong refusal for every model: the device reads as a few hundred MB,
     * everything fails to fit, and the reason names the model rather than the
     * missing measurement. Whoever reads it hunts a model problem that is not
     * there.
     *
     * Only on a NEGATIVE verdict — warning on every success trains people to
     * skip the text.
     */
    fun planFor(
        ramGb: Double, storageGb: Double, modality: ModelModality,
        minQualityRank: Int = 0, measurementWarning: String? = null
    ): ModalityPlan {
        val pick = bestFor(ramGb, storageGb, modality, minQualityRank)
            ?: return ModalityPlan(
                SelectionQuality.Unavailable, null, "no $modality model is catalogued"
            )

        var reason = "${pick.modelId} (${pick.quality})"
        if (measurementWarning != null &&
            pick.quality != SelectionQuality.Good &&
            pick.quality != SelectionQuality.BelowFloor
        ) {
            reason += " — NOTE: $measurementWarning"
        }
        return ModalityPlan(pick.quality, pick, reason)
    }

    companion object {
        /**
         * Classifies an entry when the catalogue does not say.
         *
         * By NAME, which is a guess and is documented as one. It exists so a
         * host with an older catalogue still gets speech selection instead of
         * nothing; a host that knows better supplies the modality itself.
         */
        fun inferModality(name: String): ModelModality? {
            val n = name.lowercase(Locale.ROOT)
            return when {
                n.contains("whisper") || n.contains("zipformer") ||
                    n.contains("asr") || n.contains("stt") -> ModelModality.Asr
                n.contains("piper") || n.contains("mms-") || n.contains("kokoro") ||
                    n.contains("toucan") || n.contains("tts") -> ModelModality.Tts
                n.contains("vad") || n.contains("silero") -> ModelModality.Vad
                n.contains("wake") || n.contains("kws") -> ModelModality.WakeWord
                n.contains("espeak") || n.contains("phonem") -> ModelModality.Phonemizer
                else -> null
            }
        }
    }
}
