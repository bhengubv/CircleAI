namespace CircleAI.Core;

/// <summary>
/// What KIND of model a registry entry is. Distinct from
/// <c>ChatCapability</c>, which describes what a CHAT model can do — a TTS
/// model is not a chat model with extra flags, it is a different kind of thing
/// consumed by a different runtime (<c>VoicePipeline</c>, not
/// <c>IChatGenerator</c>).
/// </summary>
/// <remarks>
/// This exists to make the speech ladder SAFE to catalogue. The chat selector
/// (<c>DeviceAwareModelSelector.BestFit</c>) filters to <see cref="Chat"/>, so a
/// speech entry can never be returned to a caller asking for a chat model.
/// <para>
/// Without it, the failure is concrete: <c>ParseCapabilities</c> skips any
/// label it does not recognise and falls back to <c>Default</c>, so a TTS entry
/// tagged <c>["Tts"]</c> would parse to a Default CHAT model and become a
/// candidate for the reasoning core. The chat brain would try to load a
/// vocoder.
/// </para>
/// </remarks>
public enum ModelModality
{
    /// <summary>A text chat / reasoning LLM (every entry catalogued to date).</summary>
    Chat = 0,

    /// <summary>Automatic speech recognition — speech to text (Whisper, Zipformer).</summary>
    Asr,

    /// <summary>Text to speech (Piper, tiny-tts, Kokoro).</summary>
    Tts,

    /// <summary>Voice activity detection (ten-vad).</summary>
    Vad,

    /// <summary>Wake-word / keyword spotting (openWakeWord).</summary>
    WakeWord,

    /// <summary>
    /// Vision / multimodal image understanding (Qwen-VL, SmolVLM, MiniCPM-V).
    /// </summary>
    /// <remarks>
    /// Catalogued as its own modality rather than a <c>ChatCapability</c> flag
    /// because a VLM bundle carries a separate vision encoder alongside the LLM
    /// weights — the device-fit maths differs, and a text-only chat selection
    /// must never return one by accident.
    /// </remarks>
    Vision,

    /// <summary>
    /// Music generation — a neural model that synthesises an audio bed
    /// (MusicGen, Stable Audio, or a catalogued on-device equivalent),
    /// consumed by <c>CircleAI.Music</c>'s pipeline, not <c>IChatGenerator</c>.
    /// </summary>
    /// <remarks>
    /// A music model is never the ONLY way to get a bed:
    /// <c>ProceduralMusicBedGenerator</c> is a pure-managed built-in that
    /// synthesises a royalty-free chord/arpeggio bed offline on any device
    /// (<c>MusicBedBackend.Procedural</c>). So the selector reports
    /// <c>SelectionQuality.HeuristicFallback</c> — not <c>Unavailable</c> — when
    /// no music model is catalogued; a neural model supersedes the bed exactly
    /// as a <c>Good</c> pick supersedes any fallback.
    /// </remarks>
    Music,

    /// <summary>
    /// Video / media generation — a neural frame model or encoder consumed by
    /// <c>CircleAI.Media.Rendering</c> (a diffusion frame model, or the
    /// <c>IHtmlFrameProvider</c> WebView-capture path).
    /// </summary>
    /// <remarks>
    /// Like <see cref="Music"/>, this has a real built-in:
    /// <c>ManagedMediaRenderer</c> composites layers, text and a motion timeline
    /// entirely in managed code, so a clip is always producible offline. The
    /// neural encoder / HTML path is a SEAM a catalogued model (or an
    /// <c>IHtmlFrameProvider</c>) fills — its absence is
    /// <c>SelectionQuality.HeuristicFallback</c>, not <c>Unavailable</c>.
    /// </remarks>
    Video,

    /// <summary>
    /// On-device coding — a real 3-7B tool-calling code model consumed by
    /// <c>CircleAI.CodeAgent</c>'s agent loop.
    /// </summary>
    /// <remarks>
    /// The one new modality with NO built-in fallback AND a hardware floor:
    /// arithmetic cannot stand in for a code model the way an energy detector
    /// stands in for a wake-word model, and a 3-7B model does not fit a low-end
    /// phone's RAM budget. The selector mirrors
    /// <c>CircleAI.CodeAgent.CodingCapabilityPlanner</c>'s tier gate — below
    /// <c>DeviceTier.Tablet</c> is <c>Unavailable</c> by design — so coding is
    /// <c>Unavailable</c> unless a real model is catalogued AND the device tier
    /// clears the floor.
    /// </remarks>
    Coding,
}
