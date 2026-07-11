//! speech.rs
//!
//! Port of `CircleAI.Speech/` — the voice-loop primitive surface for B! Butler:
//! ASR / TTS / wake-word / OCR contracts, plus the (3.3.0) real DSP backends for
//! voice-activity detection, end-of-turn detection, echo cancellation, noise
//! reduction, and G.711 ↔ PCM-16 audio-format conversion.
//!
//! C# → Rust map:
//!   * record types (`TranscribedSegment`, `TranscriptionResult`, `SynthesisResult`,
//!     `OcrResult`, `OcrTextBlock`, `WakeWordEvent`, `EndOfTurnResult`,
//!     `VadFrameResult`) → same-named structs.
//!   * `ISpeechRecognizer` / `ISpeechSynthesizer` / `IWakeWordDetector` /
//!     `IOpticalCharacterRecognizer` → `#[async_trait]` traits (C# is
//!     `ValueTask<T>`/`Task`-based), each with an associated
//!     `Error: std::error::Error`.
//!   * `IEchoCanceller` / `INoiseReducer` / `IEndOfTurnDetector` /
//!     `IVoiceActivityDetector` → plain (sync) traits — the C# surface is
//!     synchronous span-in/span-out.
//!   * `NullXxx` fail-closed defaults → same names; the pure-DSP backends
//!     (`EnergyVoiceActivityDetector`, `RuleBasedEndOfTurnDetector`,
//!     `NlmsEchoCanceller`, `SpectralSubtractionNoiseReducer`) are ported
//!     verbatim (the arithmetic is 1:1).
//!   * `AudioFormatConverter` → free functions in a `audio_format` submodule,
//!     mirroring the C# `static class`.
//!
//! Notes on constructs that did not map 1:1:
//!   * `ReadOnlySpan<byte>`/`Span<byte>` → `&[u8]`/`&mut [u8]`;
//!     `ReadOnlyMemory<byte>` on the async surface → `Vec<u8>`.
//!   * `BinaryPrimitives.ReadInt16LittleEndian`/`WriteInt16LittleEndian` →
//!     `i16::from_le_bytes`/`to_le_bytes`. `MemoryMarshal.Cast<byte,short>` is
//!     realised by reading 2 bytes at a time (avoids any alignment/UB concern).
//!   * The host-model runners (`IEchoCancellerModelRunner`,
//!     `INoiseReducerModelRunner`, `ITurnModelRunner`, `IVadModelRunner`) are the
//!     injected boundary for a native/ONNX backend; the shipped wrappers
//!     (`WebRtcEchoCanceller`, `KrispNoiseReducer`, `DeepFilterNetNoiseReducer`,
//!     `SmartTurnDetector`, `SileroVoiceActivityDetector`) fall back to the pure
//!     backend exactly as the C# does when no runner is wired.

use std::convert::Infallible;
use std::fmt;

use async_trait::async_trait;
use chrono::{DateTime, Duration, Utc};

// ─────────────────────────────────────────────────────────────────────────────
// SpeechError
// ─────────────────────────────────────────────────────────────────────────────

/// Failure surface for the async speech backends. The shipped `Null*` backends
/// never fail (they use [`Infallible`]); this enum exists for real hosted
/// recognisers/synthesisers/OCR engines that wire the same traits.
#[derive(Debug)]
pub enum SpeechError {
    /// A required argument was null / empty / whitespace.
    InvalidArgument(String),
    /// The backend / model / runtime is unavailable.
    Unavailable(String),
    /// A backend-specific failure with a human message.
    Backend(String),
}

impl fmt::Display for SpeechError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            SpeechError::InvalidArgument(m) => write!(f, "invalid argument: {m}"),
            SpeechError::Unavailable(m) => write!(f, "unavailable: {m}"),
            SpeechError::Backend(m) => write!(f, "backend error: {m}"),
        }
    }
}

impl std::error::Error for SpeechError {}

// ─────────────────────────────────────────────────────────────────────────────
// Value types (records)
// ─────────────────────────────────────────────────────────────────────────────

/// One transcribed segment.
#[derive(Debug, Clone, PartialEq)]
pub struct TranscribedSegment {
    pub text: String,
    pub offset: Duration,
    pub duration: Duration,
    pub language: Option<String>,
    pub confidence: f32,
}

/// Outcome of one ASR call.
#[derive(Debug, Clone, PartialEq)]
pub struct TranscriptionResult {
    pub text: String,
    pub language: Option<String>,
    pub segments: Vec<TranscribedSegment>,
    pub total_duration: Duration,
}

/// Outcome of one TTS call. `audio_pcm16_mono` is PCM-16 mono little-endian.
#[derive(Debug, Clone, PartialEq)]
pub struct SynthesisResult {
    pub audio_pcm16_mono: Vec<u8>,
    pub sample_rate_hz: i32,
    pub duration: Duration,
}

/// One OCR result.
#[derive(Debug, Clone, PartialEq)]
pub struct OcrResult {
    pub text: String,
    pub blocks: Vec<OcrTextBlock>,
}

/// One detected text block in an OCR result.
#[derive(Debug, Clone, PartialEq)]
pub struct OcrTextBlock {
    pub text: String,
    pub x: i32,
    pub y: i32,
    pub width: i32,
    pub height: i32,
    pub confidence: f32,
    pub language: Option<String>,
}

/// One wake-word fire.
#[derive(Debug, Clone, PartialEq)]
pub struct WakeWordEvent {
    pub keyword: String,
    pub confidence: f32,
    pub detected_at_utc: DateTime<Utc>,
}

/// (3.3.0) Verdict on whether a partial transcript represents a finished thought.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct EndOfTurnResult {
    /// True if the speaker likely finished their turn.
    pub is_complete: bool,
    /// 0..1 confidence.
    pub confidence: f32,
    /// If `is_complete = false`, how many extra ms to wait before re-asking.
    pub wait_more_ms: i32,
}

