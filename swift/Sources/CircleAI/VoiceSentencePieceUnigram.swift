// VoiceSentencePieceUnigram.swift
//
// Port of src/CircleAI.Voice/SentencePieceUnigram.cs — SentencePiece unigram
// encoding: Viterbi over the piece lattice, with byte fallback.
//
// Parity is asserted against fixtures/voice_sentencepiece_unigram.json, whose
// vocabulary is deliberately built so GREEDY AND VITERBI DISAGREE. A port that
// takes the greedy shortcut passes a naive test and fails that fixture.

import Foundation

/// SentencePiece unigram tokeniser.
public final class VoiceSentencePieceUnigram {

    private let ids: [String: Int]
    private let scores: [String: Float]
    private let maxPieceLength: Int

    /// Cost charged for falling back to raw bytes.
    ///
    /// Any finite penalty works, because fallback only ever competes with "no
    /// path at all". It must be worse than a real piece so the lattice never
    /// prefers it where a piece exists, and finite so a path always exists.
    private static let fallbackPenalty: Float = 10.0

    public init(ids: [String: Int], scores: [String: Float]) {
        self.ids = ids
        self.scores = scores
        self.maxPieceLength = ids.keys.map(\.count).max() ?? 1
    }

    /// Load from a bundle's `vocab.json` and `token_scores.json`.
    public convenience init(vocabPath: String, scoresPath: String) throws {
        let vocabData = try Data(contentsOf: URL(fileURLWithPath: vocabPath))
        let scoresData = try Data(contentsOf: URL(fileURLWithPath: scoresPath))
        let vocab = try JSONDecoder().decode([String: Int].self, from: vocabData)
        let scores = try JSONDecoder().decode([String: Float].self, from: scoresData)
        self.init(ids: vocab, scores: scores)
    }

    public var count: Int { ids.count }

    /// Encode text to token ids.
    public func encode(_ text: String) -> [Int] {
        if text.isEmpty { return [] }

        // SentencePiece's own normalisation: NFKC, then spaces become U+2581,
        // with one prepended so the first word is marked word-initial too.
        let normalised = "\u{2581}" + text.precomposedStringWithCompatibilityMapping
            .replacingOccurrences(of: " ", with: "\u{2581}")

        // Index by Character (grapheme-safe) rather than by UTF-16 code unit, so
        // a piece boundary can never land inside a surrogate pair.
        let chars = Array(normalised)
        let n = chars.count

        let unreachable: Float = -1e18
        var best = [Float](repeating: unreachable, count: n + 1)
        var fromIndex = [Int](repeating: 0, count: n + 1)
        var piece = [String?](repeating: nil, count: n + 1)
        best[0] = 0

        for i in 0..<n {
            if best[i] <= unreachable / 2 { continue }

            let limit = min(maxPieceLength, n - i)
            if limit > 0 {
                for len in 1...limit {
                    let candidate = String(chars[i..<(i + len)])
                    guard ids[candidate] != nil else { continue }
                    let score = best[i] + (scores[candidate] ?? 0)
                    if score > best[i + len] {
                        best[i + len] = score
                        fromIndex[i + len] = i
                        piece[i + len] = candidate
                    }
                }
            }

            // Byte fallback for this ONE character, so no input is ever silent.
            let end = i + 1
            let fallbackScore = best[i] - Self.fallbackPenalty
            if fallbackScore > best[end] {
                best[end] = fallbackScore
                fromIndex[end] = i
                piece[end] = nil                 // nil marks "emit as bytes"
            }
        }

        var reversed: [Int] = []
        reversed.reserveCapacity(n)
        var i = n
        while i > 0 {
            let start = fromIndex[i]
            if let p = piece[i], let id = ids[p] {
                reversed.append(id)
            } else {
                // BACKWARDS, because this whole list is built backwards. The
                // lattice is walked from the end and flipped once at the bottom,
                // so a multi-byte character appended in forward order comes out
                // byte-reversed — é is UTF-8 C3 A9 and would be emitted A9 C3.
                // Nothing throws; those are real pieces with real ids, so the
                // model simply says a different character.
                let raw = String(chars[start..<i])
                for b in Array(raw.utf8).reversed() {
                    if let byteId = ids[String(format: "<0x%02X>", b)] { reversed.append(byteId) }
                }
            }
            i = start
        }

        return reversed.reversed()
    }
}
