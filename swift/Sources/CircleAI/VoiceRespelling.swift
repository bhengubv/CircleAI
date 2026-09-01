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

/// Where a word has got to in the learning process.
public enum LearningState: Int, Sendable, Equatable, Codable, CaseIterable {
    /// Still listening. Nothing has changed how the word is spoken.
    case listening = 0
    /// Five hearings agreed; the new spelling is in use and awaiting its check.
    case adopted
    /// The check passed. This is how the word is said for this person.
    case confirmed
}

/// What has been learned about one word.
public struct LearnedWord: Sendable, Equatable, Codable {
    public let word: String
    /// The spelling in use, or nil while still listening.
    public let spelling: String?
    public let state: LearningState
    /// Each candidate spelling and how many hearings agreed on it.
    public let candidates: [String: Int]

    public init(word: String, spelling: String?, state: LearningState,
                candidates: [String: Int] = [:]) {
        self.word = word
        self.spelling = spelling
        self.state = state
        self.candidates = candidates
    }
}

/// What THIS person has corrected, and what listening to them has taught.
///
/// A respelling somebody typed themselves outranks both the shipped table and
/// anything derived, because they know how their own name is said and this code
/// does not. Below that sits what repeated hearings agree on.
///
/// THE FIVE-HEARINGS RULE AND THE CHECK AFTER IT. One hearing is a mishearing;
/// five that agree is a pattern. But agreeing five times only proves the ASR is
/// consistent, not that the new spelling is right — so an adopted word is put
/// INTO USE and the next hearing is read as the test of it. If they say it our
/// new way, it is confirmed; if they do not, we were wrong, the candidate is
/// struck out, and the evidence rebuilds from scratch.
///
/// Ported from src/CircleAI.Voice/PersonalRespellings.cs.
public final class PersonalRespellings: @unchecked Sendable {

    /// Hearings that must agree before a spelling goes into use.
    public static let adoptAfter = 5

    /// How far a hearing may sit from the word and still be that word, as a
    /// fraction of the longer of the two. Beyond this the speaker was saying
    /// something else and the hearing is not evidence about anything.
    public static let maxDifference = 0.40

    private final class Entry {
        var candidates: [String: Int] = [:]
        var spelling: String?
        var state: LearningState = .listening
    }

    private let lock = NSLock()
    private var words: [String: Entry] = [:]     // keyed lowercased
    private var originalCase: [String: String] = [:]
    private var dirty = false

    public init() {}

    /// Whether there is learning that has not reached disk. A year of learning
    /// that vanishes on restart is not learning.
    public var hasUnsavedChanges: Bool {
        lock.lock(); defer { lock.unlock() }
        return dirty
    }

    /// The spelling in use for a word, or nil while it is still being learned.
    public func respell(_ word: String) -> String? {
        lock.lock(); defer { lock.unlock() }
        guard let e = words[word.lowercased()],
              e.state == .adopted || e.state == .confirmed else { return nil }
        return e.spelling
    }

    public var all: [LearnedWord] {
        lock.lock(); defer { lock.unlock() }
        return words.map { key, e in
            LearnedWord(word: originalCase[key] ?? key, spelling: e.spelling,
                        state: e.state, candidates: e.candidates)
        }.sorted { $0.word < $1.word }
    }

    /// Somebody typed this in themselves.
    ///
    /// Straight to confirmed, skipping the evidence entirely: a person stating
    /// how their own name is said is stronger than any number of hearings, and
    /// putting it through the five-hearing rule would ignore them four times
    /// first.
    @discardableResult
    public func learn(word: String, respelling: String) -> Bool {
        let w = word.trimmingCharacters(in: .whitespacesAndNewlines)
        let r = respelling.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !w.isEmpty, !r.isEmpty else { return false }

        lock.lock()
        let key = w.lowercased()
        let e = words[key] ?? Entry()
        e.spelling = r
        e.state = .confirmed
        words[key] = e
        originalCase[key] = w
        dirty = true
        lock.unlock()
        return true
    }

