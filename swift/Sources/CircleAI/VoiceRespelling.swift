// VoiceRespelling.swift
//
// Making an English word sayable by an isiZulu voice.
//
// A TTS voice trained on isiZulu cannot pronounce "WhatsApp" - it has no such
// sounds in that order. Respelling rewrites the word into the host language
// orthography so the SAME voice can say it, instead of switching voices
// mid-sentence or reading it as noise.
//
// Ported from src/CircleAI.Voice: Respeller.cs, LoanwordRespeller.cs,
// NguniRespeller.cs, IPhonemizer.cs, VoiceTrace.cs.

import Foundation

// MARK: - Tracing

/// A log line that can never take the caller down with it.
public enum VoiceTrace {
    private static let lock = NSLock()
    nonisolated(unsafe) private static var sink: (@Sendable (String) -> Void)?

    public static func setSink(_ s: (@Sendable (String) -> Void)?) {
        lock.lock(); sink = s; lock.unlock()
    }

    public static var enabled: Bool {
        lock.lock(); defer { lock.unlock() }
        return sink != nil
    }

    public static func write(_ line: String) {
        lock.lock(); let s = sink; lock.unlock()
        s?(line)
    }
}

// MARK: - Phonemes

public protocol IPhonemizer: Sendable {
    func phonemize(_ text: String) -> [String]
}

/// The text IS already phonemes. Used when a caller has done its own G2P.
public struct PassthroughPhonemizer: IPhonemizer {
    public init() {}
    public func phonemize(_ text: String) -> [String] {
        text.isEmpty ? [] : PiperPhonemes.split(text)
    }
}

/// Splitting a phoneme string into units.
public enum PiperPhonemes {
    /// One EXTENDED GRAPHEME CLUSTER per phoneme, so a combining tone mark or a
    /// length mark stays attached to the letter it modifies rather than
    /// becoming a phoneme of its own.
    public static func split(_ s: String) -> [String] {
        s.map(String.init).filter { !$0.trimmingCharacters(in: .whitespaces).isEmpty }
    }
}

// MARK: - Attested loanwords

/// Whether a respelling is one people already write, or one this project is
/// proposing. The distinction matters: an attested form can be shipped
/// silently, a proposed one is a suggestion somebody may want to correct.
public enum RespellingSource: Int, Sendable, Equatable {
    case attested = 0
    case proposed
}

public enum LoanwordRespeller {
    /// Keyed case-insensitively; the values are the isiZulu spellings.
    static let zulu: [String: (spelling: String, source: RespellingSource)] = [
        "internet": ("inthanethi", .attested),
        "computer": ("khompiyutha", .attested),
        "phone": ("foni", .attested),
        "email": ("imeyili", .attested),
        "sms": ("esemese", .attested),
        "bank": ("bhange", .attested),
        "account": ("akhawunti", .attested),
        "station": ("siteshi", .attested),
        "radio": ("umsakazo", .attested),
        "taxi": ("theksi", .attested),
        "doctor": ("dokotela", .attested),
        "school": ("sikole", .attested),
        "whatsapp": ("wotsapha", .proposed),
        "wifi": ("wayifayi", .proposed),
        "gps": ("jiphiyesi", .proposed),
        "youtube": ("yuthubhu", .proposed),
        "google": ("gugule", .proposed),
        "facebook": ("feyisibhuku", .proposed),
        "airtime": ("eyathayimu", .proposed),
        "data": ("datha", .proposed),
        "atm": ("eythiyemu", .proposed),
        "pin": ("phini", .proposed),
        "circleai": ("Sekhele Eyi Ayi", .proposed),
    ]

    /// Nothing is respelt for a language that does not need it - an English
    /// voice saying an English word is already correct.
    public static func respell(_ word: String, hostLanguage: String) -> String? {
        guard !word.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty,
              isNguniOrSotho(hostLanguage) else { return nil }
        return zulu[word.lowercased()]?.spelling
    }

    public static func source(of word: String) -> RespellingSource? {
        zulu[word.lowercased()]?.source
    }

    public static var known: [String] { Array(zulu.keys) }

