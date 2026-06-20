// Circle31ContractTests.cs
//
// (3.1.0) Contract tests for CircleAI.Video — IVideoGenerator,
// IStyleScript, IStyleReference — plus the new ModelEntry.MinVramGb
// and DeviceProbe.VramGb fields they ride on.

using System;
using System.Linq;
using System.Threading.Tasks;
using CircleAI.Core;
using CircleAI.Core.Models;
using CircleAI.Inference;
using CircleAI.Video;
using Xunit;

namespace CircleAI.Tests;

public sealed class Circle31ContractTests
{
    // ── IVideoGenerator (Null backend) ────────────────────────────────

    [Fact]
    public async Task NullVideoGenerator_ReturnsZeroBytesAtRequestedResolution()
    {
        var req = new VideoGenerationRequest(
            Prompt:     "honey-loving bear walking through hundred-acre wood",
            Duration:   TimeSpan.FromSeconds(8),
            Resolution: VideoResolution.P720,
            FrameRate:  24);

        var result = await NullVideoGenerator.Instance.GenerateAsync(req);

        Assert.Equal("null", result.BackendId);
        Assert.Equal(VideoResolution.P720, result.Resolution);
        Assert.Equal(0, result.FrameCount);
        Assert.Equal(TimeSpan.Zero, result.Duration);
        Assert.True(result.VideoBytes.IsEmpty);
        Assert.Equal("video/mp4", result.MimeType);
    }

    // ── IStyleScript (Null backend) ───────────────────────────────────

    [Fact]
    public async Task NullStyleScript_PassesSourceMessageThroughUnchanged()
    {
        var msg = "pick up groceries on the way home";
        var req = new StyleScriptRequest(
            SourceMessage: msg,
            Style:         new StyleId("noir-detective"));

        var result = await NullStyleScript.Instance.RewriteAsync(req);

        Assert.Equal(msg, result.RewrittenText);
        Assert.Equal("noir-detective", result.Style.Value);
        Assert.Null(result.VoicePersonaId);
        Assert.Equal(TimeSpan.Zero, result.EstimatedSpokenDuration);
    }

    // ── IStyleReference (InMemory backend) ────────────────────────────

    [Fact]
    public async Task InMemoryStyleReference_RegisterThenGetRoundTrips()
    {
        var store = new InMemoryStyleReference();

        var pooh = new StyleReference(
            Id:               new StyleId("pooh-1926"),
            DisplayName:      "Pooh (1926 Shepard illustrations)",
            ShortDescription: "Honey-loving bear of very little brain",
            Attribution:      new StyleAttribution(
                                  Source:  "A. A. Milne / E. H. Shepard, 1926",
                                  License: "Public Domain (entered 2022)"),
            VoicePersonaId:   "warm-narrator",
            Frames:           Array.Empty<StyleReferenceFrame>());

        await store.RegisterAsync(pooh);

        var fetched = await store.GetAsync(new StyleId("pooh-1926"));

        Assert.NotNull(fetched);
        Assert.Equal("Pooh (1926 Shepard illustrations)", fetched!.DisplayName);
        Assert.Equal("warm-narrator", fetched.VoicePersonaId);
    }

    [Fact]
    public async Task InMemoryStyleReference_ListReturnsEveryRegisteredStyle()
    {
        var store = new InMemoryStyleReference();

        await store.RegisterAsync(NewStyle("space-opera"));
        await store.RegisterAsync(NewStyle("storybook-watercolour"));
        await store.RegisterAsync(NewStyle("claymation"));

        var all = await store.ListAsync();
        var ids = all.Select(s => s.Id.Value).OrderBy(x => x).ToArray();

        Assert.Equal(new[] { "claymation", "space-opera", "storybook-watercolour" }, ids);
    }

    [Fact]
    public async Task InMemoryStyleReference_RegisterIsIdempotentByIdOverwrite()
    {
        var store = new InMemoryStyleReference();

        await store.RegisterAsync(NewStyle("noir", "v1"));
        await store.RegisterAsync(NewStyle("noir", "v2"));

        var fetched = await store.GetAsync(new StyleId("noir"));
        Assert.NotNull(fetched);
        Assert.Equal("v2", fetched!.DisplayName);
    }

    [Fact]
    public async Task InMemoryStyleReference_GetUnknownReturnsNull()
    {
        var store = new InMemoryStyleReference();
        var missing = await store.GetAsync(new StyleId("does-not-exist"));
        Assert.Null(missing);
    }

    // ── ChatCapability.Video flag ─────────────────────────────────────

    [Fact]
    public void ChatCapability_Video_FlagIsDistinct()
    {
        var flag = ChatCapability.Video;
        Assert.NotEqual(ChatCapability.None, flag);
        Assert.NotEqual(ChatCapability.Vision, flag);
        Assert.NotEqual(ChatCapability.Default, flag);
        Assert.True((flag & ChatCapability.Video) == ChatCapability.Video);
    }

    [Fact]
    public void ChatCapability_VideoComposesWithOtherFlags()
    {
        var combo = ChatCapability.Default | ChatCapability.Tools | ChatCapability.Video;
        Assert.True((combo & ChatCapability.Video) != 0);
        Assert.True((combo & ChatCapability.Tools) != 0);
        Assert.True((combo & ChatCapability.Default) != 0);
    }

    // ── ModelEntry.MinVramGb gate ─────────────────────────────────────

    [Fact]
    public void ModelEntry_MinVramGb_DefaultsToNull()
    {
        var entry = new ModelEntry(
            Name:         "Qwen3-0.6B-MNN",
            Version:      "0.6.0",
            Quantization: "Q4_K_M");

        Assert.Null(entry.MinVramGb);
    }

    [Fact]
    public void ModelEntry_MinVramGb_PopulatesViaWithInit()
    {
        var entry = new ModelEntry(
            Name:         "CogVideoX-2B",
            Version:      "2.0.0",
            Quantization: "FP16") with
        {
            MinVramGb = 6.0,
        };

        Assert.NotNull(entry.MinVramGb);
        Assert.Equal(6.0, entry.MinVramGb!.Value);
    }

    // ── DeviceProbe.VramGb gate ───────────────────────────────────────

    [Fact]
    public void DeviceProbe_VramGb_DefaultsToNull()
    {
        var probe = DeviceProbe.Snapshot();
        Assert.Null(probe.VramGb);
    }

    [Fact]
    public void DeviceProbe_VramGb_PopulatesViaSnapshotOverride()
    {
        var probe = DeviceProbe.Snapshot(vramGbOverride: 8.0);
        Assert.NotNull(probe.VramGb);
        Assert.Equal(8.0, probe.VramGb!.Value);
    }

    [Fact]
    public void DeviceProbe_VramGb_PopulatesViaWithInit()
    {
        var probe = DeviceProbe.Snapshot() with { VramGb = 12.0 };
        Assert.NotNull(probe.VramGb);
        Assert.Equal(12.0, probe.VramGb!.Value);
    }

    // ── helpers ───────────────────────────────────────────────────────

    private static StyleReference NewStyle(string id, string? displayName = null) =>
        new(
            Id:               new StyleId(id),
            DisplayName:      displayName ?? id,
            ShortDescription: id,
            Attribution:      new StyleAttribution(Source: "test", License: "test"),
            VoicePersonaId:   null,
            Frames:           Array.Empty<StyleReferenceFrame>());
}
