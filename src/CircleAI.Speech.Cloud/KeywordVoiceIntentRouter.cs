// KeywordVoiceIntentRouter.cs
//
// (3.2.0) Generic regex-based voice intent router. Lifted from
// CircleUp's KeywordVoiceCommandRouter — vault-specific patterns
// stripped, replaced with a host-supplied list of intent definitions.
// The router matches in order; first hit wins; falls through to a
// caller-defined fallback intent (typically "AskAi") when nothing
// matches.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.Speech.Cloud;

/// <summary>
/// (3.2.0) One named intent the router recognises. <see cref="Pattern"/>
/// is matched against the trimmed transcript; on a hit, every named
/// group is exposed in <see cref="VoiceIntentMatch.Captures"/>.
/// </summary>
public sealed record VoiceIntent(string Name, Regex Pattern);

/// <summary>One match outcome.</summary>
public sealed record VoiceIntentMatch(
    string                         IntentName,
    string                         Transcript,
    IReadOnlyDictionary<string, string> Captures);

/// <summary>
/// (3.2.0) Maps a transcript to one of a host-supplied set of intents.
/// Rule-based, sub-millisecond per attempt, hermetic.
/// </summary>
public interface IVoiceIntentRouter
{
    /// <summary>Backend self-identification — "keyword", "null".</summary>
    string BackendId { get; }

    /// <summary>
    /// Match the transcript against the configured intents. Returns a
    /// match for the first hitting intent, or for the fallback intent
    /// when nothing matches (whose <see cref="VoiceIntentMatch.Captures"/>
    /// is empty).
    /// </summary>
    ValueTask<VoiceIntentMatch> RouteAsync(string transcript, CancellationToken ct = default);
}

/// <summary>
/// (3.2.0) Default <see cref="IVoiceIntentRouter"/>. Takes an ordered
/// list of intents plus a fallback name (typically "ask-ai") and tries
/// each pattern in order.
/// </summary>
public sealed class KeywordVoiceIntentRouter : IVoiceIntentRouter
{
    private readonly IReadOnlyList<VoiceIntent> _intents;
    private readonly string _fallbackIntentName;

    public KeywordVoiceIntentRouter(IEnumerable<VoiceIntent> intents, string fallbackIntentName = "ask-ai")
    {
        ArgumentNullException.ThrowIfNull(intents);
        ArgumentException.ThrowIfNullOrWhiteSpace(fallbackIntentName);
        _intents = intents.ToList();
        _fallbackIntentName = fallbackIntentName;
    }

    public string BackendId => "keyword";

    public ValueTask<VoiceIntentMatch> RouteAsync(string transcript, CancellationToken ct = default)
    {
        var text = transcript?.Trim() ?? string.Empty;
        if (text.Length == 0)
        {
            return ValueTask.FromResult(new VoiceIntentMatch(
                IntentName: _fallbackIntentName,
                Transcript: string.Empty,
                Captures:   new Dictionary<string, string>()));
        }

        foreach (var intent in _intents)
        {
            var match = intent.Pattern.Match(text);
            if (!match.Success) continue;

            var captures = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var name in intent.Pattern.GetGroupNames())
            {
                // Skip the implicit "0" group (the full match) — only
                // surface named groups.
                if (int.TryParse(name, out _)) continue;
                var g = match.Groups[name];
                if (g.Success && !string.IsNullOrEmpty(g.Value))
                {
                    captures[name] = g.Value.Trim();
                }
            }

            return ValueTask.FromResult(new VoiceIntentMatch(
                IntentName: intent.Name,
                Transcript: text,
                Captures:   captures));
        }

        return ValueTask.FromResult(new VoiceIntentMatch(
            IntentName: _fallbackIntentName,
            Transcript: text,
            Captures:   new Dictionary<string, string>()));
    }
}

/// <summary>(3.2.0) Empty router — always returns the fallback intent.</summary>
public sealed class NullVoiceIntentRouter : IVoiceIntentRouter
{
    public static readonly NullVoiceIntentRouter Instance = new();

    public string BackendId => "null";

    public ValueTask<VoiceIntentMatch> RouteAsync(string transcript, CancellationToken ct = default)
        => ValueTask.FromResult(new VoiceIntentMatch(
            IntentName: "ask-ai",
            Transcript: transcript ?? string.Empty,
            Captures:   new Dictionary<string, string>()));
}
