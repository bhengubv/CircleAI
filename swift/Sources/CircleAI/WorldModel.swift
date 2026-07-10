// WorldModel.swift
//
// Port of CircleAI.Companion world-model layer — the C# reference:
//   - IWorldModel + CausalPrediction   (HerJarvisContracts.cs)
//   - FrequencyWorldModel               (HerJarvisRealImplementations.cs)
//   - BayesianWorldModel                (BayesianWorldModel.cs)
//
// Contract #5: World model + causal reasoning. Both implementations learn
// P(outcome | observations) from registered evidence and predict the most
// likely outcome for a scenario supplied as JSON.
//
// In-memory + deterministic. FrequencyWorldModel tallies co-occurrence
// counts; BayesianWorldModel is an online Naive-Bayes classifier with
// Laplace smoothing. Both share the same scenario-JSON observation
// extraction (property "name=value", value rendered exactly like .NET's
// JsonElement.ToString()).
//
// Both models key observations and outcomes case-insensitively, mirroring
// the C# ConcurrentDictionary(StringComparer.OrdinalIgnoreCase). Like .NET,
// the FIRST-seen casing of a key is preserved and is what a prediction
// returns — later differently-cased writes fold into the same bucket but do
// not change the stored display casing.

import Foundation

// MARK: - CausalPrediction

/// The model's prediction for a scenario: the single most-likely outcome, its
/// probability in [0, 1], and the observations that supported the inference.
public struct CausalPrediction: Sendable, Equatable {
    public let outcome: String
    public let probability: Double
    public let supportingFactors: [String]

    public init(outcome: String, probability: Double, supportingFactors: [String]) {
        self.outcome = outcome
        self.probability = probability
        self.supportingFactors = supportingFactors
    }
}

// MARK: - IWorldModel

/// Contract #5 — world model + causal reasoning.
public protocol IWorldModel: AnyObject {
    /// Predict the most-likely outcome for a scenario supplied as JSON.
    func predict(scenarioJson: String) async throws -> CausalPrediction
}

// MARK: - Case-insensitive counter (mirrors .NET OrdinalIgnoreCase dictionary)

/// An Int64 counter keyed case-insensitively that, like a .NET
/// `Dictionary<string,long>(StringComparer.OrdinalIgnoreCase)`, preserves the
/// display casing of the FIRST-seen spelling of each key. Enumeration and the
/// `max`/`sum` helpers operate on those display keys.
struct CaseInsensitiveCounter {
    // lowercased key -> (displayKey, count)
    private var map: [String: (display: String, count: Int64)] = [:]

    mutating func add(_ key: String, _ delta: Int64 = 1) {
        let lower = key.lowercased()
        if let existing = map[lower] {
            map[lower] = (existing.display, existing.count + delta)
        } else {
            map[lower] = (key, delta)
        }
    }

    func count(_ key: String) -> Int64 { map[key.lowercased()]?.count ?? 0 }
    var isEmpty: Bool { map.isEmpty }
    var cardinality: Int { map.count }
    var total: Int64 { map.values.reduce(Int64(0)) { $0 + $1.count } }

    /// (displayKey, count) pairs in unspecified order.
    var entries: [(key: String, value: Int64)] {
        map.values.map { (key: $0.display, value: $0.count) }
    }
}

// MARK: - Scenario observation extraction (shared)

/// Extracts observation tokens from a scenario JSON object, mirroring the C#
/// `ExtractObservations`: for each top-level property, emit `name=value` where
/// `value` is rendered exactly as .NET's `JsonElement.ToString()` would render
/// it. Non-object roots (or malformed JSON) yield an empty list.
enum ScenarioObservations {
    static func extract(_ scenarioJson: String) -> [String] {
        let trimmed = scenarioJson.trimmingCharacters(in: .whitespacesAndNewlines)
        if trimmed.isEmpty { return [] }
        guard let data = scenarioJson.data(using: .utf8),
              let root = try? JSONSerialization.jsonObject(with: data, options: [.fragmentsAllowed]),
              root is [String: Any] else { return [] }
        // JSONSerialization does not keep object key order, so read the
        // top-level object ourselves to preserve declaration order + raw literals.
        return OrderedJsonObject.parseTopLevel(scenarioJson)
    }
}

