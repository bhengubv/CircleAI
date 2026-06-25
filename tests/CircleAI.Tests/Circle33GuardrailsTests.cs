// Circle33GuardrailsTests.cs
//
// (3.3.0) Tests for pre-TTS guardrails.

using System;
using CircleAI.Telephony;
using Xunit;

namespace CircleAI.Tests;

public class Circle33GuardrailsTests
{
    [Fact]
    public void Apply_NoRules_ReturnsTextUnchanged()
    {
        var g = new Guardrails();
        var r = g.Apply("hello");
        Assert.Equal("hello", r.FinalText);
        Assert.False(r.WasModified);
        Assert.False(r.WasBlocked);
        Assert.Empty(r.TriggeredRules);
    }

    [Fact]
    public void Apply_RedactRule_RedactsMatchedText()
    {
        var g = new Guardrails(new[] { CommonGuardrails.CreditCardRedactor });
        var r = g.Apply("Your card is 4111 1111 1111 1111 thank you.");
        Assert.Contains("[redacted card number]", r.FinalText);
        Assert.DoesNotContain("4111 1111", r.FinalText);
        Assert.True(r.WasModified);
        Assert.False(r.WasBlocked);
    }

    [Fact]
    public void Apply_ReplaceRule_ReturnsFallback()
    {
        var g = new Guardrails(new[] { CommonGuardrails.SsnBlocker });
        var r = g.Apply("Your SSN is 123-45-6789.");
        Assert.Equal("For security I can't share that information.", r.FinalText);
        Assert.True(r.WasBlocked);
        Assert.True(r.WasModified);
    }

    [Fact]
    public void Apply_WarnRule_FlagsWithoutMutating()
    {
        var rule = new GuardrailRule("uppercase", "[A-Z]{4,}", GuardrailAction.Warn);
        var g = new Guardrails(new[] { rule });
        var r = g.Apply("PLEASE help me.");
        Assert.Equal("PLEASE help me.", r.FinalText);
        Assert.Contains("uppercase", r.TriggeredRules);
    }

    [Fact]
    public void Apply_CompetitorMention_Blocks()
    {
        var g = new Guardrails(new[] { CommonGuardrails.CompetitorMention("Acme", "BetaCorp") });
        var r = g.Apply("I think Acme is better.");
        Assert.True(r.WasBlocked);
        Assert.Contains("can't comment on other providers", r.FinalText);
    }

    [Fact]
    public void Apply_MultipleRules_ReplaceWins()
    {
        var g = new Guardrails(new[]
        {
            CommonGuardrails.CreditCardRedactor,
            CommonGuardrails.SsnBlocker,
        });
        var r = g.Apply("Card 4111111111111111, SSN 123-45-6789.");
        // SSN replace wins after credit-card redact runs.
        Assert.True(r.WasBlocked);
        Assert.Contains("SSN", string.Join(",", r.TriggeredRules), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Apply_EmptyDraft_ReturnsEmpty()
    {
        var g = new Guardrails(new[] { CommonGuardrails.SsnBlocker });
        var r = g.Apply("");
        Assert.Equal("", r.FinalText);
        Assert.False(r.WasBlocked);
    }

    [Fact]
    public void Apply_DefaultFallback_UsedWhenRuleHasNone()
    {
        var rule = new GuardrailRule("any", "boom", GuardrailAction.Replace);
        var g = new Guardrails(new[] { rule }, defaultFallback: "default fallback");
        var r = g.Apply("boom!");
        Assert.Equal("default fallback", r.FinalText);
    }
}
