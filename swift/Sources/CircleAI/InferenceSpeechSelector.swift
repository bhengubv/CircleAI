// InferenceSpeechSelector.swift
//
// Device-aware selection for SPEECH models — ASR, TTS, VAD, wake word —
// alongside the chat selector rather than inside it.
//
// A speech model is a different kind of thing from a chat LLM: a different
// runtime (the voice pipeline, not a chat generator) and a different selection
// axis (modality, not capability flags). Folding them into one query would have
// let a TTS model compete to be the reasoning core. So the two selectors share
// the device-fit MATHS and not the question.
//
// AND THE FIT-VS-FUNCTION VERDICT MATTERS MORE HERE. A chat model below the
// quality floor gives a worse answer. An ASR model below the intelligibility
// floor acts on the WRONG WORDS — it is worse than none, because the assistant
// then does something confidently incorrect. That is why this returns a quality
// alongside the pick rather than just the pick.
//
// Ported from src/CircleAI.Inference/SpeechModelSelector.cs.

import Foundation

public protocol ISpeechModelSelector: Sendable {
    func bestFor(probe: DeviceProbe, modality: ModelModality,
                 minQualityRank: Int) -> ModelSelection?

    func candidatesFor(probe: DeviceProbe, modality: ModelModality) -> [ModelSelection]

    func bestFor(probe: DeviceProbe, modality: ModelModality, language: String,
                 minQualityRank: Int) -> ModelSelection?
}

public extension ISpeechModelSelector {

    func bestFor(probe: DeviceProbe, modality: ModelModality) -> ModelSelection? {
        bestFor(probe: probe, modality: modality, minQualityRank: 0)
    }

    /// Language-aware selection is OPTIONAL for an implementation, and the
    /// default is nil rather than "any model": handing back an English model
    /// for a Zulu request is the failure this whole path exists to avoid.
    func bestFor(probe: DeviceProbe, modality: ModelModality, language: String,
                 minQualityRank: Int = 0) -> ModelSelection? {
        nil
    }

    func planFor(probe: DeviceProbe, modality: ModelModality,
                 minQualityRank: Int = 0) -> ModalityPlan {
        guard let pick = bestFor(probe: probe, modality: modality,
                                 minQualityRank: minQualityRank) else {
            return ModalityPlan(quality: .unavailable, model: nil,
                                reason: "no \(modality) model is catalogued")
        }
        return ModalityPlan(quality: .good, model: pick, reason: pick.modelId)
    }

    func planFor(probe: DeviceProbe, modality: ModelModality, language: String,
                 minQualityRank: Int = 0) -> ModalityPlan {
        guard let pick = bestFor(probe: probe, modality: modality, language: language,
                                 minQualityRank: minQualityRank) else {
            return ModalityPlan(
                quality: .unavailable, model: nil,
                reason: "no \(modality) model is catalogued for language '\(language)'")
        }
        return ModalityPlan(quality: .good, model: pick,
                            reason: "\(pick.modelId) for \(modality) [\(language)]")
    }
}

public struct SpeechModelSelector: ISpeechModelSelector, Sendable {

    private let entries: @Sendable () -> [ModelEntry]
    /// How an entry's modality is known. A closure because the Swift catalogue
    /// entry has no modality column — the host classifies from whatever it has
    /// (the registry's own metadata, or the model name).
    private let modalityOf: @Sendable (ModelEntry) -> ModelModality?

    public init(registry: ModelRegistryService,
                modalityOf: (@Sendable (ModelEntry) -> ModelModality?)? = nil) {
        self.entries = { registry.allModels }
        self.modalityOf = modalityOf ?? { SpeechModelSelector.inferModality($0) }
    }

    public init(entries: @escaping @Sendable () -> [ModelEntry],
                modalityOf: @escaping @Sendable (ModelEntry) -> ModelModality?) {
        self.entries = entries
        self.modalityOf = modalityOf
    }

