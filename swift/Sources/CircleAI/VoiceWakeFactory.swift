// VoiceWakeFactory.swift
//
// Decides WHICH wake engine runs and HOW HARD its second stage judges, so a
// host does not have to know either.
//
// THERE ARE TWO ENGINES AND NOTHING CHOSE BETWEEN THEM. One runs a single-graph
// classifier trained on one phrase; the other runs three graphs and matches any
// number of phrases written as text. Both existed, both implemented the same
// interface, and every host picked by hard-coding a constructor — which meant
// the choice was made once, invisibly, by whoever wrote that line. Now it is
// made from what the bundle on disk actually IS.
//
// THE SECOND STAGE IS CHOSEN BY WHAT THE PHONE CAN AFFORD. High-end should feel
// like air; low-end should not be throttled. The onset check costs nothing and
// removes three quarters of the false accepts; the transcript check removes the
// rest and needs a speech model resident. That is a device-tier decision, and
// the device probe already knows the tier — it was simply never asked.
//
// WHAT IS HERE AND WHAT IS NOT. The engine choice, the tier decision, the
// calibration file and the language fallback are all deterministic and are
// ported. Constructing either detector needs onnxruntime, so `create` takes the
// two constructors as closures: the DECISION crosses, the binding does not.
//
// Ported from src/CircleAI.Voice/WakeWordFactory.cs.

import Foundation

public enum WakeEngine: Int, Sendable, Equatable, CaseIterable {
    case zipformerTransducer = 0
    case singleGraphClassifier
}

/// What a person's own use has taught this device about its wake word.
///
/// Advisory in both directions: a missing file is a default calibration, and a
/// failed save is ignored. Losing it costs tuning, not function, and a memory
/// that refuses to start because a tuning file is unreadable is worse than one
/// that starts untuned.
public struct WakeCalibration: Sendable, Equatable, Codable {
    public var threshold: Double?
    public var maxLeadInMs: Double?
    public var wakes: Int
    public var vetoes: Int

    public init(threshold: Double? = nil, maxLeadInMs: Double? = nil,
                wakes: Int = 0, vetoes: Int = 0) {
        self.threshold = threshold
        self.maxLeadInMs = maxLeadInMs
        self.wakes = wakes
        self.vetoes = vetoes
    }

    public var isDefault: Bool { threshold == nil && maxLeadInMs == nil }

    enum CodingKeys: String, CodingKey {
        case threshold, maxLeadInMs, wakes, vetoes
    }

    public static func load(from path: String) -> WakeCalibration {
        guard FileManager.default.fileExists(atPath: path),
              let data = try? Data(contentsOf: URL(fileURLWithPath: path)),
              let decoded = try? JSONDecoder().decode(WakeCalibration.self, from: data)
        else { return WakeCalibration() }
        return decoded
    }

    public func save(to path: String) {
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
        guard let data = try? encoder.encode(self) else { return }
        let dir = (path as NSString).deletingLastPathComponent
        try? FileManager.default.createDirectory(atPath: dir.isEmpty ? "." : dir,
                                                 withIntermediateDirectories: true)
        try? data.write(to: URL(fileURLWithPath: path))
    }
}

public struct WakeHostCapabilities: Sendable, Equatable {
    public let totalRamBytes: Int64
    public let transcriberAvailable: Bool

    public init(totalRamBytes: Int64, transcriberAvailable: Bool) {
        self.totalRamBytes = totalRamBytes
        self.transcriberAvailable = transcriberAvailable
    }
}

public enum WakeWordFactory {

    /// Below this the transcript stage is not offered at all. A speech model
    /// resident alongside everything else is what a 4 GB device cannot afford,
    /// and being throttled is worse than being slightly less precise.
    public static let transcriptConfirmerMinRam: Int64 = 4 * 1000 * 1000 * 1000

    /// Which engine the BUNDLE is, not which engine a caller assumed.
    ///
    /// A transducer needs all three graphs; anything else is the single-graph
    /// classifier. A missing directory is the classifier too — the caller gets a
    /// clear failure from the model lookup rather than a confusing one from a
    /// transducer with no encoder.
    public static func engine(forBundleAt directory: String) -> WakeEngine {
        var isDir: ObjCBool = false
        guard FileManager.default.fileExists(atPath: directory, isDirectory: &isDir),
              isDir.boolValue else { return .singleGraphClassifier }

        var names: [String] = []
        if let walker = FileManager.default.enumerator(atPath: directory) {
            for case let p as String in walker where p.lowercased().hasSuffix(".onnx") {
                names.append(((p as NSString).lastPathComponent).lowercased())
            }
        }

        let hasAll = names.contains { $0.contains("encoder") }
            && names.contains { $0.contains("decoder") }
            && names.contains { $0.contains("joiner") }
        return hasAll ? .zipformerTransducer : .singleGraphClassifier
    }

    /// The second stage, chosen by what the device can afford.
    ///
    /// BOTH, IN ORDER, when the device can pay: the cheap one first so the
    /// expensive one is never asked about a wake it would have let through
    /// anyway. On the measured corpus that is 27 of 30 clips never reaching the
    /// transcriber at all, which is most of the battery the precise tier would
    /// otherwise cost.
    public static func confirmer(
        host: WakeHostCapabilities,
        calibration: WakeCalibration,
        transcribe: (@Sendable ([UInt8]) async throws -> String)? = nil
    ) -> any IWakeConfirmer {
        let onset = UtteranceOnsetConfirmer(
            maxLeadInMs: calibration.maxLeadInMs ?? UtteranceOnsetConfirmer().maxLeadInMs)

        guard let transcribe,
              host.transcriberAvailable,
              host.totalRamBytes >= transcriptConfirmerMinRam
        else { return onset }

        return EitherConfirmer(onset, TranscriptConfirmer(transcribe: transcribe))
    }

