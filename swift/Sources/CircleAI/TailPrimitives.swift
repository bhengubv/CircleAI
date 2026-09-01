// TailPrimitives.swift
//
// Small types from several modules that had no Swift equivalent: sync version
// vectors, the language registry and its no-op detector, the script-normaliser
// seam, and the provider-id vocabularies.
//
// Ported from src/CircleAI.Sync/SyncPrimitives.cs,
// src/CircleAI.Languages/{DefaultLanguageRegistry,NullLanguageDetector,
// IScriptNormaliser}.cs, src/CircleAI.Hosting.CloudFallback and
// src/CircleAI.Vision.Cloud ServiceCollectionExtensions.cs.

import Foundation

// MARK: - Sync

/// One clock per replica. Two devices that have never met still merge cleanly,
/// which is the whole reason this is a vector and not a timestamp.
public struct VersionVector: Sendable, Equatable, Codable {
    public let clocks: [String: Int64]

    public init(clocks: [String: Int64] = [:]) {
        self.clocks = clocks
    }

    /// A clock this vector does not carry is 0, not missing. A replica that has
    /// never been heard from has simply made no changes.
    public func clock(for replica: String) -> Int64 { clocks[replica] ?? 0 }
}

public enum SyncReconciliation {

    /// The pairwise maximum, which is what "everything both sides have seen"
    /// means for a version vector.
    public static func merge(_ a: VersionVector, _ b: VersionVector) -> VersionVector {
        var merged: [String: Int64] = [:]
        for k in Set(a.clocks.keys).union(b.clocks.keys) {
            merged[k] = max(a.clock(for: k), b.clock(for: k))
        }
        return VersionVector(clocks: merged)
    }

    /// True when `a` has seen everything `b` has AND something more.
    ///
    /// Strictly greater somewhere is the second half and it is not optional:
    /// without it two IDENTICAL vectors would each dominate the other, and a
    /// caller using this to pick a winner would pick both.
    public static func aDominatesB(_ a: VersionVector, _ b: VersionVector) -> Bool {
        var anyStrictlyGreater = false
        for k in Set(a.clocks.keys).union(b.clocks.keys) {
            let av = a.clock(for: k), bv = b.clock(for: k)
            if av < bv { return false }
            if av > bv { anyStrictlyGreater = true }
        }
        return anyStrictlyGreater
    }

    /// Last writer wins, with ties going to the FIRST argument.
    ///
    /// Ties are real — two devices writing in the same millisecond is ordinary
    /// on a mesh — so the rule has to be stated rather than left to whichever
    /// comparison the compiler picked. A caller that needs a deterministic
    /// winner across machines orders the arguments itself.
    public static func lastWriterWins<T>(_ a: (at: Date, value: T),
                                         _ b: (at: Date, value: T)) -> (at: Date, value: T) {
        a.at >= b.at ? a : b
    }
}

// MARK: - Languages

/// Turns text written in one script into something a caller can work with, and
/// says what it did.
///
/// A seam rather than an implementation because the right answer is per-script:
/// Ethiopic needs a syllabary walk, Arabic needs bidi handling, and a single
/// "normalise" that tried to do both would do neither.
public protocol IScriptNormaliser: Sendable {
    func normalise(_ text: String, targetLanguage: LanguageTag?) -> ScriptNormalisationResult

    /// A best-effort Latin rendering, for places that can only show ASCII.
    func toAsciiApproximation(_ text: String) -> String

    /// Whether any of this text runs right to left. Checked rather than assumed
    /// from the language tag: a Hebrew name inside an English sentence is still
    /// right to left, and the tag says "en".
    func containsRtl(_ text: String) -> Bool
}

public extension IScriptNormaliser {
    func normalise(_ text: String) -> ScriptNormalisationResult {
        normalise(text, targetLanguage: nil)
    }
}

/// Every language the system knows, indexed for lookup.
public struct DefaultLanguageRegistry: ILanguageRegistry, Sendable {

    private let byTag: [String: LanguageTag]
    private let byRegion: [String: [LanguageTag]]

    public init() {
        var tags: [String: LanguageTag] = [:]
        var regions: [String: [LanguageTag]] = [:]
        for t in KnownLanguages.all {
            // Lower-cased keys, because a BCP-47 tag is case-insensitive and
            // "en-ZA" arriving as "en-za" must not read as an unknown language.
            tags[t.bcpTag.lowercased()] = t
            regions[t.primaryRegion.lowercased(), default: []].append(t)
        }
        self.byTag = tags
        self.byRegion = regions
    }

    public func getByBcpTag(_ bcpTag: String) -> LanguageTag? {
        byTag[bcpTag.lowercased()]
    }

    public func getAll() -> [LanguageTag] { KnownLanguages.all }

    public func getForRegion(_ isoRegion: String) -> [LanguageTag] {
        byRegion[isoRegion.lowercased()] ?? []
    }

    public func isSupported(_ bcpTag: String) -> Bool {
        byTag[bcpTag.lowercased()] != nil
    }
}

/// Detects nothing, and says so.
///
/// Confidence 0 and `unknown`, never a plausible-looking guess: a detector that
/// quietly answers "English" makes every downstream choice wrong in a way that
/// looks like a working system.
public struct NullLanguageDetector: ILanguageDetector, Sendable {
    public static let instance = NullLanguageDetector()

    public init() {}

    public func detect(text: String) async throws -> DetectionResult {
        DetectionResult(language: .unknown, confidence: 0, isReliable: false)
    }

    public func detectMultiple(text: String, maxResults: Int) async throws -> [DetectionResult] {
        [DetectionResult(language: .unknown, confidence: 0, isReliable: false)]
    }
}

// MARK: - Provider vocabularies

/// The ids a cloud-fallback chat provider is registered under.
///
/// Named constants rather than literals scattered through the registration
/// code: a typo in one of these is a provider that is configured, present, and
/// never selected, with nothing anywhere reporting a problem.
public enum CloudProviderIds {
    public static let openAi = "openai"
    public static let anthropic = "anthropic"
    public static let gemini = "gemini"
    public static let groq = "groq"
    public static let cerebras = "cerebras"
    public static let together = "together"
    public static let deepSeek = "deepseek"

    public static let all = [openAi, anthropic, gemini, groq, cerebras, together, deepSeek]
}

/// The ids a cloud image generator is registered under.
public enum VisionGeneratorIds {
    public static let openAi = "openai-images"
    public static let stability = "stability"

    public static let all = [openAi, stability]
}