    public func bestFor(probe: DeviceProbe, modality: ModelModality,
                        minQualityRank: Int = 0) -> ModelSelection? {
        precondition(modality != .chat,
                     "Chat selection goes through IModelSelector.bestFit, not the speech selector.")

        let ofModality = entries().filter { modalityOf($0) == modality }
        // NOT CATALOGUED IS AN HONEST NIL, distinct from "catalogued and does
        // not fit" — the first needs a different build, the second a different
        // phone, and one answer for both sends people to the wrong fix.
        guard !ofModality.isEmpty else { return nil }

        let ramGb = probe.usableRamGb
        let storageGb = probe.storageFreeGb

        let deviceOk = ofModality.filter {
            $0.minRamGb <= ramGb + 0.0001
                && (storageGb <= 0 || $0.minStorageGb <= storageGb + 0.0001)
        }

        // Same rule as chat: the best quality that FITS; failing that the
        // smallest thing there is, so the caller has something to show and a
        // quality that says it will not run well.
        let winner: ModelEntry?
        if !deviceOk.isEmpty {
            winner = deviceOk.max { a, b in
                a.qualityRank != b.qualityRank
                    ? a.qualityRank < b.qualityRank
                    : a.minRamGb > b.minRamGb
            }
        } else {
            winner = ofModality.min { a, b in
                a.minRamGb != b.minRamGb ? a.minRamGb < b.minRamGb : a.totalBytes < b.totalBytes
            }
        }
        guard let winner else { return nil }

        return ModelSelection(modelId: winner.name, requiresDownload: true,
                              estimatedBytes: winner.totalBytes, tier: probe.classify())
    }

    public func candidatesFor(probe: DeviceProbe, modality: ModelModality) -> [ModelSelection] {
        let tier = probe.classify()
        return entries()
            .filter { modalityOf($0) == modality }
            .sorted { a, b in
                a.qualityRank != b.qualityRank
                    ? a.qualityRank > b.qualityRank
                    : a.name < b.name
            }
            .map { ModelSelection(modelId: $0.name, requiresDownload: true,
                                  estimatedBytes: $0.totalBytes, tier: tier) }
    }

    /// The verdict, with the quality and a reason a person can read.
    ///
    /// A "NO" BUILT ON A GUESSED MEMORY FIGURE HAS TO SAY SO. Without this a
    /// mobile head that never set the platform memory probe gets a confident,
    /// specific, wrong refusal for every model: the device reads as ~100 MB,
    /// everything fails to fit, and the reason names the model rather than the
    /// missing measurement. Whoever reads it goes hunting a model problem that
    /// is not there.
    ///
    /// Only on a NEGATIVE verdict. A good plan chosen on a bad number is a
    /// different question, and warning on every success trains people to skip
    /// the text.
    public func planFor(probe: DeviceProbe, modality: ModelModality,
                        minQualityRank: Int = 0) -> ModalityPlan {
        let ofModality = entries().filter { modalityOf($0) == modality }
        guard !ofModality.isEmpty else {
            return ModalityPlan(quality: .unavailable, model: nil,
                                reason: "no \(modality) model is catalogued")
        }

        let ramGb = probe.usableRamGb
        let storageGb = probe.storageFreeGb
        let somethingFits = ofModality.contains {
            $0.minRamGb <= ramGb + 0.0001
                && (storageGb <= 0 || $0.minStorageGb <= storageGb + 0.0001)
        }

        guard let pick = bestFor(probe: probe, modality: modality,
                                 minQualityRank: minQualityRank) else {
            return ModalityPlan(quality: .unavailable, model: nil,
                                reason: "no \(modality) model is catalogued")
        }

        let rank = ofModality.first { $0.name == pick.modelId }?.qualityRank ?? 0
        let quality: SelectionQuality =
            !somethingFits ? .nothingFits
            : rank < minQualityRank ? .belowFloor
            : .good

        var reason = "\(pick.modelId) (\(quality))"
        if quality != .good && quality != .belowFloor,
           let warning = probe.measurementWarning(source: .heuristic) {
            reason += " — NOTE: \(warning)"
        }
        return ModalityPlan(quality: quality, model: pick, reason: reason)
    }

    /// Classifies an entry when the catalogue does not say.
    ///
    /// By NAME, which is a guess and is documented as one. It exists so a host
    /// with an older catalogue still gets speech selection instead of nothing;
    /// a host that knows better passes its own classifier.
    static func inferModality(_ entry: ModelEntry) -> ModelModality? {
        let n = entry.name.lowercased()
        if n.contains("whisper") || n.contains("zipformer") || n.contains("asr")
            || n.contains("stt") { return .asr }
        if n.contains("piper") || n.contains("mms-") || n.contains("kokoro")
            || n.contains("toucan") || n.contains("tts") { return .tts }
        if n.contains("vad") || n.contains("silero") { return .vad }
        if n.contains("wake") || n.contains("kws") { return .wakeWord }
        if n.contains("espeak") || n.contains("phonem") { return .phonemizer }
        return nil
    }
}
