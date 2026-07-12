// TelephonyEval.swift
//
// Port of the CircleAI.Telephony evaluation + guardrail layer:
//   • LlmJudge.cs   — JudgeDimension, JudgeVerdict, JudgeCompletion, LlmJudge.
//   • EvalSession.cs — EvalTurn, EvalTurnResult, EvalRunResult, EvalTurnHandler,
//                      EvalSession.
//   • Guardrails.cs — GuardrailRule, GuardrailAction, GuardrailResult,
//                      Guardrails, CommonGuardrails.
//
// CONVENTIONS:
//   • `delegate Task<string>(prompt, ct)` → `@Sendable (String) async throws -> String`.
//   • `System.Text.Json` parsing → `TelephonyJson` / `JSONSerialization`.
//   • `System.Text.RegularExpressions.Regex(..., IgnoreCase | Compiled)` →
//     `NSRegularExpression` with `.caseInsensitive`. `.Compiled` has no Swift
//     analogue (NSRegularExpression is already pre-compiled at init).
//   • `IReadOnlyDictionary<string,int>` (ordinal-ignore-case) → `[String: Int]`.
//     The judge keys the dimension scores by the dimension's exact display name
//     (as C# does — it writes `scores[dim.Name]`), so a plain dictionary keyed
//     by `Name` preserves lookups; the OrdinalIgnoreCase only mattered for the
//     internal build and is irrelevant to the returned surface.

import Foundation

// =====================================================================
// LlmJudge.cs
// =====================================================================

/// One scoring dimension. Port of the C# record
/// `CircleAI.Telephony.JudgeDimension`.
public struct JudgeDimension: Sendable, Equatable {
    /// Display name.
    public let name: String
    /// Plain-English rubric the judge sees.
    public let description: String

    public init(name: String, description: String) {
        self.name = name
        self.description = description
    }
}

/// Result of one judging call. Port of the C# record
/// `CircleAI.Telephony.JudgeVerdict`.
public struct JudgeVerdict: Sendable, Equatable {
    /// 0..10 per dimension, keyed by dimension name.
    public let scores: [String: Int]
    /// pass / borderline / fail.
    public let overall: String
    public let reasoning: String

    public init(scores: [String: Int], overall: String, reasoning: String) {
        self.scores = scores
        self.overall = overall
        self.reasoning = reasoning
    }
}

/// Delegate that asks the actual LLM to grade. Port of the C# delegate
/// `CircleAI.Telephony.JudgeCompletion` (`Task<string>(string, CancellationToken)`).
public typealias JudgeCompletion = @Sendable (_ prompt: String) async throws -> String

/// LLM-as-judge driver. Port of `CircleAI.Telephony.LlmJudge`.
public final class LlmJudge: @unchecked Sendable {
    private let completion: JudgeCompletion

    public init(completion: @escaping JudgeCompletion) {
        self.completion = completion
    }

    /// Build the rubric prompt, ask the judge, parse JSON, return the verdict.
    public func judge(
        userUtterance: String,
        assistantResponse: String,
        dimensions: [JudgeDimension]
    ) async throws -> JudgeVerdict {
        let prompt = Self.buildPrompt(user: userUtterance, assistant: assistantResponse, dims: dimensions)
        let raw = try await completion(prompt)
        return Self.parseVerdict(raw, dims: dimensions)
    }

    private static func buildPrompt(user: String, assistant: String, dims: [JudgeDimension]) -> String {
        var rubric = ""
        rubric += "You are an evaluation judge. Score the assistant's reply across the rubric below.\n"
        rubric += "Reply ONLY in this JSON shape:\n"
        rubric += #"{ "scores": { "<dim_name>": <0-10>, ... }, "overall": "pass|borderline|fail", "reasoning": "<one paragraph>" }"# + "\n"
        rubric += "\n"
        rubric += "Rubric:\n"
        for d in dims {
            rubric += "- \(d.name): \(d.description)\n"
        }
        rubric += "\n"
        rubric += "User utterance:\n"
        rubric += user + "\n"
        rubric += "\n"
        rubric += "Assistant reply:\n"
        rubric += assistant + "\n"
        return rubric
    }

