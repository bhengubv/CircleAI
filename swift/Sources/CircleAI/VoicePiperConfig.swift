// VoicePiperConfig.swift
//
// Ports of src/CircleAI.Voice/PiperVoiceConfig.cs, LexiconTokeniser.cs and
// AudioFormat.cs.
//
// Parity is asserted against fixtures/voice_piper_config.json,
// fixtures/voice_lexicon_tokeniser.json and fixtures/voice_audio_format.json.

import Foundation

/// A PCM audio format expected or produced by voice components.
public struct VoiceAudioFormat: Equatable, Sendable {
    public let sampleRate: Int
    public let channels: Int
    public let bitsPerSample: Int

    public init(sampleRate: Int, channels: Int, bitsPerSample: Int) {
        self.sampleRate = sampleRate
        self.channels = channels
        self.bitsPerSample = bitsPerSample
    }

    /// Canonical input format: PCM signed 16-bit, mono, 16 kHz. Most
    /// open-source ASR engines (sherpa-onnx, Vosk) accept this directly.
    public static let pcm16Mono16k = VoiceAudioFormat(sampleRate: 16000, channels: 1, bitsPerSample: 16)
}

/// What a `phonemesToIds` call did, beyond the ids.
public struct VoicePhonemeMapping: Equatable {
    public let ids: [Int64]
    /// How many symbols the vocabulary had no entry for.
    public let skipped: Int
    /// WHICH symbols were dropped. A dropped symbol is inaudible, so this list
    /// is the only evidence a front-end is broken.
    public let skippedSymbols: [String]
    /// Symbols APPROXIMATED rather than spoken exactly — a diacritic the voice
    /// lacks, folded to its base letter. A compromise, not a success, so it is
    /// reported separately.
    public let approximatedSymbols: [String]
}

/// A Piper-layout voice's phoneme→id vocabulary and inference settings.
public final class VoicePiperConfig {

    // Piper's special phoneme symbols (piper-phonemize defaults).
    private static let pad = "_"
    private static let bos = "^"
    private static let eos = "$"

    private let phonemeIdMap: [String: [Int64]]

    public let sampleRate: Int
    public let noiseScale: Float
    public let lengthScale: Float
    public let noiseW: Float
    /// e.g. `espeak` (needs a phonemizer) or `text` (graphemes are phonemes).
    public let phonemeType: String

    public init(map: [String: [Int64]], sampleRate: Int = 22050,
                noiseScale: Float = 0.667, lengthScale: Float = 1.0,
                noiseW: Float = 0.8, phonemeType: String = "espeak") {
        self.phonemeIdMap = map
        self.sampleRate = sampleRate
        self.noiseScale = noiseScale
        self.lengthScale = lengthScale
        self.noiseW = noiseW
        self.phonemeType = phonemeType
    }

    /// True when this config has a usable phoneme→id map.
    public var hasPhonemeMap: Bool { !phonemeIdMap.isEmpty }

    /// THE PAD RULE: the id THIS voice uses for blank.
    ///
    /// It is 0 in sherpa/MMS exports and 3 in Piper-family ones, and pointing it
    /// at an ordinary vocabulary entry is what made 42 MMS voices speak fluent
    /// nonsense. Never assume a constant — read it from the model. Falls back to
    /// 0 only when the vocabulary has no `_` at all.
    public var padId: Int64 { phonemeIdMap[Self.pad]?.first ?? 0 }

    /// Parse a Piper `.onnx.json` sidecar.
    public static func parse(_ root: [String: Any]) -> VoicePiperConfig {
        var sampleRate = 22050
        if let audio = root["audio"] as? [String: Any],
           let sr = audio["sample_rate"] as? NSNumber { sampleRate = sr.intValue }

        var noise: Float = 0.667, length: Float = 1.0, noiseW: Float = 0.8
        if let inference = root["inference"] as? [String: Any] {
            if let v = inference["noise_scale"] as? NSNumber { noise = v.floatValue }
            if let v = inference["length_scale"] as? NSNumber { length = v.floatValue }
            if let v = inference["noise_w"] as? NSNumber { noiseW = v.floatValue }
        }

        let phonemeType = root["phoneme_type"] as? String ?? "espeak"

        var map: [String: [Int64]] = [:]
        if let pim = root["phoneme_id_map"] as? [String: Any] {
            for (symbol, value) in pim {
                if let arr = value as? [NSNumber] { map[symbol] = arr.map { $0.int64Value } }
            }
        }

        return VoicePiperConfig(map: map, sampleRate: sampleRate, noiseScale: noise,
                                lengthScale: length, noiseW: noiseW, phonemeType: phonemeType)
    }

