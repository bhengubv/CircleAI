// ModelScopeModalityTests.cs
//
// Pins ModelScopeCatalogClient.InferModality — the load-bearing part of
// "catalogue a real on-device vision model."
//
// The ModelScope listing API does not report modality. Before this method a
// vision-language bundle discovered live (Qwen2-VL, MiniCPM-V, …) was
// catalogued as the default Chat modality, so vision selection could never see
// it and an on-device VLM was, in practice, uncatalogable from the live path.
// The naming table here IS the fix; if someone loosens it (plain chat models
// start reading as Vision) or tightens it (a real VLM family stops matching),
// vision selection silently breaks on a phone rather than failing a build.
// These tests are what stop that.

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
}
