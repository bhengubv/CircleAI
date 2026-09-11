// ModelScopeModalityTests.cs
//
// Pins ModelScopeCatalogClient.InferModality - the load-bearing part of
// "catalogue a real on-device model live."
//
// THE LISTING API DOES NOT REPORT MODALITY. Two things follow from that, and
// this file pins both.
//
// First: a vision-language bundle discovered live (Qwen2-VL, MiniCPM-V, ...)
// was catalogued as the default Chat modality, so vision selection could never
// see it and an on-device VLM was, in practice, uncatalogable from the live
// path. The naming table is the fix.
//
// Second, and found later by asking the real API what it actually returns:
// EVERYTHING UNRECOGNISED FELL THROUGH TO CHAT. Four of the first hundred
// models this publisher returns are not chat models - a Stable Diffusion
// checkpoint, two embedding models and a paraformer ASR bundle - and
// CatalogueMerge lets a live entry ADD, so the first successful refresh would
// have put a diffusion checkpoint in the ladder of models to talk to.
//
// The rule is what this product can LOAD, not what the model is. If someone
// loosens it (plain chat models start reading as Vision), tightens it (a real
// VLM family stops matching), or reopens the fall-through (an ASR bundle
// becomes catalogable again and outranks the ggml Whisper that works), the
// failure is on a phone rather than in a build. These tests are what stop that.

using CircleAI.Core;
using CircleAI.Core.Models;
using Xunit;

namespace CircleAI.Tests;

public sealed class ModelScopeModalityTests
{
    // ── real VLM families must read as Vision ────────────────────────────────

    [Theory]
    [InlineData("Qwen2-VL-2B-Instruct-MNN")]     // "VL" as a delimited token
    [InlineData("Qwen2.5-VL-3B-Instruct-MNN")]
    [InlineData("MiniCPM-V-2_6-MNN")]            // named family, marker isn't a "VL" token
    [InlineData("SmolVLM-Instruct-MNN")]         // "VL" buried inside "VLM"
    [InlineData("InternVL2-2B-MNN")]             // "VL" buried inside "InternVL"
    [InlineData("llava-1.5-7b-MNN")]             // case-insensitive LLaVA
    public void VlmFamilies_ReadAsVision(string name)
    {
        Assert.Equal(ModelModality.Vision, ModelScopeCatalogClient.InferModality(name));
    }

    // ── plain chat models must stay Chat ─────────────────────────────────────

    [Theory]
    [InlineData("Qwen3-4B-MNN")]
    [InlineData("Qwen2.5-7B-Instruct-MNN")]
    [InlineData("Llama-3.1-8B-Instruct-MNN")]
    [InlineData("gemma-2-2b-it-MNN")]
    public void ChatModels_StayChat(string name)
    {
        Assert.Equal(ModelModality.Chat, ModelScopeCatalogClient.InferModality(name));
    }

    // ── the token guard: a stray "VL" substring is NOT a VLM ──────────────────

    [Theory]
    [InlineData("MySVLModel-MNN")]   // "VL" bounded by letters both sides
    [InlineData("REVLON-7B-MNN")]    // "VL" inside a word
    public void EmbeddedVlSubstring_DoesNotFalsePositive(string name)
    {
        Assert.Equal(ModelModality.Chat, ModelScopeCatalogClient.InferModality(name));
    }

    // ── the repo argument is searched too ────────────────────────────────────

    [Fact]
    public void Repo_CarriesTheVisionMarker_WhenNameDoesNot()
    {
        // A caller who passes a bare model name plus the full repo path still
        // gets Vision when the marker lives on the repo side.
        var modality = ModelScopeCatalogClient.InferModality(
            name: "3B-Instruct",
            repo: "MNN/Qwen2.5-VL-3B-Instruct-MNN");

        Assert.Equal(ModelModality.Vision, modality);
    }

    // ── what this product cannot load is not catalogued at all ───────────────

    [Theory]
    // THE FOUR REAL ONES. Every name here was returned by the live listing on
    // 2026-09-11, not invented, and every one of them read as Chat before.
    [InlineData("stable-diffusion-v1-5-mnn-opencl")]
    [InlineData("speech_paraformer-large_asr_nat-zh-cn-16k-common-vocab8404-online-mnn")]
    // ...and the families that would arrive next from the same publisher.
    [InlineData("bge-reranker-base-MNN")]
    [InlineData("whisper-large-v3-MNN")]
    [InlineData("SenseVoiceSmall-MNN")]
    [InlineData("CosyVoice2-0.5B-MNN")]
    [InlineData("bert-vits2-MNN")]
    [InlineData("FLUX.1-schnell-mnn")]
    public void WhatNothingHereCanLoad_IsNotCatalogued(string name)
    {
        // NULL, NOT Asr/Tts/ImageGen. Cataloguing one of these under its true
        // modality would be a truthful entry and a broken download - and for
        // ASR it is actively destructive, because PlanFor(Asr) would rank a
        // large fresh MNN bundle above the 78 MB ggml Whisper that works and
        // the phone would lose hearing it already had.
        Assert.Null(ModelScopeCatalogClient.InferModality(name));
    }

    [Fact]
    public void Rerankers_AreRefusedBeforeTheEmbeddingRuleCanClaimThem()
    {
        // "bge-reranker-base" matches the bge family AND is not an embedder.
        // Order is the whole fix, so it is pinned rather than assumed.
        Assert.Null(ModelScopeCatalogClient.InferModality("bge-reranker-large-MNN"));
        Assert.Equal(ModelModality.Embedding,
            ModelScopeCatalogClient.InferModality("bge-large-zh-MNN"));
    }

    // ── embedding models read as Embedding ───────────────────────────────────

    [Theory]
    [InlineData("bge-large-zh-MNN")]                                // live, Apache-2.0
    [InlineData("gte_sentence-embedding_multilingual-base-MNN")]    // live, Apache-2.0
    [InlineData("Qwen3-Embedding-0.6B-MNN")]
    public void EmbeddingModels_ReadAsEmbedding(string name)
    {
        Assert.Equal(ModelModality.Embedding, ModelScopeCatalogClient.InferModality(name));
    }

    [Theory]
    [InlineData("Magtek-7B-MNN")]       // "gte" bounded by letters, inside "Magtek"
    [InlineData("Clubgear-3B-MNN")]     // "bge" bounded by letters, inside "Clubgear"
    public void EmbeddingFamilyLetters_InsideAWord_DoNotFalsePositive(string name)
    {
        Assert.Equal(ModelModality.Chat, ModelScopeCatalogClient.InferModality(name));
    }
}
