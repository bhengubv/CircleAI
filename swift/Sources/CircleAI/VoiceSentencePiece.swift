// VoiceSentencePiece.swift
//
// Reading a SentencePiece model and segmenting text with it.
//
// Ported from src/CircleAI.Voice/SentencePieceTokenizer.cs.

import Foundation

public enum SentencePieceKind: Int, Sendable, Equatable {
    case normal = 1
    case unknown = 2
    case control = 3
    case userDefined = 4
    case byte = 5
    case unused = 6
}

public struct SentencePiece: Sendable, Equatable {
    public let piece: String
    public let score: Float
    public let kind: SentencePieceKind
    public let id: Int

    public init(piece: String, score: Float, kind: SentencePieceKind, id: Int) {
        self.piece = piece
        self.score = score
        self.kind = kind
        self.id = id
    }
}

/// Segments text into the pieces a model was trained on.
public final class SentencePieceTokenizer: @unchecked Sendable {
    /// U+2581 LOWER ONE EIGHTH BLOCK - what SentencePiece uses for a word
    /// boundary. Not a space: a space is a character the model never sees.
    public static let wordStart: Character = "\u{2581}"

    public let pieces: [SentencePiece]

    /// Some vocabularies are entirely upper case. Detected rather than
    /// configured, because getting it wrong makes every word unknown.
    public let vocabularyIsUpperCase: Bool

    private let byPiece: [String: SentencePiece]
    private let unknownPenalty: Float
    private let longest: Int

    public init(model: Data) {
        let parsed = SentencePieceTokenizer.readPieces([UInt8](model))
        pieces = parsed

        var map: [String: SentencePiece] = [:]
        for p in parsed where map[p.piece] == nil { map[p.piece] = p }
        byPiece = map

        // Worse than any real piece, so a segmentation covering the text with
        // known pieces always beats one that gives up.
        unknownPenalty = parsed.isEmpty ? -100 : (parsed.map(\.score).min() ?? 0) - 10

        var lower = 0, upper = 0
        for p in parsed where p.kind == .normal {
            for c in p.piece {
                if c.isLowercase { lower += 1 } else if c.isUppercase { upper += 1 }
            }
        }
        vocabularyIsUpperCase = upper > lower * 8

        longest = map.keys.map(\.count).max() ?? 1
    }

    public convenience init?(modelPath: String) {
        guard let d = FileManager.default.contents(atPath: modelPath) else { return nil }
        self.init(model: d)
    }

    /// Viterbi over the string: `best[i]` is the score of the best segmentation
    /// of the first i characters, `back[i]` the length of the piece ending
    /// there. A single character ALWAYS has a way through, at a penalty, so no
    /// input can be unsegmentable.
    public func encode(_ text: String) -> [String] {
        let norm = Array(normalise(text))
        if norm.isEmpty { return [] }

        let n = norm.count
        var best = [Float](repeating: -.infinity, count: n + 1)
        var back = [Int](repeating: 0, count: n + 1)
        best[0] = 0

        for end in 1...n {
            for len in 1...min(longest, end) {
                let start = end - len
                if best[start] == -.infinity { continue }
                let span = String(norm[start..<end])

                let score: Float
                if let piece = byPiece[span], piece.kind == .normal || piece.kind == .userDefined {
                    score = piece.score
                } else if len == 1 {
                    score = unknownPenalty
                } else {
                    continue
                }

                let total = best[start] + score
                if total > best[end] { best[end] = total; back[end] = len }
            }
        }

        var out: [String] = []
        var at = n
        while at > 0 {
            let len = max(1, back[at])
            out.append(String(norm[(at - len)..<at]))
            at -= len
        }
        return out.reversed()
    }

    /// Whether every piece the segmentation produced is one the model knows.
    /// Returns the unknown pieces so a caller can tell somebody WHICH sounds
    /// the listener does not have.
    public func canRepresent(_ text: String) -> (ok: Bool, unknown: [String]) {
        var seen = Set<String>()
        var bad: [String] = []
        for p in encode(text) where byPiece[p] == nil {
            if seen.insert(p).inserted { bad.append(p) }
        }
        return (bad.isEmpty, bad)
    }