    private static func parseVerdict(_ raw: String, dims: [JudgeDimension]) -> JudgeVerdict {
        var scores: [String: Int] = [:]
        do {
            let trimmed = extractJson(raw)
            let root = try TelephonyJson.parse(Data(trimmed.utf8))
            // Matches C#: scores are only populated when "scores" is present AND a
            // JSON object. When it is absent / not an object, the map stays EMPTY
            // (only the parse-failure catch path fills zeros).
            if let s = root["scores"] as? [String: Any] {
                for dim in dims {
                    if let v = s[dim.name] {
                        scores[dim.name] = intValue(v)
                    } else {
                        scores[dim.name] = 0
                    }
                }
            }
            let overall = (root["overall"] as? String) ?? "borderline"
            let reason = (root["reasoning"] as? String) ?? ""
            return JudgeVerdict(scores: scores, overall: overall, reasoning: reason)
        } catch {
            for d in dims { scores[d.name] = 0 }
            return JudgeVerdict(scores: scores, overall: "borderline",
                reasoning: "Judge response could not be parsed.")
        }
    }

    /// Mirror of the C# `v.ValueKind switch`: Number → Int32; numeric String →
    /// parsed Int; anything else → 0. Booleans (NSNumber) are not "numbers" here
    /// and fall through to 0, matching C# (a JSON bool is `JsonValueKind.True`).
    private static func intValue(_ raw: Any) -> Int {
        if let n = raw as? NSNumber {
            if CFGetTypeID(n) == CFBooleanGetTypeID() { return 0 }
            // GetInt32 truncates toward zero for a JSON number.
            return n.intValue
        }
        if let s = raw as? String, let n = Int(s) {
            return n
        }
        return 0
    }

    /// Tolerate models that wrap JSON in prose or fenced code blocks. Port of the
    /// C# `ExtractJson`: slice from the first `{` to the last `}`.
    private static func extractJson(_ raw: String) -> String {
        guard let start = raw.firstIndex(of: "{"),
              let end = raw.lastIndex(of: "}"),
              start < end else {
            return raw
        }
        return String(raw[start...end])
    }
}

// =====================================================================
// EvalSession.cs
// =====================================================================

/// One scripted turn from a fake caller. Port of the C# record
/// `CircleAI.Telephony.EvalTurn`.
public struct EvalTurn: Sendable, Equatable {
    /// What the caller said (already-transcribed).
    public let userTranscript: String
    /// Optional keywords the AI's response should include.
    public let expectedKeywords: [String]?

    public init(userTranscript: String, expectedKeywords: [String]? = nil) {
        self.userTranscript = userTranscript
        self.expectedKeywords = expectedKeywords
    }
}

/// Outcome of one eval turn. Port of the C# record
/// `CircleAI.Telephony.EvalTurnResult`. `TimeSpan Latency` → `TimeInterval`.
public struct EvalTurnResult: Sendable, Equatable {
    public let assistantResponse: String
    public let missingKeywords: [String]
    public let latency: TimeInterval

    public init(assistantResponse: String, missingKeywords: [String], latency: TimeInterval) {
        self.assistantResponse = assistantResponse
        self.missingKeywords = missingKeywords
        self.latency = latency
    }
}

/// Overall eval result. Port of the C# record
/// `CircleAI.Telephony.EvalRunResult`.
public struct EvalRunResult: Sendable, Equatable {
    public let turns: [EvalTurnResult]
    public let allKeywordsHit: Bool
    public let totalLatency: TimeInterval

    public init(turns: [EvalTurnResult], allKeywordsHit: Bool, totalLatency: TimeInterval) {
        self.turns = turns
        self.allKeywordsHit = allKeywordsHit
        self.totalLatency = totalLatency
    }
}

