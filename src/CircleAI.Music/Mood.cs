namespace CircleAI.Music;

/// <summary>
/// The emotional colour a music bed should carry. Mood drives the procedural
/// synthesiser's timbre, octave, dynamics and arpeggio feel, and — via
/// <see cref="MusicSpec.ForMood"/> — a sensible default tempo and key.
/// </summary>
/// <remarks>
/// The set is deliberately small and clip/video-CV oriented. It is stable
/// public API: a downloaded neural backend maps its own conditioning prompt
/// from the same enum, so callers never have to change to gain the real model.
/// </remarks>
public enum Mood
{
    /// <summary>Even, unobtrusive, no strong emotional pull. The safe default.</summary>
    Neutral = 0,

    /// <summary>Slow, gentle, spacious. Good under calm narration.</summary>
    Calm,

    /// <summary>Rounded and friendly, mid tempo, slightly richer timbre.</summary>
    Warm,

    /// <summary>Minor-leaning, slow, pad-forward. Introspective.</summary>
    Reflective,

    /// <summary>Bright major progression, forward motion, hopeful.</summary>
    Uplifting,

    /// <summary>Clean, steady, professional. The classic "explainer video" bed.</summary>
    Corporate,

    /// <summary>Steady, understated pulse designed to sit behind concentration.</summary>
    Focus,

    /// <summary>Fast arpeggio, brighter harmonics, driving.</summary>
    Energetic,

    /// <summary>Bouncy, light, pentatonic. Fun and informal.</summary>
    Playful,

    /// <summary>Wide, slow, pad-dominant with a high sparkle. Trailer-like.</summary>
    Cinematic,
}
