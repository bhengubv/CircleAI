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
        // TWO STORES QUALIFY, and the rule is about CONTROL, not about which
        // company hosts it. The Hugging Face bucket needs a credential that
        // exists on no machine here, and the cost of that was measured: 45 of
        // the small files the catalogue named had quietly stopped existing, so
        // those languages downloaded a 114 MB model and then failed on 2 KB of
        // settings. A store we cannot write to cannot be kept correct.
        //
        // github.com/bhengubv is the account's canonical storage and we hold its
        // token, so GitHubRelease satisfies the same intent: one address we
        // control, and a voice can be published the day it is proven.
        //
        // What is still forbidden is pointing at whichever stranger's repository
        // first published a voice — that is the guarantee the doc exists to give.
        var ourStores = new[] { ModelSource.HuggingFaceBucket, ModelSource.GitHubRelease };

        Assert.All(registry.AllModels.Where(e => e.Modality == ModelModality.Tts),
            e => Assert.Contains(e.Source, ourStores));

        // And "ours" has to mean ours: a GitHub-hosted voice must sit under our
        // own account, or the source enum is just a label on someone else's repo.
        Assert.All(
            registry.AllModels.Where(e => e.Modality == ModelModality.Tts
                                       && e.Source == ModelSource.GitHubRelease),
            e => Assert.StartsWith("bhengubv/", e.Repo));

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
