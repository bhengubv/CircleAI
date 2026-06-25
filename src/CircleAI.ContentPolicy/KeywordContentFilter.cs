// KeywordContentFilter.cs
//
// (3.3.0) Real keyword/regex content filter + threshold refusal policy
// + prompt-injection detector. These are not LLM-grade safety models —
// they're production-grade fast checks. Hosts that need a real safety
// LLM wrap one behind the same contract.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.ContentPolicy;

/// <summary>(3.3.0) Rule for the keyword content filter.</summary>
public sealed record KeywordRule(string Category, string Pattern, SafetyVerdict OnMatch, float Confidence = 0.9f)
{
    public Regex Regex { get; } = new(Pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled);
}

/// <summary>(3.3.0) Default rule set for everyday harm classes.</summary>
public static class CommonKeywordRules
{
    public static IReadOnlyList<KeywordRule> Default { get; } = new[]
    {
        new KeywordRule("self-harm",        @"\b(kill myself|suicide|self\s*-?\s*harm)\b",       SafetyVerdict.Refuse, 0.95f),
        new KeywordRule("explicit-sexual",  @"\b(porn|sexual content|nsfw)\b",                   SafetyVerdict.Flag,   0.7f),
        new KeywordRule("violence",         @"\b(how to make a bomb|chemical weapon|murder)\b",  SafetyVerdict.Refuse, 0.9f),
        new KeywordRule("hate",             @"\b(racial slur|hate speech)\b",                    SafetyVerdict.Refuse, 0.9f),
        new KeywordRule("pii-card",         @"\b(?:\d[ -]*?){13,19}\b",                          SafetyVerdict.Flag,   0.8f),
    };
}

public sealed class KeywordContentFilter : IContentFilter
{
    private readonly IReadOnlyList<KeywordRule> _rules;

    public KeywordContentFilter(IReadOnlyList<KeywordRule>? rules = null)
    {
        _rules = rules ?? CommonKeywordRules.Default;
    }

    public string BackendId => "keyword";

    public ValueTask<SafetyFinding> ClassifyAsync(string text, CancellationToken ct = default)
    {
        if (text is null) throw new ArgumentNullException(nameof(text));
        foreach (var r in _rules)
        {
            if (r.Regex.IsMatch(text))
            {
                return ValueTask.FromResult(new SafetyFinding(r.OnMatch, r.Category, $"Matched rule '{r.Category}'", r.Confidence));
            }
        }
        return ValueTask.FromResult(new SafetyFinding(SafetyVerdict.Allow, "ok", "No rule matched", 1f));
    }
}

/// <summary>(3.3.0) Threshold refusal policy — refuse when any finding's Refuse verdict is above the threshold,
/// or when the count of Flag findings exceeds the configured ceiling.</summary>
public sealed class ThresholdRefusalPolicy : IRefusalPolicy
{
    private readonly float _refuseThreshold;
    private readonly int   _flagCeiling;

    public ThresholdRefusalPolicy(float refuseThreshold = 0.5f, int flagCeiling = 3)
    {
        _refuseThreshold = refuseThreshold;
        _flagCeiling     = flagCeiling;
    }

    public string BackendId => "threshold";

    public ValueTask<bool> ShouldRefuseAsync(IReadOnlyList<SafetyFinding> findings, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(findings);
        if (findings.Any(f => f.Verdict == SafetyVerdict.Refuse && f.Confidence >= _refuseThreshold))
        {
            return ValueTask.FromResult(true);
        }
        var flagCount = findings.Count(f => f.Verdict == SafetyVerdict.Flag);
        return ValueTask.FromResult(flagCount > _flagCeiling);
    }
}

/// <summary>(3.3.0) Detect common prompt-injection patterns in untrusted text from RAG / tool output / web.</summary>
public sealed class KeywordPromptInjectionDetector : IPromptInjectionDetector
{
    private static readonly Regex[] Patterns = new[]
    {
        new Regex(@"ignore (all|the|any) (previous|prior) instructions",                                 RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new Regex(@"forget (everything|all) (above|prior)",                                              RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new Regex(@"you (are now|will be|are no longer)",                                                RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new Regex(@"system prompt[:\s]",                                                                 RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new Regex(@"reveal (your|the) (instructions|system prompt|hidden context)",                      RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new Regex(@"<\|im_(start|end)\|>",                                                               RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new Regex(@"(BEGIN|END)\s+(SYSTEM|DEVELOPER|ASSISTANT)\s+MESSAGE",                                RegexOptions.IgnoreCase | RegexOptions.Compiled),
    };

    public string BackendId => "keyword";

    public ValueTask<SafetyFinding> InspectAsync(string untrustedContent, string sourceLabel, CancellationToken ct = default)
    {
        if (untrustedContent is null) throw new ArgumentNullException(nameof(untrustedContent));
        foreach (var p in Patterns)
        {
            var match = p.Match(untrustedContent);
            if (match.Success)
            {
                return ValueTask.FromResult(new SafetyFinding(
                    SafetyVerdict.Refuse,
                    "prompt-injection",
                    $"Pattern matched in {sourceLabel}: \"{Truncate(match.Value, 60)}\"",
                    0.9f));
            }
        }
        return ValueTask.FromResult(new SafetyFinding(SafetyVerdict.Allow, "ok", "No injection patterns", 1f));
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
