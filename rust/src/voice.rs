//! voice — CircleAI.Voice (Rust port).
//!
//! On-device voice pipeline: audio capture → VAD → transcription → wake-word
//! orchestration, over injected seams. This is DISTINCT from [`crate::speech`]
//! (the richer `CircleAI.Speech` ASR/TTS/OCR surface); the `CircleAI.Voice`
//! assembly is its own smaller surface with its own types, ported here under
//! the `voice::` namespace so the by-name collisions with `speech::` are
//! path-disambiguated.
//!
//! Ports:
//!   - `AudioFormat`             → [`AudioFormat`]
//!   - `VadSegment`              → [`VadSegment`]
//!   - `TranscriptionResult`     → [`TranscriptionResult`]
//!   - `PartialTranscription`    → [`PartialTranscription`]
//!   - `WakeWordDetectedEventArgs` → [`WakeWordDetectedEvent`]
//!   - `IAudioCapture`           → [`IAudioCapture`] (+ [`NullAudioCapture`])
//!   - `IVoiceActivityDetector`  → [`IVoiceActivityDetector`]
//!   - `IVoiceTranscriber`       → [`IVoiceTranscriber`]
//!   - `IWakeWordDetector`       → [`IWakeWordDetector`]
//!   - `ITtsEngine`              → [`ITtsEngine`] (+ [`TtsSynthesisResult`])
//!   - `EnergyVadDetector`       → [`EnergyVadDetector`] (RMS framing + residual carry)
//!   - `EnergyWakeWordDetector`  → [`EnergyWakeWordDetector`]
//!   - `VoicePipeline`           → [`VoicePipeline`]
//!
//! The C# surface is async-streaming (`IAsyncEnumerable`, C# events, thread-pool
//! activation loops). Per crate convention this port is synchronous and
//! pull-based: audio "streams" are materialised `Vec<Vec<u8>>` chunk lists and
//! the detectors/pipeline expose the same processing as plain method calls. All
//! load-bearing DSP (RMS energy, 20 ms framing, residual carry-over, silence
//! counting, mid-speech flush) and orchestration (VAD-filter → transcribe →
//! final-result drain, wake-word substring match) is preserved exactly.

// ─────────────────────────────────────────────────────────────────────────────
// Value types
// ─────────────────────────────────────────────────────────────────────────────

/// PCM audio format expected/produced by voice components. Mirrors `AudioFormat`.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct AudioFormat {
    pub sample_rate: i32,
    pub channels: i32,
    pub bits_per_sample: i32,
}

impl AudioFormat {
    /// Canonical B! voice input: PCM signed 16-bit, mono, 16 kHz. Mirrors
    /// `AudioFormat.Pcm16Mono16k`.
    pub const PCM16_MONO_16K: AudioFormat = AudioFormat {
        sample_rate: 16_000,
        channels: 1,
        bits_per_sample: 16,
    };
}

/// A segment identified by a [`IVoiceActivityDetector`]. Mirrors `VadSegment`.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct VadSegment {
    /// Raw PCM bytes; non-empty for speech segments.
    pub audio: Vec<u8>,
    /// `true` when this segment contains detected speech.
    pub is_speech: bool,
}

impl VadSegment {
    /// Creates a segment.
    pub fn new(audio: Vec<u8>, is_speech: bool) -> Self {
        Self { audio, is_speech }
    }
}

/// Final transcription result. Mirrors `TranscriptionResult`.
#[derive(Debug, Clone, PartialEq)]
pub struct TranscriptionResult {
    pub text: String,
    pub confidence: f32,
    /// BCP-47 / ISO-639 code (e.g. `"en"`, `"zu"`, `"und"` for unknown).
    pub language_code: String,
}

impl TranscriptionResult {
    /// Creates a result.
    pub fn new(
        text: impl Into<String>,
        confidence: f32,
        language_code: impl Into<String>,
    ) -> Self {
        Self {
            text: text.into(),
            confidence,
            language_code: language_code.into(),
        }
    }
}

/// Partial or final transcription during streaming recognition. Mirrors
/// `PartialTranscription`.
#[derive(Debug, Clone, PartialEq)]
pub struct PartialTranscription {
    pub text: String,
    pub is_final: bool,
    pub confidence: f32,
}