    /// The smallest .onnx in the bundle, which is the classifier's single graph.
    ///
    /// Smallest rather than first: a bundle can carry a spare or a quantised
    /// variant alongside, and picking by directory order would load whichever
    /// the filesystem happened to hand back.
    public static func singleGraphModel(inBundleAt directory: String) -> String? {
        guard let walker = FileManager.default.enumerator(atPath: directory) else { return nil }
        var candidates: [(path: String, size: Int64)] = []
        for case let p as String in walker where p.lowercased().hasSuffix(".onnx") {
            let full = (directory as NSString).appendingPathComponent(p)
            let attrs = try? FileManager.default.attributesOfItem(atPath: full)
            let size = (attrs?[.size] as? NSNumber)?.int64Value ?? 0
            candidates.append((full, size))
        }
        return candidates.min { $0.size < $1.size }?.path
    }

    /// The default threshold for each engine, which differ because the two
    /// score entirely different things: a transducer's mean acoustic
    /// probability and a classifier's single output are not comparable numbers.
    public static func defaultThreshold(for engine: WakeEngine) -> Double {
        switch engine {
        case .zipformerTransducer: return 0.5
        case .singleGraphClassifier: return 0.7
        }
    }
}

/// Which wake model to use for a language, and what to say about it.
public struct WakeLanguageChoice: Sendable, Equatable {
    public let modelName: String?
    public let isNative: Bool
    /// Said to a PERSON. Empty when there is nothing they need to know.
    public let note: String

    public init(modelName: String?, isNative: Bool, note: String) {
        self.modelName = modelName
        self.isNative = isNative
        self.note = note
    }
}

public enum WakeLanguages {

    public struct Model: Sendable, Equatable {
        public let name: String
        public let language: String?
        public let quality: Int

        public init(name: String, language: String?, quality: Int) {
            self.name = name
            self.language = language
            self.quality = quality
        }
    }

    /// A native model if there is one, else English, else the best of whatever
    /// there is — and it SAYS SO when it falls back.
    ///
    /// The note is the point. Falling back silently leaves somebody repeating a
    /// phrase in their own language at a device that is listening for it in
    /// English, with nothing on screen to explain why it never answers.
    public static func choose(from available: [Model], languageCode: String) -> WakeLanguageChoice {
        guard !available.isEmpty else {
            return WakeLanguageChoice(
                modelName: nil, isNative: false,
                note: "No wake word is available yet, so it cannot listen for a phrase.")
        }

        let wanted = base(languageCode)

        if let native = available
            .filter({ base($0.language).caseInsensitiveCompare(wanted) == .orderedSame })
            .max(by: { $0.quality < $1.quality }) {
            return WakeLanguageChoice(modelName: native.name, isNative: true, note: "")
        }

        let english = available
            .filter { base($0.language).caseInsensitiveCompare("en") == .orderedSame }
            .max(by: { $0.quality < $1.quality })
        let fallback = english ?? available.max(by: { $0.quality < $1.quality })!

        return WakeLanguageChoice(
            modelName: fallback.name, isNative: false,
            note: "There is no wake word for this language yet, so an English one is being used. "
                + "It will still hear you, but the phrase has to be said the English way.")
    }

    /// en-ZA and en are the same language for this purpose; the region never
    /// changes which acoustic model can hear a phrase.
    static func base(_ code: String?) -> String {
        guard let code, !code.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
            return ""
        }
        let head = code.split(whereSeparator: { $0 == "-" || $0 == "_" }).first ?? ""
        return String(head).trimmingCharacters(in: .whitespacesAndNewlines)
    }
}

/// Everything the zipformer wake detector needs to be built.
///
/// Separated from the detector so the CHOICES — which bundle, which phrases,
/// how hard the second stage judges, how close together two wakes may fire —
/// can be made, stored and tested on a host with no onnxruntime at all.
public struct ZipformerWakeConfig: Sendable {

    public let bundleDirectory: String

    /// Phrases as TEXT, one per line. This is what the transducer engine can do
    /// that the single-graph classifier cannot: any number of phrases, each
    /// matched independently, so a household can give each permitted person
    /// their own. nil uses whatever the bundle ships with.
    public let keywordsFile: String?

    public let threshold: Double

    /// The second stage. nil is the onset check, which is what
    /// `WakeWordFactory.confirmer` picks for a device that cannot pay for more.
    public let confirmer: (any IWakeConfirmer)?

    /// How close together two wakes may fire.
    ///
    /// The decoder emits a detection per frame while the phrase is still under
    /// the microphone, so one spoken "Hey B" is several detections. Without this
    /// the loop is woken three or four times by one utterance and starts three
    /// or four conversations.
    public let minIntervalBetweenFires: TimeInterval

    public init(bundleDirectory: String,
                keywordsFile: String? = nil,
                threshold: Double = 0.5,
                confirmer: (any IWakeConfirmer)? = nil,
                minIntervalBetweenFires: TimeInterval = 1.2) {
        self.bundleDirectory = bundleDirectory
        self.keywordsFile = keywordsFile
        self.threshold = threshold
        self.confirmer = confirmer
        self.minIntervalBetweenFires = minIntervalBetweenFires
    }
}
