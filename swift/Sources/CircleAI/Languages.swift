// Languages.swift
//
// WritingSystem, LanguageTag, DetectionResult, KnownLanguages,
// ILanguageDetector, ILanguageRegistry.

import Foundation

// MARK: - WritingSystem

/// The writing system (script) used by a language.
public enum WritingSystem: String, Sendable, CaseIterable {
    case latin
    case arabic
    case ethiopic
    case geez
    case devanagari
    case han
    case cyrillic
    case hebrew
    case greek
    case other
}

// MARK: - LanguageTag

/// A BCP-47 language tag enriched with display metadata.
public struct LanguageTag: Sendable, Equatable {
    /// BCP-47 tag, e.g. "zu", "en", "ar".
    public var bcpTag: String

    /// English display name, e.g. "isiZulu".
    public var englishName: String

    /// Name in the language itself, e.g. "isiZulu".
    public var nativeName: String

    /// Writing system used by this language.
    public var writingSystem: WritingSystem

    /// True for right-to-left scripts (Arabic, Hebrew, etc.).
    public var isRtl: Bool

    /// ISO 3166-1 alpha-2 primary region code, e.g. "ZA".
    public var primaryRegion: String

    public init(
        bcpTag: String,
        englishName: String,
        nativeName: String,
        writingSystem: WritingSystem,
        isRtl: Bool,
        primaryRegion: String
    ) {
        self.bcpTag = bcpTag
        self.englishName = englishName
        self.nativeName = nativeName
        self.writingSystem = writingSystem
        self.isRtl = isRtl
        self.primaryRegion = primaryRegion
    }

    /// Sentinel value returned when language detection fails.
    public static let unknown = LanguageTag(
        bcpTag: "und",
        englishName: "Unknown",
        nativeName: "Unknown",
        writingSystem: .latin,
        isRtl: false,
        primaryRegion: ""
    )
}

// MARK: - DetectionResult

/// Result of language detection.
public struct DetectionResult: Sendable {
    /// The detected language.
    public var language: LanguageTag

    /// Confidence score in [0, 1].
    public var confidence: Float

    /// True when the detection is considered reliable.
    public var isReliable: Bool

    public init(language: LanguageTag, confidence: Float, isReliable: Bool) {
        self.language = language
        self.confidence = confidence
        self.isReliable = isReliable
    }
}

// MARK: - ScriptNormalisationResult

/// Result of script normalisation.
public struct ScriptNormalisationResult: Sendable {
    public var input: String
    public var normalised: String
    public var detectedLanguage: LanguageTag

    public init(input: String, normalised: String, detectedLanguage: LanguageTag) {
        self.input = input
        self.normalised = normalised
        self.detectedLanguage = detectedLanguage
    }
}

// MARK: - KnownLanguages

/// Static registry of every language Circle AI ships support for.
public enum KnownLanguages {

    // ── Africa ────────────────────────────────────────────────────────────────
    public static let isiZulu   = LanguageTag(bcpTag: "zu",  englishName: "isiZulu",    nativeName: "isiZulu",       writingSystem: .latin,      isRtl: false, primaryRegion: "ZA")
    public static let sesotho   = LanguageTag(bcpTag: "st",  englishName: "Sesotho",    nativeName: "Sesotho",       writingSystem: .latin,      isRtl: false, primaryRegion: "ZA")
    public static let afrikaans = LanguageTag(bcpTag: "af",  englishName: "Afrikaans",  nativeName: "Afrikaans",     writingSystem: .latin,      isRtl: false, primaryRegion: "ZA")
    public static let swahili   = LanguageTag(bcpTag: "sw",  englishName: "Swahili",    nativeName: "Kiswahili",     writingSystem: .latin,      isRtl: false, primaryRegion: "KE")
    public static let hausa     = LanguageTag(bcpTag: "ha",  englishName: "Hausa",      nativeName: "Hausa",         writingSystem: .latin,      isRtl: false, primaryRegion: "NG")
    public static let amharic   = LanguageTag(bcpTag: "am",  englishName: "Amharic",    nativeName: "አማርኛ",          writingSystem: .ethiopic,   isRtl: false, primaryRegion: "ET")
    public static let yoruba    = LanguageTag(bcpTag: "yo",  englishName: "Yoruba",     nativeName: "Yorùbá",        writingSystem: .latin,      isRtl: false, primaryRegion: "NG")
    public static let igbo      = LanguageTag(bcpTag: "ig",  englishName: "Igbo",       nativeName: "Igbo",          writingSystem: .latin,      isRtl: false, primaryRegion: "NG")
    public static let xhosa     = LanguageTag(bcpTag: "xh",  englishName: "isiXhosa",   nativeName: "isiXhosa",      writingSystem: .latin,      isRtl: false, primaryRegion: "ZA")
    public static let sepedi    = LanguageTag(bcpTag: "nso", englishName: "Sepedi",     nativeName: "Sepedi",        writingSystem: .latin,      isRtl: false, primaryRegion: "ZA")
    public static let setswana  = LanguageTag(bcpTag: "tn",  englishName: "Setswana",   nativeName: "Setswana",      writingSystem: .latin,      isRtl: false, primaryRegion: "ZA")
    public static let somali    = LanguageTag(bcpTag: "so",  englishName: "Somali",     nativeName: "Soomaali",      writingSystem: .latin,      isRtl: false, primaryRegion: "SO")
    public static let oromo     = LanguageTag(bcpTag: "om",  englishName: "Oromo",      nativeName: "Afaan Oromoo",  writingSystem: .latin,      isRtl: false, primaryRegion: "ET")

