// VoiceProsody.swift
//
// Open JTalk full-context labels → the token ids the Japanese VITS was trained
// on.
//
// JAPANESE IS A FOURTH FAMILY. The other three ONNX voice layouts take phonemes
// or characters; this one takes PROSODY — the accent structure written into the
// symbol stream as brackets. Feed it bare phonemes and it speaks, flatly and
// wrongly, with no error anywhere.
//
// The label is Open JTalk's, shaped
//   xx^xx-k+o=r/A:-2+1+3/B:xx-xx_xx/.../F:5_2/...!0_xx/...
// and the three fields that matter are A (position relative to the accent
// nucleus), F (mora count of the phrase) and E (sentence type). Everything below
// is reading those out.
//
// Ported from src/CircleAI.Voice/OpenJTalkProsodyTokeniser.cs.

import Foundation

public final class OpenJTalkProsodyTokeniser: @unchecked Sendable {

    /// The model's own symbol table, in its own order. The ids ARE the indices,
    /// so this list cannot be reordered or tidied.
    static let vocabulary: [String] = [
        "<blank>", "<unk>", "a", "o", "i", "[", "#", "u", "]", "e", "k", "n",
        "t", "r", "s", "N", "m", "_", "sh", "d", "g", "^", "$", "w", "cl", "h",
        "y", "b", "j", "ts", "ch", "z", "p", "f", "ky", "ry", "gy", "hy", "ny",
        "by", "my", "py", "v", "dy", "?", "ty", "<sos/eos>",
    ]

    public static let blankId = 0
    public static let unkId = 1

    private static let ids: [String: Int] = {
        var m: [String: Int] = [:]
        for (i, s) in vocabulary.enumerated() { m[s] = i }
        return m
    }()

    /// A field that is not present at all. Deliberately far from any real value
    /// so an absent field can never compare equal to a legitimate one — 0 and -1
    /// are both real answers here.
    private static let absent = -50

    private let lock = NSLock()
    private var unknownSymbols: [String] = []
    private var symbolsOut: [String] = []

    public init() {}

    /// Symbols the vocabulary did not contain. Each one is a silent flat spot in
    /// the prosody, so a caller that cares about quality wants to see this.
    public var lastUnknown: [String] {
        lock.lock(); defer { lock.unlock() }
        return unknownSymbols
    }

    public var lastSymbols: [String] {
        lock.lock(); defer { lock.unlock() }
        return symbolsOut
    }

    public func encode(labels: String) -> [Int] {
        let lines = labels
            .split(separator: "\n")
            .map { $0.trimmingCharacters(in: .whitespaces) }
            .filter { !$0.isEmpty }
        return encode(labels: lines)
    }

    public func encode(labels: [String]) -> [Int] {
        var symbols: [String] = []
        symbols.reserveCapacity(labels.count + 8)
        var unknown: [String] = []

        for n in 0..<labels.count {
            let current = labels[n]

            guard var p3 = Self.currentPhoneme(current) else { continue }

            // DEVOICED VOWELS ARE WRITTEN AS CAPITALS by Open JTalk and are NOT
            // in this vocabulary — the model was trained with them folded into
            // the plain vowels. Without this fold every devoiced vowel becomes
            // <unk>, and that is most sentence-final -masu and -desu.
            if p3.count == 1, let c = p3.first, "AEIOU".contains(c) {
                p3 = p3.lowercased()
            }

            if p3 == "sil" {
                // Utterance-boundary silence carries the sentence TYPE rather
                // than a sound: '$' for a statement, '?' for a question. That
                // distinction is the difference between a flat and a rising
                // final contour, so it is worth reading the label for.
                if n == 0 {
                    symbols.append("^")
                } else if n == labels.count - 1 {
                    symbols.append(Self.numeric(Self.reE3, current) == 1 ? "?" : "$")
                }
                continue
            }

            if p3 == "pau" {
                symbols.append("_")
                continue
            }

            symbols.append(p3)

            // Accent structure, read from THIS label and the position of the
            // next mora. a1 = pitch offset from the accent nucleus, a2 = mora
            // index in the accent phrase, a3 = mora index counted back,
            // f1 = mora count of the phrase.
            let a1 = Self.numeric(Self.reA1, current)
            let a2 = Self.numeric(Self.reA2, current)
            let a3 = Self.numeric(Self.reA3, current)
            let f1 = Self.numeric(Self.reF1, current)
            let a2Next = n + 1 < labels.count ? Self.numeric(Self.reA2, labels[n + 1]) : Self.absent

            // Only a vowel, moraic n, or the geminate can carry a boundary or a
            // pitch movement — a consonant is mid-mora and gets nothing.
            let carries = (p3.count == 1 && "aeiouAEIOUN".contains(p3.first!)) || p3 == "cl"

            if a3 == 1 && a2Next == 1 && carries {
                symbols.append("#")                              // phrase border
            } else if a1 == 0 && a2Next == a2 + 1 && a2 != f1 {
                symbols.append("]")                              // pitch fall
            } else if a2 == 1 && a2Next == 2 {
                symbols.append("[")                              // pitch rise
            }
        }

        var out = [Int](repeating: 0, count: symbols.count)
        for (i, s) in symbols.enumerated() {
            if let id = Self.ids[s] {
                out[i] = id
            } else {
                out[i] = Self.unkId
                unknown.append(s)
            }
        }

        lock.lock()
        symbolsOut = symbols
        unknownSymbols = unknown
        lock.unlock()
        return out
    }

    public static func symbol(for id: Int) -> String {
        id >= 0 && id < vocabulary.count ? vocabulary[id] : "<oob>"
    }

    // MARK: - Field accessors

    private static let reA1 = try! NSRegularExpression(pattern: "/A:([0-9\\-]+)\\+")
    private static let reA2 = try! NSRegularExpression(pattern: "\\+(\\d+)\\+")
    private static let reA3 = try! NSRegularExpression(pattern: "\\+(\\d+)/")
    private static let reF1 = try! NSRegularExpression(pattern: "/F:(\\d+)_")
    private static let reE3 = try! NSRegularExpression(pattern: "!(\\d+)_")
    private static let rePhoneme = try! NSRegularExpression(pattern: "\\-(.*?)\\+")

    static func currentPhoneme(_ label: String) -> String? {
        capture(rePhoneme, label)
    }

    private static func capture(_ re: NSRegularExpression, _ s: String) -> String? {
        let ns = s as NSString
        guard let m = re.firstMatch(in: s, range: NSRange(location: 0, length: ns.length)),
              m.numberOfRanges > 1, m.range(at: 1).location != NSNotFound
        else { return nil }
        return ns.substring(with: m.range(at: 1))
    }

    static func numeric(_ re: NSRegularExpression, _ label: String) -> Int {
        guard let raw = capture(re, label), let v = Int(raw) else { return absent }
        return v
    }
}
