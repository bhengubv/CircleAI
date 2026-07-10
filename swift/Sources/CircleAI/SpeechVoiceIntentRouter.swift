// SpeechVoiceIntentRouter.swift
//
// Port of CircleAI.Speech.Cloud.KeywordVoiceIntentRouter (KeywordVoiceIntentRouter.cs).
//
// Generic regex-based voice intent router. The router matches in order; first
// hit wins; falls through to a caller-defined fallback intent (typically
// "ask-ai") when nothing matches. Named regex groups are surfaced as captures.
//
// C# uses System.Text.RegularExpressions.Regex; Swift uses NSRegularExpression.
// C#'s Regex.GetGroupNames() enumerates named groups and skips the numeric ones
// (int.TryParse). NSRegularExpression does not expose group names, so a
// `VoiceIntent` carries the ordered list of named-group names parsed from the
// pattern at construction; the router reads those named ranges after a match.

import Foundation

/// One named intent the router recognises. `pattern` is matched against the
/// trimmed transcript; on a hit, every named group is exposed in
/// `VoiceIntentMatch.captures`. Port of `CircleAI.Speech.Cloud.VoiceIntent`.
public struct VoiceIntent: @unchecked Sendable {
    public let name: String
    /// The compiled expression (the C# `Regex`).
    public let pattern: NSRegularExpression
    /// The named capture-group names in the pattern, in declaration order.
    /// Mirrors the non-numeric subset of C#'s `Regex.GetGroupNames()`.
    public let groupNames: [String]

    /// Construct from a pre-compiled regex plus its named-group list.
    public init(name: String, pattern: NSRegularExpression, groupNames: [String]) {
        self.name = name
        self.pattern = pattern
        self.groupNames = groupNames
    }

    /// Convenience: compile a pattern string, auto-detecting `(?<name>...)`
    /// groups. Case-insensitive by default (typical for voice commands).
    public init(name: String, pattern patternString: String, options: NSRegularExpression.Options = [.caseInsensitive]) throws {
        let regex = try NSRegularExpression(pattern: patternString, options: options)
        self.init(name: name, pattern: regex, groupNames: VoiceIntent.parseGroupNames(patternString))
    }

    /// Extract named-group names from a regex pattern. Recognises `(?<name>`
    /// and `(?'name'` (the two .NET / ICU named-group syntaxes), skipping
    /// non-capturing look-behinds `(?<=` / `(?<!`.
    public static func parseGroupNames(_ pattern: String) -> [String] {
        var names: [String] = []
        let chars = Array(pattern)
        var i = 0
        var escaped = false
        while i < chars.count {
            let c = chars[i]
            if escaped {
                escaped = false
                i += 1
                continue
            }
            if c == "\\" {
                escaped = true
                i += 1
                continue
            }
            if c == "(" && i + 2 < chars.count && chars[i + 1] == "?" {
                let marker = chars[i + 2]
                if marker == "<" {
                    // Could be (?<name>  or  (?<=  or  (?<!
                    if i + 3 < chars.count, chars[i + 3] == "=" || chars[i + 3] == "!" {
                        i += 1
                        continue
                    }
                    if let name = readName(chars, start: i + 3, terminator: ">") {
                        names.append(name)
                    }
                } else if marker == "'" {
                    if let name = readName(chars, start: i + 3, terminator: "'") {
                        names.append(name)
                    }
                }
            }
            i += 1
        }
        return names
    }

    private static func readName(_ chars: [Character], start: Int, terminator: Character) -> String? {
        var j = start
        var name = ""
        while j < chars.count && chars[j] != terminator {
            name.append(chars[j])
            j += 1
        }
        return name.isEmpty ? nil : name
    }
}

/// One match outcome. Port of `CircleAI.Speech.Cloud.VoiceIntentMatch`.
public struct VoiceIntentMatch: Sendable, Equatable {
    public let intentName: String
    public let transcript: String
    public let captures: [String: String]

    public init(intentName: String, transcript: String, captures: [String: String]) {
        self.intentName = intentName
        self.transcript = transcript
        self.captures = captures
    }
}

/// Maps a transcript to one of a host-supplied set of intents. Rule-based,
/// sub-millisecond per attempt, hermetic. Port of
/// `CircleAI.Speech.Cloud.IVoiceIntentRouter`.
public protocol IVoiceIntentRouter: Sendable {
    /// Backend self-identification — "keyword", "null".
    var backendId: String { get }

    /// Match the transcript against the configured intents. Returns a match for
    /// the first hitting intent, or for the fallback intent when nothing matches
    /// (whose `captures` is empty).
    func route(transcript: String) async -> VoiceIntentMatch
}

/// Default `IVoiceIntentRouter`. Takes an ordered list of intents plus a
/// fallback name (typically "ask-ai") and tries each pattern in order. Port of
/// `CircleAI.Speech.Cloud.KeywordVoiceIntentRouter`.
public final class KeywordVoiceIntentRouter: IVoiceIntentRouter, @unchecked Sendable {
    private let intents: [VoiceIntent]
    private let fallbackIntentName: String

    public init(intents: [VoiceIntent], fallbackIntentName: String = "ask-ai") {
        precondition(!fallbackIntentName.trimmingCharacters(in: .whitespaces).isEmpty,
                     "fallbackIntentName required")
        self.intents = intents
        self.fallbackIntentName = fallbackIntentName
    }

    public var backendId: String { "keyword" }

    public func route(transcript: String) async -> VoiceIntentMatch {
        let text = transcript.trimmingCharacters(in: .whitespacesAndNewlines)
        if text.isEmpty {
            return VoiceIntentMatch(intentName: fallbackIntentName, transcript: "", captures: [:])
        }

        let ns = text as NSString
        let fullRange = NSRange(location: 0, length: ns.length)

        for intent in intents {
            guard let match = intent.pattern.firstMatch(in: text, options: [], range: fullRange) else {
                continue
            }

            var captures: [String: String] = [:]
            for name in intent.groupNames {
                let r = match.range(withName: name)
                guard r.location != NSNotFound, r.length > 0 else { continue }
                let value = ns.substring(with: r)
                // C#: g.Success && !string.IsNullOrEmpty(g.Value) -> captures[name] = g.Value.Trim();
                let trimmed = value.trimmingCharacters(in: .whitespacesAndNewlines)
                if !value.isEmpty {
                    captures[name] = trimmed
                }
            }

            return VoiceIntentMatch(intentName: intent.name, transcript: text, captures: captures)
        }

        return VoiceIntentMatch(intentName: fallbackIntentName, transcript: text, captures: [:])
    }
}

/// Empty router — always returns the fallback intent. Port of
/// `CircleAI.Speech.Cloud.NullVoiceIntentRouter`.
public final class NullVoiceIntentRouter: IVoiceIntentRouter, @unchecked Sendable {
    public static let instance = NullVoiceIntentRouter()
    public init() {}

    public var backendId: String { "null" }

    public func route(transcript: String) async -> VoiceIntentMatch {
        VoiceIntentMatch(intentName: "ask-ai", transcript: transcript, captures: [:])
    }
}
