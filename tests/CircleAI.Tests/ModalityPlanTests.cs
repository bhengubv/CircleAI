// ModalityPlanTests.cs
//
// Guards the FALLBACK TABLE in SpeechModelSelector.PlanFor.
//
// Every HeuristicFallback row is a claim that a non-model implementation ships
// and works. If someone deletes EnergyVadDetector, or adds a row for a modality
// with no built-in, the selector starts telling callers a capability is
// available when it is not — and the failure surfaces as silence on a phone,
// not as a red build. These tests are the thing that stops that.
//
// They deliberately assert against the REAL embedded registry rather than a
// fake: the question "can this build do X" is only meaningful about the models
// actually catalogued.

using CircleAI.Core;
using CircleAI.Core.Models;
using CircleAI.Inference;
using CircleAI.Voice;
using Xunit;

namespace CircleAI.Tests;

public sealed class ModalityPlanTests
{
    private static DeviceProbe Device(double ramGb, double storageGb = 32) =>
        new(
            RamAvailableBytes: (long)(ramGb * 1024 * 1024 * 1024),
            StorageFreeBytes:  (long)(storageGb * 1024 * 1024 * 1024),
            Gpu:               GpuKind.None,
            CpuCores:          8,
            Thermal:           ThermalClass.Passive,
            Connectivity:      Connectivity.Online);

    private static SpeechModelSelector Selector() => new(new ModelRegistryService());

    // ── the fallback table ───────────────────────────────────────────────────

    [Fact]
    public void Vad_IsAlwaysAvailable_EvenOnATinyDevice()
    {
        // EnergyVadDetector is arithmetic over PCM frames — no model, no RAM.
        // VAD must never come back Unavailable, on any hardware.
        var plan = Selector().PlanFor(Device(0.25, storageGb: 0.1), ModelModality.Vad);

        Assert.True(plan.IsAvailable);
        Assert.Equal(SelectionQuality.HeuristicFallback, plan.Quality);
        Assert.Null(plan.Model);
    }

    [Fact]
    public void Vad_FallbackClaimIsBackedByARealType()
    {
        // The row claims a built-in exists. Prove it does and implements the
        // contract the pipeline consumes — deleting it must break this test,
        // not just make phones go quiet.
        Assert.True(typeof(IVoiceActivityDetector).IsAssignableFrom(typeof(EnergyVadDetector)));
        _ = new EnergyVadDetector(0.02f, silenceFrames: 10, frameSizeBytes: 640);
    }

    [Fact]
    public void WakeWord_FallsBackToTranscribeAndMatch_WhenAsrIsAvailable()
    {
        // EnergyWakeWordDetector has no wake model — it transcribes and matches.
        // Given an ASR model is catalogued, wake word is served, not missing.
        var probe = Device(4);
        var selector = Selector();

        Assert.NotNull(selector.BestFor(probe, ModelModality.Asr));   // premise

        var plan = selector.PlanFor(probe, ModelModality.WakeWord);
        Assert.Equal(SelectionQuality.HeuristicFallback, plan.Quality);
        Assert.Contains("battery", plan.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Asr_HasNoFallback_SoItIsModelOrNothing()
    {
        // There is no way to transcribe without a model. If ASR is ever reported
        // as HeuristicFallback, someone has claimed a capability that cannot run.
        var plan = Selector().PlanFor(Device(4), ModelModality.Asr);
        Assert.NotEqual(SelectionQuality.HeuristicFallback, plan.Quality);
    }

    [Fact]
    public void Tts_HasNoFallback()
    {
        // Same as ASR. The platform TTS engine would be a fallback on paper, but
        // it is Google-backed on Android — excluded by the de-Googled rule — so
        // there genuinely is none.
        var plan = Selector().PlanFor(Device(4), ModelModality.Tts);
        Assert.NotEqual(SelectionQuality.HeuristicFallback, plan.Quality);
    }

    // ── the honest hole ──────────────────────────────────────────────────────

    [Fact]
    public void Vision_IsUnavailable_AndSaysWhy()
    {
        // No vision model is catalogued and there is no fallback. The selector
        // must say so plainly. When a VLM IS catalogued with a real hash this
        // test flips — and it should be UPDATED then, not deleted now.
        var plan = Selector().PlanFor(Device(8), ModelModality.Vision);

        Assert.False(plan.IsAvailable);
        Assert.Equal(SelectionQuality.Unavailable, plan.Quality);
        Assert.Null(plan.Model);
        Assert.False(string.IsNullOrWhiteSpace(plan.Reason));
    }

    [Fact]
    public void UnavailablePlansCarryAReasonAUserCouldRead()
    {
        // The Reason is shown to a user ("I can't see images: ..."), so it must
        // never be empty or a bare type name.
        foreach (var m in new[] { ModelModality.Vision, ModelModality.Asr, ModelModality.Tts })
        {
            var plan = Selector().PlanFor(Device(0.2, storageGb: 0.05), m);
            Assert.False(string.IsNullOrWhiteSpace(plan.Reason));
            Assert.True(plan.Reason.Length > 10, $"{m} reason too terse: '{plan.Reason}'");
        }
    }

    // ── the distinction that was collapsed ───────────────────────────────────

    [Fact]
    public void NothingFits_IsNotTheSameAsUnavailable()
    {
        // The original defect: a bare null made "models exist, this phone is too
        // small" indistinguishable from "we ship no such model". They have
        // different fixes — a smaller model vs cataloguing one — so the selector
        // must not conflate them.
        var tiny = Device(0.05, storageGb: 0.01);
        var asr  = Selector().PlanFor(tiny, ModelModality.Asr);

        // ASR models ARE catalogued, so even on absurd hardware the verdict is
        // about FIT, never about absence.
        Assert.NotEqual(SelectionQuality.Unavailable, asr.Quality);
        Assert.NotNull(asr.Model);
    }

    [Fact]
    public void ChatStillRefusesToGoThroughTheSpeechSelector()
    {
        // Chat selection belongs to IModelSelector.BestFit. Adding Vision here
        // must not have loosened that guard.
        Assert.Throws<ArgumentException>(
            () => Selector().PlanFor(Device(4), ModelModality.Chat));
    }
}
