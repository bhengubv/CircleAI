// ModelModalityTests.cs
//
// The safety property that makes the speech ladder catalogue-able at all:
// a chat request must NEVER select a speech model.
//
// Reading the registry (2026-07-20) revealed the trap. ModelEntry had no
// modality — every entry was implicitly a chat LLM — and ParseCapabilities
// DROPS any capability label it does not recognise, falling back to Default.
// So a TTS entry tagged Capabilities:["Tts"] would parse to a Default CHAT
// model and become a candidate for the reasoning core. The chat brain would
// try to load a vocoder.
//
// ModelModality closes that. These tests hold it closed.

using System;
using System.Collections.Generic;
using System.Linq;
using CircleAI.Core;
using CircleAI.Core.Models;
using CircleAI.Inference;
using Xunit;

namespace CircleAI.Tests;

public sealed class ModelModalityTests
{
    private static DeviceProbe Device(double ramGb = 8, double storageGb = 32) =>
        new(
            RamAvailableBytes: (long)(ramGb * 1024 * 1024 * 1024),
            StorageFreeBytes:  (long)(storageGb * 1024 * 1024 * 1024),
            Gpu:               GpuKind.None,
            CpuCores:          8,
            Thermal:           ThermalClass.Passive,
            Connectivity:      Connectivity.Online);

    // A registry double we can seed with mixed modalities.
    private sealed class MixedRegistry : ModelRegistryService
    {
        private readonly IReadOnlyList<ModelEntry> _entries;
        public MixedRegistry(IReadOnlyList<ModelEntry> entries) : base() => _entries = entries;
        public override IReadOnlyList<ModelEntry> AllModels => _entries;
    }

    private static ModelEntry Chat(string name, int rank) =>
        new(name, "1.0", "Q4") { QualityRank = rank, MinRamGb = 0.5, MinStorageGb = 0.5 };

    private static ModelEntry Speech(string name, ModelModality modality) =>
        new(name, "1.0", "ONNX")
        {
            Modality     = modality,
            QualityRank  = 999,            // deliberately the "best" — must STILL be ignored for chat
            MinRamGb     = 0.1,
            MinStorageGb = 0.1,
            Capabilities = new[] { modality.ToString() },  // "Tts" etc — unknown to ChatCapability
        };

    [Fact]
    public void ChatSelection_NeverReturnsASpeechModel_EvenWhenItRanksHighest()
    {
        using var registry = new MixedRegistry(new[]
        {
            Chat("chat-small", rank: 5),
            Speech("tts-voice", ModelModality.Tts),   // rank 999 — would win if not filtered
            Speech("asr-whisper", ModelModality.Asr),
        });

        var pick = new DeviceAwareModelSelector(registry).BestFit(Device(), ChatCapability.Default);

        Assert.Equal("chat-small", pick.ModelId);
    }

    [Fact]
    public void ChatSelection_WithOnlySpeechModels_ThrowsRatherThanLoadingAVocoder()
    {
        // No chat model exists → "no model satisfies capabilities", NOT "here,
        // have the TTS model". Loading a vocoder as a chat brain is the failure
        // this whole thing prevents.
        using var registry = new MixedRegistry(new[]
        {
            Speech("tts-voice", ModelModality.Tts),
            Speech("vad", ModelModality.Vad),
        });

        Assert.Throws<InvalidOperationException>(
            () => new DeviceAwareModelSelector(registry).BestFit(Device(), ChatCapability.Default));
    }

    [Fact]
    public void AllCandidates_ListsChatModelsOnly()
    {
        using var registry = new MixedRegistry(new[]
        {
            Chat("chat-a", 5),
            Chat("chat-b", 8),
            Speech("tts", ModelModality.Tts),
            Speech("wake", ModelModality.WakeWord),
        });

        var ids = new DeviceAwareModelSelector(registry)
            .AllCandidates(Device()).Select(s => s.ModelId).ToList();

        Assert.Equal(new[] { "chat-b", "chat-a" }, ids);   // quality desc, no speech
    }

    [Fact]
    public void ChainFor_DoesNotWalkIntoSpeechModels()
    {
        using var registry = new MixedRegistry(new[]
        {
            new ModelEntry("chat-head", "1.0", "Q4") { QualityRank = 8, FallbackModelId = "tts" },
            Speech("tts", ModelModality.Tts),
        });

        var chain = new DeviceAwareModelSelector(registry).ChainFor("chat-head");

        Assert.Equal(new[] { "chat-head" }, chain);   // the tts fallback is not a chat model
    }

