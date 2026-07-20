// SpeechModelSelectorTests.cs
//
// The speech selector must pick by MODALITY and honour device fit, and must be
// completely disjoint from chat selection in both directions.

using System;
using System.Collections.Generic;
using System.Linq;
using CircleAI.Core;
using CircleAI.Core.Models;
using CircleAI.Inference;
using Xunit;

namespace CircleAI.Tests;

public sealed class SpeechModelSelectorTests
{
    private static DeviceProbe Device(double ramGb, double storageGb = 32) =>
        new(
            RamAvailableBytes: (long)(ramGb * 1024 * 1024 * 1024),
            StorageFreeBytes:  (long)(storageGb * 1024 * 1024 * 1024),
            Gpu:               GpuKind.None,
            CpuCores:          8,
            Thermal:           ThermalClass.Passive,
            Connectivity:      Connectivity.Online);

    private sealed class MixedRegistry : ModelRegistryService
    {
        private readonly IReadOnlyList<ModelEntry> _entries;
        public MixedRegistry(IReadOnlyList<ModelEntry> entries) : base() => _entries = entries;
        public override IReadOnlyList<ModelEntry> AllModels => _entries;
    }

    private static ModelEntry Tts(string name, int rank, double ramGb) =>
        new(name, "1.0", "ONNX") { Modality = ModelModality.Tts, QualityRank = rank, MinRamGb = ramGb, MinStorageGb = ramGb };

    private static ModelEntry Chat(string name) =>
        new(name, "1.0", "Q4") { QualityRank = 50, MinRamGb = 0.5, MinStorageGb = 0.5 };

    [Fact]
    public void PicksBestTtsThatFits()
    {
        using var reg = new MixedRegistry(new[]
        {
            Tts("tts-tiny", rank: 5, ramGb: 0.3),
            Tts("tts-good", rank: 9, ramGb: 1.0),
            Tts("tts-big",  rank: 12, ramGb: 8.0),   // does not fit a 2 GB device
        });

        var pick = new SpeechModelSelector(reg).BestFor(Device(2.0), ModelModality.Tts);

        Assert.NotNull(pick);
        Assert.Equal("tts-good", pick!.ModelId);
        Assert.Equal(SelectionQuality.Good, pick.Quality);
    }

    [Fact]
    public void UncataloguedModality_ReturnsNull_NotAnException()
    {
        // "we have no wake-word model" must be answerable as null, so a host can
        // fall back to the energy detector rather than crash.
        using var reg = new MixedRegistry(new[] { Tts("tts", 5, 0.3) });

        Assert.Null(new SpeechModelSelector(reg).BestFor(Device(4), ModelModality.WakeWord));
    }

    [Fact]
    public void NothingFits_ReturnsSmallestMarkedNothingFits()
    {
        using var reg = new MixedRegistry(new[]
        {
            Tts("tts-a", rank: 9, ramGb: 6.0),
            Tts("tts-b", rank: 12, ramGb: 9.0),
        });

        var pick = new SpeechModelSelector(reg).BestFor(Device(1.0), ModelModality.Tts);

        Assert.NotNull(pick);
        Assert.Equal("tts-a", pick!.ModelId);   // smallest MinRamGb
        Assert.Equal(SelectionQuality.NothingFits, pick.Quality);
    }

    [Fact]
    public void BelowFloor_IsReported()
    {
        using var reg = new MixedRegistry(new[] { Tts("tts-weak", rank: 3, ramGb: 0.3) });

        var pick = new SpeechModelSelector(reg).BestFor(Device(4), ModelModality.Tts, minQualityRank: 8);

        Assert.Equal(SelectionQuality.BelowFloor, pick!.Quality);
    }

    [Fact]
    public void SpeechSelector_IgnoresChatModels()
    {
        using var reg = new MixedRegistry(new[] { Chat("qwen"), Tts("tts", 5, 0.3) });

        var pick = new SpeechModelSelector(reg).BestFor(Device(8), ModelModality.Tts);
        Assert.Equal("tts", pick!.ModelId);

        // And asking for a chat model through the speech selector is a misuse.
        Assert.Throws<ArgumentException>(
            () => new SpeechModelSelector(reg).BestFor(Device(8), ModelModality.Chat));
    }

    [Fact]
    public void CandidatesFor_ListsOnlyThatModality_FitMarked()
    {
        using var reg = new MixedRegistry(new[]
        {
            Tts("tts-fits", rank: 9, ramGb: 0.5),
            Tts("tts-toobig", rank: 12, ramGb: 20.0),
            Chat("qwen"),
        });

        var list = new SpeechModelSelector(reg).CandidatesFor(Device(2.0), ModelModality.Tts);

        Assert.Equal(2, list.Count);
        Assert.DoesNotContain(list, c => c.ModelId == "qwen");
        Assert.Equal(SelectionQuality.NothingFits, list.Single(c => c.ModelId == "tts-toobig").Quality);
        Assert.Equal(SelectionQuality.Good,        list.Single(c => c.ModelId == "tts-fits").Quality);
    }
}