    public static func table(hostLanguage: String) -> [String: String] {
        guard isNguniOrSotho(hostLanguage) else { return [:] }
        return zulu.mapValues(\.spelling)
    }

    /// The Nguni and Sotho-Tswana groups share the sound system this respelling
    /// targets, so one table serves all of them.
    public static func isNguniOrSotho(_ tag: String) -> Bool {
        switch tag.lowercased() {
        case "zu", "zul", "xh", "xho", "ss", "ssw", "nr", "nbl": return true
        case "st", "sot", "nso", "tn", "tsn": return true
        default: return false
        }
    }
}

// MARK: - Deriving a respelling from IPA

/// Turns an English IPA transcription into isiZulu orthography.
///
/// The rule that does the work is VOWEL EPENTHESIS: Nguni syllables are open,
/// so a consonant cluster gets a vowel pushed between its parts and a
/// word-final consonant gets one after it. That is why "WhatsApp" becomes
/// something a Zulu voice can actually say instead of a consonant pile-up.
public enum NguniRespeller {
    static let consonants: [String: String] = [
        "p": "ph", "b": "b", "t": "th", "d": "d",
        "k": "kh", "g": "g", "m": "m", "n": "n",
        "\u{014B}": "ng", "f": "f", "v": "v", "s": "s",
        "z": "z", "\u{0283}": "sh", "\u{0292}": "j", "h": "h",
        "l": "l", "r": "r", "w": "w", "j": "y",
        "\u{03B8}": "th", "\u{00F0}": "d", "\u{02A7}": "tsh", "\u{02A4}": "j",
        "t\u{0283}": "tsh", "d\u{0292}": "j", "\u{0279}": "r", "\u{026B}": "l",
    ]

    static let vowels: [String: String] = [
        "i": "i", "\u{026A}": "i", "i\u{02D0}": "i", "e": "e",
        "\u{025B}": "e", "\u{00E6}": "a", "a": "a", "\u{0251}": "a",
        "\u{0251}\u{02D0}": "a", "\u{028C}": "a", "\u{0259}": "e", "\u{025C}": "e",
        "\u{025C}\u{02D0}": "e", "\u{0252}": "o", "\u{0254}": "o", "\u{0254}\u{02D0}": "o",
        "o": "o", "o\u{028A}": "o", "u": "u", "\u{028A}": "u",
        "u\u{02D0}": "u", "a\u{026A}": "ayi", "a\u{028A}": "awu", "\u{0254}\u{026A}": "oyi",
        "e\u{026A}": "eyi", "\u{026A}\u{0259}": "iye", "e\u{0259}": "eya", "\u{028A}\u{0259}": "uwa",
    ]

    /// The vowel epenthesis reaches for. Chosen because it is the least marked
    /// in the language, so an inserted one reads as part of the word.
    static let defaultVowel = "e"

    public static func fromIpa(_ ipa: String?) -> String {
        guard let ipa, !ipa.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else { return "" }

        var out = ""
        var pendingConsonant = false
        for unit in parse(ipa) {
            if unit.isVowel {
                out += unit.text
                pendingConsonant = false
                continue
            }
            // Two consonants in a row get a vowel between them...
            if pendingConsonant { out += defaultVowel }
            out += unit.text
            pendingConsonant = true
        }
        // ...and a word-final consonant gets one after it.
        if pendingConsonant { out += defaultVowel }
        return out
    }

