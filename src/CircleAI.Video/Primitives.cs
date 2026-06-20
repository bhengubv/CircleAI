// Primitives.cs
//
// (3.1.0) Shared shapes for the video contract surface.

using System;
using System.Collections.Generic;

namespace CircleAI.Video;

/// <summary>Identifier for one registered style (e.g. "pooh-1926", "noir-detective", "space-opera").</summary>
public readonly record struct StyleId(string Value)
{
    public override string ToString() => Value;
    public static implicit operator string(StyleId id) => id.Value;
}

/// <summary>Output resolution for a generated video.</summary>
public readonly record struct VideoResolution(int Width, int Height)
{
    public static VideoResolution P480 => new(720, 480);
    public static VideoResolution P720 => new(1280, 720);
    public static VideoResolution P1080 => new(1920, 1080);
}

/// <summary>
/// One reference frame the generator can ground style on — public-domain
/// illustration, original-character render, etc.
/// </summary>
public sealed record StyleReferenceFrame(
    ReadOnlyMemory<byte> ImageBytes,
    string               MimeType,
    string?              Caption = null);

/// <summary>
/// Attribution + license metadata for one style. Letting txtMe (and any
/// other consumer) display the source to the user before rendering.
/// </summary>
public sealed record StyleAttribution(
    string  Source,
    string  License,
    string? Url = null);

/// <summary>
/// One style the host has registered with the catalogue. Picked up by
/// IStyleReference.GetAsync(styleId).
/// </summary>
public sealed record StyleReference(
    StyleId                           Id,
    string                            DisplayName,
    string                            ShortDescription,
    StyleAttribution                  Attribution,
    string?                           VoicePersonaId,
    IReadOnlyList<StyleReferenceFrame> Frames);

/// <summary>Audio track produced by CircleAI.Speech for the generator to embed.</summary>
public sealed record AudioTrack(
    ReadOnlyMemory<byte> AudioPcm16Mono,
    int                  SampleRateHz,
    TimeSpan             Duration);

/// <summary>One generation request — text + optional style + optional grounding image + optional audio.</summary>
public sealed record VideoGenerationRequest(
    string               Prompt,
    TimeSpan             Duration,
    VideoResolution      Resolution,
    int                  FrameRate     = 24,
    StyleId?             StyleId       = null,
    StyleReferenceFrame? ReferenceImage = null,
    AudioTrack?          AudioTrack     = null,
    long?                Seed           = null);

/// <summary>One generation outcome.</summary>
public sealed record VideoGenerationResult(
    ReadOnlyMemory<byte> VideoBytes,
    string               MimeType,
    TimeSpan             Duration,
    int                  FrameCount,
    VideoResolution      Resolution,
    string               BackendId);

/// <summary>One style-script request — raw user message + chosen voice.</summary>
public sealed record StyleScriptRequest(
    string  SourceMessage,
    StyleId Style,
    string? SpeakerHint   = null,
    string? LanguageHint  = null);

/// <summary>One style-script outcome — the rewritten line + voice + estimated duration.</summary>
public sealed record StyleScriptResult(
    string   RewrittenText,
    StyleId  Style,
    string?  VoicePersonaId,
    TimeSpan EstimatedSpokenDuration);