    [Fact]
    public void RealRegistry_ChatEntriesStayedChat_SpeechEntriesAreClassified()
    {
        // The nine chat Qwen models must NOT have been reclassified by the
        // modality default (they are all Chat), AND the speech + vision rungs
        // must carry their real modality — not silently default to Chat and
        // pollute chat selection. Qwen2.5-VL is a Qwen BY NAME but a VISION
        // model, so it is excluded from the chat assertion and checked on its own.
        using var registry = new ModelRegistryService();

        Assert.All(registry.AllModels.Where(e => e.Name.StartsWith("Qwen") && !e.Name.Contains("-VL-")),
            e => Assert.Equal(ModelModality.Chat, e.Modality));

        var byName = registry.AllModels.ToDictionary(e => e.Name);
        Assert.Equal(ModelModality.Vision, byName["Qwen2.5-VL-3B-Instruct-MNN"].Modality);
        Assert.Equal(ModelModality.Tts,    byName["Piper-en_US-lessac-medium"].Modality);
        Assert.Equal(ModelModality.Tts,    byName["Piper-en_US-lessac-high"].Modality);
        Assert.Equal(ModelModality.Asr,    byName["Whisper-tiny-ggml"].Modality);

        // Source: every voice comes from our own Hugging Face bucket rather than
        // from whichever stranger's repository first published it — see
        // docs/VOICE_PROVENANCE.md.
        //
        // ONE NAMED EXCEPTION, and it is a list rather than a predicate so it
        // cannot quietly grow. Korean was catalogued but never uploaded: its
        // bundle is absent from the bucket under every path, while its pins match
        // rhasspy/piper-voices byte for byte. The entry had Repo pointing
        // upstream and Source still saying bucket, so it built
        // huggingface.co/buckets/rhasspy/piper-voices/... and 404'd on every
        // file — catalogued, undownloadable, and silent about it.
        //
        // Pointing it upstream is the weaker guarantee the provenance doc warns
        // about: if rhasspy's repository goes away, Korean goes with it. It is
        // accepted deliberately because the alternative is a voice that does not
        // work at all. When the bundle reaches the bucket, delete this entry from
        // the list and the assertion tightens itself back up.
        var upstreamUntilUploaded = new[] { "Piper-ko_KR-kss-medium" };

        Assert.All(
            registry.AllModels.Where(e => e.Modality == ModelModality.Tts
                                       && !upstreamUntilUploaded.Contains(e.Name)),
            e => Assert.Equal(ModelSource.HuggingFaceBucket, e.Source));

        // The exception is not a free pass: an entry on that list must still name
        // a real upstream repository, or it is just a broken entry with a note.
        Assert.All(
            registry.AllModels.Where(e => upstreamUntilUploaded.Contains(e.Name)),
            e =>
            {
                Assert.Equal(ModelSource.HuggingFace, e.Source);
                Assert.False(string.IsNullOrWhiteSpace(e.Repo));
                Assert.DoesNotContain("circleai-voices", e.Repo);
            });

        // EVERY MODEL COMES OFF A CDN. This asserted ModelScope until it was
        // measured: from Osaka, on the same file in the same minute, Hugging
        // Face served 4.22 MB/s against ModelScope's 1.01. The header said
        // X-Amz-Cf-Pop: KIX56-P4 — CloudFront's Osaka edge, chosen by anycast
        // with nothing configured. ModelScope has no edge network; it serves
        // Shanghai to the whole planet through egress filtering that throttles
        // cross-border TCP however close you are standing.
        //
        // That is not a preference, it is the difference between a model
        // arriving and a person giving up. taobao-mnn mirrors all 217 MNN
        // conversions and the SHA-256s verify byte-identical against both, so
        // nothing downstream changes.
        //
        // ModelScope remains implemented as a fallback — it was made primary
        // for sanctions resilience, which is a real concern this does not
        // answer. What must not happen again is a model DEFAULTING to the
        // non-CDN source because nobody set the field.
        Assert.All(registry.AllModels.Where(e => e.Name.StartsWith("Qwen") || e.Name.StartsWith("SmolVLM")),
            e => Assert.Equal(ModelSource.HuggingFace, e.Source));
    }
}
