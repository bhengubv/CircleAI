// SystemPromptEnrichmentTests.cs
//
// PrepareMessagesAsync used to skip persona, device context, RAG recall and
// skill context ENTIRELY whenever the caller supplied its own system message.
// Silently. A host that set a system prompt therefore lost memory grounding
// without any signal — which presents to a user as "the assistant forgot",
// i.e. as a bad model rather than as a dropped feature.
//
// Enrichment now applies in both cases, with the caller's own instructions
// first and never rewritten, and SystemPromptEnrichment.OnlyWhenAbsent restores
// the old behaviour for hosts that genuinely want total prompt control.

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

    private static async Task<string> SystemTurn(AIService svc, params ChatMessage[] messages)
    {
        var prepared = await svc.PrepareMessagesAsync(messages, "where am i?", CancellationToken.None);
        var system = prepared.FirstOrDefault(m =>
            string.Equals(m.Role, "system", System.StringComparison.OrdinalIgnoreCase));
        return system?.Content ?? "";
    }

    [Fact]
    public async Task CallerOwnsSystemTurn_StillGetsEnrichment()
    {
        // The regression. Previously "Durban" never reached the model here.
        await using var svc = Build(SystemPromptEnrichment.Always);

        var content = await SystemTurn(svc,
            new ChatMessage("system", "CALLER-PROMPT"),
            new ChatMessage("user", "where am i?"));

        Assert.Contains("CALLER-PROMPT", content);
        Assert.Contains("Durban", content);
    }

    [Fact]
    public async Task CallerInstructionsComeFirst()
    {
        // Grounding is appended AFTER the caller's instructions — it must never
        // read as though it overrides them.
        await using var svc = Build(SystemPromptEnrichment.Always);

        var content = await SystemTurn(svc,
            new ChatMessage("system", "CALLER-PROMPT"),
            new ChatMessage("user", "where am i?"));

        Assert.True(content.IndexOf("CALLER-PROMPT", System.StringComparison.Ordinal)
                    < content.IndexOf("Durban", System.StringComparison.Ordinal),
            "Caller's own instructions must precede appended grounding.");
    }

    [Fact]
    public async Task CallerOwnsSystemTurn_DoesNotGetTheSdkDefaultPrompt()
    {
        // Enrichment yes; overriding their prompt with ours, no. Injecting
        // AIOptions.SystemPrompt here would silently contradict the caller.
        await using var svc = Build(SystemPromptEnrichment.Always);

        var content = await SystemTurn(svc,
            new ChatMessage("system", "CALLER-PROMPT"),
            new ChatMessage("user", "where am i?"));

        Assert.DoesNotContain("SDK-DEFAULT-PROMPT", content);
    }

    [Fact]
    public async Task OnlyWhenAbsent_RestoresTheOldBehaviour()
    {
        await using var svc = Build(SystemPromptEnrichment.OnlyWhenAbsent);

        var content = await SystemTurn(svc,
            new ChatMessage("system", "CALLER-PROMPT"),
            new ChatMessage("user", "where am i?"));

        Assert.Contains("CALLER-PROMPT", content);
        Assert.DoesNotContain("Durban", content);
    }

    [Fact]
    public async Task NoCallerSystemTurn_GetsBasePromptAndEnrichment()
    {
        // Unchanged path — must not regress while fixing the other one.
        await using var svc = Build(SystemPromptEnrichment.Always);

        var content = await SystemTurn(svc, new ChatMessage("user", "where am i?"));

        Assert.Contains("SDK-DEFAULT-PROMPT", content);
        Assert.Contains("Durban", content);
    }

    [Fact]
    public async Task ConversationTurns_SurviveIntact()
    {
        // Composition must not drop or reorder the actual conversation.
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

        Assert.Equal(new[] { "first", "reply", "where am i?" }, nonSystem);
    }
}