impl PartialTranscription {
    /// Creates a partial transcription.
    pub fn new(text: impl Into<String>, is_final: bool, confidence: f32) -> Self {
        Self {
            text: text.into(),
            is_final,
            confidence,
        }
    }
}

/// Payload describing a single wake-word detection. Mirrors
/// `WakeWordDetectedEventArgs`.
#[derive(Debug, Clone, PartialEq)]
pub struct WakeWordDetectedEvent {
    pub wake_word: String,
    pub detected_at: chrono::DateTime<chrono::Utc>,
    /// Detector-reported confidence in `[0, 1]`.
    pub confidence: f32,
}

/// Result of a single-shot TTS synthesis. Mirrors `TtsSynthesisResult`.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct TtsSynthesisResult {
    pub audio_data: Vec<u8>,
    pub sample_rate: i32,
    pub channels: i32,
    pub bits_per_sample: i32,
}

// ─────────────────────────────────────────────────────────────────────────────
// Errors
// ─────────────────────────────────────────────────────────────────────────────

/// Errors surfaced by voice components. Hand-rolled (no `thiserror`).
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum VoiceError {
    /// `ArgumentException` / `ArgumentOutOfRangeException`.
    Argument(String),
    /// A backend (capture / transcription / synthesis) failed.
    Backend(String),
}

impl std::fmt::Display for VoiceError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            VoiceError::Argument(m) | VoiceError::Backend(m) => f.write_str(m),
        }
    }
}

impl std::error::Error for VoiceError {}

// ─────────────────────────────────────────────────────────────────────────────
// Seams
// ─────────────────────────────────────────────────────────────────────────────

/// Captures raw audio and exposes it as PCM byte chunks. Mirrors
/// `IAudioCapture` (the async `IAsyncEnumerable` stream is materialised to a
/// `Vec` of chunks in this pull-based port).
pub trait IAudioCapture {
    /// The PCM format produced by [`capture`](Self::capture).
    fn format(&self) -> AudioFormat;

    /// Produces the captured PCM chunks. An empty result means no audio.
    fn capture(&self) -> Result<Vec<Vec<u8>>, VoiceError>;
}

/// No-op capture that yields no audio. Mirrors `NullAudioCapture`.
#[derive(Debug, Default, Clone, Copy)]
pub struct NullAudioCapture;

impl NullAudioCapture {
    /// Creates the null capture.
    pub fn new() -> Self {
        Self
    }
}

impl IAudioCapture for NullAudioCapture {
    fn format(&self) -> AudioFormat {
        AudioFormat::PCM16_MONO_16K
    }

    fn capture(&self) -> Result<Vec<Vec<u8>>, VoiceError> {
        Ok(Vec::new())
    }
}

/// Detects speech vs silence in a raw PCM chunk stream. Mirrors
/// `IVoiceActivityDetector` — returns the speech-containing segments.
pub trait IVoiceActivityDetector {
    /// Processes the chunk stream and yields complete speech segments.
    fn detect(&self, audio_chunks: &[Vec<u8>]) -> Result<Vec<VadSegment>, VoiceError>;
}

/// Converts captured audio into text. Mirrors `IVoiceTranscriber`.
pub trait IVoiceTranscriber {
    /// Transcribes a complete PCM buffer (16-bit, 16 kHz mono).
    fn transcribe(&self, pcm_audio: &[u8]) -> Result<TranscriptionResult, VoiceError>;

    /// Streams chunks and returns the sequence of partial transcriptions; the
    /// final element has `is_final == true`.
    fn stream_transcribe(
        &self,
        audio_chunks: &[Vec<u8>],
    ) -> Result<Vec<PartialTranscription>, VoiceError>;
}

/// Detects a configured wake word in a continuous audio stream. Mirrors
/// `IWakeWordDetector` — pull-based here: [`scan`](Self::scan) runs one pass
/// over the currently-available audio and returns any detections.
pub trait IWakeWordDetector {
    /// The phrase the detector listens for (e.g. `"hey b"`).
    fn wake_word(&self) -> &str;

    /// Runs one detection pass and returns any wake-word events found.
    fn scan(&self) -> Result<Vec<WakeWordDetectedEvent>, VoiceError>;
}

