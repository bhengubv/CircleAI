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

    [Fact]
    public void Tts_ClimbsToTheHighVoice_WhenTheDeviceCanHoldIt()
    {
        // The rung above Piper: en_US-lessac-high (QualityRank 9) sits above
        // en_US-lessac-medium (QualityRank 7). Any normal phone holds the 114 MB
        // high voice, so the selector climbs to it — the floor is not the ceiling.
        var plan = Selector().PlanFor(Device(4), ModelModality.Tts);

        Assert.Equal(SelectionQuality.Good, plan.Quality);
        Assert.NotNull(plan.Model);
        Assert.Equal("Piper-en_US-lessac-high", plan.Model!.ModelId);
    }

    [Fact]
    public void Tts_FallsBackToTheMediumVoice_WhenTheHighVoiceDoesNotFit()
    {
        // A device with room for the medium voice but not the high one (0.4 GB:
        // above medium's 0.3 floor, below high's 0.5) degrades to medium rather
        // than failing — the ladder has a real lower rung, not only a top.
        var plan = Selector().PlanFor(Device(0.4), ModelModality.Tts);

        Assert.Equal(SelectionQuality.Good, plan.Quality);
        Assert.NotNull(plan.Model);
        Assert.Equal("Piper-en_US-lessac-medium", plan.Model!.ModelId);
    }

    // ── the model-backed modalities added in 3.5 (Music / Video / Coding) ──────

    [Fact]
    public void Music_IsAlwaysAvailable_ViaTheProceduralBed()
    {
        // ProceduralMusicBedGenerator synthesises a bed with pure managed maths —
        // no model, no RAM, no download. Like VAD, music must never come back
        // Unavailable, and the verdict is HeuristicFallback until a neural music
        // model is catalogued.
        var plan = Selector().PlanFor(Device(0.25, storageGb: 0.1), ModelModality.Music);

        Assert.True(plan.IsAvailable);
        Assert.True(plan.UsesBuiltIn);
        Assert.Equal(SelectionQuality.HeuristicFallback, plan.Quality);
        Assert.Null(plan.Model);
    }

    [Fact]
    public void Video_IsAlwaysAvailable_ViaTheProgrammaticRenderer()
    {
        // ManagedMediaRenderer composites layers / text / motion in managed code,
        // so a clip is always producible offline. The neural encoder / HTML path
        // is a seam; its absence is HeuristicFallback, not Unavailable.
        var plan = Selector().PlanFor(Device(0.25, storageGb: 0.1), ModelModality.Video);

        Assert.True(plan.IsAvailable);
        Assert.True(plan.UsesBuiltIn);
        Assert.Equal(SelectionQuality.HeuristicFallback, plan.Quality);
        Assert.Null(plan.Model);
    }

    [Fact]
    public void Coding_IsUnavailableOnALowEndDevice_ByHardwareFloor()
    {
        // Mirrors CodingCapabilityPlanner: a real 3-7B code model cannot run in a
        // low-end phone's RAM budget, so below the Tablet tier coding is declined
        // on hardware ALONE — independent of the catalogue. 4 GB => Phone tier.
        var plan = Selector().PlanFor(Device(4), ModelModality.Coding);

        Assert.False(plan.IsAvailable);
        Assert.Equal(SelectionQuality.Unavailable, plan.Quality);
        Assert.Null(plan.Model);
        Assert.False(string.IsNullOrWhiteSpace(plan.Reason));
    }

    [Fact]
    public void Coding_IsUnavailableOnACapableDevice_WhenNoModelCatalogued()
    {
        // A capable device (8 GB => Tablet) clears the hardware floor, but coding
        // still cannot run without a real model — there is no procedural coder to
        // stand in. Unavailable, and never HeuristicFallback, for a DIFFERENT
        // reason than the low-end case.
        var plan = Selector().PlanFor(Device(8), ModelModality.Coding);

        Assert.False(plan.IsAvailable);
        Assert.Equal(SelectionQuality.Unavailable, plan.Quality);
        Assert.NotEqual(SelectionQuality.HeuristicFallback, plan.Quality);
        Assert.Null(plan.Model);
    }

    // ── the honest hole ──────────────────────────────────────────────────────

    [Fact]
    public void Vision_IsServedByACataloguedVlm_OnACapableDevice()
    {
        // THE FLIP the old Vision_IsUnavailable test promised: vision used to be
        // model-or-nothing with no model, and now a real VLM is catalogued. A
        // capable device clears the floor, so vision is Good and names a model.
        //
        // It names THE BIGGEST ONE THAT FITS rather than a specific id. Pinning
        // the id is what made the two tests below go red when a smaller VLM was
        // added — cataloguing a better model is the goal, not a regression, and a
        // test that has to be edited every time we improve the ladder is a test
        // people learn to edit without reading.
        // The selector's own rule: best quality that fits, ties broken by the
        // smaller model. Sized so everything catalogued fits, so this is asking
        // "which does it PREFER", not "which can it afford".
        var best = CataloguedVlms()
            .OrderByDescending(e => e.QualityRank)
            .ThenBy(e => e.MinRamGb)
            .First();
        var roomForAll = CataloguedVlms().Max(e => e.MinRamGb) / DeviceProbe.RamFitHeadroom * 1.5;
        var plan = Selector().PlanFor(Device(roomForAll), ModelModality.Vision);

        Assert.True(plan.IsAvailable);
        Assert.Equal(SelectionQuality.Good, plan.Quality);
        Assert.NotNull(plan.Model);
        Assert.Equal(best.Name, plan.Model!.ModelId);
        Assert.False(string.IsNullOrWhiteSpace(plan.Reason));
    }

    // ── vision fit, derived from the catalogue rather than frozen against it ──
    //
    // These two used to hardcode "a 2.5 GB phone cannot do vision", which was true
    // when a 3B VLM was the only one catalogued. Adding SmolVLM-256M made both go
    // red while the selector was behaving perfectly — the catalogue had improved
    // and the tests were asserting an old fact about the world.
    //
    // A test that fails when the product gets BETTER teaches the wrong lesson: the
    // next person reads red and starts looking for a bug in the selector. So these
    // now derive their thresholds from whatever is catalogued today. The property
    // under test is the RULE — nothing-fits stays distinct from nothing-exists, and
    // the headroom margin excludes a model that would fit only on paper — not this
    // month's line-up.

    private static System.Collections.Generic.IReadOnlyList<ModelEntry> CataloguedVlms() =>
        new ModelRegistryService().AllModels
            .Where(e => e.Modality == ModelModality.Vision)
            .OrderBy(e => e.MinRamGb)
            .ToList();

    [Fact]
    public void Vision_OnADeviceBelowEveryCataloguedVlm_IsNothingFits_NotUnavailable()
    {
        // Half of what the SMALLEST catalogued VLM needs, whatever that is today.
        // Honest verdict: NothingFits — a vision model EXISTS, this device just
        // cannot run it — NOT Unavailable, which would mean none is catalogued at
        // all. The fixes differ (ship a smaller VLM vs catalogue one), so the
        // selector must keep them distinct.
        var smallest = CataloguedVlms().First();
        var ramGb    = smallest.MinRamGb / DeviceProbe.RamFitHeadroom / 2;

        var plan = Selector().PlanFor(Device(ramGb), ModelModality.Vision);

        Assert.Equal(SelectionQuality.NothingFits, plan.Quality);
        Assert.NotEqual(SelectionQuality.Unavailable, plan.Quality);
        Assert.NotNull(plan.Model);   // the smallest VLM, named but marked not-fitting
        Assert.False(string.IsNullOrWhiteSpace(plan.Reason));
    }

    [Fact]
    public void Vision_AModelThatFitsOnPaperButNotInPractice_IsNotSelected()
    {
        // The biggest VLM's BUNDLE would sit in free RAM, but its runtime working
        // set (~1.4x bundle) plus the 0.85 headroom will not. Committing all of
        // free RAM is exactly what OOM-killed the app on the P30 once the KV cache
        // grew, so the margin has to exclude it.
        //
        // What changed is the CONSEQUENCE, and it is the better one: with a smaller
        // VLM catalogued the device now degrades to it instead of losing vision
        // entirely. Same rule, kinder outcome.
        var vlms    = CataloguedVlms();
        var biggest = vlms.Last();
        var rawGiB  = biggest.TotalBytes / (1024.0 * 1024 * 1024);

        // The margin has to exist at all, or this test proves nothing.
        Assert.True(biggest.MinRamGb > rawGiB,
            $"{biggest.Name} declares no working-set margin over its {rawGiB:0.##} GiB bundle");

        // Free RAM landing between "the bundle fits" and "the working set fits".
        var ramGb = (rawGiB + biggest.MinRamGb) / 2 / DeviceProbe.RamFitHeadroom;
        var plan  = Selector().PlanFor(Device(ramGb), ModelModality.Vision);

        Assert.NotEqual(biggest.Name, plan.Model?.ModelId);

        if (vlms.Count > 1)
        {
            // A smaller one exists, so this is a downgrade, not a refusal.
            Assert.Equal(SelectionQuality.Good, plan.Quality);
            Assert.True(plan.Model!.EstimatedBytes < biggest.TotalBytes);
        }
        else
        {
            Assert.Equal(SelectionQuality.NothingFits, plan.Quality);
        }
    }

    [Fact]
    public void Vision_OnAP30ClassPhone_WorksBecauseASmallVlmIsCatalogued()
    {
        // The mission as an assertion. A 2.5 GB phone got nothing until a small
        // VLM was catalogued; that is the whole point of curating a ladder rather
        // than one good model. If this goes red it means the cheapest phones lost
        // vision — which IS worth a red build, unlike the two tests above going red
        // because they gained it.
        var plan = Selector().PlanFor(Device(2.5), ModelModality.Vision);

        Assert.Equal(SelectionQuality.Good, plan.Quality);
        Assert.NotNull(plan.Model);
        Assert.True(plan.Model!.EstimatedBytes < 1_000_000_000,
            $"expected a small VLM on a 2.5 GB phone, got {plan.Model.ModelId} " +
            $"at {plan.Model.EstimatedBytes / 1e9:0.##} GB");
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
