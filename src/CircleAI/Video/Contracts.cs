// Contracts.cs
//
// (3.1.0) The CircleAI.Video contract surface. Three interfaces — one
// generator, one script rewriter, one style catalogue. Null
// implementations ship out of the box; real backends (CogVideoX-2B
// ONNX→MNN, LTX-Video distilled-2B) land in 3.1.x.
//
// Driving use case: txtMe Video Mail. Sender calls, no answer, types a
// message. Recipient's B! (where capable) renders the message as a
// short styled video — public-domain or original-character voice. Gated
// at the BestFit selector on the new MinVramGb dimension so the feature
// only surfaces on devices that can actually honour it.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.Video;

/// <summary>
/// (3.1.0) Generate a short video from a text prompt (and optional
/// style + reference frame + audio track). The single concrete backend
/// CircleAI will ship first is CogVideoX-2B; LTX-Video distilled-2B
/// follows. Both run on-device (≤ 12 GB VRAM quantised), under MNN.
/// </summary>
public interface IVideoGenerator
{
    /// <summary>Backend self-identification — "cogvideox-2b", "ltx-video-2b-distilled", "null".</summary>
    string BackendId { get; }

    /// <summary>Synthesise the requested video. Throws if the device cannot satisfy the request.</summary>
    ValueTask<VideoGenerationResult> GenerateAsync(
        VideoGenerationRequest request,
        CancellationToken      ct = default);
}

/// <summary>
/// (3.1.0) Rewrite a user message in a chosen style's voice. Runs
/// against the existing IChatGenerator with a style-specific system
/// prompt — no new model needed for this leg.
/// </summary>
public interface IStyleScript
{
    /// <summary>Backend self-identification — "circleai-llm", "null".</summary>
    string BackendId { get; }

    /// <summary>Rewrite the source message in the requested style.</summary>
    ValueTask<StyleScriptResult> RewriteAsync(
        StyleScriptRequest request,
        CancellationToken  ct = default);
}

/// <summary>
/// (3.1.0) Catalogue of registered styles — public-domain illustrations,
/// original-character renders, genre presets (noir, space-opera,
/// storybook-watercolour, claymation, anime, …). Lets the txtMe UI
/// present a picker and lets the generator look up grounding frames.
/// </summary>
public interface IStyleReference
{
    /// <summary>Backend self-identification — "in-memory", "embedded-defaults", "null".</summary>
    string BackendId { get; }

    /// <summary>Register a style (typically at host startup).</summary>
    ValueTask RegisterAsync(StyleReference style, CancellationToken ct = default);

    /// <summary>Look up one style by id.</summary>
    ValueTask<StyleReference?> GetAsync(StyleId id, CancellationToken ct = default);

    /// <summary>Enumerate every registered style — drives picker UIs.</summary>
    ValueTask<IReadOnlyList<StyleReference>> ListAsync(CancellationToken ct = default);
}
