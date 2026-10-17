// SystemPromptEnrichmentTests.cs
//
// PrepareMessagesAsync used to skip persona, device context, RAG recall and
// skill context ENTIRELY whenever the caller supplied its own system message.
// Silently. A host that set a system prompt therefore lost memory grounding
// without any signal — which presents to a user as "the assistant forgot",
// i.e. as a bad model rather than as a dropped feature.
//
// Enrichment still applies in both cases and the caller's own instructions are
// never rewritten. WHERE it goes changed (2026-10-17): MNN now owns the chat template
// and holds the persona as its own system prompt, so the system turn must stay
// byte-identical turn after turn and the per-turn notes ride at the head of the LAST
// user message instead. These tests pin that: the system turn is the caller's (or the
// SDK's) prompt and nothing else, the notes are with the question, and the
// conversation itself is untouched.

using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CircleAI.Core;
using CircleAI.Hosting;
using CircleAI.Inference;
using Xunit;

namespace CircleAI.Tests;

public sealed class SystemPromptEnrichmentTests
{
    /// <summary>Device context is the cheapest enrichment to trigger — no stores needed.</summary>
    private sealed class LocatedDevice : IDeviceContext
    {
        public string? LocationHint => "Durban, ZA";

        public string? ActiveAppId => null;
        public string? Locale => null;
        public string? TimeZoneId => null;
        public System.DateTimeOffset? LocalTime => null;
        public double? Latitude => null;
        public double? Longitude => null;
        public float? BatteryLevel => null;
        public bool? IsCharging => null;
        public string? NetworkType => null;
        public float? CpuUsagePercent => null;
        public long? AvailableMemoryBytes => null;
        // Fully qualified: ThermalState is declared in BOTH CircleAI.Core and
        // CircleAI.Hosting, so importing both makes the bare name ambiguous.
        public CircleAI.Core.ThermalState? ThermalState => null;
        public long? StorageFreeBytes => null;
        public System.DateTimeOffset? LastActiveUtc => null;
    }

    private static AIService Build(SystemPromptEnrichment mode) =>
        new(new AIOptions
        {
            SystemPrompt            = "SDK-DEFAULT-PROMPT",
            DeviceContext           = new LocatedDevice(),
            SystemPromptEnrichment  = mode,
        });

    private static async Task<(string System, string LastUser)> Turns(AIService svc, params ChatMessage[] messages)
    {
        var prepared = await svc.PrepareMessagesAsync(messages, "where am i?", CancellationToken.None);
        var system = prepared.FirstOrDefault(m =>
            string.Equals(m.Role, "system", System.StringComparison.OrdinalIgnoreCase));
        var user = prepared.LastOrDefault(m =>
            string.Equals(m.Role, "user", System.StringComparison.OrdinalIgnoreCase));
        return (system?.Content ?? "", user?.Content ?? "");
    }

    [Fact]
    public async Task CallerOwnsSystemTurn_StillGetsEnrichment()
    {
        // The regression this file was written for: "Durban" must reach the model.
        await using var svc = Build(SystemPromptEnrichment.Always);

        var (system, user) = await Turns(svc,
            new ChatMessage("system", "CALLER-PROMPT"),
            new ChatMessage("user", "where am i?"));

        Assert.Equal("CALLER-PROMPT", system);
        Assert.Contains("Durban", user);
    }

    [Fact]
    public async Task CallerInstructionsComeFirst()
    {
        // The caller's instructions are the system turn, untouched; the notes are in
        // the user turn after it - so they can never read as overriding them.
        await using var svc = Build(SystemPromptEnrichment.Always);

        var (system, user) = await Turns(svc,
            new ChatMessage("system", "CALLER-PROMPT"),
            new ChatMessage("user", "where am i?"));

        Assert.DoesNotContain("Durban", system);
        Assert.True(user.IndexOf("Durban", System.StringComparison.Ordinal)
                    < user.IndexOf("where am i?", System.StringComparison.Ordinal),
            "The notes lead the question, never follow it.");
    }

    [Fact]
    public async Task CallerOwnsSystemTurn_DoesNotGetTheSdkDefaultPrompt()
    {
        // Enrichment yes; overriding their prompt with ours, no. Injecting
        // AIOptions.SystemPrompt here would silently contradict the caller.
        await using var svc = Build(SystemPromptEnrichment.Always);

        var (system, user) = await Turns(svc,
            new ChatMessage("system", "CALLER-PROMPT"),
            new ChatMessage("user", "where am i?"));

        Assert.DoesNotContain("SDK-DEFAULT-PROMPT", system);
        Assert.DoesNotContain("SDK-DEFAULT-PROMPT", user);
    }

    [Fact]
    public async Task OnlyWhenAbsent_RestoresTheOldBehaviour()
    {
        await using var svc = Build(SystemPromptEnrichment.OnlyWhenAbsent);

        var (system, user) = await Turns(svc,
            new ChatMessage("system", "CALLER-PROMPT"),
            new ChatMessage("user", "where am i?"));

        Assert.Equal("CALLER-PROMPT", system);
        Assert.DoesNotContain("Durban", user);
    }

    [Fact]
    public async Task NoCallerSystemTurn_GetsBasePromptAndEnrichment()
    {
        await using var svc = Build(SystemPromptEnrichment.Always);

        var (system, user) = await Turns(svc, new ChatMessage("user", "where am i?"));

        Assert.Equal("SDK-DEFAULT-PROMPT", system);   // byte-identical: MNN holds it
        Assert.Contains("Durban", user);
    }

    [Fact]
    public async Task ConversationTurns_SurviveIntact()
    {
        // Composition must not drop or reorder the actual conversation; only the
        // question being answered carries the notes, and it still ends with itself.
        await using var svc = Build(SystemPromptEnrichment.Always);

        var prepared = await svc.PrepareMessagesAsync(
            new List<ChatMessage>
            {
                new("system", "CALLER-PROMPT"),
                new("user", "first"),
                new("assistant", "reply"),
                new("user", "where am i?"),
            },
            "where am i?", CancellationToken.None);

        var nonSystem = prepared
            .Where(m => !string.Equals(m.Role, "system", System.StringComparison.OrdinalIgnoreCase))
            .Select(m => m.Content)
            .ToList();

        Assert.Equal(3, nonSystem.Count);
        Assert.Equal("first", nonSystem[0]);
        Assert.Equal("reply", nonSystem[1]);
        Assert.EndsWith("where am i?", nonSystem[2]);
    }
}
