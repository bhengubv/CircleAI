#nullable enable

// IImageGenerator.cs
//
// The seam for making a picture from a prompt, sitting beside IChatGenerator
// rather than inside it: a chat model is asked what to SAY, this is asked for
// pixels, and a selection that could hand one to the other would give a draw
// request a model that cannot paint.
//
// ENGINE: ONNX Runtime, which this repo already ships and already drives (the
// whole speech stack — Piper/VITS/MMS TTS, Silero VAD). Diffusion is a graph
// ONNX can hold, so image generation needs no engine that is not already here.
// That is why this is a real seam and not a wish.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.Core;

/// <summary>What to draw, and how hard to work at it.</summary>
/// <param name="Prompt">What the picture should show.</param>
/// <param name="NegativePrompt">What it should avoid. Null when unused.</param>
/// <param name="Width">Pixels. Rounded down to the model's step by the generator.</param>
/// <param name="Height">Pixels.</param>
/// <param name="Steps">
/// Denoising steps. The single biggest cost knob: a distilled model makes a
/// usable picture in 1–4, a full one wants 20–50, and on a phone the difference
/// is between seconds and minutes.
/// </param>
/// <param name="GuidanceScale">How strictly to follow the prompt. 0 disables.</param>
/// <param name="Seed">Null means non-deterministic.</param>
public sealed record ImageRequest(
    string  Prompt,
    string? NegativePrompt = null,
    int     Width          = 512,
    int     Height         = 512,
    int     Steps          = 4,
    float   GuidanceScale  = 0f,
    int?    Seed           = null);

/// <summary>A generated picture.</summary>
/// <param name="Png">The encoded image, as bytes the caller owns.</param>
/// <param name="Width">Actual width produced, which may differ from the request.</param>
/// <param name="Height">Actual height produced.</param>
/// <param name="Steps">Steps actually run.</param>
/// <param name="Elapsed">Wall-clock time.</param>
public sealed record GeneratedImage(
    byte[]   Png,
    int      Width,
    int      Height,
    int      Steps,
    TimeSpan Elapsed);

/// <summary>
/// Progress during denoising. Worth surfacing because on a phone this is tens
/// of seconds, and a caller with no signal has nothing to show.
/// </summary>
/// <param name="Step">Completed step, 1-based.</param>
/// <param name="TotalSteps">Steps in this run.</param>
public readonly record struct ImageProgress(int Step, int TotalSteps)
{
    /// <summary>0..1.</summary>
    public double Fraction => TotalSteps <= 0 ? 0 : (double)Step / TotalSteps;
}

/// <summary>
/// Contract for an on-device image generator. Implementations own native model
/// state and must be disposed.
/// </summary>
public interface IImageGenerator : IDisposable
{
    /// <summary>Makes one picture.</summary>
    Task<GeneratedImage> GenerateAsync(
        ImageRequest request,
        IProgress<ImageProgress>? progress = null,
        CancellationToken ct = default);

    /// <summary>
    /// Sizes this model actually supports. Diffusion graphs are usually built
    /// for fixed resolutions, so a caller that asks for an arbitrary size gets
    /// the nearest supported one rather than a failure — but it should be able
    /// to ask first.
    /// </summary>
    IReadOnlyList<(int Width, int Height)> SupportedSizes { get; }
}