/// Minimal ordered top-level JSON object reader. Returns `name=value` strings
/// with each value rendered the way .NET's `JsonElement.ToString()` renders it:
///   - string  → the raw string, unquoted, unescaped
///   - number  → the number's source text
///   - true    → "True",  false → "False"  (note .NET capitalisation)
///   - null    → ""       (empty string)
///   - object/array → the VERBATIM source slice (GetRawText — whitespace kept)
enum OrderedJsonObject {
    static func parseTopLevel(_ json: String) -> [String] {
        var scanner = JsonScanner(json)
        scanner.skipWhitespace()
        guard scanner.consume("{") else { return [] }
        var out: [String] = []
        scanner.skipWhitespace()
        if scanner.consume("}") { return out }
        while true {
            scanner.skipWhitespace()
            guard let name = scanner.readString() else { return out }
            scanner.skipWhitespace()
            guard scanner.consume(":") else { return out }
            scanner.skipWhitespace()
            guard let rendered = scanner.readValueRendered() else { return out }
            out.append(name + "=" + rendered)
            scanner.skipWhitespace()
            if scanner.consume(",") { continue }
            _ = scanner.consume("}")
            break
        }
        return out
    }
}

/// A tiny hand-rolled JSON scanner — just enough to walk one object's
/// key/value pairs and render each value to match .NET's JsonElement.ToString().
private struct JsonScanner {
    private let chars: [Character]
    private var i = 0
    init(_ s: String) { chars = Array(s) }

    private func peek() -> Character? { i < chars.count ? chars[i] : nil }

    mutating func skipWhitespace() {
        while let c = peek(), c == " " || c == "\t" || c == "\n" || c == "\r" { i += 1 }
    }

    mutating func consume(_ c: Character) -> Bool {
        if peek() == c { i += 1; return true }
        return false
    }

    /// Reads a JSON string literal (leading quote required) and returns its
    /// decoded contents.
    mutating func readString() -> String? {
        guard peek() == "\"" else { return nil }
        i += 1
        var out = ""
        while let c = peek() {
            i += 1
            if c == "\"" { return out }
            if c == "\\" {
                guard let esc = peek() else { return nil }
                i += 1
                switch esc {
                case "\"": out.append("\"")
                case "\\": out.append("\\")
                case "/": out.append("/")
                case "b": out.append("\u{08}")
                case "f": out.append("\u{0C}")
                case "n": out.append("\n")
                case "r": out.append("\r")
                case "t": out.append("\t")
                case "u":
                    var hex = ""
                    for _ in 0..<4 { if let h = peek() { hex.append(h); i += 1 } }
                    if let code = UInt32(hex, radix: 16), let scalar = Unicode.Scalar(code) {
                        out.append(Character(scalar))
                    }
                default: out.append(esc)
                }
            } else {
                out.append(c)
            }
        }
        return nil
    }

    /// Reads any JSON value and renders it as .NET's JsonElement.ToString() would.
    mutating func readValueRendered() -> String? {
        guard let c = peek() else { return nil }
        switch c {
        case "\"":
            return readString()
        case "{", "[":
            return readContainerRaw()
        case "t":
            if matchLiteral("true") { return "True" }
            return nil
        case "f":
            if matchLiteral("false") { return "False" }
            return nil
        case "n":
            if matchLiteral("null") { return "" }
            return nil
        default:
            return readNumberRaw()
        }
    }

    private mutating func matchLiteral(_ lit: String) -> Bool {
        let litChars = Array(lit)
        guard i + litChars.count <= chars.count else { return false }
        for k in 0..<litChars.count where chars[i + k] != litChars[k] { return false }
        i += litChars.count
        return true
    }

    /// Reads a number token verbatim (matching .NET, which echoes source text).
    private mutating func readNumberRaw() -> String? {
        var out = ""
        while let c = peek(), "+-0123456789.eE".contains(c) {
            out.append(c); i += 1
        }
        return out.isEmpty ? nil : out
    }

    /// Reads a nested object/array and returns its VERBATIM source text,
    /// including insignificant whitespace — matching JsonElement.ToString(),
    /// which returns GetRawText() for objects and arrays (e.g. `{ "k" : 1 }`).
    private mutating func readContainerRaw() -> String? {
        var out = ""
        var depth = 0
        var inString = false
        while let c = peek() {
            i += 1
            out.append(c)
            if inString {
                if c == "\\" {
                    if let n = peek() { out.append(n); i += 1 }
                } else if c == "\"" {
                    inString = false
                }
                continue
            }
            switch c {
            case "\"": inString = true
            case "{", "[": depth += 1
            case "}", "]":
                depth -= 1
                if depth == 0 { return out }
            default: break
            }
        }
        return out
    }
}

// MARK: - FrequencyWorldModel

/// Learns `P(outcome | observation)` from registered co-occurrence counts and
/// predicts by tallying, across the scenario's observations, the outcome with
/// the highest summed count. Ported from `FrequencyWorldModel`
/// (HerJarvisRealImplementations.cs). Observation + outcome keys are
/// case-insensitive.
public final class FrequencyWorldModel: IWorldModel, @unchecked Sendable {
    private let lock = NSLock()
    // observation -> (outcome -> count), all keys case-insensitive.
    private var counts: [String: CaseInsensitiveCounter] = [:]

    public init() {}