impl EndOfTurnResult {
    pub fn new(is_complete: bool, confidence: f32, wait_more_ms: i32) -> Self {
        Self {
            is_complete,
            confidence,
            wait_more_ms,
        }
    }
}

/// (3.3.0) One verdict from a voice-activity detector.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct VadFrameResult {
    /// True if this frame contains speech.
    pub is_speech: bool,
    /// 0..1 confidence the frame is speech.
    pub speech_probability: f32,
    /// Frame start offset relative to the stream start.
    pub offset: Duration,
}

impl VadFrameResult {
    pub fn new(is_speech: bool, speech_probability: f32, offset: Duration) -> Self {
        Self {
            is_speech,
            speech_probability,
            offset,
        }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Async contracts: ISpeechRecognizer / ISpeechSynthesizer / IWakeWordDetector /
// IOpticalCharacterRecognizer
// ─────────────────────────────────────────────────────────────────────────────

/// (2.3.0) Convert audio to text.
#[async_trait]
pub trait ISpeechRecognizer {
    type Error: std::error::Error;

    /// Backend self-identification — "funasr-1.x" / "yapsnap" / "null".
    fn backend_id(&self) -> &str;

    /// Recognise one buffer of PCM-16 mono audio.
    async fn transcribe(
        &self,
        audio_pcm16_mono: &[u8],
        sample_rate_hz: i32,
        language_hint: Option<&str>,
    ) -> Result<TranscriptionResult, Self::Error>;
}

/// (2.3.0) Convert text to spoken audio.
#[async_trait]
pub trait ISpeechSynthesizer {
    type Error: std::error::Error;

    /// Backend self-identification — "chattts" / "null".
    fn backend_id(&self) -> &str;

    /// Synthesise one utterance. Returns PCM-16 mono.
    async fn synthesize(
        &self,
        text: &str,
        voice_id: Option<&str>,
        language_hint: Option<&str>,
    ) -> Result<SynthesisResult, Self::Error>;
}

/// A subscription handle. Dropping it unsubscribes.
pub trait ISpeechDisposable: Send {}

/// (2.3.0) Spot a wake word ("Hey B") in a continuous audio stream.
/// Implementations are long-running (`start`/`stop`).
#[async_trait]
pub trait IWakeWordDetector {
    type Error: std::error::Error;

    /// Backend self-identification — "hey-snips" / "null".
    fn backend_id(&self) -> &str;

    /// Begin listening on the system mic. Idempotent.
    async fn start(&self) -> Result<(), Self::Error>;

    /// Stop listening. Idempotent.
    async fn stop(&self) -> Result<(), Self::Error>;
}

/// (2.3.0) Read text out of an image.
#[async_trait]
pub trait IOpticalCharacterRecognizer {
    type Error: std::error::Error;

    /// Backend self-identification — "paddleocr-2.x" / "null".
    fn backend_id(&self) -> &str;

    /// Recognise text in an image. `language_hint` e.g. "eng" / "chi" / "auto".
    async fn recognize(
        &self,
        image_bytes: &[u8],
        language_hint: Option<&str>,
    ) -> Result<OcrResult, Self::Error>;
}

// ─────────────────────────────────────────────────────────────────────────────
// Sync DSP contracts: IEchoCanceller / INoiseReducer / IEndOfTurnDetector /
// IVoiceActivityDetector
// ─────────────────────────────────────────────────────────────────────────────

/// (3.3.0) Acoustic echo canceller — subtracts the far-end reference from the
/// near-end mic input.
pub trait IEchoCanceller {
    /// Backend self-identification — "nlms" / "webrtc-aec3" / "null".
    fn backend_id(&self) -> &str;

    /// Cancel echo of `far_end_reference` out of `near_end_microphone`. Writes
    /// the result into `destination`. Both inputs must be the same sample rate
    /// and length (PCM-16 mono). Returns the number of bytes written.
    fn cancel(
        &mut self,
        near_end_microphone: &[u8],
        far_end_reference: &[u8],
        sample_rate_hz: i32,
        destination: &mut [u8],
    ) -> usize;

    /// Reset adaptive-filter state at the start of a new call.
    fn reset(&mut self);
}

/// (3.3.0) Audio noise reducer — cleans a frame of PCM-16 mono audio.
pub trait INoiseReducer {
    /// Backend self-identification — "krisp" / "deepfilternet" / "passthrough" / "null".
    fn backend_id(&self) -> &str;

    /// True when the underlying model / runtime is available.
    fn is_available(&self) -> bool;

    /// Reduce noise in `audio_pcm16_mono` and write into `destination`. The
    /// destination buffer must be at least as long as the input. Returns the
    /// number of bytes written.
    fn reduce(&mut self, audio_pcm16_mono: &[u8], sample_rate_hz: i32, destination: &mut [u8]) -> usize;
}

/// (3.3.0) Decide whether the caller has finished their turn.
pub trait IEndOfTurnDetector {
    /// Backend self-identification — "rules" / "smart-turn-v2" / "null".
    fn backend_id(&self) -> &str;

    /// Classify the current state.
    fn predict(&self, partial_transcript: &str, trailing_silence: Duration) -> EndOfTurnResult;

    /// Reset internal state at the start of a fresh turn.
    fn reset(&mut self);
}

/// (3.3.0) Voice-activity detector. Classifies each 10-30 ms audio frame.
pub trait IVoiceActivityDetector {
    /// Backend self-identification — "energy" / "silero" / "null".
    fn backend_id(&self) -> &str;

    /// Speech probability threshold for [`VadFrameResult::is_speech`].
    fn speech_threshold(&self) -> f32;

    /// Classify one frame of PCM-16 mono audio.
    fn classify(&mut self, audio_pcm16_mono: &[u8], sample_rate_hz: i32, offset: Duration) -> VadFrameResult;

    /// Reset any internal hangover state at the start of a fresh utterance.
    fn reset(&mut self);
}

// ─────────────────────────────────────────────────────────────────────────────
// Null implementations (fail-closed defaults)
// ─────────────────────────────────────────────────────────────────────────────

/// (2.3.0) Deterministic empty ASR — DI default.
#[derive(Debug, Default, Clone, Copy)]
pub struct NullSpeechRecognizer;

#[async_trait]
impl ISpeechRecognizer for NullSpeechRecognizer {
    type Error = Infallible;
    fn backend_id(&self) -> &str {
        "null"
    }
    async fn transcribe(
        &self,
        _audio_pcm16_mono: &[u8],
        _sample_rate_hz: i32,
        language_hint: Option<&str>,
    ) -> Result<TranscriptionResult, Infallible> {
        Ok(TranscriptionResult {
            text: String::new(),
            language: language_hint.map(str::to_owned),
            segments: Vec::new(),
            total_duration: Duration::zero(),
        })
    }
}

/// (2.3.0) Empty TTS — DI default.
#[derive(Debug, Default, Clone, Copy)]
pub struct NullSpeechSynthesizer;

#[async_trait]
impl ISpeechSynthesizer for NullSpeechSynthesizer {
    type Error = Infallible;
    fn backend_id(&self) -> &str {
        "null"
    }
    async fn synthesize(
        &self,
        _text: &str,
        _voice_id: Option<&str>,
        _language_hint: Option<&str>,
    ) -> Result<SynthesisResult, Infallible> {
        Ok(SynthesisResult {
            audio_pcm16_mono: Vec::new(),
            sample_rate_hz: 16_000,
            duration: Duration::zero(),
        })
    }
}

/// (2.3.0) Never-firing wake-word detector — DI default.
#[derive(Debug, Default, Clone, Copy)]
pub struct NullWakeWordDetector;

#[async_trait]
impl IWakeWordDetector for NullWakeWordDetector {
    type Error = Infallible;
    fn backend_id(&self) -> &str {
        "null"
    }
    async fn start(&self) -> Result<(), Infallible> {
        Ok(())
    }
    async fn stop(&self) -> Result<(), Infallible> {
        Ok(())
    }
}

/// (2.3.0) Empty OCR — DI default.
#[derive(Debug, Default, Clone, Copy)]
pub struct NullOpticalCharacterRecognizer;

#[async_trait]
impl IOpticalCharacterRecognizer for NullOpticalCharacterRecognizer {
    type Error = Infallible;
    fn backend_id(&self) -> &str {
        "null"
    }
    async fn recognize(
        &self,
        _image_bytes: &[u8],
        _language_hint: Option<&str>,
    ) -> Result<OcrResult, Infallible> {
        Ok(OcrResult {
            text: String::new(),
            blocks: Vec::new(),
        })
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Small PCM-16 helpers (BinaryPrimitives / MemoryMarshal equivalents)
// ─────────────────────────────────────────────────────────────────────────────

#[inline]
fn read_i16_le(buf: &[u8], byte_index: usize) -> i16 {
    i16::from_le_bytes([buf[byte_index], buf[byte_index + 1]])
}

#[inline]
fn write_i16_le(buf: &mut [u8], byte_index: usize, value: i16) {
    let bytes = value.to_le_bytes();
    buf[byte_index] = bytes[0];
    buf[byte_index + 1] = bytes[1];
}

const I16_MAX: f32 = i16::MAX as f32;

// ─────────────────────────────────────────────────────────────────────────────
// Voice-activity detectors
// ─────────────────────────────────────────────────────────────────────────────

/// (3.3.0) Always reports speech — DI default so nothing breaks before a real
/// VAD is wired.
#[derive(Debug, Default, Clone, Copy)]
pub struct NullVoiceActivityDetector;

impl IVoiceActivityDetector for NullVoiceActivityDetector {
    fn backend_id(&self) -> &str {
        "null"
    }
    fn speech_threshold(&self) -> f32 {
        0.5
    }
    fn classify(&mut self, _audio_pcm16_mono: &[u8], _sample_rate_hz: i32, offset: Duration) -> VadFrameResult {
        VadFrameResult::new(true, 1.0, offset)
    }
    fn reset(&mut self) {}
}

/// (3.3.0) Production-grade VAD using RMS energy + zero-crossing rate +
/// hangover-frame smoothing. No ML model required.
#[derive(Debug, Clone)]
pub struct EnergyVoiceActivityDetector {
    speech_threshold: f32,
    energy_threshold: f32,
    hangover_frames: i32,
    hangover_remaining: i32,
}

impl EnergyVoiceActivityDetector {
    /// C# defaults: `speechThreshold = 0.55`, `energyThreshold = 0.012`,
    /// `hangoverFrames = 8`.
    pub fn new() -> Self {
        Self::with_params(0.55, 0.012, 8)
    }

    pub fn with_params(speech_threshold: f32, energy_threshold: f32, hangover_frames: i32) -> Self {
        Self {
            speech_threshold,
            energy_threshold,
            hangover_frames,
            hangover_remaining: 0,
        }
    }
}

impl Default for EnergyVoiceActivityDetector {
    fn default() -> Self {
        Self::new()
    }
}

impl IVoiceActivityDetector for EnergyVoiceActivityDetector {
    fn backend_id(&self) -> &str {
        "energy"
    }
    fn speech_threshold(&self) -> f32 {
        self.speech_threshold
    }
    fn classify(&mut self, audio_pcm16_mono: &[u8], _sample_rate_hz: i32, offset: Duration) -> VadFrameResult {
        if audio_pcm16_mono.len() < 2 {
            return VadFrameResult::new(false, 0.0, offset);
        }

        let sample_count = audio_pcm16_mono.len() / 2;
        let mut sum_squares: f64 = 0.0;
        let mut zero_crossings: i32 = 0;
        let mut previous: i16 = 0;
        for i in 0..sample_count {
            let s = read_i16_le(audio_pcm16_mono, i * 2);
            sum_squares += (s as f64) * (s as f64);
            if i > 0 && s.signum() != previous.signum() && s != 0 && previous != 0 {
                zero_crossings += 1;
            }
            previous = s;
        }
        let rms = (sum_squares / sample_count as f64).sqrt() / I16_MAX as f64; // 0..1
        let zcr_rate = zero_crossings as f32 / sample_count as f32;

        // Speech: high RMS + moderate ZCR (~0.05–0.25 for voiced speech).
        let energy_good = rms >= self.energy_threshold as f64;
        let zcr_good = (0.02..=0.30).contains(&zcr_rate);
        let mut raw_prob = if energy_good {
            if zcr_good {
                0.85
            } else {
                0.6
            }
        } else {
            0.1
        };

        let is_speech;
        if raw_prob >= self.speech_threshold {
            is_speech = true;
            self.hangover_remaining = self.hangover_frames;
        } else if self.hangover_remaining > 0 {
            is_speech = true;
            self.hangover_remaining -= 1;
            raw_prob = raw_prob.max(self.speech_threshold);
        } else {
            is_speech = false;
        }

        VadFrameResult::new(is_speech, raw_prob, offset)
    }
    fn reset(&mut self) {
        self.hangover_remaining = 0;
    }
}

/// (3.3.0) ONNX model runner contract supplied by the host package.
pub trait IVadModelRunner {
    /// Score one 30 ms / 16 kHz PCM-16 frame; result is 0..1.
    fn score_frame(&self, audio_pcm16_mono: &[u8], sample_rate_hz: i32) -> f32;
}

/// (3.3.0) Silero VAD wrapper. Delegates the per-frame score to a host
/// [`IVadModelRunner`]; when no runner is wired it transparently falls back to
/// [`EnergyVoiceActivityDetector`]'s scoring.
pub struct SileroVoiceActivityDetector {
    runner: Option<Box<dyn IVadModelRunner + Send + Sync>>,
    fallback: EnergyVoiceActivityDetector,
    speech_threshold: f32,
    hangover_frames: i32,
    hangover_remaining: i32,
}

impl SileroVoiceActivityDetector {
    pub fn new(
        runner: Option<Box<dyn IVadModelRunner + Send + Sync>>,
        speech_threshold: f32,
        hangover_frames: i32,
    ) -> Self {
        Self {
            runner,
            fallback: EnergyVoiceActivityDetector::with_params(speech_threshold, 0.012, 8),
            speech_threshold,
            hangover_frames,
            hangover_remaining: 0,
        }
    }

    /// The C# parameterless-ish default (`runner = null`, `speechThreshold = 0.5`,
    /// `hangoverFrames = 8`).
    pub fn fallback_only() -> Self {
        Self::new(None, 0.5, 8)
    }
}

impl IVoiceActivityDetector for SileroVoiceActivityDetector {
    fn backend_id(&self) -> &str {
        if self.runner.is_none() {
            "silero (fallback)"
        } else {
            "silero"
        }
    }
    fn speech_threshold(&self) -> f32 {
        self.speech_threshold
    }
    fn classify(&mut self, audio_pcm16_mono: &[u8], sample_rate_hz: i32, offset: Duration) -> VadFrameResult {
        let prob = match &self.runner {
            None => return self.fallback.classify(audio_pcm16_mono, sample_rate_hz, offset),
            Some(r) => r.score_frame(audio_pcm16_mono, sample_rate_hz),
        };
        let is_speech;
        if prob >= self.speech_threshold {
            is_speech = true;
            self.hangover_remaining = self.hangover_frames;
        } else if self.hangover_remaining > 0 {
            is_speech = true;
            self.hangover_remaining -= 1;
        } else {
            is_speech = false;
        }
        VadFrameResult::new(is_speech, prob, offset)
    }
    fn reset(&mut self) {
        self.hangover_remaining = 0;
        self.fallback.reset();
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// End-of-turn detectors
// ─────────────────────────────────────────────────────────────────────────────

/// (3.3.0) Always says "they finished" — DI default.
#[derive(Debug, Default, Clone, Copy)]
pub struct NullEndOfTurnDetector;

impl IEndOfTurnDetector for NullEndOfTurnDetector {
    fn backend_id(&self) -> &str {
        "null"
    }
    fn predict(&self, _partial_transcript: &str, _trailing_silence: Duration) -> EndOfTurnResult {
        EndOfTurnResult::new(true, 1.0, 0)
    }
    fn reset(&mut self) {}
}

const EOT_TERMINAL_PUNCTUATION: [char; 6] = ['.', '!', '?', '。', '！', '？'];

/// (3.3.0) Rule-based detector. Considers a turn complete when the transcript
/// ends with terminal punctuation AND the user has been silent for at least the
/// minimum hangover, OR when silence exceeds the maximum-wait ceiling regardless
/// of text.
#[derive(Debug, Clone)]
pub struct RuleBasedEndOfTurnDetector {
    min_silence: Duration,
    hanging_silence: Duration,
    max_silence: Duration,
}

impl RuleBasedEndOfTurnDetector {
    /// C# defaults: min 400 ms, hanging 900 ms, max 2500 ms.
    pub fn new() -> Self {
        Self::with_params(
            Duration::milliseconds(400),
            Duration::milliseconds(900),
            Duration::milliseconds(2500),
        )
    }

    pub fn with_params(min_silence: Duration, hanging_silence: Duration, max_silence: Duration) -> Self {
        Self {
            min_silence,
            hanging_silence,
            max_silence,
        }
    }

    /// The exact hanging-word set (`and`, `but`, `so`, … `an`).
    fn hanging_words() -> &'static [&'static str] {
        &[
            "and", "but", "so", "or", "because", "if", "when", "while", "though", "however", "um",
            "uh", "like", "you", "the", "a", "an",
        ]
    }
}

impl Default for RuleBasedEndOfTurnDetector {
    fn default() -> Self {
        Self::new()
    }
}

impl IEndOfTurnDetector for RuleBasedEndOfTurnDetector {
    fn backend_id(&self) -> &str {
        "rules"
    }
    fn predict(&self, partial_transcript: &str, trailing_silence: Duration) -> EndOfTurnResult {
        let text = partial_transcript.trim();
        if trailing_silence >= self.max_silence {
            return EndOfTurnResult::new(true, 0.7, 0);
        }

        if text.is_empty() {
            let wait = ((self.min_silence - trailing_silence).num_milliseconds() as f64).max(150.0);
            return EndOfTurnResult::new(false, 0.2, wait as i32);
        }

        let ends_terminal = EOT_TERMINAL_PUNCTUATION
            .iter()
            .any(|p| text.ends_with(*p));
        let last_word = text
            .split([' ', '\t', '\n'])
            .filter(|s| !s.is_empty())
            .next_back()
            .unwrap_or("");
        let normalised = last_word
            .trim_end_matches(['.', ',', '!', '?'])
            .to_lowercase();
        let ends_hanging = Self::hanging_words().contains(&normalised.as_str());

        if ends_hanging {
            let remaining = self.hanging_silence - trailing_silence;
            if remaining <= Duration::zero() {
                return EndOfTurnResult::new(true, 0.6, 0);
            }
            let ms = (remaining.num_milliseconds() as f64).ceil() as i32;
            return EndOfTurnResult::new(false, 0.4, ms);
        }

        if ends_terminal && trailing_silence >= self.min_silence {
            return EndOfTurnResult::new(true, 0.9, 0);
        }

        if trailing_silence >= self.min_silence {
            return EndOfTurnResult::new(true, 0.75, 0);
        }

        let ms = ((self.min_silence - trailing_silence).num_milliseconds() as f64).max(50.0) as i32;
        EndOfTurnResult::new(false, 0.6, ms)
    }
    fn reset(&mut self) {}
}

/// (3.3.0) Host-supplied semantic turn model.
pub trait ITurnModelRunner {
    /// Score the current state; 0..1 = probability the turn is complete.
    fn score_completion(&self, partial_transcript: &str, trailing_silence: Duration) -> f32;
}

/// (3.3.0) Smart-turn wrapper. Uses the supplied semantic model when present;
/// otherwise falls back to [`RuleBasedEndOfTurnDetector`].
pub struct SmartTurnDetector {
    runner: Option<Box<dyn ITurnModelRunner + Send + Sync>>,
    fallback: RuleBasedEndOfTurnDetector,
    threshold: f32,
}

impl SmartTurnDetector {
    pub fn new(runner: Option<Box<dyn ITurnModelRunner + Send + Sync>>, threshold: f32) -> Self {
        Self {
            runner,
            fallback: RuleBasedEndOfTurnDetector::new(),
            threshold,
        }
    }

    /// The C# default (`runner = null`, `threshold = 0.5`).
    pub fn fallback_only() -> Self {
        Self::new(None, 0.5)
    }
}

impl IEndOfTurnDetector for SmartTurnDetector {
    fn backend_id(&self) -> &str {
        if self.runner.is_none() {
            "smart-turn (fallback)"
        } else {
            "smart-turn-v2"
        }
    }
    fn predict(&self, partial_transcript: &str, trailing_silence: Duration) -> EndOfTurnResult {
        let runner = match &self.runner {
            None => return self.fallback.predict(partial_transcript, trailing_silence),
            Some(r) => r,
        };
        let prob = runner
            .score_completion(partial_transcript, trailing_silence)
            .clamp(0.0, 1.0);
        if prob >= self.threshold {
            return EndOfTurnResult::new(true, prob, 0);
        }
        let wait_ms = ((1.0 - prob) * 1000.0).round() as i32;
        EndOfTurnResult::new(false, prob, wait_ms)
    }
    fn reset(&mut self) {
        self.fallback.reset();
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Echo cancellers
// ─────────────────────────────────────────────────────────────────────────────

/// (3.3.0) Pass-through DI default.
#[derive(Debug, Default, Clone, Copy)]
pub struct NullEchoCanceller;

impl IEchoCanceller for NullEchoCanceller {
    fn backend_id(&self) -> &str {
        "null"
    }
    fn cancel(
        &mut self,
        near_end_microphone: &[u8],
        _far_end_reference: &[u8],
        _sample_rate_hz: i32,
        destination: &mut [u8],
    ) -> usize {
        let n = near_end_microphone.len();
        destination[..n].copy_from_slice(near_end_microphone);
        n
    }
    fn reset(&mut self) {}
}

/// (3.3.0) Normalised LMS adaptive-filter AEC. Pure Rust, no model downloads.
/// Filter length defaults to 256 taps (~16 ms @ 16 kHz).
///
/// # Panics
/// Panics (like the C# `ArgumentException`) if the near-end and far-end buffers
/// differ in length, or if `destination` is shorter than the input.
#[derive(Debug, Clone)]
pub struct NlmsEchoCanceller {
    w: Vec<f32>,
    step_size: f32,
    epsilon: f32,
    filter_length: usize,
    ref_buffer: Vec<f32>,
    ref_index: usize,
}

impl NlmsEchoCanceller {
    /// C# defaults: `filterLength = 256`, `stepSize = 0.4`, `epsilon = 1e-6`.
    pub fn new() -> Self {
        Self::with_params(256, 0.4, 1e-6)
    }

    pub fn with_params(filter_length: usize, step_size: f32, epsilon: f32) -> Self {
        Self {
            w: vec![0.0; filter_length],
            step_size,
            epsilon,
            filter_length,
            ref_buffer: vec![0.0; filter_length],
            ref_index: 0,
        }
    }
}

impl Default for NlmsEchoCanceller {
    fn default() -> Self {
        Self::new()
    }
}

impl IEchoCanceller for NlmsEchoCanceller {
    fn backend_id(&self) -> &str {
        "nlms"
    }
    fn cancel(
        &mut self,
        near_end_microphone: &[u8],
        far_end_reference: &[u8],
        _sample_rate_hz: i32,
        destination: &mut [u8],
    ) -> usize {
        assert!(
            near_end_microphone.len() == far_end_reference.len(),
            "near-end and far-end must be the same length."
        );
        assert!(
            destination.len() >= near_end_microphone.len(),
            "destination must be at least as long as input."
        );

        let sample_count = near_end_microphone.len() / 2;
        for n in 0..sample_count {
            let mic_sample = read_i16_le(near_end_microphone, n * 2) as f32 / I16_MAX;
            let far_sample = read_i16_le(far_end_reference, n * 2) as f32 / I16_MAX;

            // Push far-end into circular reference buffer.
            self.ref_buffer[self.ref_index] = far_sample;

            // Estimated echo: dot(w, ref).
            let mut echo_estimate = 0.0f32;
            let mut power = self.epsilon;
            for k in 0..self.filter_length {
                let r_idx = (self.ref_index + self.filter_length - k) % self.filter_length;
                let x = self.ref_buffer[r_idx];
                echo_estimate += self.w[k] * x;
                power += x * x;
            }

            // Error = mic - echo estimate.
            let error = mic_sample - echo_estimate;

            // Update filter weights.
            let mu = self.step_size / power;
            for k in 0..self.filter_length {
                let r_idx = (self.ref_index + self.filter_length - k) % self.filter_length;
                self.w[k] += mu * error * self.ref_buffer[r_idx];
            }

            self.ref_index = (self.ref_index + 1) % self.filter_length;

            // Clamp + write.
            let out_sample = (error * I16_MAX).clamp(i16::MIN as f32, i16::MAX as f32) as i32;
            write_i16_le(destination, n * 2, out_sample as i16);
        }

        near_end_microphone.len()
    }
    fn reset(&mut self) {
        self.w.iter_mut().for_each(|x| *x = 0.0);
        self.ref_buffer.iter_mut().for_each(|x| *x = 0.0);
        self.ref_index = 0;
    }
}

/// (3.3.0) Host-supplied AEC model runner (e.g. WebRTC AEC3).
pub trait IEchoCancellerModelRunner {
    fn process(
        &mut self,
        near_end: &[u8],
        far_end: &[u8],
        sample_rate_hz: i32,
        destination: &mut [u8],
    ) -> usize;

    fn reset(&mut self);
}

/// (3.3.0) WebRTC AEC3 wrapper — falls back to NLMS when no runner is wired.
pub struct WebRtcEchoCanceller {
    runner: Option<Box<dyn IEchoCancellerModelRunner + Send + Sync>>,
    fallback: NlmsEchoCanceller,
}

impl WebRtcEchoCanceller {
    pub fn new(runner: Option<Box<dyn IEchoCancellerModelRunner + Send + Sync>>) -> Self {
        Self {
            runner,
            fallback: NlmsEchoCanceller::new(),
        }
    }
}

impl IEchoCanceller for WebRtcEchoCanceller {
    fn backend_id(&self) -> &str {
        if self.runner.is_none() {
            "webrtc-aec3 (fallback)"
        } else {
            "webrtc-aec3"
        }
    }
    fn cancel(
        &mut self,
        near_end_microphone: &[u8],
        far_end_reference: &[u8],
        sample_rate_hz: i32,
        destination: &mut [u8],
    ) -> usize {
        match &mut self.runner {
            None => self
                .fallback
                .cancel(near_end_microphone, far_end_reference, sample_rate_hz, destination),
            Some(r) => r.process(near_end_microphone, far_end_reference, sample_rate_hz, destination),
        }
    }
    fn reset(&mut self) {
        self.fallback.reset();
        if let Some(r) = &mut self.runner {
            r.reset();
        }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Noise reducers
// ─────────────────────────────────────────────────────────────────────────────

/// (3.3.0) No-op reducer — DI default.
#[derive(Debug, Default, Clone, Copy)]
pub struct NullNoiseReducer;

impl INoiseReducer for NullNoiseReducer {
    fn backend_id(&self) -> &str {
        "null"
    }
    fn is_available(&self) -> bool {
        true
    }
    fn reduce(&mut self, audio_pcm16_mono: &[u8], _sample_rate_hz: i32, destination: &mut [u8]) -> usize {
        let n = audio_pcm16_mono.len();
        destination[..n].copy_from_slice(audio_pcm16_mono);
        n
    }
}

/// (3.3.0) Lightweight time-domain noise gate: attenuates samples below the
/// estimated floor with a soft knee. Zero runtime cost, works on every device.
///
/// # Panics
/// Panics (like the C# `ArgumentException`) when `destination` is shorter than
/// the input.
#[derive(Debug, Clone)]
pub struct SpectralSubtractionNoiseReducer {
    floor_estimate: f32,
    attenuation: f32,
}

impl SpectralSubtractionNoiseReducer {
    /// C# defaults: `floorEstimate = 0.008`, `attenuation = 0.25`.
    pub fn new() -> Self {
        Self::with_params(0.008, 0.25)
    }

    pub fn with_params(floor_estimate: f32, attenuation: f32) -> Self {
        Self {
            floor_estimate,
            attenuation,
        }
    }
}

impl Default for SpectralSubtractionNoiseReducer {
    fn default() -> Self {
        Self::new()
    }
}

impl INoiseReducer for SpectralSubtractionNoiseReducer {
    fn backend_id(&self) -> &str {
        "passthrough"
    }
    fn is_available(&self) -> bool {
        true
    }
    fn reduce(&mut self, audio_pcm16_mono: &[u8], _sample_rate_hz: i32, destination: &mut [u8]) -> usize {
        assert!(
            destination.len() >= audio_pcm16_mono.len(),
            "destination must be at least as long as input."
        );
        let sample_count = audio_pcm16_mono.len() / 2;
        let floor = (self.floor_estimate * I16_MAX) as i32;
        for i in 0..sample_count {
            let s = read_i16_le(audio_pcm16_mono, i * 2) as i32;
            let abs = s.abs();
            let out = if abs <= floor {
                (s as f32 * self.attenuation) as i16
            } else {
                s as i16
            };
            write_i16_le(destination, i * 2, out);
        }
        audio_pcm16_mono.len()
    }
}

/// (3.3.0) Host-supplied DNN runner for noise reduction.
pub trait INoiseReducerModelRunner {
    /// Process one frame; write cleaned PCM-16 mono into `destination`.
    fn process(&mut self, audio_pcm16_mono: &[u8], sample_rate_hz: i32, destination: &mut [u8]) -> usize;
}

/// (3.3.0) Krisp wrapper — uses the host's [`INoiseReducerModelRunner`] when
/// present; otherwise falls back to spectral subtraction.
pub struct KrispNoiseReducer {
    runner: Option<Box<dyn INoiseReducerModelRunner + Send + Sync>>,
    fallback: SpectralSubtractionNoiseReducer,
}

impl KrispNoiseReducer {
    pub fn new(runner: Option<Box<dyn INoiseReducerModelRunner + Send + Sync>>) -> Self {
        Self {
            runner,
            fallback: SpectralSubtractionNoiseReducer::new(),
        }
    }
}

impl INoiseReducer for KrispNoiseReducer {
    fn backend_id(&self) -> &str {
        if self.runner.is_none() {
            "krisp (fallback)"
        } else {
            "krisp"
        }
    }
    fn is_available(&self) -> bool {
        true
    }
    fn reduce(&mut self, audio_pcm16_mono: &[u8], sample_rate_hz: i32, destination: &mut [u8]) -> usize {
        match &mut self.runner {
            None => self.fallback.reduce(audio_pcm16_mono, sample_rate_hz, destination),
            Some(r) => r.process(audio_pcm16_mono, sample_rate_hz, destination),
        }
    }
}

/// (3.3.0) DeepFilterNet wrapper.
pub struct DeepFilterNetNoiseReducer {
    runner: Option<Box<dyn INoiseReducerModelRunner + Send + Sync>>,
    fallback: SpectralSubtractionNoiseReducer,
}

impl DeepFilterNetNoiseReducer {
    pub fn new(runner: Option<Box<dyn INoiseReducerModelRunner + Send + Sync>>) -> Self {
        Self {
            runner,
            fallback: SpectralSubtractionNoiseReducer::new(),
        }
    }
}

impl INoiseReducer for DeepFilterNetNoiseReducer {
    fn backend_id(&self) -> &str {
        if self.runner.is_none() {
            "deepfilternet (fallback)"
        } else {
            "deepfilternet"
        }
    }
    fn is_available(&self) -> bool {
        true
    }
    fn reduce(&mut self, audio_pcm16_mono: &[u8], sample_rate_hz: i32, destination: &mut [u8]) -> usize {
        match &mut self.runner {
            None => self.fallback.reduce(audio_pcm16_mono, sample_rate_hz, destination),
            Some(r) => r.process(audio_pcm16_mono, sample_rate_hz, destination),
        }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// AudioFormatConverter — G.711 μ-law / a-law ↔ PCM-16 + linear resample.
// Port of the C# `static class AudioFormatConverter`.
// ─────────────────────────────────────────────────────────────────────────────

pub use audio_format::{AudioCodec, AudioFormatConverter};

pub mod audio_format {
    //! Stateless audio-format conversion. The G.711 codec arithmetic is ported
    //! verbatim from ITU-T G.711 / the C# reference.

    use super::{read_i16_le, write_i16_le};

    /// (3.3.0) Carrier-native audio formats we know how to convert.
    #[derive(Debug, Clone, Copy, PartialEq, Eq)]
    pub enum AudioCodec {
        /// 16-bit signed linear PCM, little-endian, mono.
        Pcm16,
        /// G.711 μ-law (telephony, North America / Japan).
        MuLaw,
        /// G.711 A-law (telephony, Europe).
        ALaw,
    }

    /// (3.3.0) Stateless audio-format converter, mirroring the C# static class.
    pub struct AudioFormatConverter;

    impl AudioFormatConverter {
        /// (3.3.0) Convert audio from one (codec, sample rate) to another.
        /// Returns a freshly allocated output buffer.
        ///
        /// # Panics
        /// Panics (like the C# `ArgumentOutOfRangeException`) if either sample
        /// rate is `<= 0`.
        pub fn convert(
            input: &[u8],
            input_codec: AudioCodec,
            input_sample_rate_hz: i32,
            output_codec: AudioCodec,
            output_sample_rate_hz: i32,
        ) -> Vec<u8> {
            assert!(input_sample_rate_hz > 0, "input_sample_rate_hz must be positive");
            assert!(output_sample_rate_hz > 0, "output_sample_rate_hz must be positive");

            // 1) Decode source to PCM-16.
            let pcm_in = match input_codec {
                AudioCodec::Pcm16 => input.to_vec(),
                AudioCodec::MuLaw => Self::decode_mulaw_to_pcm16(input),
                AudioCodec::ALaw => Self::decode_alaw_to_pcm16(input),
            };

            // 2) Resample if needed.
            let pcm_resampled = if input_sample_rate_hz == output_sample_rate_hz {
                pcm_in
            } else {
                Self::resample_pcm16_linear(&pcm_in, input_sample_rate_hz, output_sample_rate_hz)
            };

            // 3) Encode to target codec.
            match output_codec {
                AudioCodec::Pcm16 => pcm_resampled,
                AudioCodec::MuLaw => Self::encode_pcm16_to_mulaw(&pcm_resampled),
                AudioCodec::ALaw => Self::encode_pcm16_to_alaw(&pcm_resampled),
            }
        }

        // ===== μ-law =====

        pub fn decode_mulaw_to_pcm16(mulaw: &[u8]) -> Vec<u8> {
            let mut pcm = vec![0u8; mulaw.len() * 2];
            for (i, &byte) in mulaw.iter().enumerate() {
                let s = Self::mulaw_to_linear(byte);
                write_i16_le(&mut pcm, i * 2, s);
            }
            pcm
        }

        pub fn encode_pcm16_to_mulaw(pcm: &[u8]) -> Vec<u8> {
            let samples = pcm.len() / 2;
            let mut mulaw = vec![0u8; samples];
            for (i, out) in mulaw.iter_mut().enumerate() {
                let s = read_i16_le(pcm, i * 2);
                *out = Self::linear_to_mulaw(s);
            }
            mulaw
        }

        fn mulaw_to_linear(mu: u8) -> i16 {
            // G.711 μ-law decode (ITU-T G.711).
            let mu = !mu;
            let sign = (mu & 0x80) as i32;
            let exponent = ((mu >> 4) & 0x07) as i32;
            let mantissa = (mu & 0x0F) as i32;
            let magnitude = ((mantissa << 3) + 0x84) << exponent;
            let sample = magnitude - 0x84;
            if sign != 0 {
                (-sample) as i16
            } else {
                sample as i16
            }
        }

        fn linear_to_mulaw(pcm: i16) -> u8 {
            const BIAS: i32 = 0x84;
            const CLIP: i32 = 32635;
            let pcm = pcm as i32;
            let sign = (pcm >> 8) & 0x80;
            let mut v = pcm;
            if sign != 0 {
                v = -v;
            }
            if v > CLIP {
                v = CLIP;
            }
            v += BIAS;

            let exponent = if v >= 0x4000 {
                7
            } else if v >= 0x2000 {
                6
            } else if v >= 0x1000 {
                5
            } else if v >= 0x0800 {
                4
            } else if v >= 0x0400 {
                3
            } else if v >= 0x0200 {
                2
            } else if v >= 0x0100 {
                1
            } else {
                0
            };

            let mantissa = (v >> (exponent + 3)) & 0x0F;
            !((sign | (exponent << 4) | mantissa) as u8)
        }

        // ===== a-law =====

        pub fn decode_alaw_to_pcm16(alaw: &[u8]) -> Vec<u8> {
            let mut pcm = vec![0u8; alaw.len() * 2];
            for (i, &byte) in alaw.iter().enumerate() {
                let s = Self::alaw_to_linear(byte);
                write_i16_le(&mut pcm, i * 2, s);
            }
            pcm
        }

        pub fn encode_pcm16_to_alaw(pcm: &[u8]) -> Vec<u8> {
            let samples = pcm.len() / 2;
            let mut alaw = vec![0u8; samples];
            for (i, out) in alaw.iter_mut().enumerate() {
                let s = read_i16_le(pcm, i * 2);
                *out = Self::linear_to_alaw(s);
            }
            alaw
        }

        fn alaw_to_linear(a: u8) -> i16 {
            let a = a ^ 0x55;
            let sign = (a & 0x80) as i32;
            let exponent = ((a >> 4) & 0x07) as i32;
            let mantissa = (a & 0x0F) as i32;
            let magnitude = if exponent != 0 {
                ((mantissa << 4) + 0x108) << (exponent - 1)
            } else {
                (mantissa << 4) + 0x08
            };
            if sign != 0 {
                (-magnitude) as i16
            } else {
                magnitude as i16
            }
        }

        fn linear_to_alaw(pcm: i16) -> u8 {
            let pcm = pcm as i32;
            let sign = (pcm >> 8) & 0x80;
            let mut v = pcm;
            if sign != 0 {
                v = -v;
            }
            if v > 0x7FFF {
                v = 0x7FFF;
            }

            let (exponent, mantissa);
            if v < 256 {
                exponent = 0;
                mantissa = v >> 4;
            } else {
                exponent = if v >= 0x4000 {
                    7
                } else if v >= 0x2000 {
                    6
                } else if v >= 0x1000 {
                    5
                } else if v >= 0x0800 {
                    4
                } else if v >= 0x0400 {
                    3
                } else if v >= 0x0200 {
                    2
                } else {
                    1
                };
                mantissa = (v >> (exponent + 3)) & 0x0F;
            }
            ((sign | (exponent << 4) | mantissa) as u8) ^ 0x55
        }

        // ===== resample (linear interpolation) =====

        pub fn resample_pcm16_linear(pcm: &[u8], from_hz: i32, to_hz: i32) -> Vec<u8> {
            if from_hz == to_hz {
                return pcm.to_vec();
            }
            let src_samples = pcm.len() / 2;
            let dst_samples = (src_samples as i64 * to_hz as i64 / from_hz as i64) as usize;
            let mut dst = vec![0u8; dst_samples * 2];
            for i in 0..dst_samples {
                let src_idx = i as f64 * from_hz as f64 / to_hz as f64;
                let idx0 = src_idx.floor() as usize;
                let idx1 = (idx0 + 1).min(src_samples.saturating_sub(1));
                let frac = src_idx - idx0 as f64;
                let s0 = read_i16_le(pcm, idx0 * 2) as f64;
                let s1 = read_i16_le(pcm, idx1 * 2) as f64;
                let s = (s0 + (s1 - s0) * frac) as i16;
                write_i16_le(&mut dst, i * 2, s);
            }
            dst
        }
    }
}