    /// LONGEST MATCH FIRST, two symbols then one, so an affricate or a
    /// diphthong is read as one unit rather than two.
    static func parse(_ ipa: String) -> [(text: String, isVowel: Bool)] {
        var units: [(String, Bool)] = []

        // UNICODE SCALARS, NOT CHARACTERS. Swift Character is a grapheme
        // cluster, so a tie bar or a combining mark fuses with the letter
        // before it and that letter is then never matched - the consonant just
        // vanishes. C# iterates UTF-16 units; this matches that.
        let scalars = Array(ipa.unicodeScalars)
        var i = 0

        while i < scalars.count {
            let u = scalars[i]
            // Stress marks, syllable dots, spaces, the tie bar and any
            // combining mark carry no segment of their own.
            if u == "\u{02C8}" || u == "\u{02CC}" || u == "." || u == " " || u == "\u{0361}"
                || u.properties.generalCategory == .nonspacingMark {
                i += 1
                continue
            }

            var matched = false
            var len = min(2, scalars.count - i)
            while len >= 1 && !matched {
                let slice = String(String.UnicodeScalarView(scalars[i..<(i + len)]))

                // A following length mark makes this a long vowel.
                if i + len < scalars.count, scalars[i + len] == "\u{02D0}",
                   let longV = vowels[slice + "\u{02D0}"] {
                    units.append((longV, true))
                    i += len + 1
                    matched = true
                } else if let v = vowels[slice] {
                    units.append((v, true))
                    i += len
                    matched = true
                } else if let cns = consonants[slice] {
                    units.append((cns, false))
                    i += len
                    matched = true
                }
                len -= 1
            }
            // A symbol this does not model contributes nothing rather than
            // breaking the whole word.
            if !matched { i += 1 }
        }
        return units.map { (text: $0.0, isVowel: $0.1) }
    }
}

// MARK: - Learned respellings

public struct LearnedWord: Sendable, Equatable {
    public let word: String
    public let respelling: String
    public let learnedAt: Date
    public init(word: String, respelling: String, learnedAt: Date) {
        self.word = word
        self.respelling = respelling
        self.learnedAt = learnedAt
    }
}

/// What THIS person has corrected. A respelling somebody typed themselves
/// outranks both the shipped table and anything derived, because they know how
/// their own name is said and this code does not.
public final class PersonalRespellings: @unchecked Sendable {
    private let lock = NSLock()
    private var learned: [String: LearnedWord] = [:]

    public init() {}

    @discardableResult
    public func learn(word: String, respelling: String, at: Date = Date()) -> Bool {
        let w = word.trimmingCharacters(in: .whitespacesAndNewlines)
        let r = respelling.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !w.isEmpty, !r.isEmpty else { return false }
        lock.lock()
        learned[w.lowercased()] = LearnedWord(word: w, respelling: r, learnedAt: at)
        lock.unlock()
        return true
    }

    public func respell(_ word: String) -> String? {
        lock.lock(); defer { lock.unlock() }
        return learned[word.lowercased()]?.respelling
    }

    @discardableResult
    public func forget(_ word: String) -> Bool {
        lock.lock(); defer { lock.unlock() }
        return learned.removeValue(forKey: word.lowercased()) != nil
    }

    public var all: [LearnedWord] {
        lock.lock(); defer { lock.unlock() }
        return Array(learned.values).sorted { $0.word < $1.word }
    }
}

// MARK: - The respeller

/// Decides how one foreign word should be written so the host voice can say it.
public struct Respeller: Sendable {
    public var hostLanguage: String
    public var personal: PersonalRespellings?
    public var englishPhonemizer: (any IPhonemizer)?

    public init(hostLanguage: String = "",
                personal: PersonalRespellings? = nil,
                englishPhonemizer: (any IPhonemizer)? = nil) {
        self.hostLanguage = hostLanguage
        self.personal = personal
        self.englishPhonemizer = englishPhonemizer
    }

    /// THREE SOURCES, IN THIS ORDER, and the order is the whole design:
    ///   1. what this person corrected  - they know their own words
    ///   2. the attested table          - what people already write
    ///   3. derived from English IPA    - a guess, and only for languages
    ///                                    whose sound system this models
    ///
    /// Returns nil when none applies, so the caller can fall back to spelling
    /// the word out rather than mispronouncing it confidently.
    public func respelling(for word: String) -> String? {
        let w = word.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !w.isEmpty else { return nil }

        if let learned = personal?.respell(w) { return learned }
        if let settled = LoanwordRespeller.respell(w, hostLanguage: hostLanguage) { return settled }

        guard let phonemizer = englishPhonemizer,
              LoanwordRespeller.isNguniOrSotho(hostLanguage) else { return nil }

        let ipa = phonemizer.phonemize(w).joined()
        let derived = NguniRespeller.fromIpa(ipa)
        guard !derived.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else { return nil }

        VoiceTrace.write("derived \(w) -> \(derived) (from \(ipa))")
        return derived
    }
}
