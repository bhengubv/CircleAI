// VoiceXsampaToIpa.swift
//
// Port of src/CircleAI.Voice/XsampaToIpa.cs. Turns the X-SAMPA that the NCHLT
// phonemiser emits into the IPA that Mimic3-family voices are trained on.
//
// Parity is asserted against fixtures/voice_xsampa_to_ipa.json, which the C#
// reference generates. If this file and that file disagree, one of them is wrong
// and the test says which case.

import Foundation

/// X-SAMPA → IPA for the 11 South African languages.
public enum VoiceXsampaToIpa {

    /// Every phone in the NCHLT Afrikaans dictionary, mapped to IPA.
    ///
    /// Derived from the corpus, not from memory: these are exactly the distinct
    /// phones in `nchlt_afr.dict`, and every IPA character was checked against the
    /// target voice's own token table. A hand-recalled table is how the Ethiopic
    /// romaniser silently dropped characters.
    private static let map: [String: String] = [
        // Vowels
        "a": "a", "A:": "ɑː", "A:r": "ɑːr",
        "E": "ɛ", "O": "ɔ", "@": "ə",
        "i": "i", "u": "u", "y": "y",
        "9": "œ", "2:": "øː", "{": "æ",

        // Diphthongs — NCHLT gives one token, the voice wants both elements.
        "9y": "œy", "@i": "əi", "@u": "əu",
        "i@": "iə", "u@": "uə",

        // Consonants
        "b": "b", "d": "d", "f": "f",
        // U+0261 LATIN SMALL LETTER SCRIPT G — the IPA letter, NOT ASCII 'g'.
        // The voice's vocabulary carries ɡ; a plain 'g' would miss and be dropped.
        "g": "\u{0261}",
        "j": "j", "k": "k", "l": "l",
        "m": "m", "n": "n", "N": "ŋ",
        "p": "p", "r": "r", "s": "s",
        "S": "ʃ", "t": "t", "v": "v",
        "w": "w", "x": "x", "z": "z",
        "Z": "ʒ",

        // APPROXIMATION, DELIBERATE AND THE ONLY ONE. X-SAMPA h\ is ɦ, the voiced
        // glottal fricative Afrikaans uses in "hond". This voice's vocabulary has
        // no ɦ, only h. Voicing is lost; place and manner are right, so the word
        // stays recognisable.
        "h\\": "h",
    ]

    /// Phones the last `convert` call could not map.
    ///
    /// Empty is the good case. An unmapped phone produces NO SOUND and the audio
    /// is merely shorter — every acoustic measure still passes. Counting them is
    /// the only way a caller can refuse rather than speak a shorter sentence than
    /// it was given.
    public private(set) static var lastUnmapped: [String] = []

    /// Convert X-SAMPA phone tokens to a flat IPA symbol list.
    ///
    /// LONGEST MATCH ON WHOLE TOKENS. Several entries are multi-character
    /// (`A:r`, `@i`, `9y`) and NCHLT emits them as single tokens; matching on the
    /// token — never character by character — is what keeps `A:r` from becoming
    /// `A` + `:` + `r`.
    @discardableResult
    public static func convert(_ xsampa: [String]) -> [String] {
        var ipa: [String] = []
        ipa.reserveCapacity(xsampa.count + 8)
        var unmapped: [String] = []

        for phone in xsampa {
            if phone.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty { continue }

            if let mapped = map[phone] {
                // Emit per-character: the voice tokenises ɑ, ː and r separately,
                // so "ɑːr" must arrive as three symbols, not one.
                for ch in mapped { ipa.append(String(ch)) }
                continue
            }

            if !unmapped.contains(phone) { unmapped.append(phone) }
        }

        lastUnmapped = unmapped
        return ipa
    }

    /// True when every phone in `xsampa` has a mapping.
    public static func canSayAll(_ xsampa: [String]) -> Bool {
        for p in xsampa
        where !p.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty && map[p] == nil {
            return false
        }
        return true
    }

    /// The X-SAMPA phones this table knows — for tests and diagnostics.
    public static var knownPhones: [String] { Array(map.keys) }
}
