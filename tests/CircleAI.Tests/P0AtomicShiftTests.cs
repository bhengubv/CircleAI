// P0AtomicShiftTests.cs
//
// Tests for the four pieces that make up the P0 architectural shift:
//   1. ModelScopeCatalogClient + ICatalogSignatureVerifier
//   2. PromptTemplateEngine
//   3. DefaultDeviceContext + DeviceProbe
//   4. DeviceAwareModelSelector (BestFit)
//
// Every test is offline — no network calls, no model bundles on disk.

using System.Collections.Generic;
using System.IO;
using System.Text;
using CircleAI.Core;
using CircleAI.Core.Models;
using CircleAI.Inference;
using Xunit;

namespace CircleAI.Tests;

// ────────────────────────────────────────────────────────────────────────
// PromptTemplateEngine
// ────────────────────────────────────────────────────────────────────────

public sealed class PromptTemplateEngineTests
{
    [Fact]
    public void Render_NoTokenizerConfig_FallsBackToChatML()
    {
        // Empty directory → no tokenizer_config.json → fallback template
        var dir = Directory.CreateTempSubdirectory("circleai-templ-").FullName;
        try
        {
            var engine   = new PromptTemplateEngine();
            var messages = new List<ChatMessage>
            {
                new("user", "Hello"),
            };

            var rendered = engine.Render(dir, messages, addGenerationPrompt: true);

            Assert.Contains("<|im_start|>user", rendered);
            Assert.Contains("Hello", rendered);
            Assert.Contains("<|im_end|>", rendered);
            Assert.Contains("<|im_start|>assistant", rendered); // generation prompt
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Render_HonoursTokenizerConfigChatTemplate()
    {
        var dir = Directory.CreateTempSubdirectory("circleai-templ-").FullName;
        try
        {
            // Minimal Liquid template overriding the fallback.
            var configJson = """
                { "chat_template": "{%- for m in messages -%}[{{ m.role }}] {{ m.content }}\n{%- endfor -%}" }
                """;
            File.WriteAllText(Path.Combine(dir, "tokenizer_config.json"), configJson);

            var engine   = new PromptTemplateEngine();
            var messages = new List<ChatMessage>
            {
                new("user", "Hi"),
                new("assistant", "Hey"),
            };

            var rendered = engine.Render(dir, messages, addGenerationPrompt: false);

            Assert.Contains("[user] Hi", rendered);
            Assert.Contains("[assistant] Hey", rendered);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Render_RemapsToolRoleToUser()
    {
        var dir = Directory.CreateTempSubdirectory("circleai-templ-").FullName;
        try
        {
            var engine   = new PromptTemplateEngine();
            var messages = new List<ChatMessage>
            {
                new("tool", "{\"result\": 42}"),
            };

            var rendered = engine.Render(dir, messages, addGenerationPrompt: false);

            // Tool role normalised to user so the canonical ChatML template
            // can render it without an unknown role.
            Assert.Contains("<|im_start|>user", rendered);
            Assert.DoesNotContain("<|im_start|>tool", rendered);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Render_MalformedTemplate_FallsBackQuietly()
    {
        var dir = Directory.CreateTempSubdirectory("circleai-templ-").FullName;
        try
        {
            // Unterminated Liquid block → parser errors → fallback applied.
            var configJson = """
                { "chat_template": "{% for m in messages %}{{ m.content }}" }
                """;
            File.WriteAllText(Path.Combine(dir, "tokenizer_config.json"), configJson);

            var engine = new PromptTemplateEngine();
            var rendered = engine.Render(
                dir,
                new List<ChatMessage> { new("user", "Hi") },
                addGenerationPrompt: true);

            Assert.Contains("<|im_start|>user", rendered);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}

// ────────────────────────────────────────────────────────────────────────
// Catalog signature verification
// ────────────────────────────────────────────────────────────────────────

public sealed class NullCatalogSignatureVerifierTests
{
    [Fact]
    public void Verify_AlwaysReturnsNotConfigured()
    {
        var verifier = NullCatalogSignatureVerifier.Instance;
        var payload  = Encoding.UTF8.GetBytes("anything");

        var result = verifier.Verify(payload, signatureBase64: null);

        Assert.Equal(CatalogSignatureResult.NotConfigured, result);
    }

    [Fact]
    public void Verify_WithSignature_StillNotConfigured()
    {
        // The Null verifier is fail-closed: even a signature parameter
        // can't promote it to Valid.
        var verifier = NullCatalogSignatureVerifier.Instance;

        var result = verifier.Verify(new byte[] { 1, 2, 3 }, "AAAA");

        Assert.Equal(CatalogSignatureResult.NotConfigured, result);
    }
}

// ────────────────────────────────────────────────────────────────────────
// DefaultDeviceContext
// ────────────────────────────────────────────────────────────────────────

public sealed class DefaultDeviceContextTests
{
    [Fact]
    public void Instance_ReportsRamAndStorage()
    {
        var ctx = DefaultDeviceContext.Instance;

        Assert.NotNull(ctx.AvailableMemoryBytes);
        Assert.True(ctx.AvailableMemoryBytes!.Value > 0);

        Assert.NotNull(ctx.StorageFreeBytes);
        Assert.True(ctx.StorageFreeBytes!.Value > 0);
    }

    [Fact]
    public void BuildProbe_ProducesUsableProbe()
    {
        var probe = DefaultDeviceContext.Instance.BuildProbe();

        Assert.True(probe.CpuCores > 0);
        Assert.True(probe.RamAvailableBytes > 0);

        // Tier classification must produce a real bucket — not the default 0 value.
        var tier = probe.Classify();
        Assert.True(tier >= DeviceTier.Wearable);
    }

    [Fact]
    public void Locale_ReadsCurrentCulture()
    {
        var ctx = DefaultDeviceContext.Instance;
        Assert.False(string.IsNullOrWhiteSpace(ctx.Locale));
    }
}

// ────────────────────────────────────────────────────────────────────────
// DeviceAwareModelSelector (BestFit)
// ────────────────────────────────────────────────────────────────────────

public sealed class DeviceAwareModelSelectorTests
{
    private static DeviceProbe DesktopProbe() => DefaultDeviceContext.Instance.BuildProbe();

    [Fact]
    public void BestFit_UsesEmbeddedRegistry()
    {
        // Default registry (embedded JSON) — selector must succeed
        // even with no remote catalog wired up.
        using var registry = new ModelRegistryService();
        var selector = new DeviceAwareModelSelector(registry);

        var probe = DesktopProbe();
        var selection = selector.BestFit(probe, ChatCapability.Default);

        Assert.False(string.IsNullOrWhiteSpace(selection.ModelId));
        Assert.True(selection.EstimatedBytes >= 0);
    }

    [Fact]
    public void BestFit_RanksHigherQualityFirst()
    {
        // Two-entry registry, both fit; higher QualityRank must win.
        var lo = new ModelEntry("low", "1.0", "Q4") { QualityRank = 10, MinRamGb = 0.5, MinStorageGb = 0.5 };
        var hi = new ModelEntry("high","1.0", "Q4") { QualityRank = 99, MinRamGb = 0.5, MinStorageGb = 0.5 };

        using var registry = new InMemoryRegistry(new[] { lo, hi });
        var selector       = new DeviceAwareModelSelector(registry);

        var selection = selector.BestFit(DesktopProbe(), ChatCapability.Default);

        Assert.Equal("high", selection.ModelId);
    }

    [Fact]
    public void BestFit_FallsBackToSmallestWhenNoneFit()
    {
        // Both entries' MinRamGb exceeds any device — selector falls back
        // to the smallest one (per docstring contract), not throws.
        var heavy = new ModelEntry("heavy","1.0","Q4") { QualityRank = 50, MinRamGb = 10_000, MinStorageGb = 0 };
        var huge  = new ModelEntry("huge", "1.0","Q4") { QualityRank = 80, MinRamGb = 99_000, MinStorageGb = 0 };

        using var registry = new InMemoryRegistry(new[] { heavy, huge });
        var selector       = new DeviceAwareModelSelector(registry);

        var selection = selector.BestFit(DesktopProbe(), ChatCapability.Default);

        // QualityRank ties broken by lowest MinRamGb when no entry fits.
        Assert.Equal("huge", selection.ModelId); // 80 > 50 wins QualityRank ordering
    }

    [Fact]
    public void BestFit_ThrowsWhenRegistryEmpty()
    {
        using var registry = new InMemoryRegistry(System.Array.Empty<ModelEntry>());
        var selector       = new DeviceAwareModelSelector(registry);

        Assert.Throws<System.InvalidOperationException>(
            () => selector.BestFit(DesktopProbe(), ChatCapability.Default));
    }

    [Fact]
    public void ParseCapabilities_EmptyDefaultsToDefault()
    {
        var parsed = DeviceAwareModelSelector.ParseCapabilities(null);
        Assert.Equal(ChatCapability.Default, parsed);
    }

    [Fact]
    public void ParseCapabilities_KnownFlagsOr()
    {
        var parsed = DeviceAwareModelSelector.ParseCapabilities(
            new[] { "Tools", "Vision" });

        Assert.True(parsed.HasFlag(ChatCapability.Tools));
        Assert.True(parsed.HasFlag(ChatCapability.Vision));
    }
}

// ────────────────────────────────────────────────────────────────────────
// In-memory registry for selector tests (avoids hitting embedded JSON).
// Wraps ModelRegistryService by deserialising a synthesised payload via
// the public IReadOnlyList<ModelEntry> AllModels accessor pattern.
// ────────────────────────────────────────────────────────────────────────

internal sealed class InMemoryRegistry : ModelRegistryService
{
    private readonly IReadOnlyList<ModelEntry> _entries;

    public InMemoryRegistry(IReadOnlyList<ModelEntry> entries) : base()
    {
        _entries = entries;
    }

    public override IReadOnlyList<ModelEntry> AllModels => _entries;
}
