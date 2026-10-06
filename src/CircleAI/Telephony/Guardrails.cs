// Guardrails.cs
//
// (3.3.0) Pre-TTS phrase blocking. The model's draft response runs
// through the guardrails before TTS — banned phrases are rewritten or
// the whole turn is replaced with a fallback message. Useful for
// keeping the AI on-script, banning PII leaks, or stopping competitor
// name mentions.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace CircleAI.Telephony;

/// <summary>(3.3.0) One rule the guardrail checks.</summary>
/// <param name="Name">Display name for logging.</param>
/// <param name="Pattern">Regex pattern (case-insensitive).</param>
/// <param name="Action">What to do when the pattern matches.</param>
/// <param name="ReplaceWith">Replacement text for <see cref="GuardrailAction.Redact"/>.</param>
/// <param name="FallbackMessage">Speak this instead when <see cref="GuardrailAction.Replace"/>.</param>
public sealed record GuardrailRule(
    string           Name,
    string           Pattern,
    GuardrailAction  Action,
    string?          ReplaceWith     = null,
    string?          FallbackMessage = null);

/// <summary>(3.3.0) What a guardrail does on match.</summary>
public enum GuardrailAction
{
    /// <summary>Block the turn entirely — the AI says <see cref="GuardrailRule.FallbackMessage"/> instead.</summary>
    Replace,
    /// <summary>Redact only the matched text (e.g. credit-card numbers → "[redacted]").</summary>
    Redact,
    /// <summary>Pass through but flag in the audit log.</summary>
    Warn,
}

/// <summary>(3.3.0) Outcome of running guardrails on one text draft.</summary>
public sealed record GuardrailResult(
    string                         FinalText,
    bool                           WasModified,
    bool                           WasBlocked,
    IReadOnlyList<string>          TriggeredRules);

/// <summary>(3.3.0) Pre-TTS guardrail engine.</summary>
public sealed class Guardrails
{
    private readonly List<(GuardrailRule Rule, Regex Regex)> _rules;
    private readonly string _defaultFallback;

    public Guardrails(
        IEnumerable<GuardrailRule>? rules = null,
        string defaultFallback = "I'm sorry, I can't help with that right now.")
    {
        _defaultFallback = defaultFallback;
        _rules = (rules ?? Array.Empty<GuardrailRule>())
            .Select(r => (r, new Regex(r.Pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled)))
            .ToList();
    }

    /// <summary>(3.3.0) Run the guardrails against a draft response.</summary>
    public GuardrailResult Apply(string draft)
    {
        if (string.IsNullOrEmpty(draft))
        {
            return new GuardrailResult(draft ?? "", false, false, Array.Empty<string>());
        }

        var triggered = new List<string>();
        var text = draft;
        bool blocked = false;

        foreach (var (rule, regex) in _rules)
        {
            if (!regex.IsMatch(text)) continue;
            triggered.Add(rule.Name);

            switch (rule.Action)
            {
                case GuardrailAction.Replace:
                    blocked = true;
                    text = rule.FallbackMessage ?? _defaultFallback;
                    return new GuardrailResult(text, true, true, triggered);

                case GuardrailAction.Redact:
                    text = regex.Replace(text, rule.ReplaceWith ?? "[redacted]");
                    break;

                case GuardrailAction.Warn:
                    // No mutation; just flag.
                    break;
            }
        }

        var modified = !ReferenceEquals(text, draft) && text != draft;
        return new GuardrailResult(text, modified, blocked, triggered);
    }
}

/// <summary>(3.3.0) Common guardrails out of the box.</summary>
public static class CommonGuardrails
{
    /// <summary>(3.3.0) Redact 13-19 digit credit-card numbers.</summary>
    public static GuardrailRule CreditCardRedactor { get; } =
        new("credit-card",
            Pattern: @"\b(?:\d[ -]*?){13,19}\b",
            Action: GuardrailAction.Redact,
            ReplaceWith: "[redacted card number]");

    /// <summary>(3.3.0) Block US SSN-shaped sequences (xxx-xx-xxxx).</summary>
    public static GuardrailRule SsnBlocker { get; } =
        new("ssn",
            Pattern: @"\b\d{3}-\d{2}-\d{4}\b",
            Action: GuardrailAction.Replace,
            FallbackMessage: "For security I can't share that information.");

    /// <summary>(3.3.0) Block competitor mentions — supply names per deployment.</summary>
    public static GuardrailRule CompetitorMention(params string[] competitors) =>
        new("competitor",
            Pattern: @"\b(?:" + string.Join("|", competitors.Select(Regex.Escape)) + @")\b",
            Action: GuardrailAction.Replace,
            FallbackMessage: "I can't comment on other providers, but I can help with your account.");
}