    /// Turn a phoneme sequence into model token ids, in piper-phonemize's exact
    /// layout with interspersed pad:
    /// `[BOS, PAD, id(p1), PAD, id(p2), PAD, …, id(pN), PAD, EOS]`.
    ///
    /// BOS and EOS appear only when the vocabulary has them — the MMS-family
    /// exports do not. Unknown symbols are SKIPPED and REPORTED, never fatal: a
    /// single unknown symbol must not abort the whole utterance.
    public func phonemesToIds(_ phonemes: [String]) -> VoicePhonemeMapping {
        var ids: [Int64] = []
        var dropped: [String] = []
        var approximated: [String] = []
        var skipped = 0

        if let b = phonemeIdMap[Self.bos] { ids += b }
        let pad = phonemeIdMap[Self.pad]
        if let p = pad { ids += p }

        for phoneme in phonemes {
            guard let mapped = mapSymbol(phoneme) else {
                skipped += 1
                if !dropped.contains(phoneme) { dropped.append(phoneme) }
                continue
            }
            if mapped.approximated && !approximated.contains(phoneme) {
                approximated.append(phoneme)
            }
            ids += mapped.ids
            if let p = pad { ids += p }
        }

        if let e = phonemeIdMap[Self.eos] { ids += e }

        return VoicePhonemeMapping(ids: ids, skipped: skipped,
                                   skippedSymbols: dropped, approximatedSymbols: approximated)
    }

    /// Split a phoneme string into grapheme clusters.
    ///
    /// Swift Strings iterate by grapheme cluster natively, which is exactly the
    /// unit wanted here — "bát" is three elements, not four.
    public static func splitPhonemeString(_ s: String) -> [String] {
        s.map(String.init)
    }

    // ── Symbol lookup ───────────────────────────────────────────────────────

    private func mapSymbol(_ symbol: String) -> (ids: [Int64], approximated: Bool)? {
        if let exact = phonemeIdMap[symbol] { return (exact, false) }

        // A grapheme voice's vocabulary is built AFTER the training text has
        // been through the model's own cleaner, and every cleaner in use here
        // lower-cases. Such a vocab contains no capitals at all, so matching on
        // the raw character silently discarded every sentence-initial letter —
        // the model received "awubona" for "Sawubona".
        let lower = symbol.lowercased()
        if lower != symbol, let l = phonemeIdMap[lower] { return (l, false) }

        // A GRAPHEME CLUSTER the vocabulary stores as separate codepoints.
        // Burmese "ကြို" arrives as ONE symbol while the vocabulary holds each
        // codepoint on its own. Splitting it back keeps every mark, so this must
        // be tried BEFORE any approximation.
        if symbol.unicodeScalars.count > 1 {
            var parts: [Int64] = []
            var whole = true
            for scalar in symbol.unicodeScalars {
                // Zero-width formatting characters shape how text is DRAWN and
                // say nothing about how it sounds. Persian writes them
                // constantly, as do most Indic scripts, and one invisible
                // character was failing the whole cluster.
                if scalar.properties.generalCategory == .format { continue }
                let s = String(scalar)
                if let part = phonemeIdMap[s] ?? phonemeIdMap[s.lowercased()] {
                    parts += part
                } else {
                    whole = false
                    break
                }
            }
            if whole && !parts.isEmpty { return (parts, false) }  // exact — nothing lost
        }

        // A letter the voice never learned. Dropping it deletes a consonant from
        // the middle of a word, so an approximation is worth more than a hole —
        // so long as it is declared rather than passed off as correct.
        for candidate in Self.approximations(symbol) {
            if let a = phonemeIdMap[candidate] ?? phonemeIdMap[candidate.lowercased()] {
                return (a, true)
            }
        }

        return nil
    }

    private static func approximations(_ symbol: String) -> [String] {
        var out: [String] = []

        // Where the vocabulary carries the true phoneme under a different
        // spelling, use it — Tshivenda's ṅ IS /ŋ/, so that loses nothing at all.
        if symbol == "ṅ" || symbol == "Ṅ" { out.append("ŋ") }
        if symbol == "š" || symbol == "Š" { out.append("ʃ") }

        // Folding a diacritic away is only defensible where the mark modifies a
        // letter that still carries most of the sound without it — Latin š→s,
        // ṱ→t. In Thai, Burmese, Devanagari, Arabic and Vietnamese the marks ARE
        // the vowels and tones; dropping them does not approximate the word, it
        // deletes it. Thai measured 4.3 s instead of ~15 s because every vowel
        // sign was folded off a consonant and filed as a harmless approximation.
        let stripped = stripDiacritics(symbol)
        guard !stripped.isEmpty, stripped != symbol, isLatinBase(stripped) else { return out }
        out.append(stripped)
        return out
    }

