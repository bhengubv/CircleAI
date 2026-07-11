// Search.swift
//
// Port of src/CircleAI.Search/:
//   • VectorSearch.cs        — VectorMath.CosineSimilarity (SIMD + scalar tail;
//                              the Swift port uses the scalar computation, which
//                              is numerically the reference path).
//   • SimdOps.cs             — SimdOps.CosineSimilarity (same contract).
//   • SearchPrimitives.cs    — SearchTokenisation.Tokenise,
//                              SearchScoring.TermFrequency / SimpleRelevance.
//
// Porting notes:
//   • C# `static class` with static methods → Swift `enum` with `static func`.
//   • `ReadOnlySpan<float>` → `[Float]`. The C# SIMD path and scalar fallback
//     compute the same value; Swift accumulates in the same order as the scalar
//     fallback (`dot / (sqrt(normA) * sqrt(normB))`).
//   • Guards (`ArgumentException` / `ArgumentNullException`) → `SearchError`.

import Foundation

// MARK: - Errors

public enum SearchError: Error, Equatable, CustomStringConvertible {
    case vectorLengthMismatch
    case nullArgument

    public var description: String {
        switch self {
        case .vectorLengthMismatch: return "Vectors must be the same non-zero length."
        case .nullArgument: return "argument was null"
        }
    }
}

// MARK: - VectorMath

/// Vector maths over dense `Float` vectors.
public enum VectorMath {
    /// Cosine similarity between two equal-length, non-empty vectors.
    /// Throws when lengths differ or either is empty.
    public static func cosineSimilarity(_ a: [Float], _ b: [Float]) throws -> Float {
        if a.count != b.count || a.isEmpty { throw SearchError.vectorLengthMismatch }
        var dot: Float = 0, normA: Float = 0, normB: Float = 0
        for i in 0..<a.count {
            dot += a[i] * b[i]
            normA += a[i] * a[i]
            normB += b[i] * b[i]
        }
        return dot / (normA.squareRoot() * normB.squareRoot())
    }
}

// MARK: - SimdOps

/// SIMD-flavoured ops. The Swift port computes the same value as the C# SIMD +
/// scalar-tail path via the reference scalar accumulation.
public enum SimdOps {
    /// Cosine similarity between two equal-length, non-empty vectors.
    public static func cosineSimilarity(_ a: [Float], _ b: [Float]) throws -> Float {
        if a.count != b.count || a.isEmpty { throw SearchError.vectorLengthMismatch }
        var dot: Float = 0, normA: Float = 0, normB: Float = 0
        for i in 0..<a.count {
            dot += a[i] * b[i]
            normA += a[i] * a[i]
            normB += b[i] * b[i]
        }
        return dot / (normA.squareRoot() * normB.squareRoot())
    }
}

// MARK: - SearchTokenisation

/// Query / document tokenisation.
public enum SearchTokenisation {
    private static let separators = CharacterSet(charactersIn: " \n\r\t,.;:()[]\"'")

    /// Splits text on whitespace/punctuation, lowercases, drops empties.
    public static func tokenise(_ text: String) -> [String] {
        text.components(separatedBy: separators)
            .map { $0.trimmingCharacters(in: .whitespaces).lowercased() }
            .filter { !$0.isEmpty }
    }
}

// MARK: - SearchScoring

/// Simple term-frequency relevance scoring.
public enum SearchScoring {
    /// Fraction of `docTokens` that equal `term` (ordinal equality).
    public static func termFrequency(_ term: String, _ docTokens: [String]) -> Double {
        if docTokens.isEmpty { return 0 }
        var c = 0
        for t in docTokens where t == term { c += 1 }
        return Double(c) / Double(docTokens.count)
    }

    /// Sum of per-query-term term-frequencies over the document.
    public static func simpleRelevance(_ queryTokens: [String], _ docTokens: [String]) -> Double {
        if queryTokens.isEmpty || docTokens.isEmpty { return 0 }
        var score = 0.0
        for q in queryTokens { score += termFrequency(q, docTokens) }
        return score
    }
}
