// P1DeviceDefaultsTests.cs
//
// Tests for the P1 "infer from device, don't ask consumer" shift:
//   - AIOptions.ModelId / ContextSize default null
//   - AIService.StartAsync auto-resolves ModelId via IModelSelector
//   - DeviceTierDefaults sizes the context window
//   - IAIObserver.OnModelFetchingAsync fires with autoSelected=true
//
// No real model loads — uses a fake loader + fake generator so the test
// hits the resolution logic without touching native MNN.

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CircleAI.Core;
using CircleAI.Core.Models;
using CircleAI.Hosting;
using CircleAI.Inference;
using Xunit;

namespace CircleAI.Tests;

public sealed class P1AIOptionsDefaultsTests
{
    [Fact]
    public void Defaults_ModelIdNull_ContextSizeNull()
    {
        var opts = new AIOptions();
        Assert.Null(opts.ModelId);
        Assert.Null(opts.ContextSize);
    }

    [Fact]
    public void ContextSize_CanStillBePinned()
    {
        var opts = new AIOptions { ContextSize = 16_384 };
        Assert.Equal(16_384, opts.ContextSize);
    }

    [Fact]
    public void ModelId_CanStillBePinned()
    {
        var opts = new AIOptions { ModelId = "Qwen3-0.6B-MNN" };
        Assert.Equal("Qwen3-0.6B-MNN", opts.ModelId);
    }
}

public sealed class P1ContextWindowFromTierTests
{
    [Theory]
    [InlineData(DeviceTier.Wearable,      2048)]
    [InlineData(DeviceTier.Phone,         4096)]
    [InlineData(DeviceTier.Tablet,        8192)]
    [InlineData(DeviceTier.Desktop,       32768)]
    [InlineData(DeviceTier.Workstation,   131072)]
    public void ContextWindow_TracksTier(DeviceTier tier, int expected)
    {
        Assert.Equal(expected, DeviceTierDefaults.ContextWindow(tier));
    }
}

public sealed class P1AgenticMaxIterationsFromTierTests
{
    [Theory]
    [InlineData(DeviceTier.Wearable,    2)]
    [InlineData(DeviceTier.Phone,       3)]
    [InlineData(DeviceTier.Tablet,      5)]
    [InlineData(DeviceTier.Desktop,    10)]
    [InlineData(DeviceTier.Workstation,10)]
    public void AgenticMaxIterations_TracksTier(DeviceTier tier, int expected)
    {
        Assert.Equal(expected, DeviceTierDefaults.AgenticMaxIterations(tier));
    }
}

public sealed class P1ConcurrencyFromTierTests
{
    [Theory]
    [InlineData(DeviceTier.Wearable,   16, 1)]
    [InlineData(DeviceTier.Phone,      16, 2)]
    [InlineData(DeviceTier.Tablet,     16, 4)]
    [InlineData(DeviceTier.Desktop,    16, 8)]
    [InlineData(DeviceTier.Workstation,16, 14)] // min(16, cores-2)
    public void MaxConcurrency_TracksTier(DeviceTier tier, int cores, int expected)
    {
        Assert.Equal(expected, DeviceTierDefaults.MaxConcurrency(tier, cores));
    }

}

public sealed class P1AutoResolutionTests
{
    [Fact]
    public async Task StartAsync_NullModelId_CallsSelector_AndEmitsObserver()
    {
        // Wire fakes: selector returns "auto-pick" model; loader returns
        // a temp dummy file so the AIService doesn't try to load native MNN.
        var dummyDir   = Directory.CreateTempSubdirectory("circleai-p1-").FullName;
        var dummyPath  = Path.Combine(dummyDir, "model.bin");
        File.WriteAllText(dummyPath, "stub");

        try
        {
            var selector = new FakeSelector("auto-pick", DeviceTier.Desktop);
            var loader   = new FakeLoader(dummyPath, expectModelId: "auto-pick");
            var observer = new RecordingObserver();

            var opts = new AIOptions
            {
                ModelId  = null,                  // <-- the whole point
                Observer = observer,
                WarmOnStart = false,              // skip warm to avoid native call
            };

            // FakeGenerator avoids any native call.
            IChatGenerator GenFactory(string path) => new FakeGenerator();

            var svc = new AIService(opts, loader, GenFactory, selector, logger: null);
            await svc.StartAsync(CancellationToken.None);

            Assert.True(svc.IsReady);
            Assert.True(selector.WasCalled, "Selector must run when ModelId is null.");
            Assert.True(loader.GetCalled,   "Loader.GetModelPath must run for selected id.");
            Assert.Single(observer.FetchEvents);
            var ev = observer.FetchEvents[0];
            Assert.Equal("auto-pick", ev.ModelId);
            Assert.True(ev.AutoSelected);

            await svc.DisposeAsync();
        }
        finally { Directory.Delete(dummyDir, recursive: true); }
    }

