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
        // THE ORDERING RULE ON THE NO-FIT BRANCH, WHICH MEANS THE BRANCH HAS TO BE
        // REACHED.
        //
        // TWO WRONG REWRITES OF THIS TEST, BOTH MINE, BOTH THE SAME ERROR: building a
        // device where the models ARE candidates and then asserting the fallback's
        // answer. The first took percentages of TOTAL ram while the gate read FREE ram,
        // and on a dev box those are nearly equal, so both models fitted and rank 80
        // won. The second gave an 8 GB handset models of 1 GB and 2 GB: both under its
        // 6.5 GB ceiling, so BestFit ranked them durably, answered "huge" and called it
        // Good - correctly, because the device CAN hold either.
        //
        // The branch under test runs when NOTHING is holdable. So the models have to be
        // bigger than the handset, which is what the original 10_000 / 99_000 GB entries
        // were crudely doing. 7 GB and 9 GB against an 8 GB phone is the same thing said
        // in numbers a reader can check: ceiling max(8 - 1.5, 8 x 0.40) = 6.5 GB, and
        // both are over it.
        var probe = new DeviceProbe(
            RamAvailableBytes: 300_000_000,
            StorageFreeBytes:  60_000_000_000,
            Gpu:               GpuKind.None,
            CpuCores:          8,
            Thermal:           ThermalClass.Passive,
            Connectivity:      Connectivity.Online)
        { RamTotalBytes = 8_000_000_000 };

        var heavy = new ModelEntry("heavy","1.0","Q4") { QualityRank = 50, MinRamGb = 7.0, MinStorageGb = 0 };
        var huge  = new ModelEntry("huge", "1.0","Q4") { QualityRank = 80, MinRamGb = 9.0, MinStorageGb = 0 };

        // Stated as preconditions, so a change to either rule fails here with its reason
        // rather than showing up as a surprising winner.
        Assert.False(ModelFit.Fits(heavy, probe), "precondition: nothing fits this second");
        Assert.False(ModelFit.Fits(huge,  probe), "precondition: nothing fits this second");
        Assert.False(ModelFit.CouldEverHold(heavy, probe), "precondition: the device could never hold either");
        Assert.False(ModelFit.CouldEverHold(huge,  probe), "precondition: the device could never hold either");

        using var registry = new InMemoryRegistry(new[] { heavy, huge });
        var selector       = new DeviceAwareModelSelector(registry);

        var selection = selector.BestFit(probe, ChatCapability.Default);

        // Was Assert.Equal("huge") — i.e. the LARGEST, contradicting both this
        // test's own name and BestFit's docstring ("a wearable that can only run
        // the smallest model should still get the smallest model"). The old
        // assertion codified the bug: the no-fit branch ordered by QualityRank
        // DESC, so an unfittable device was handed the biggest model in the
        // catalogue. Fixed 2026-07-20; now ordered by MinRamGb ASC.
        Assert.Equal(SelectionQuality.NothingFits, selection.Quality);
        Assert.Equal("heavy", selection.ModelId); // smaller MinRamGb wins, rank 50 < 80
    }

    // THE TEST THAT ASSERTED A THROW IS GONE, AND SO IS THE THROW.
    //
    // I added both an hour ago: a ceiling on the least-bad branch, and a test
    // pinning that a device which could hold nothing got an exception. The
    // contract is the opposite and was already written in the code I was
    // changing - BestFit always answers and FLAGS it SelectionQuality
    // .NothingFits, because setup needs a fetch target and the census needs
    // something to count even where none of it will load. DeviceCapability
    // .PlanFor renders that flag as an unavailable line; ModelChoice.For turns
    // it into null for a screen. Eleven tests said so at once.
    //
    // The ceiling was not wrong, only its consequence: it decides the FLAG now,
    // not whether there is an answer at all.


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
