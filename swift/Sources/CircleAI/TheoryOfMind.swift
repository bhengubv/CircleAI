// TheoryOfMind.swift
//
// Port of CircleAI.Companion theory-of-mind layer — the C# reference:
//   - ITheoryOfMind + OtherMindEstimate   (HerJarvisContracts.cs)
//   - BeliefTrackerTheoryOfMind           (HerJarvisRealImplementations.cs)
//
// Contract #10: model another agent's mind. Scans an interaction history for
// belief-verb phrases ("thinks / believes / wants / fears / hopes …"),
// accumulates a weighted bag of belief claims (with positional decay), and
// serialises it as JSON alongside a confidence score.
//
// In-memory + deterministic.

import Foundation

// MARK: - OtherMindEstimate

/// An estimate of another agent's mental state: the target identifier, a JSON
/// object of weighted belief claims, and an overall confidence in [0, 1].
public struct OtherMindEstimate: Sendable, Equatable {
    public let targetIdentifier: String
    public let likelyBeliefJson: String
    public let confidence: Double

    public init(targetIdentifier: String, likelyBeliefJson: String, confidence: Double) {
        self.targetIdentifier = targetIdentifier
        self.likelyBeliefJson = likelyBeliefJson
        self.confidence = confidence
    }
}

// MARK: - ITheoryOfMind

/// Contract #10 — theory of mind.
public protocol ITheoryOfMind: AnyObject {
    /// Estimate `target`'s beliefs from an interaction history supplied as JSON.
    func estimate(target: String, interactionHistoryJson: String) async throws -> OtherMindEstimate
}

// MARK: - BeliefTrackerTheoryOfMind

/// Bag-of-belief inference with confidence decay. For every belief-verb phrase
/// found (in order), accumulates `weight × decay` against a `"verb:claim"` key,
/// where `weight` is 1.0 for "believe…" phrases and 0.7 otherwise, and `decay`
/// is `1 / (1 + idx·0.1)`. Confidence is `min(1, Σweights / 5)`. Ported from
/// `BeliefTrackerTheoryOfMind` (HerJarvisRealImplementations.cs).
public final class BeliefTrackerTheoryOfMind: ITheoryOfMind, @unchecked Sendable {
    // \b(thinks?|believes?|wants?|fears?|hopes?)\s+([^.;!?]+)  (case-insensitive)
    private static let beliefRx: NSRegularExpression = {
        // swiftlint:disable:next force_try
        try! NSRegularExpression(
            pattern: #"\b(thinks?|believes?|wants?|fears?|hopes?)\s+([^.;!?]+)"#,
            options: [.caseInsensitive])
    }()

    public init() {}

    public func estimate(target: String, interactionHistoryJson: String) async throws -> OtherMindEstimate {
        precondition(!target.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty,
                     "target required")

        // Ordered accumulation so JSON key order mirrors the C# Dictionary
        // (insertion order of each first-seen key).
        var order: [String] = []
        var weights: [String: Double] = [:]

        let ns = interactionHistoryJson as NSString
        let matches = Self.beliefRx.matches(
            in: interactionHistoryJson, options: [],
            range: NSRange(location: 0, length: ns.length))

        var idx = 0
        for m in matches {
            let verb = ns.substring(with: m.range(at: 1)).lowercased()
            let claim = ns.substring(with: m.range(at: 2))
                .trimmingCharacters(in: .whitespacesAndNewlines)
            let decay = 1.0 / (1.0 + Double(idx) * 0.1)
            let weight = verb.hasPrefix("believ") ? 1.0 : 0.7
            let key = verb + ":" + claim
            if let prev = weights[key] {
                weights[key] = prev + weight * decay
            } else {
                weights[key] = weight * decay
                order.append(key)
            }
            idx += 1
        }

        let json = Self.serialise(order: order, weights: weights)
        let sum = weights.values.reduce(0.0, +)
        let conf = order.isEmpty ? 0.0 : min(1.0, sum / 5.0)
        return OtherMindEstimate(targetIdentifier: target, likelyBeliefJson: json, confidence: conf)
    }

    /// Serialises the weighted belief bag as a JSON object, in insertion order,
    /// with keys JSON-escaped and values formatted the way .NET's
    /// `JsonSerializer.Serialize(Dictionary<string,double>)` formats a double
    /// (shortest round-trippable; integer values carry no `.0`).
    static func serialise(order: [String], weights: [String: Double]) -> String {
        var parts: [String] = []
        parts.reserveCapacity(order.count)
        for key in order {
            let value = weights[key] ?? 0
            parts.append(jsonString(key) + ":" + netJsonNumber(value))
        }
        return "{" + parts.joined(separator: ",") + "}"
    }

    /// JSON string escaping that reproduces System.Text.Json's DEFAULT encoder
    /// (JavaScriptEncoder.Default) exactly — the encoder used by
    /// `JsonSerializer.Serialize(Dictionary<string,double>)` in the C# reference.
    ///
    /// Verified against .NET 8: backslash → `\\`; the short control forms
    /// `\b \t \n \f \r`; and `\uXXXX` (uppercase hex) for every other control
    /// char, for the HTML-sensitive ASCII set `" & ' + < > ` + "`", and for ALL
    /// non-ASCII (>= 0x7F). Everything else (letters, digits, space, and the
    /// punctuation `#$%()*,-./:;=?@[]^_{|}~`) is emitted literally.
    static func jsonString(_ s: String) -> String {
        // ASCII characters the default encoder escapes as \uXXXX even though
        // they are printable.
        let escapedAscii: Set<Unicode.Scalar> = ["\"", "&", "'", "+", "<", ">", "`"]
        var out = "\""
        for scalar in s.unicodeScalars {
            switch scalar {
            case "\\": out += "\\\\"
            case "\u{08}": out += "\\b"
            case "\t": out += "\\t"
            case "\n": out += "\\n"
            case "\u{0C}": out += "\\f"
            case "\r": out += "\\r"
            default:
                if scalar.value < 0x20 || scalar.value >= 0x7F || escapedAscii.contains(scalar) {
                    out += String(format: "\\u%04X", scalar.value)
                } else {
                    out.unicodeScalars.append(scalar)
                }
            }
        }
        out += "\""
        return out
    }

    /// Formats a double the way .NET's JSON serializer does: shortest
    /// round-trippable decimal, with an integer value rendered without a
    /// fractional part (e.g. 1.0 → "1", 0.7 → "0.7").
    static func netJsonNumber(_ d: Double) -> String {
        if d == d.rounded() && abs(d) < 1e15 {
            return String(Int64(d))
        }
        // Swift's default Double description is the shortest round-trippable form.
        return String(d)
    }
}
