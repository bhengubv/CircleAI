using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using CircleAI.Inference;
using Xunit;

namespace CircleAI.Tests;

/// <summary>
/// Smoke tests for Inference project gaps (2, 5, 7).
/// Verifies the MNN P/Invoke surface that replaced llama.cpp in 1.2.0 —
/// reflection-based existence checks; no native library required.
/// </summary>
public sealed class InferenceGapTests
{
    // ── Gap 2: Session save/load P/Invokes (MNN) ──────────────────────────

    [Fact]
    public void MnnInterop_HasSessionPInvokes()
    {
        var t = typeof(MnnInterop);

        Assert.NotNull(t.GetMethod("mnn_llm_save_session",
            BindingFlags.Public | BindingFlags.Static));

        Assert.NotNull(t.GetMethod("mnn_llm_load_session",
            BindingFlags.Public | BindingFlags.Static));

        Assert.NotNull(t.GetMethod("mnn_llm_reset_session",
            BindingFlags.Public | BindingFlags.Static));
    }

    [Fact]
    public void MnnInterop_HasSaveAndLoadSessionHelpers()
    {
        var t = typeof(MnnInterop);

        // The high-level managed helpers exposed alongside the raw P/Invokes.
        // SaveSession / LoadSession wrap mnn_llm_save_session / load_session
        // and return bool for ergonomic error handling.
        Assert.NotNull(t.GetMethod("SaveSession",
            BindingFlags.Public | BindingFlags.Static));

        Assert.NotNull(t.GetMethod("LoadSession",
            BindingFlags.Public | BindingFlags.Static));
    }

    // ── Gap 7: Vision P/Invokes (MNN) ─────────────────────────────────────

    [Fact]
    public void MnnInterop_HasVisionPInvokes()
    {
        var t = typeof(MnnInterop);

        // MNN's vision surface: image-handle free + streaming generate-with-image.
        // (llava_* was the llama.cpp surface; replaced 1:1 by these.)
        Assert.NotNull(t.GetMethod("mnn_llm_image_free",
            BindingFlags.Public | BindingFlags.Static));

        Assert.NotNull(t.GetMethod("mnn_llm_generate_with_image_stream_ex",
            BindingFlags.Public | BindingFlags.Static));
    }

    // ── Gap 7: VisionInput ────────────────────────────────────────────────

    [Fact]
    public void VisionInput_Properties_RoundTrip()
    {
        var img = new byte[] { 1, 2, 3 };
        var vi = new VisionInput { ImageBytes = img, MimeType = "image/jpeg" };

        Assert.Equal(img, vi.ImageBytes);
        Assert.Equal("image/jpeg", vi.MimeType);
    }

    [Fact]
    public void VisionInput_MimeType_IsOptional()
    {
        var vi = new VisionInput { ImageBytes = new byte[] { 0xFF } };
        Assert.Null(vi.MimeType);
    }

    // ── Gap 5: NativeLibraryResolver ─────────────────────────────────────

    [Fact]
    public void NativeLibraryResolver_EnsureRegistered_DoesNotThrow()
    {
        // Calling twice should be idempotent
        var ex1 = Record.Exception(NativeLibraryResolver.EnsureRegistered);
        var ex2 = Record.Exception(NativeLibraryResolver.EnsureRegistered);

        Assert.Null(ex1);
        Assert.Null(ex2);
    }
}

// ── ModelSelectorTests ────────────────────────────────────────────────────────

// (ModelSelectorTests removed — the hardcoded static `ModelSelector` it exercised
// was deleted as an architecture-invariant violation. Model selection is
// catalog-driven via `DeviceAwareModelSelector` (see DeviceAwareModelSelectorTests).)

// ── ContextWindowBudgetManagerTests ──────────────────────────────────────────

public sealed class ContextWindowBudgetManagerTests
{
    [Fact]
    public void RemainingTokens_DecreasesAfterRecordExchange()
    {
        var mgr = new ContextWindowBudgetManager(contextSize: 4096);
        mgr.RecordExchange(promptTokens: 200, completionTokens: 100);

        Assert.Equal(4096 - 300, mgr.RemainingTokens);
        Assert.Equal(300, mgr.UsedTokens);
    }

    [Fact]
    public void ShouldEvict_TrueWhenFillRatioAtOrAboveThreshold()
    {
        // Default threshold is 0.85 → need >= 0.85 * 4096 = 3481.6 → 3482 tokens
        var mgr = new ContextWindowBudgetManager(contextSize: 4096, evictionThreshold: 0.85);
        mgr.RecordExchange(promptTokens: 3482, completionTokens: 0);

        Assert.True(mgr.ShouldEvict);
        Assert.True(mgr.FillRatio >= 0.85);
    }

    [Fact]
    public void CalculateEvictionCount_ReturnsSensibleValue()
    {
        var mgr = new ContextWindowBudgetManager(contextSize: 4096);
        mgr.RecordExchange(promptTokens: 3000, completionTokens: 500); // 3500 used

        // At targetFillRatio=0.50: target = 2048 tokens → evict 3500-2048 = 1452
        var evict = mgr.CalculateEvictionCount(targetFillRatio: 0.50);
        Assert.Equal(1452, evict);
        Assert.True(evict > 0);
    }

    [Fact]
    public void Reset_ClearsUsedTokens()
    {
        var mgr = new ContextWindowBudgetManager(contextSize: 4096);
        mgr.RecordExchange(promptTokens: 1000, completionTokens: 500);
        Assert.NotEqual(0, mgr.UsedTokens);

        mgr.Reset();
        Assert.Equal(0, mgr.UsedTokens);
        Assert.False(mgr.ShouldEvict);
    }
}

// ── ModelDownloadServiceTests ─────────────────────────────────────────────────

public sealed class ModelDownloadServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly ModelDownloadService _svc;

    public ModelDownloadServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"BhenguAI_Test_{Guid.NewGuid():N}");
        _svc = new ModelDownloadService(_tempDir);
    }

    [Fact]
    public async Task IsModelCachedAsync_ReturnsFalse_ForUnknownModel()
    {
        var cached = await _svc.IsModelCachedAsync("nonexistent-model", CancellationToken.None);
        Assert.False(cached);
    }

    [Fact]
    public async Task DeleteModelAsync_IsNoOp_ForMissingModel()
    {
        // Should not throw
        var ex = await Record.ExceptionAsync(() =>
            _svc.DeleteModelAsync("ghost-model", CancellationToken.None));
        Assert.Null(ex);
    }

    [Fact]
    public async Task GetAvailableDiskSpaceBytesAsync_ReturnsPositiveValue()
    {
        var bytes = await _svc.GetAvailableDiskSpaceBytesAsync(CancellationToken.None);
        Assert.True(bytes > 0, $"Expected positive disk space but got {bytes}");
    }

    public void Dispose()
    {
        _svc.Dispose();
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }
}
