//! hosting_voice — CircleAI.Hosting.VoiceOptions (Rust port).
//!
//! Configuration DTO for the B! voice pipeline (composed via `AIOptions.Voice`
//! in the C# host). All fields have safe defaults that produce a voice-disabled,
//! silent-TTS pipeline when left unchanged. Mirrors `VoiceOptions`.

/// Configuration for the B! voice pipeline. Mirrors `CircleAI.Hosting.VoiceOptions`.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct VoiceOptions {
    /// Wake word (phrase) that triggers the voice pipeline. Default `"hey b"`.
    pub wake_word: String,

    /// Target sample rate for microphone capture, in Hz. Default `16000`
    /// (the format required by most open-source ASR engines and by
    /// `voice::AudioFormat::PCM16_MONO_16K`).
    pub sample_rate_hz: i32,

    /// When `true`, the voice pipeline starts automatically alongside the butler
    /// service. Default `false` — callers start it manually.
    pub auto_start: bool,

    /// Selects the TTS engine backend for spoken responses:
    /// `"null"` (silent, default), `"kokoro"`, or `"piper"`.
    pub tts_backend: String,

    /// Duration of trailing silence (ms) that marks end-of-utterance for VAD.
    /// Default `800` ms.
    pub end_of_speech_silence_ms: i32,
}

impl Default for VoiceOptions {
    fn default() -> Self {
        Self {
            wake_word: "hey b".to_string(),
            sample_rate_hz: 16_000,
            auto_start: false,
            tts_backend: "null".to_string(),
            end_of_speech_silence_ms: 800,
        }
    }
}

impl VoiceOptions {
    /// Creates a `VoiceOptions` with the default, voice-disabled configuration.
    pub fn new() -> Self {
        Self::default()
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn defaults_are_voice_disabled_silent() {
        let o = VoiceOptions::default();
        assert_eq!(o.wake_word, "hey b");
        assert_eq!(o.sample_rate_hz, 16_000);
        assert!(!o.auto_start);
        assert_eq!(o.tts_backend, "null");
        assert_eq!(o.end_of_speech_silence_ms, 800);
    }
}
