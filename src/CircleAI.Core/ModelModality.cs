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
}