    /// Trim, upper-case when the vocabulary is, then replace every run of
    /// whitespace with a single word-start marker - including a leading one.
    func normalise(_ text: String) -> String {
        var s = text.trimmingCharacters(in: .whitespacesAndNewlines)
        if vocabularyIsUpperCase { s = s.uppercased() }

        var out = String(Self.wordStart)
        var lastWasSpace = true
        for c in s {
            if c.isWhitespace {
                if !lastWasSpace { out.append(Self.wordStart) }
                lastWasSpace = true
            } else {
                out.append(c)
                lastWasSpace = false
            }
        }
        return out
    }
}

// MARK: - The smallest protobuf reader that does this job
//
//   ModelProto    { repeated SentencePiece pieces = 1; ... }
//   SentencePiece { string piece = 1; float score = 2; Type type = 3; }
//
// Unknown fields are skipped BY WIRE TYPE, so a model carrying a trainer spec
// or a normaliser blob - which every real one does - still reads.

extension SentencePieceTokenizer {

    static func readPieces(_ data: [UInt8]) -> [SentencePiece] {
        var pieces: [SentencePiece] = []
        var i = 0
        while i < data.count {
            guard let key = readVarint(data, &i) else { break }
            let field = Int(key >> 3)
            let wire = Int(key & 7)

            if field == 1 && wire == 2 {
                guard let len = readVarint(data, &i), i + Int(len) <= data.count else { break }
                pieces.append(readPiece(Array(data[i..<(i + Int(len))]), id: pieces.count))
                i += Int(len)
            } else if !skipField(data, &i, wire: wire) {
                break
            }
        }
        return pieces
    }

    static func readPiece(_ data: [UInt8], id: Int) -> SentencePiece {
        var piece = ""
        var score: Float = 0
        var kind = SentencePieceKind.normal
        var i = 0

        while i < data.count {
            guard let key = readVarint(data, &i) else { break }
            let field = Int(key >> 3)
            let wire = Int(key & 7)

            switch (field, wire) {
            case (1, 2):
                guard let len = readVarint(data, &i), i + Int(len) <= data.count else { return
                    SentencePiece(piece: piece, score: score, kind: kind, id: id) }
                piece = String(decoding: data[i..<(i + Int(len))], as: UTF8.self)
                i += Int(len)
            case (2, 5):
                guard i + 4 <= data.count else { return
                    SentencePiece(piece: piece, score: score, kind: kind, id: id) }
                // fixed32, little-endian IEEE-754.
                let bits = UInt32(data[i]) | (UInt32(data[i + 1]) << 8)
                        | (UInt32(data[i + 2]) << 16) | (UInt32(data[i + 3]) << 24)
                score = Float(bitPattern: bits)
                i += 4
            case (3, 0):
                guard let v = readVarint(data, &i) else { return
                    SentencePiece(piece: piece, score: score, kind: kind, id: id) }
                // An unknown type from a newer trainer reads as normal rather
                // than dropping the piece.
                kind = SentencePieceKind(rawValue: Int(v)) ?? .normal
            default:
                if !skipField(data, &i, wire: wire) {
                    return SentencePiece(piece: piece, score: score, kind: kind, id: id)
                }
            }
        }
        return SentencePiece(piece: piece, score: score, kind: kind, id: id)
    }

    /// Base-128, low group first, high bit set while more follow. Bounded at
    /// ten groups so a corrupt file cannot spin.
    static func readVarint(_ data: [UInt8], _ i: inout Int) -> UInt64? {
        var result: UInt64 = 0
        var shift: UInt64 = 0
        var groups = 0
        while i < data.count && groups < 10 {
            let b = data[i]
            i += 1
            groups += 1
            result |= UInt64(b & 0x7F) << shift
            if b & 0x80 == 0 { return result }
            shift += 7
        }
        return nil
    }

    static func skipField(_ data: [UInt8], _ i: inout Int, wire: Int) -> Bool {
        switch wire {
        case 0: return readVarint(data, &i) != nil
        case 1: i += 8; return i <= data.count
        case 2:
            guard let len = readVarint(data, &i) else { return false }
            i += Int(len)
            return i <= data.count
        case 5: i += 4; return i <= data.count
        default: return false   // groups (3, 4) are long gone from proto3
        }
    }
}
