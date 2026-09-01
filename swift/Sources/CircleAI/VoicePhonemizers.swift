// VoicePhonemizers.swift
//
// The three phonemizers that are not a native library: Ge'ez transliteration,
// dictionary lookup, and the espeak-ng subprocess.
//
// Ported from src/CircleAI.Voice/GeezRomanizer.cs, LexiconPhonemizer.cs,
// IPhonemizer.cs and ITtsEngine.cs.

import Foundation

// MARK: - What a front end can report about what it lost

/// Optional on a TTS engine: what the last synthesis could NOT say.
///
/// A front end that drops a symbol still produces audio, so a caller has no way
/// to tell a clean render from one that quietly deleted every 'š' in the
/// sentence. This is how it finds out — and approximations are reported
/// separately from outright drops, because an approximation is a declared
/// substitution and a drop is a hole.
public protocol ITtsFrontEndDiagnostics: AnyObject {
    var lastSkippedCount: Int { get }
    var lastSkippedSymbols: [String] { get }
    var lastApproximatedSymbols: [String] { get }
}

public extension ITtsFrontEndDiagnostics {
    var lastApproximatedSymbols: [String] { [] }
}

// MARK: - Ge'ez

/// Ethiopic text for the Amharic and Tigrinya voices, which cannot read it.
///
/// Meta ships those two MMS models with `is_uroman: true` — their vocabularies
/// are 28 and 27 LATIN letters. Measured on the P30, Amharic fed Ethiopic lost
/// 43 distinct characters and produced 3.2 s of noise for a 15 s paragraph. The
/// model has simply never seen an Ethiopic codepoint.
public final class GeezPhonemizer: IPhonemizer, @unchecked Sendable {

    private let lock = NSLock()
    private var romanised = ""

    public init() {}

    /// What the last call transliterated to. Kept because when a voice sounds
    /// wrong the first question is always whether the transliteration or the
    /// model is at fault, and without this there is no way to tell.
    public var lastRomanised: String {
        lock.lock(); defer { lock.unlock() }
        return romanised
    }

    public func phonemize(_ text: String) -> [String] {
        let r = GeezRomanizer.romanize(text)
        lock.lock(); romanised = r; lock.unlock()
        return r.isEmpty ? [] : VoicePiperConfig.splitPhonemeString(r)
    }
}

// MARK: - Tones

/// A phonemizer that also produces a tone per phoneme.
///
/// Separate from `IPhonemizer` because most languages have no tone channel at
/// all, and a voice that has one needs the two arrays to stay exactly in step.
public protocol IToneSource: AnyObject {
    var lastTones: [Int64] { get }
}

// MARK: - Lexicon lookup

/// Text to phonemes by DICTIONARY LOOKUP, for scripts that do not encode sound.
///
/// Chinese characters carry meaning, not sound, so no character-driven model can
/// read them and no letter-to-sound rule can help. The usual answer is a Python
/// G2P library (pypinyin, jieba, MeCab), which cannot run on the phone. But the
/// sherpa-onnx builds ship the mapping as a plain lexicon.txt beside the model —
/// 195,828 entries for Mandarin, 21,806 for Cantonese. A lookup table is
/// something a Kirin 710 can do.
public final class LexiconPhonemizer: IPhonemizer, IToneSource, @unchecked Sendable {

    private struct Entry {
        let phones: [String]
        let tones: [Int64]
    }

    private let lexicon: [String: Entry]
    /// Longest key in CHARACTERS, so the greedy match knows where to start.
    private let longestEntry: Int

    private let lock = NSLock()
    private var tones: [Int64] = []
    private var unknown: [String] = []

    public var lastTones: [Int64] {
        lock.lock(); defer { lock.unlock() }
        return tones
    }

    /// Characters the lexicon had no entry for. A voice that reads 90% of a
    /// sentence sounds broken rather than absent, so this is how a caller learns
    /// the dictionary is the problem and not the model.
    public var lastUnknownWords: [String] {
        lock.lock(); defer { lock.unlock() }
        return unknown
    }

    public var entryCount: Int { lexicon.count }

    private init(lexicon: [String: Entry], longestEntry: Int) {
        self.lexicon = lexicon
        self.longestEntry = longestEntry
    }

    public static func load(from path: String) throws -> LexiconPhonemizer {
        guard FileManager.default.fileExists(atPath: path) else {
            throw VoiceError.fileNotFound(path)
        }
        let text = try String(contentsOfFile: path, encoding: .utf8)
        return parse(text)
    }

    public static func parse(_ text: String) -> LexiconPhonemizer {
        var map: [String: Entry] = [:]
        var longest = 1

        for raw in text.split(whereSeparator: { $0 == "\n" || $0 == "\r" }) {
            let line = raw.trimmingCharacters(in: .whitespaces)
            if line.isEmpty { continue }

            let parts = line.split(whereSeparator: { $0 == " " || $0 == "\t" }).map(String.init)
            // A word with no pronunciation is unusable.
            if parts.count < 2 { continue }

            let word = parts[0]
            let rest = Array(parts[1...])

            // A TRAILING RUN OF BARE INTEGERS, EXACTLY HALF THE REMAINDER, IS
            // THE TONE CHANNEL. Anything else is all phonemes. Guessing wrong in
            // either direction is silent: read as phonemes, the tone digits get
            // looked up and dropped; read as tones, half the pronunciation
            // disappears.
            var phoneCount = rest.count
            var toneValues: [Int64] = []
            if rest.count % 2 == 0 && rest.count > 0 {
                let half = rest.count / 2
                var allNumeric = true
                var parsed: [Int64] = []
                parsed.reserveCapacity(half)
                for i in half..<rest.count {
                    guard let v = Int64(rest[i]) else { allNumeric = false; break }
                    parsed.append(v)
                }
                if allNumeric {
                    phoneCount = half
                    toneValues = parsed
                }
            }

            map[word] = Entry(phones: Array(rest[0..<phoneCount]), tones: toneValues)
            longest = max(longest, word.count)
        }

        return LexiconPhonemizer(lexicon: map, longestEntry: longest)
    }