/// Function that runs one turn through the AI under test. Port of the C#
/// delegate `CircleAI.Telephony.EvalTurnHandler`
/// (`Task<string>(string userTranscript, CancellationToken)`).
public typealias EvalTurnHandler = @Sendable (_ userTranscript: String) async throws -> String

/// Drives an EvalSession against a real LLM-based handler. Port of
/// `CircleAI.Telephony.EvalSession`.
public final class EvalSession: @unchecked Sendable {
    private let handler: EvalTurnHandler

    public init(handler: @escaping EvalTurnHandler) {
        self.handler = handler
    }

    /// Run the script and assemble results. Latency is measured wall-clock per
    /// turn (mirrors `DateTime.UtcNow` deltas around the handler call).
    public func run(_ script: [EvalTurn]) async throws -> EvalRunResult {
        var results: [EvalTurnResult] = []
        results.reserveCapacity(script.count)
        var total: TimeInterval = 0
        var allHit = true

        for turn in script {
            let started = Date()
            let response = try await handler(turn.userTranscript)
            let elapsed = Date().timeIntervalSince(started)
            total += elapsed

            var missing: [String] = []
            if let expected = turn.expectedKeywords {
                for kw in expected {
                    // C#: IndexOf(kw, OrdinalIgnoreCase) < 0 → missing.
                    if response.range(of: kw, options: .caseInsensitive) == nil {
                        missing.append(kw)
                    }
                }
            }
            if !missing.isEmpty { allHit = false }
            results.append(EvalTurnResult(
                assistantResponse: response, missingKeywords: missing, latency: elapsed))
        }
        return EvalRunResult(turns: results, allKeywordsHit: allHit, totalLatency: total)
    }
}

// =====================================================================
// Guardrails.cs
// =====================================================================

/// What a guardrail does on match. Port of `CircleAI.Telephony.GuardrailAction`.
///
/// C# ordinals in declaration order: Replace = 0, Redact = 1, Warn = 2.
public enum GuardrailAction: Int, Sendable, Codable, CaseIterable {
    /// Block the turn entirely — the AI says the fallback message instead.
    case replace = 0
    /// Redact only the matched text (e.g. card numbers → "[redacted]").
    case redact = 1
    /// Pass through but flag in the audit log.
    case warn = 2
}

/// One rule the guardrail checks. Port of the C# record
/// `CircleAI.Telephony.GuardrailRule`.
public struct GuardrailRule: Sendable, Equatable {
    /// Display name for logging.
    public let name: String
    /// Regex pattern (applied case-insensitively).
    public let pattern: String
    /// What to do when the pattern matches.
    public let action: GuardrailAction
    /// Replacement text for `.redact`.
    public let replaceWith: String?
    /// Speak this instead when `.replace`.
    public let fallbackMessage: String?

    public init(
        name: String,
        pattern: String,
        action: GuardrailAction,
        replaceWith: String? = nil,
        fallbackMessage: String? = nil
    ) {
        self.name = name
        self.pattern = pattern
        self.action = action
        self.replaceWith = replaceWith
        self.fallbackMessage = fallbackMessage
    }
}

/// Outcome of running guardrails on one text draft. Port of the C# record
/// `CircleAI.Telephony.GuardrailResult`.
public struct GuardrailResult: Sendable, Equatable {
    public let finalText: String
    public let wasModified: Bool
    public let wasBlocked: Bool
    public let triggeredRules: [String]

    public init(finalText: String, wasModified: Bool, wasBlocked: Bool, triggeredRules: [String]) {
        self.finalText = finalText
        self.wasModified = wasModified
        self.wasBlocked = wasBlocked
        self.triggeredRules = triggeredRules
    }
}

/// Pre-TTS guardrail engine. Port of `CircleAI.Telephony.Guardrails`.
///
/// Rules are pre-compiled to `NSRegularExpression` at init (the `.Compiled`
/// flag has no runtime analogue). Immutable after construction, hence a plain
/// `Sendable` final class with no lock.
public final class Guardrails: @unchecked Sendable {
    private let rules: [(rule: GuardrailRule, regex: NSRegularExpression)]
    private let defaultFallback: String