    /// One hearing of a word. Returns true only when this hearing CHANGED how
    /// the word is spoken.
    @discardableResult
    public func observe(word: String, heard: String, currentSpelling: String? = nil) -> Bool {
        let w = word.trimmingCharacters(in: .whitespacesAndNewlines)
        let h = heard.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !w.isEmpty, !h.isEmpty else { return false }

        lock.lock(); defer { lock.unlock() }
        return observeLocked(word: w, heard: h, currentSpelling: currentSpelling)
    }

    private func observeLocked(word: String, heard: String, currentSpelling: String?) -> Bool {
        let key = word.lowercased()
        let existing = words[key]
        let reference = existing?.spelling ?? currentSpelling ?? word

        // Too far from the word to BE that word. Checked BEFORE the entry is
        // created, so a rejected hearing leaves no trace — otherwise every
        // unrelated word in earshot would litter the table with empty entries
        // and show up in a "words your CircleAI knows" view.
        guard Self.isSameWord(reference, heard) else { return false }

        let entry: Entry
        if let existing {
            entry = existing
        } else {
            entry = Entry()
            words[key] = entry
            originalCase[key] = word
        }

        // THE CHECK. A word adopted last time is now being said our new way;
        // this hearing is the test of whether we got it right.
        if entry.state == .adopted, let adopted = entry.spelling {
            if Self.agrees(adopted, heard) {
                entry.state = .confirmed
                dirty = true
                return false                        // confirmed, but nothing changed
            }
            // We were wrong. Undo it and let the evidence rebuild — including
            // this hearing, which is evidence for something else.
            entry.candidates.removeValue(forKey: adopted)
            entry.spelling = nil
            entry.state = .listening
            dirty = true
        }

        // They said it the way we already say it. That is agreement, not a
        // lesson — and counting it would build a personal entry that overrides
        // the shipped spelling with an identical one, for no reason.
        if Self.agrees(entry.spelling ?? currentSpelling, heard) { return false }

        let count = (entry.candidates[heard] ?? 0) + 1
        entry.candidates[heard] = count
        dirty = true

        guard count >= Self.adoptAfter else { return false }

        entry.spelling = heard
        entry.state = .adopted
        return true
    }

    /// Reads a whole transcript against the spellings currently in use and
    /// returns the words whose pronunciation this changed.
    @discardableResult
    public func learnFrom(transcript: String?, currentSpellings: [String: String]) -> [String] {
        guard let transcript,
              !transcript.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty,
              !currentSpellings.isEmpty
        else { return [] }

        let separators = CharacterSet(charactersIn: " \t\n\r,.?!;:\"")
        let tokens = transcript
            .components(separatedBy: separators)
            .map { t -> String in
                // A hyphenated form is the tail: "e-mail" is heard as "mail".
                if let i = t.lastIndex(of: "-") { return String(t[t.index(after: i)...]) }
                return t
            }
            .filter { $0.count > 1 }
        guard !tokens.isEmpty else { return [] }

        var changed: [String] = []
        for (word, spelling) in currentSpellings.sorted(by: { $0.key < $1.key }) {
            // The NEAREST token to how we say it today. Nearest, not merely
            // close: a sentence can hold two similar words and picking the wrong
            // one would teach the wrong lesson.
            let reference = respell(word) ?? spelling
            let scored = tokens
                .map { ($0, Self.editDistance($0.lowercased(), reference.lowercased())) }
                .min { $0.1 < $1.1 }!

            let allowed = Double(max(reference.count, scored.0.count)) * Self.maxDifference
            if Double(scored.1) > allowed { continue }      // that word was not said

            if observe(word: word, heard: scored.0, currentSpelling: spelling) {
                changed.append(word)
            }
        }
        return changed
    }

    @discardableResult
    public func forget(_ word: String) -> Bool {
        lock.lock(); defer { lock.unlock() }
        let key = word.lowercased()
        let removed = words.removeValue(forKey: key) != nil
        originalCase.removeValue(forKey: key)
        if removed { dirty = true }
        return removed
    }