    public func phonemize(_ text: String) -> [String] {
        var phones: [String] = []
        var toneOut: [Int64] = []
        var unknownOut: [String] = []

        guard !text.isEmpty else {
            lock.lock(); tones = toneOut; unknown = unknownOut; lock.unlock()
            return phones
        }

        // Characters, not UTF-16 units: a greedy match that counts code units
        // splits an emoji or a surrogate pair down the middle.
        let chars = Array(text)
        var i = 0
        while i < chars.count {
            if chars[i].isWhitespace { i += 1; continue }

            var matched = false
            let maxLen = min(longestEntry, chars.count - i)
            var len = maxLen
            while len >= 1 {
                let candidate = String(chars[i..<(i + len)])
                let entry = lexicon[candidate] ?? lexicon[candidate.lowercased()]
                if let entry {
                    phones.append(contentsOf: entry.phones)
                    // One tone per phone, padded with 0. Without the pad the two
                    // arrays drift apart at the first gap and every syllable
                    // after it gets the wrong tone — audible, and never an error.
                    for k in 0..<entry.phones.count {
                        toneOut.append(k < entry.tones.count ? entry.tones[k] : 0)
                    }
                    i += len
                    matched = true
                    break
                }
                len -= 1
            }

            if !matched {
                let ch = String(chars[i])
                if !unknownOut.contains(ch) { unknownOut.append(ch) }
                i += 1
            }
        }

        lock.lock(); tones = toneOut; unknown = unknownOut; lock.unlock()
        return phones
    }
}

// MARK: - espeak-ng

#if os(macOS) || os(Linux) || os(Windows)

/// Text to IPA by running espeak-ng.
///
/// Out of process on purpose: espeak-ng is GPL, and linking it would make this
/// GPL too. A pipe is a boundary the licence respects.
public final class EspeakPhonemizer: IPhonemizer, @unchecked Sendable {

    private let executable: String
    private let voice: String

    public init(voice: String = "en-us", executable: String = "espeak-ng") {
        self.voice = voice
        self.executable = executable
    }

    public func phonemize(_ text: String) -> [String] {
        guard !text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else { return [] }
        guard let raw = try? run(text) else { return [] }
        return Self.clean(raw)
    }

    private func run(_ text: String) throws -> String {
        let proc = Process()
        proc.executableURL = URL(fileURLWithPath: "/usr/bin/env")
        // -q suppresses audio; --ipa=3 prints IPA with no separators, which is
        // exactly the symbol set in Piper's phoneme_id_map.
        proc.arguments = [executable, "-q", "-v", voice, "--ipa=3"]

        let stdin = Pipe(), stdout = Pipe(), stderr = Pipe()
        proc.standardInput = stdin
        proc.standardOutput = stdout
        proc.standardError = stderr

        try proc.run()

        // THE TEXT GOES IN ON STDIN, NOT AS AN ARGUMENT.
        //
        // espeak-ng reads argv through the ANSI code page on Windows, so
        // Devanagari, Cyrillic, Hangul, Bengali, Sinhala and Arabic never reach
        // it — and it exits 0 with EMPTY output rather than failing, which is
        // the silent kind. Passed as an argument, Hindi, Russian, Korean, Urdu,
        // Bengali and Sinhala all produced nothing at all; fed on stdin as UTF-8
        // all six phonemise correctly. Latin script survives either way, which
        // is precisely why this hid — every language anyone spot-checked in
        // English or French worked.
        //
        // AND IT ENDS WITH A NEWLINE, which is not cosmetic. espeak treats a
        // newline as the end of a clause and will not flush the final one
        // without it. Unterminated, the last character is either dropped or —
        // worse — read as a Unicode character NAME and spoken in English:
        // "안녕하세요 친구" came out "…circumflex micro" said out loud. Every one
        // of those is audible, none of it is an error, and it costs exactly the
        // last character of every utterance.
        stdin.fileHandleForWriting.write(Data((text + "\n").utf8))
        stdin.fileHandleForWriting.closeFile()

        let out = stdout.fileHandleForReading.readDataToEndOfFile()
        _ = stderr.fileHandleForReading.readDataToEndOfFile()
        proc.waitUntilExit()

        return String(decoding: out, as: UTF8.self)
    }

    /// Strips espeak's language-switch markers and folds the output to one line.
    ///
    /// "(en)hello(ko)" — espeak annotates a switch when the text is not in the
    /// voice's own language. They are not phonemes; left in, the LETTERS inside
    /// the brackets get mapped and spoken aloud.
    static func clean(_ raw: String) -> [String] {
        var s = raw.replacingOccurrences(of: "\r", with: "")
            .replacingOccurrences(of: "\n", with: " ")
            .trimmingCharacters(in: .whitespaces)

        var out = ""
        var depth = 0
        for c in s {
            if c == "(" { depth += 1; continue }
            if c == ")" { if depth > 0 { depth -= 1 }; continue }
            if depth == 0 { out.append(c) }
        }
        s = out.trimmingCharacters(in: .whitespaces)

        return s.isEmpty ? [] : VoicePiperConfig.splitPhonemeString(s)
    }
}

#endif