    // ── Middle East & North Africa ─────────────────────────────────────────────
    public static let arabic    = LanguageTag(bcpTag: "ar",  englishName: "Arabic",     nativeName: "العربية",       writingSystem: .arabic,     isRtl: true,  primaryRegion: "SA")

    // ── Europe & Americas ──────────────────────────────────────────────────────
    public static let english    = LanguageTag(bcpTag: "en", englishName: "English",    nativeName: "English",       writingSystem: .latin,      isRtl: false, primaryRegion: "GB")
    public static let portuguese = LanguageTag(bcpTag: "pt", englishName: "Portuguese", nativeName: "Português",     writingSystem: .latin,      isRtl: false, primaryRegion: "PT")
    public static let french     = LanguageTag(bcpTag: "fr", englishName: "French",     nativeName: "Français",      writingSystem: .latin,      isRtl: false, primaryRegion: "FR")
    public static let spanish    = LanguageTag(bcpTag: "es", englishName: "Spanish",    nativeName: "Español",       writingSystem: .latin,      isRtl: false, primaryRegion: "ES")

    // ── Asia ───────────────────────────────────────────────────────────────────
    public static let mandarin  = LanguageTag(bcpTag: "zh",  englishName: "Mandarin",   nativeName: "中文",           writingSystem: .han,        isRtl: false, primaryRegion: "CN")
    public static let hindi     = LanguageTag(bcpTag: "hi",  englishName: "Hindi",      nativeName: "हिन्दी",         writingSystem: .devanagari, isRtl: false, primaryRegion: "IN")

    /// All languages shipped with Circle AI — in declaration order (20 total).
    public static let all: [LanguageTag] = [
        isiZulu, sesotho, afrikaans, swahili, hausa, amharic,
        yoruba, igbo, xhosa, sepedi, setswana, somali, oromo,
        arabic,
        english, portuguese, french, spanish,
        mandarin, hindi
    ]
}

// MARK: - ILanguageDetector

/// Detects the BCP-47 language of a piece of text.
public protocol ILanguageDetector {
    /// Detects the most likely language.
    /// Returns LanguageTag.unknown with confidence=0 when detection fails.
    func detect(text: String) async throws -> DetectionResult

    /// Returns up to maxResults candidates ranked by confidence.
    func detectMultiple(text: String, maxResults: Int) async throws -> [DetectionResult]
}

// MARK: - ILanguageRegistry

/// Registry of all BCP-47 language tags that Circle AI understands.
public protocol ILanguageRegistry {
    /// Returns the LanguageTag for the given BCP-47 tag, or nil.
    func getByBcpTag(_ bcpTag: String) -> LanguageTag?

    /// Returns all registered language tags.
    func getAll() -> [LanguageTag]

    /// Returns all language tags whose primaryRegion matches isoRegion.
    func getForRegion(_ isoRegion: String) -> [LanguageTag]

    /// Returns true if the BCP-47 tag is supported.
    func isSupported(_ bcpTag: String) -> Bool
}