    public init(
        rules: [GuardrailRule]? = nil,
        defaultFallback: String = "I'm sorry, I can't help with that right now."
    ) {
        self.defaultFallback = defaultFallback
        self.rules = (rules ?? []).compactMap { r in
            // C# `new Regex(...)` would throw on an invalid pattern; a rule with
            // an unparseable pattern is dropped here rather than crashing the
            // whole engine. Valid patterns behave identically.
            guard let re = try? NSRegularExpression(pattern: r.pattern, options: [.caseInsensitive]) else {
                return nil
            }
            return (r, re)
        }
    }

    /// Run the guardrails against a draft response.
    public func apply(_ draft: String) -> GuardrailResult {
        if draft.isEmpty {
            return GuardrailResult(finalText: draft, wasModified: false, wasBlocked: false, triggeredRules: [])
        }

        var triggered: [String] = []
        var text = draft
        var blocked = false

        for (rule, regex) in rules {
            if !Self.isMatch(regex, text) { continue }
            triggered.append(rule.name)

            switch rule.action {
            case .replace:
                blocked = true
                text = rule.fallbackMessage ?? defaultFallback
                return GuardrailResult(finalText: text, wasModified: true, wasBlocked: true, triggeredRules: triggered)

            case .redact:
                text = Self.replaceAll(regex, in: text, with: rule.replaceWith ?? "[redacted]")

            case .warn:
                // No mutation; just flag.
                break
            }
        }

        let modified = text != draft
        return GuardrailResult(finalText: text, wasModified: modified, wasBlocked: blocked, triggeredRules: triggered)
    }

    private static func isMatch(_ regex: NSRegularExpression, _ text: String) -> Bool {
        let ns = text as NSString
        return regex.firstMatch(in: text, range: NSRange(location: 0, length: ns.length)) != nil
    }

    private static func replaceAll(_ regex: NSRegularExpression, in text: String, with template: String) -> String {
        let ns = text as NSString
        // The replacement is a literal, not a regex template — escape `$` and `\`
        // so `[redacted]`-style text is inserted verbatim (matches C#
        // `regex.Replace(text, replacement)` where the replacement is plain text
        // containing no `$` groups).
        let literal = NSRegularExpression.escapedTemplate(for: template)
        return regex.stringByReplacingMatches(
            in: text, range: NSRange(location: 0, length: ns.length), withTemplate: literal)
    }
}

/// Common guardrails out of the box. Port of the C# static class
/// `CircleAI.Telephony.CommonGuardrails`.
public enum CommonGuardrails {
    /// Redact 13-19 digit credit-card numbers.
    public static let creditCardRedactor = GuardrailRule(
        name: "credit-card",
        pattern: #"\b(?:\d[ -]*?){13,19}\b"#,
        action: .redact,
        replaceWith: "[redacted card number]")

    /// Block US SSN-shaped sequences (xxx-xx-xxxx).
    public static let ssnBlocker = GuardrailRule(
        name: "ssn",
        pattern: #"\b\d{3}-\d{2}-\d{4}\b"#,
        action: .replace,
        fallbackMessage: "For security I can't share that information.")

    /// Block competitor mentions — supply names per deployment. Names are
    /// regex-escaped and OR-joined, mirroring `string.Join("|", ...Select(Regex.Escape))`.
    public static func competitorMention(_ competitors: String...) -> GuardrailRule {
        competitorMention(competitors)
    }

    /// Array overload (variadic bridges to this).
    public static func competitorMention(_ competitors: [String]) -> GuardrailRule {
        let escaped = competitors.map { NSRegularExpression.escapedPattern(for: $0) }.joined(separator: "|")
        return GuardrailRule(
            name: "competitor",
            pattern: #"\b(?:"# + escaped + #")\b"#,
            action: .replace,
            fallbackMessage: "I can't comment on other providers, but I can help with your account.")
    }
}