    // MARK: - Keeping it between sessions
    //
    // This is the person's own file, on their own device: never uploaded, never
    // shared, never merged with anybody else's. Two people's files will disagree
    // about the same word, and that is the whole point.

    public func save(to path: String) throws {
        let snapshot = all
        let data = try JSONEncoder().encode(snapshot)

        let dir = (path as NSString).deletingLastPathComponent
        if !dir.isEmpty {
            try? FileManager.default.createDirectory(atPath: dir, withIntermediateDirectories: true)
        }

        // Written beside and moved into place, so an interrupted save leaves the
        // previous table intact rather than a half-written one.
        let temp = path + ".tmp"
        try data.write(to: URL(fileURLWithPath: temp))
        _ = try? FileManager.default.removeItem(atPath: path)
        try FileManager.default.moveItem(atPath: temp, toPath: path)

        // Only once the bytes are actually in place. Clearing it before the move
        // would mean a failed write leaves the table looking saved.
        lock.lock(); dirty = false; lock.unlock()
    }

    /// An unreadable file starts over rather than refusing to start. Losing the
    /// table costs tuning; refusing to start costs the whole voice.
    public static func load(from path: String) -> PersonalRespellings {
        let table = PersonalRespellings()
        guard FileManager.default.fileExists(atPath: path),
              let data = try? Data(contentsOf: URL(fileURLWithPath: path)),
              let snapshot = try? JSONDecoder().decode([LearnedWord].self, from: data)
        else { return table }

        for s in snapshot {
            let e = Entry()
            e.spelling = s.spelling
            e.state = s.state
            e.candidates = s.candidates
            let key = s.word.lowercased()
            table.words[key] = e
            table.originalCase[key] = s.word
        }
        return table
    }

    // MARK: - Comparing what was heard to what was meant

    static func agrees(_ a: String?, _ b: String) -> Bool {
        guard let a else { return false }
        return a.caseInsensitiveCompare(b) == .orderedSame
    }

    static func isSameWord(_ a: String, _ b: String) -> Bool {
        if agrees(a, b) { return true }
        let longest = max(a.count, b.count)
        guard longest > 0 else { return false }
        return Double(editDistance(a.lowercased(), b.lowercased())) / Double(longest)
            <= maxDifference
    }

    /// Levenshtein over two rolling rows rather than a full matrix — the table
    /// is never inspected, only its last row, and a phone has better uses for
    /// the memory.
    static func editDistance(_ a: String, _ b: String) -> Int {
        let x = Array(a), y = Array(b)
        if x.isEmpty { return y.count }
        if y.isEmpty { return x.count }

        var prev = Array(0...y.count)
        var cur = [Int](repeating: 0, count: y.count + 1)

        for i in 1...x.count {
            cur[0] = i
            for j in 1...y.count {
                let cost = x[i - 1] == y[j - 1] ? 0 : 1
                cur[j] = min(min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost)
            }
            swap(&prev, &cur)
        }
        return prev[y.count]
    }
}

// MARK: - The respeller

/// Decides how one foreign word should be written so the host voice can say it.
public struct Respeller: Sendable {
    public var hostLanguage: String
    public var personal: PersonalRespellings?
    public var englishPhonemizer: (any IPhonemizer)?

    /// Where this says what it changed. A respelling that fires silently is
    /// indistinguishable from one that never ran, and both sound like a voice
    /// that simply cannot say the word.
    public var log: (@Sendable (String) -> Void)?

    public init(hostLanguage: String = "",
                personal: PersonalRespellings? = nil,
                englishPhonemizer: (any IPhonemizer)? = nil,
                log: (@Sendable (String) -> Void)? = nil) {
        self.hostLanguage = hostLanguage
        self.personal = personal
        self.englishPhonemizer = englishPhonemizer
        self.log = log
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