    /// Tell the model: when these observations happen, this outcome was seen.
    public func observe<S: Sequence>(observations: S, outcome: String) where S.Element == String {
        precondition(!outcome.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty,
                     "outcome required")
        lock.lock(); defer { lock.unlock() }
        for obs in observations {
            let obsKey = obs.lowercased()
            var inner = counts[obsKey] ?? CaseInsensitiveCounter()
            inner.add(outcome, 1)
            counts[obsKey] = inner
        }
    }

    public func predict(scenarioJson: String) async throws -> CausalPrediction {
        let observations = ScenarioObservations.extract(scenarioJson)
        lock.lock()
        var tally = CaseInsensitiveCounter()
        var supporters: [String] = []
        for obs in observations {
            guard let inner = counts[obs.lowercased()] else { continue }
            supporters.append(obs)
            for entry in inner.entries {
                tally.add(entry.key, entry.value)
            }
        }
        lock.unlock()

        if tally.isEmpty {
            return CausalPrediction(outcome: "unknown", probability: 0.5, supportingFactors: supporters)
        }
        let total = tally.total
        // OrderByDescending(Value).First() — highest count wins.
        let top = tally.entries.max { a, b in a.value < b.value }!
        return CausalPrediction(
            outcome: top.key,
            probability: Double(top.value) / Double(total),
            supportingFactors: supporters)
    }
}

// MARK: - BayesianWorldModel

/// Online Naive-Bayes classifier over (observations → outcome) pairs with
/// Laplace smoothing. At predict time evaluates, for every seen outcome,
/// `log P(outcome) + Σ log P(obs_i | outcome)` and softmaxes the log-posteriors
/// for a normalised probability. Ported from `BayesianWorldModel`
/// (BayesianWorldModel.cs). Outcome + observation keys are case-insensitive.
public final class BayesianWorldModel: IWorldModel, @unchecked Sendable {
    private let lock = NSLock()
    private var outcomeCounts = CaseInsensitiveCounter()
    // outcome (lowercased) -> (observation -> count)
    private var condCounts: [String: CaseInsensitiveCounter] = [:]
    private var vocab: Set<String> = []
    private var totalObservations: Int64 = 0
    private let alpha: Double // Laplace smoothing strength

    public init(laplaceAlpha: Double = 1.0) {
        precondition(laplaceAlpha > 0, "laplaceAlpha out of range")
        self.alpha = laplaceAlpha
    }

    /// Update the model with one (observations → outcome) example.
    public func observe<S: Sequence>(observations: S, outcome: String) where S.Element == String {
        precondition(!outcome.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty,
                     "outcome required")
        lock.lock(); defer { lock.unlock() }
        outcomeCounts.add(outcome, 1)
        totalObservations += 1
        let outKey = outcome.lowercased()
        var cond = condCounts[outKey] ?? CaseInsensitiveCounter()
        for obs in observations {
            if obs.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty { continue }
            cond.add(obs, 1)
            vocab.insert(obs.lowercased())
        }
        condCounts[outKey] = cond
    }

    public func predict(scenarioJson: String) async throws -> CausalPrediction {
        let observations = ScenarioObservations.extract(scenarioJson)

        lock.lock(); defer { lock.unlock() }
        if observations.isEmpty || outcomeCounts.isEmpty {
            return CausalPrediction(outcome: "unknown", probability: 0.5, supportingFactors: [])
        }

        let vocabSize = Double(max(1, vocab.count))
        let totalEx = Double(max(1, totalObservations))
        let outcomeCardinality = Double(outcomeCounts.cardinality)

        var scored: [(outcome: String, logPosterior: Double)] = []
        for entry in outcomeCounts.entries {
            let outcome = entry.key
            let outcomeCount = entry.value
            // Log P(outcome) — Laplace-smoothed prior.
            let logPrior = log((Double(outcomeCount) + alpha) / (totalEx + alpha * outcomeCardinality))

            let cond = condCounts[outcome.lowercased()] ?? CaseInsensitiveCounter()
            let totalForOutcome = Double(cond.total)
            var logLikelihood = 0.0
            for obs in observations {
                let n = Double(cond.count(obs))
                let p = (n + alpha) / (totalForOutcome + alpha * vocabSize)
                logLikelihood += log(p)
            }
            scored.append((outcome, logPrior + logLikelihood))
        }

        // Softmax over log-posteriors for normalised probability.
        let maxLogPost = scored.map { $0.logPosterior }.max()!
        let expSum = scored.reduce(0.0) { $0 + exp($1.logPosterior - maxLogPost) }
        let top = scored.max { a, b in a.logPosterior < b.logPosterior }!
        let prob = exp(top.logPosterior - maxLogPost) / expSum
        return CausalPrediction(outcome: top.outcome, probability: prob, supportingFactors: observations)
    }
}