    /// True when the symbol's base is Latin, i.e. stripping its marks leaves a
    /// letter that still approximates the original sound.
    ///
    /// Judges the BASE that remains, not the composed character: Tshivenda ṱ
    /// lives at U+1E71, far above the Latin block, yet strips to a plain 't'.
    /// Thai วั strips to ว, which is not Latin at all — the case to refuse.
    private static func isLatinBase(_ stripped: String) -> Bool {
        guard !stripped.isEmpty else { return false }
        return stripped.unicodeScalars.allSatisfy { $0.value <= 0x024F }
    }

    /// Decompose and remove combining marks: ṱ → t.
    private static func stripDiacritics(_ s: String) -> String {
        String(String.UnicodeScalarView(
            s.decomposedStringWithCanonicalMapping.unicodeScalars.filter {
                !($0.properties.generalCategory == .nonspacingMark
                  || $0.properties.generalCategory == .spacingMark
                  || $0.properties.generalCategory == .enclosingMark)
            }))
    }
}

/// Turns text into model tokens using a voice's own lexicon files.
///
/// Pronunciation as a FILE, which is what makes these voices shippable: a
/// word→phoneme table and a phoneme→id table beside the model. No phonemizer
/// process, no second package, no licence wall.
public final class VoiceLexiconTokeniser {

    private let words: [String: [Int64]]
    private let longest: Int

    /// Blank id, interleaved between tokens when the model expects it.
    public var blank: Int64 = 0

    /// Symbols the lexicon had no entry for on the last call.
    public private(set) var lastUnmapped: [String] = []

    public init(words: [String: [Int64]], blank: Int64 = 0) {
        self.words = words
        self.longest = words.keys.map(\.count).max() ?? 1
        self.blank = blank
    }

    /// Build from a voice's `tokens.txt` and `lexicon.txt` content.
    public static func from(tokensText: String, lexiconText: String, blank: Int64 = 0)
        -> VoiceLexiconTokeniser?
    {
        // tokens.txt is "<symbol> <id>" per line. The symbol MAY BE A SPACE, so
        // split on the LAST space rather than the first.
        var ids: [String: Int64] = [:]
        for line in tokensText.split(separator: "\n", omittingEmptySubsequences: true) {
            let text = String(line).trimmingCharacters(in: .newlines)
            guard let cut = text.lastIndex(of: " ") else { continue }
            let symbol = String(text[text.startIndex..<cut])
            guard !symbol.isEmpty,
                  let id = Int64(text[text.index(after: cut)...]) else { continue }
            ids[symbol] = id
        }
        guard !ids.isEmpty else { return nil }

        // lexicon.txt is "<word> <phoneme> <phoneme> ...".
        var words: [String: [Int64]] = [:]
        for line in lexiconText.split(separator: "\n", omittingEmptySubsequences: true) {
            let parts = String(line).trimmingCharacters(in: .newlines)
                .split(separator: " ", omittingEmptySubsequences: true).map(String.init)
            guard parts.count >= 2 else { continue }
            let seq = parts.dropFirst().compactMap { ids[$0] }
            guard !seq.isEmpty else { continue }
            words[parts[0]] = seq
        }
        return words.isEmpty ? nil : VoiceLexiconTokeniser(words: words, blank: blank)
    }

    /// Segment `text` and return the model's tokens.
    ///
    /// LONGEST MATCH FIRST, because these lexicons are word-keyed and the words
    /// overlap: あい, あいさつ and あいかわらず all start the same way, and taking
    /// the shortest would pronounce a different word. Falls back to the single
    /// character when no word matches.
    public func encode(_ text: String, interleaveBlank: Bool = true) -> [Int64] {
        var out: [Int64] = []
        var unmapped: [String] = []
        let chars = Array(text)
        var i = 0

        while i < chars.count {
            var taken = 0
            let max = Swift.min(longest, chars.count - i)
            if max > 0 {
                for len in stride(from: max, through: 1, by: -1) {
                    let candidate = String(chars[i..<(i + len)])
                    if let seq = words[candidate] {
                        out += seq
                        taken = len
                        break
                    }
                }
            }

            if taken == 0 {
                let c = chars[i]
                if !c.isWhitespace { unmapped.append(String(c)) }
                taken = 1
            }
            i += taken
        }

        lastUnmapped = unmapped
        guard interleaveBlank else { return out }

        // add_blank: a blank opens the utterance and follows every token.
        var padded: [Int64] = [blank]
        for id in out { padded.append(id); padded.append(blank) }
        return padded
    }
}