    [Fact]
    public async Task StartAsync_PinnedModelId_SkipsSelector_AutoSelectedFalse()
    {
        var dummyDir  = Directory.CreateTempSubdirectory("circleai-p1-").FullName;
        var dummyPath = Path.Combine(dummyDir, "model.bin");
        File.WriteAllText(dummyPath, "stub");

        try
        {
            var selector = new FakeSelector("should-not-be-used", DeviceTier.Desktop);
            var loader   = new FakeLoader(dummyPath, expectModelId: "pinned-id");
            var observer = new RecordingObserver();

            var opts = new AIOptions
            {
                ModelId  = "pinned-id",
                Observer = observer,
                WarmOnStart = false,
            };

            IChatGenerator GenFactory(string path) => new FakeGenerator();

            var svc = new AIService(opts, loader, GenFactory, selector, logger: null);
            await svc.StartAsync(CancellationToken.None);

            Assert.False(selector.WasCalled, "Pinned ModelId must skip the selector.");
            Assert.Single(observer.FetchEvents);
            Assert.Equal("pinned-id", observer.FetchEvents[0].ModelId);
            Assert.False(observer.FetchEvents[0].AutoSelected);

            await svc.DisposeAsync();
        }
        finally { Directory.Delete(dummyDir, recursive: true); }
    }

    [Fact]
    public async Task StartAsync_NullModelId_NoSelector_ThrowsHelpful()
    {
        var loader = new FakeLoader(modelPath: "/never/used", expectModelId: "");
        var opts   = new AIOptions { ModelId = null, WarmOnStart = false };

        IChatGenerator GenFactory(string path) => new FakeGenerator();

        var svc = new AIService(opts, loader, GenFactory, modelSelector: null, logger: null);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.StartAsync(CancellationToken.None));
        Assert.Contains("IModelSelector", ex.Message);

        await svc.DisposeAsync();
    }
}

// ────────────────────────────────────────────────────────────────────────
// Fakes — minimal stand-ins so the resolution logic runs without native
// MNN or a real ModelScope catalog.
// ────────────────────────────────────────────────────────────────────────

internal sealed class FakeSelector : IModelSelector
{
    private readonly string _modelId;
    private readonly DeviceTier _tier;

    public bool WasCalled { get; private set; }

    public FakeSelector(string modelId, DeviceTier tier)
    {
        _modelId = modelId;
        _tier    = tier;
    }

    public ModelSelection BestFit(DeviceProbe probe, ChatCapability required)
    {
        WasCalled = true;
        return new ModelSelection(_modelId, RequiresDownload: false, EstimatedBytes: 0, Tier: _tier);
    }

    public IReadOnlyList<ModelSelection> AllCandidates(DeviceProbe probe)
        => Array.Empty<ModelSelection>();
}

internal sealed class FakeLoader : IModelLoader
{
    private readonly string _modelPath;
    private readonly string _expectModelId;

    public bool GetCalled { get; private set; }

    public FakeLoader(string modelPath, string expectModelId)
    {
        _modelPath     = modelPath;
        _expectModelId = expectModelId;
    }

    public string GetModelPath(string modelName)
    {
        GetCalled = true;
        return _modelPath;
    }

    public Task<string> DownloadModelAsync(string modelName, IProgress<float>? progress = null)
        => Task.FromResult(_modelPath);

    public bool ModelExists(string modelName) => true;

    public Task<bool> CheckForCriticalUpdateAsync() => Task.FromResult(false);

    public void Dispose() { }
}

internal sealed class FakeGenerator : IChatGenerator
{
    public Task<string> GenerateAsync(
        IReadOnlyList<ChatMessage> messages,
        GenerationOptions? options = null,
        CancellationToken ct = default)
        => Task.FromResult(string.Empty);

    public async IAsyncEnumerable<string> StreamAsync(
        IReadOnlyList<ChatMessage> messages,
        GenerationOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.CompletedTask;
        yield break;
    }

    public void Dispose() { }
}

internal sealed class RecordingObserver : IAIObserver
{
    public List<(string ModelId, bool AutoSelected)> FetchEvents { get; } = new();

    public ValueTask OnModelFetchingAsync(string modelId, bool autoSelected, CancellationToken ct = default)
    {
        FetchEvents.Add((modelId, autoSelected));
        return ValueTask.CompletedTask;
    }
}