/// Text-to-speech engine. Mirrors `ITtsEngine`.
pub trait ITtsEngine {
    /// Synthesises `text` to a single PCM buffer.
    fn synthesise(&self, text: &str) -> Result<TtsSynthesisResult, VoiceError>;

    /// Streams PCM chunks as they are synthesised.
    fn stream_synthesise(&self, text: &str) -> Result<Vec<Vec<u8>>, VoiceError>;
}

// ─────────────────────────────────────────────────────────────────────────────
// EnergyVadDetector
// ─────────────────────────────────────────────────────────────────────────────

/// Energy-based [`IVoiceActivityDetector`] using RMS energy to distinguish
/// speech from silence. Pure code, no external deps. Mirrors `EnergyVadDetector`.
///
/// Processes audio in fixed-size frames; when a frame's RMS exceeds
/// `energy_threshold` it is speech. Speech frames are buffered until
/// `silence_frame_count` consecutive below-threshold frames are seen, at which
/// point the buffered segment is emitted. Expected input: PCM 16-bit, 16 kHz
/// mono little-endian.
#[derive(Debug, Clone)]
pub struct EnergyVadDetector {
    energy_threshold: f32,
    silence_frame_count: usize,
    frame_size_bytes: usize,
}

impl EnergyVadDetector {
    /// Creates a detector.
    ///
    /// * `energy_threshold` — RMS threshold in `[0, 1]` (default `0.02`).
    /// * `silence_frames` — consecutive below-threshold frames = end-of-speech
    ///   (default `15` = 300 ms at 20 ms/frame).
    /// * `frame_size_bytes` — analysis frame size (default `640` = 20 ms at
    ///   16 kHz mono 16-bit).
    pub fn new(
        energy_threshold: f32,
        silence_frames: usize,
        frame_size_bytes: usize,
    ) -> Result<Self, VoiceError> {
        if silence_frames == 0 {
            return Err(VoiceError::Argument("silenceFrames must be positive.".into()));
        }
        if frame_size_bytes == 0 {
            return Err(VoiceError::Argument(
                "frameSizeBytes must be positive.".into(),
            ));
        }
        if energy_threshold < 0.0 {
            return Err(VoiceError::Argument(
                "energyThreshold must be non-negative.".into(),
            ));
        }
        Ok(Self {
            energy_threshold,
            silence_frame_count: silence_frames,
            frame_size_bytes,
        })
    }

    /// Creates a detector with the C# defaults (`0.02`, `15`, `640`).
    pub fn with_defaults() -> Self {
        Self {
            energy_threshold: 0.02,
            silence_frame_count: 15,
            frame_size_bytes: 640,
        }
    }

    /// RMS threshold in `[0, 1]`.
    pub fn energy_threshold(&self) -> f32 {
        self.energy_threshold
    }

    /// Consecutive below-threshold frames required for end-of-speech.
    pub fn silence_frame_count(&self) -> usize {
        self.silence_frame_count
    }

    /// Analysis frame size in bytes.
    pub fn frame_size_bytes(&self) -> usize {
        self.frame_size_bytes
    }

    /// Root Mean Square energy of a PCM 16-bit frame, normalised to `[0, 1]`.
    fn compute_rms_energy(frame: &[u8]) -> f32 {
        // Interpret as little-endian signed 16-bit samples.
        let sample_count = frame.len() / 2;
        if sample_count == 0 {
            return 0.0;
        }
        let mut sum_squares = 0.0f64;
        for i in 0..sample_count {
            let lo = frame[i * 2] as i16;
            let hi = frame[i * 2 + 1] as i16;
            let sample = (lo & 0xff) | (hi << 8);
            let normalised = sample as f64 / 32768.0;
            sum_squares += normalised * normalised;
        }
        (sum_squares / sample_count as f64).sqrt() as f32
    }
}

