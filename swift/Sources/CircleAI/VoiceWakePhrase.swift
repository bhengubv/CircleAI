// VoiceWakePhrase.swift
//
// Judging a wake phrase before somebody relies on it: can the listener even
// represent it, is it long enough to hear across a room, and does it collide
// with one already in the book.
//
// Ported from src/CircleAI.Voice/WakePhraseBook.cs.

import Foundation

public enum WakePhraseVerdict: Int, Sendable, Equatable {
    case good = 0
    case caution
    case unusable
}

public struct WakePhrase: Sendable, Equatable {
    public let text: String
    public let tokens: [String]
    public let verdict: WakePhraseVerdict
    /// Said to a PERSON, not logged - this is what appears under the field.
    public let advice: String
    public let threshold: Double?
    public let boost: Double?

    public init(text: String, tokens: [String], verdict: WakePhraseVerdict,
                advice: String, threshold: Double? = nil, boost: Double? = nil) {
        self.text = text
        self.tokens = tokens
        self.verdict = verdict
        self.advice = advice
        self.threshold = threshold
        self.boost = boost
    }

    func with(tokens: [String]) -> WakePhrase {
        WakePhrase(text: text, tokens: tokens, verdict: verdict, advice: advice,
                   threshold: threshold, boost: boost)
    }
}

public final class WakePhraseBook: @unchecked Sendable {
    /// Below this, a phrase is too short to be heard reliably from across a
    /// room. Not a hard refusal - a caution, because the user may still want it.
    public static let minReliableTokens = 4

    /// Words common enough that a wake phrase built only from them will fire
    /// while somebody is talking to another person.
    static let everyday: Set<String> = [
        "circle", "listen", "hello", "hey", "okay", "ok", "yes", "no", "stop", "go",
        "play", "open", "close", "help", "please", "wait", "back", "up", "down",
        "phone", "call", "text", "time", "now", "today", "one", "two", "three",
    ]

    public static let candidatesByLanguage: [String: [String]] = [
        "en": ["Hey B"],
        "ja": ["\u{30D3}\u{30FC}\u{3055}\u{3093}", "\u{30D3}\u{30FC}\u{3055}\u{307E}", "Bee san"],
        "ko": ["\u{BE44} \u{B2D8}", "Bee nim"],
        "zh": ["\u{5C0F}B", "Xiao B"],
        "yue": ["\u{5C0F}B", "Siu B"],
    ]

    /// en-ZA and en both find the English list: the region is dropped, and an
    /// unknown language falls back to English rather than to nothing.
    public static func candidates(for languageCode: String?) -> [String] {
        var code = (languageCode ?? "").trimmingCharacters(in: .whitespaces)
        if let cut = code.firstIndex(of: "-"), cut != code.startIndex {
            code = String(code[code.startIndex..<cut])
        }
        return candidatesByLanguage[code.lowercased()] ?? candidatesByLanguage["en"]!
    }

    private let tokenizer: SentencePieceTokenizer
    private var stored: [WakePhrase] = []

    public init(tokenizer: SentencePieceTokenizer) { self.tokenizer = tokenizer }

    public var phrases: [WakePhrase] { stored }

    /// The best usable candidate for a language: the LONGEST one, because more
    /// tokens means fewer false wakes.
    public func best(for languageCode: String?) -> WakePhrase? {
        var best: WakePhrase?
        for candidate in Self.candidates(for: languageCode) {
            let judged = evaluate(candidate)
            if judged.verdict == .unusable { continue }
            if best == nil || judged.tokens.count > best!.tokens.count { best = judged }
        }
        return best
    }

    public func evaluate(_ text: String, threshold: Double? = nil,
                         boost: Double? = nil) -> WakePhrase {
        let trimmed = text.trimmingCharacters(in: .whitespacesAndNewlines)
        if trimmed.isEmpty {
            return WakePhrase(text: trimmed, tokens: [], verdict: .unusable,
                              advice: "Type something to say.", threshold: threshold, boost: boost)
        }

        let tokens = tokenizer.encode(trimmed)

        let (ok, unknown) = tokenizer.canRepresent(trimmed)
        if !ok {
            return WakePhrase(text: trimmed, tokens: tokens, verdict: .unusable,
                advice: "This wake word uses sounds the listener does not know "
                      + "(\(unknown.joined(separator: ", "))). Try a different word.",
                threshold: threshold, boost: boost)
        }

        // A PREFIX COLLISION makes one of the two phrases dead: the shorter one
        // always fires first, so the longer can never complete.
        for other in stored {
            if Self.startsWith(tokens, prefix: other.tokens) {
                return WakePhrase(text: trimmed, tokens: tokens, verdict: .unusable,
                    advice: "\(other.text) would always trigger first, so this one could never work. "
                          + "Remove that one, or pick something that does not start the same way.",
                    threshold: threshold, boost: boost)
            }
            if Self.startsWith(other.tokens, prefix: tokens) {
                return WakePhrase(text: trimmed, tokens: tokens, verdict: .unusable,
                    advice: "This would always trigger before \(other.text), which would stop working.",
                    threshold: threshold, boost: boost)
            }
        }

        if tokens.count < Self.minReliableTokens {
            return WakePhrase(text: trimmed, tokens: tokens, verdict: .caution,
                advice: "This is very short, so it may not be heard from across a room. "
                      + "A slightly longer phrase is more reliable.",
                threshold: threshold, boost: boost)
        }

        let words = trimmed.split(separator: " ", omittingEmptySubsequences: true)
            .map { $0.trimmingCharacters(in: CharacterSet(charactersIn: ",.!?")).lowercased() }
        if !words.isEmpty && words.allSatisfy({ Self.everyday.contains($0) }) {
            return WakePhrase(text: trimmed, tokens: tokens, verdict: .caution,
                advice: "These are everyday words, so it may wake up when you are talking to someone else.",
                threshold: threshold, boost: boost)
        }

        return WakePhrase(text: trimmed, tokens: tokens, verdict: .good, advice: "",
                          threshold: threshold, boost: boost)
    }

    /// Adds when usable. An unusable phrase is NOT stored, so the book can
    /// never hold one that cannot fire.
    @discardableResult
    public func tryAdd(_ text: String, threshold: Double? = nil,
                       boost: Double? = nil) -> (added: Bool, phrase: WakePhrase) {
        let phrase = evaluate(text, threshold: threshold, boost: boost)
        if phrase.verdict == .unusable { return (false, phrase) }
        stored.append(phrase)
        return (true, phrase)
    }

    @discardableResult
    public func remove(_ text: String) -> Bool {
        let before = stored.count
        stored.removeAll { $0.text.lowercased() == text.lowercased() }
        return stored.count != before
    }

    /// A prefix must be SHORTER than what it prefixes - a phrase does not
    /// collide with itself.
    static func startsWith(_ longer: [String], prefix: [String]) -> Bool {
        guard !prefix.isEmpty, prefix.count < longer.count else { return false }
        for i in 0..<prefix.count where longer[i] != prefix[i] { return false }
        return true
    }
}