impl IVoiceActivityDetector for EnergyVadDetector {
    fn detect(&self, audio_chunks: &[Vec<u8>]) -> Result<Vec<VadSegment>, VoiceError> {
        let mut out: Vec<VadSegment> = Vec::new();

        // Carry-over buffer for bytes that don't fill a complete frame.
        let mut residual: Vec<u8> = Vec::new();
        // Accumulator for the current speech segment.
        let mut speech_buffer: Vec<u8> = Vec::new();

        let mut in_speech = false;
        let mut consecutive_silence_frames = 0usize;

        for chunk in audio_chunks {
            if chunk.is_empty() {
                continue;
            }
            residual.extend_from_slice(chunk);

            let mut offset = 0usize;
            while residual.len() - offset >= self.frame_size_bytes {
                let frame = &residual[offset..offset + self.frame_size_bytes];
                let rms = Self::compute_rms_energy(frame);
                let is_speech_frame = rms >= self.energy_threshold;

                if is_speech_frame {
                    if !in_speech {
                        in_speech = true;
                        consecutive_silence_frames = 0;
                        speech_buffer.clear();
                    } else {
                        consecutive_silence_frames = 0;
                    }
                    speech_buffer.extend_from_slice(frame);
                } else if in_speech {
                    // Buffer silence frames in case speech resumes.
                    speech_buffer.extend_from_slice(frame);
                    consecutive_silence_frames += 1;

                    if consecutive_silence_frames >= self.silence_frame_count {
                        in_speech = false;
                        consecutive_silence_frames = 0;
                        out.push(VadSegment::new(std::mem::take(&mut speech_buffer), true));
                    }
                }
                // else: silence while not in speech — discard.

                offset += self.frame_size_bytes;
            }

            // Move unconsumed residual bytes to the start of the buffer.
            if offset > 0 {
                residual.drain(0..offset);
            }
        }

        // Stream ended — if mid-speech, emit what we have.
        if in_speech && !speech_buffer.is_empty() {
            out.push(VadSegment::new(speech_buffer, true));
        }

        Ok(out)
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// EnergyWakeWordDetector
// ─────────────────────────────────────────────────────────────────────────────

/// [`IWakeWordDetector`] that combines energy VAD with transcription to detect
/// a configurable wake-word phrase. Audio is captured, speech segments are
/// transcribed, and a segment whose transcript contains the wake word (case-
/// insensitive) produces a detection. Mirrors `EnergyWakeWordDetector`.
///
/// The C# reference runs a continuous background listen loop; this pull-based
/// port exposes [`scan`](IWakeWordDetector::scan), which performs exactly one
/// capture→VAD→transcribe→match pass over the available audio — the same
/// per-segment logic, minus the thread-pool lifecycle.
pub struct EnergyWakeWordDetector<C: IAudioCapture, T: IVoiceTranscriber> {
    capture: C,
    transcriber: T,
    vad: EnergyVadDetector,
    wake_word: String,
}

impl<C: IAudioCapture, T: IVoiceTranscriber> EnergyWakeWordDetector<C, T> {
    /// Creates a detector.
    ///
    /// * `wake_word` — phrase to listen for; matching is case-insensitive and
    ///   substring-based (default `"hey b"`).
    /// * `energy_threshold` — RMS VAD threshold (default `0.02`).
    ///
    /// The internal VAD uses `silence_frames = 10`, `frame_size_bytes = 640`
    /// (matching the C# constructor).
    pub fn new(
        capture: C,
        transcriber: T,
        wake_word: impl Into<String>,
        energy_threshold: f32,
    ) -> Result<Self, VoiceError> {
        let wake_word = wake_word.into();
        let trimmed = wake_word.trim();
        if trimmed.is_empty() {
            return Err(VoiceError::Argument("wakeWord cannot be empty.".into()));
        }
        let vad = EnergyVadDetector::new(energy_threshold, 10, 640)?;
        Ok(Self {
            capture,
            transcriber,
            vad,
            wake_word: trimmed.to_string(),
        })
    }

    /// Creates a detector with the canonical `"hey b"` wake word and default
    /// `0.02` energy threshold.
    pub fn with_defaults(capture: C, transcriber: T) -> Result<Self, VoiceError> {
        Self::new(capture, transcriber, "hey b", 0.02)
    }
}

impl<C: IAudioCapture, T: IVoiceTranscriber> IWakeWordDetector for EnergyWakeWordDetector<C, T> {
    fn wake_word(&self) -> &str {
        &self.wake_word
    }

    fn scan(&self) -> Result<Vec<WakeWordDetectedEvent>, VoiceError> {
        let audio_stream = self.capture.capture()?;
        let segments = self.vad.detect(&audio_stream)?;
        let wake_lower = self.wake_word.to_lowercase();

        let mut detections = Vec::new();
        for segment in segments {
            if !segment.is_speech || segment.audio.is_empty() {
                continue;
            }
            // Transcription failure for one segment is non-fatal: skip it.
            let result = match self.transcriber.transcribe(&segment.audio) {
                Ok(r) => r,
                Err(_) => continue,
            };
            if result.text.trim().is_empty() {
                continue;
            }
            if result.text.to_lowercase().contains(&wake_lower) {
                detections.push(WakeWordDetectedEvent {
                    wake_word: self.wake_word.clone(),
                    detected_at: chrono::Utc::now(),
                    confidence: result.confidence,
                });
            }
        }
        Ok(detections)
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// VoicePipeline
// ─────────────────────────────────────────────────────────────────────────────

/// Composition of a wake-word detector, audio capture, transcriber, and
/// optionally a VAD and a TTS engine. Mirrors `VoicePipeline`.
///
/// On activation the pipeline captures audio, optionally filters it through VAD
/// (forwarding only `is_speech` segments), feeds the speech to the transcriber,
/// and returns the final [`TranscriptionResult`] (or `None` for silence/empty
/// audio — matching the C# behaviour of not raising the `Transcribed` event).
/// The TTS engine, if present, is exposed for the host to drive; the pipeline
/// never invokes TTS itself.
pub struct VoicePipeline<W, T, C>
where
    W: IWakeWordDetector,
    T: IVoiceTranscriber,
    C: IAudioCapture,
{
    wake: W,
    transcriber: T,
    capture: C,
    vad: Option<EnergyVadDetector>,
    tts: Option<Box<dyn ITtsEngine + Send + Sync>>,
}

impl<W, T, C> VoicePipeline<W, T, C>
where
    W: IWakeWordDetector,
    T: IVoiceTranscriber,
    C: IAudioCapture,
{
    /// Constructs a pipeline.
    ///
    /// * `vad` — when `Some`, raw audio is piped through it and only `is_speech`
    ///   segments reach the transcriber; when `None`, all captured audio is
    ///   forwarded directly.
    /// * `tts` — optional TTS engine, exposed via [`tts_engine`](Self::tts_engine).
    pub fn new(
        wake: W,
        transcriber: T,
        capture: C,
        vad: Option<EnergyVadDetector>,
        tts: Option<Box<dyn ITtsEngine + Send + Sync>>,
    ) -> Self {
        Self {
            wake,
            transcriber,
            capture,
            vad,
            tts,
        }
    }

    /// The wake-word detector this pipeline observes.
    pub fn wake_detector(&self) -> &W {
        &self.wake
    }

    /// The transcriber this pipeline drives.
    pub fn transcriber(&self) -> &T {
        &self.transcriber
    }

    /// The audio capture source this pipeline reads from.
    pub fn audio_capture(&self) -> &C {
        &self.capture
    }

    /// The optional TTS engine supplied at construction.
    pub fn tts_engine(&self) -> Option<&(dyn ITtsEngine + Send + Sync)> {
        self.tts.as_deref()
    }

    /// The optional VAD supplied at construction.
    pub fn voice_activity_detector(&self) -> Option<&EnergyVadDetector> {
        self.vad.as_ref()
    }

    /// Runs one activation: capture → (optional VAD filter) → transcribe →
    /// final result. Returns `None` when the transcriber produced no final
    /// result (silence/empty audio). Mirrors `RunActivationAsync` +
    /// `ToFinalAsync`.
    pub fn run_activation(&self) -> Result<Option<TranscriptionResult>, VoiceError> {
        let raw = self.capture.capture()?;

        // When VAD is configured, forward only speech segments; else forward
        // the raw capture directly.
        let audio_input: Vec<Vec<u8>> = match &self.vad {
            None => raw,
            Some(vad) => {
                let segments = vad.detect(&raw)?;
                segments
                    .into_iter()
                    .filter(|s| s.is_speech)
                    .map(|s| s.audio)
                    .collect()
            }
        };

        let partials = self.transcriber.stream_transcribe(&audio_input)?;
        Ok(Self::to_final(partials))
    }

    /// Drains the partial-transcription stream and returns the final result, or
    /// `None` if the stream is empty. Stops at the first `is_final` element.
    /// Mirrors `ToFinalAsync` — the final result's language is unknown at this
    /// layer, so `"und"` is used.
    fn to_final(partials: Vec<PartialTranscription>) -> Option<TranscriptionResult> {
        let mut last: Option<PartialTranscription> = None;
        for partial in partials {
            let is_final = partial.is_final;
            last = Some(partial);
            if is_final {
                break;
            }
        }
        last.map(|p| TranscriptionResult::new(p.text, p.confidence, "und"))
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    /// Capture that replays a fixed set of chunks.
    struct FixedCapture(Vec<Vec<u8>>);
    impl IAudioCapture for FixedCapture {
        fn format(&self) -> AudioFormat {
            AudioFormat::PCM16_MONO_16K
        }
        fn capture(&self) -> Result<Vec<Vec<u8>>, VoiceError> {
            Ok(self.0.clone())
        }
    }

    /// Transcriber that returns a fixed string.
    struct FixedTranscriber(String);
    impl IVoiceTranscriber for FixedTranscriber {
        fn transcribe(&self, _pcm: &[u8]) -> Result<TranscriptionResult, VoiceError> {
            Ok(TranscriptionResult::new(self.0.clone(), 0.9, "en"))
        }
        fn stream_transcribe(
            &self,
            _chunks: &[Vec<u8>],
        ) -> Result<Vec<PartialTranscription>, VoiceError> {
            Ok(vec![
                PartialTranscription::new("hey", false, 0.5),
                PartialTranscription::new(self.0.clone(), true, 0.9),
            ])
        }
    }

    fn loud_frame() -> Vec<u8> {
        // 640-byte frame of high-amplitude samples (0x40 0x40 => ~0.5 normalised).
        vec![0x00, 0x40].repeat(320)
    }
    fn silent_frame() -> Vec<u8> {
        vec![0u8; 640]
    }

    #[test]
    fn vad_emits_speech_segment_after_silence() {
        let vad = EnergyVadDetector::new(0.02, 2, 640).unwrap();
        let mut chunks = vec![loud_frame(), loud_frame()];
        // Trailing silence to trigger end-of-speech (>= 2 silent frames).
        chunks.push(silent_frame());
        chunks.push(silent_frame());
        let segs = vad.detect(&chunks).unwrap();
        assert_eq!(segs.len(), 1);
        assert!(segs[0].is_speech);
        assert!(!segs[0].audio.is_empty());
    }

    #[test]
    fn vad_flushes_mid_speech_on_stream_end() {
        let vad = EnergyVadDetector::new(0.02, 10, 640).unwrap();
        let segs = vad.detect(&[loud_frame(), loud_frame()]).unwrap();
        assert_eq!(segs.len(), 1);
    }

    #[test]
    fn wake_detector_matches_phrase() {
        let capture = FixedCapture(vec![loud_frame(), loud_frame()]);
        let transcriber = FixedTranscriber("okay Hey B please".into());
        let det =
            EnergyWakeWordDetector::new(capture, transcriber, "hey b", 0.02).unwrap();
        let hits = det.scan().unwrap();
        assert_eq!(hits.len(), 1);
        assert_eq!(hits[0].wake_word, "hey b");
    }

    #[test]
    fn pipeline_returns_final_transcription() {
        let capture = FixedCapture(vec![loud_frame()]);
        let pipeline = VoicePipeline::new(
            NullWakeStub,
            FixedTranscriber("hello world".into()),
            capture,
            None,
            None,
        );
        let result = pipeline.run_activation().unwrap().unwrap();
        assert_eq!(result.text, "hello world");
        assert_eq!(result.language_code, "und");
    }

    struct NullWakeStub;
    impl IWakeWordDetector for NullWakeStub {
        fn wake_word(&self) -> &str {
            "hey b"
        }
        fn scan(&self) -> Result<Vec<WakeWordDetectedEvent>, VoiceError> {
            Ok(Vec::new())
        }
    }
}
