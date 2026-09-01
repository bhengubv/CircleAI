//! The voice loop, WAV, and the telephony surface around a call.
//!
//! BARGE-IN IS NOT A FEATURE. It is the difference between a voice assistant
//! that works and one people stop using: without it the device keeps talking
//! over somebody who has started speaking, and there is no way to stop it but to
//! wait.
//!
//! A CALL IS A CONVERSATION WITH A PERSON WAITING, which changes what "good
//! enough" means. Silence over a phone line is not neutral - a second of it
//! reads as the call having dropped, and two seconds has somebody saying
//! "hello? hello?". Every latency decision in the telephony half is about that.

use std::collections::HashMap;

// ─────────────────────────────────────────────────────────────────────────────
// WAV

/// The shape of a block of PCM.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct WavFormat {
    pub sample_rate_hz: u32,
    pub channels: u16,
    pub bits_per_sample: u16,
}

impl Default for WavFormat {
    /// 16 kHz mono is what every speech model here wants. A transcriber fed
    /// 22050 does not fail - it hears the wrong speed and transcribes it
    /// confidently.
    fn default() -> Self {
        Self { sample_rate_hz: 16_000, channels: 1, bits_per_sample: 16 }
    }
}

/// WAV in both directions.
///
/// THE TWO SIZE FIELDS ARE DIFFERENT: the RIFF size is the whole file minus 8,
/// and the data size is the PCM bytes only. Getting either wrong produces a file
/// that plays in one program and not another - the worst kind of wrong, because
/// the first program you test in is usually the forgiving one.
///
/// Everything in a WAV header is LITTLE-endian, unlike PNG.
pub struct WavIo;

impl WavIo {
    pub fn header(format: WavFormat, data_bytes: u32) -> Vec<u8> {
        let block_align = format.channels * format.bits_per_sample / 8;
        let mut out = Vec::with_capacity(44);
        out.extend_from_slice(b"RIFF");
        out.extend_from_slice(&(36 + data_bytes).to_le_bytes());
        out.extend_from_slice(b"WAVEfmt ");
        out.extend_from_slice(&16u32.to_le_bytes());
        out.extend_from_slice(&1u16.to_le_bytes()); // PCM, uncompressed
        out.extend_from_slice(&format.channels.to_le_bytes());
        out.extend_from_slice(&format.sample_rate_hz.to_le_bytes());
        out.extend_from_slice(&(format.sample_rate_hz * block_align as u32).to_le_bytes());
        out.extend_from_slice(&block_align.to_le_bytes());
        out.extend_from_slice(&format.bits_per_sample.to_le_bytes());
        out.extend_from_slice(b"data");
        out.extend_from_slice(&data_bytes.to_le_bytes());
        out
    }

    /// Floats in -1..1 to 16-bit, CLAMPED not wrapped.
    ///
    /// A sample of 1.2 that wraps becomes a large negative number - a click at
    /// full scale, louder than anything else in the file. Scaled by 32767 rather
    /// than 32768 so +1.0 is representable and does not become the one value
    /// that wraps.
    pub fn write(format: WavFormat, samples: &[f32]) -> Vec<u8> {
        let mut body = Vec::with_capacity(samples.len() * 2);
        for s in samples {
            body.extend_from_slice(&((s.clamp(-1.0, 1.0) * 32767.0).round() as i16).to_le_bytes());
        }
        let mut out = Self::header(format, body.len() as u32);
        out.extend_from_slice(&body);
        out
    }

    /// Reads a WAV back.
    ///
    /// CHUNKS ARE WALKED, not assumed. A WAV from a recorder usually has a LIST
    /// or fact chunk between `fmt ` and `data`, and code that seeks to a fixed
    /// offset reads that metadata as audio - which plays as a burst of noise at
    /// the start of every file from that recorder.
    pub fn read(data: &[u8]) -> Option<(WavFormat, Vec<f32>)> {
        if data.len() < 12 || &data[0..4] != b"RIFF" || &data[8..12] != b"WAVE" {
            return None;
        }
        // Returns an OPTION, like its u32 sibling: a truncated header must
        // stop the parse rather than read past the end of the buffer.
        let u16_at = |i: usize| {
            Some(u16::from_le_bytes([*data.get(i)?, *data.get(i + 1)?]))
        };
        let u32_at = |i: usize| {
            Some(u32::from_le_bytes([
                *data.get(i)?,
                *data.get(i + 1)?,
                *data.get(i + 2)?,
                *data.get(i + 3)?,
            ]))
        };

        let mut p = 12usize;
        let mut format: Option<WavFormat> = None;
        let mut samples = Vec::new();
        while p + 8 <= data.len() {
            let kind = &data[p..p + 4];
            let size = u32_at(p + 4)? as usize;
            if kind == b"fmt " && size >= 16 {
                format = Some(WavFormat {
                    channels: u16_at(p + 10)?,
                    sample_rate_hz: u32_at(p + 12)?,
                    bits_per_sample: u16_at(p + 22)?,
                });
            } else if kind == b"data" {
                let available = size.min(data.len().saturating_sub(p + 8));
                samples = data[p + 8..p + 8 + available]
                    .chunks_exact(2)
                    .map(|c| i16::from_le_bytes([c[0], c[1]]) as f32 / 32768.0)
                    .collect();
            }
            // Chunks are WORD-ALIGNED: an odd-sized chunk is followed by a pad
            // byte that is not counted in its size. Skipping it puts every
            // subsequent chunk one byte out.
            p += 8 + size + (size & 1);
        }
        format.map(|f| (f, samples))
    }

    /// Linear resampling, and it is honest about being that.
    ///
    /// Good enough to feed a wake detector and NOT good enough to feed a
    /// transcriber trained on properly filtered audio - downsampling without a
    /// low-pass folds everything above the new Nyquist back into the band, which
    /// a model hears as noise it was never trained on.
    pub fn resample_linear(samples: &[f32], from_hz: u32, to_hz: u32) -> Vec<f32> {
        if from_hz == to_hz || samples.is_empty() {
            return samples.to_vec();
        }
        let ratio = from_hz as f64 / to_hz as f64;
        let count = ((samples.len() as f64 / ratio) as usize).max(1);
        (0..count)
            .map(|i| {
                let position = i as f64 * ratio;
                let left = position as usize;
                let frac = (position - left as f64) as f32;
                let right = (left + 1).min(samples.len() - 1);
                samples[left] * (1.0 - frac) + samples[right] * frac
            })
            .collect()
    }

    /// AVERAGED, not left-channel-only.
    ///
    /// Taking one channel loses anything panned away from it, and a phone's two
    /// microphones are the same voice with different noise rather than a stereo
    /// image.
    pub fn to_mono(samples: &[f32], channels: u16) -> Vec<f32> {
        if channels <= 1 {
            return samples.to_vec();
        }
        samples
            .chunks_exact(channels as usize)
            .map(|frame| frame.iter().sum::<f32>() / channels as f32)
            .collect()
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Engines the loop drives

/// Audio a voice produced.
#[derive(Debug, Clone, PartialEq)]
pub struct VoiceAudio {
    pub samples: Vec<f32>,
    pub sample_rate_hz: u32,
}

/// Plays audio.
pub trait AudioPlayer {
    fn is_available(&self) -> bool;
    fn play(&mut self, audio: &VoiceAudio) -> bool;
    fn stop(&mut self);
}

/// Plays nothing.
#[derive(Debug, Default, Clone, Copy)]
pub struct NullAudioPlayer;

impl AudioPlayer for NullAudioPlayer {
    fn is_available(&self) -> bool {
        false
    }
    fn play(&mut self, _audio: &VoiceAudio) -> bool {
        false
    }
    fn stop(&mut self) {}
}

/// Turns text into audio.
pub trait LoopTtsEngine {
    fn is_available(&self) -> bool;
    fn synthesize(&self, text: &str, language: &str) -> Option<VoiceAudio>;
}

/// PocketTTS, where the voice rides on the text input.
///
/// NaN marks the beginning of a sequence, and EOS is NOT a stop - the model
/// emits it and keeps going, so a caller that stops there truncates the last
/// word of every utterance.
pub struct PocketTtsEngine {
    #[allow(clippy::type_complexity)]
    run: Option<Box<dyn Fn(&[u32], &[f32]) -> Vec<f32> + Send + Sync>>,
    reference: Vec<f32>,
    sample_rate_hz: u32,
}

impl PocketTtsEngine {
    #[allow(clippy::type_complexity)]
    pub fn new(
        run: Option<Box<dyn Fn(&[u32], &[f32]) -> Vec<f32> + Send + Sync>>,
        reference: Vec<f32>,
        sample_rate_hz: u32,
    ) -> Self {
        Self { run, reference, sample_rate_hz }
    }

    /// The reference voice. WITHOUT ONE the model has nothing to sound like, so
    /// an engine with a loaded model and no reference is not available.
    pub fn has_reference(&self) -> bool {
        !self.reference.is_empty()
    }

    pub fn synthesize_tokens(&self, tokens: &[u32]) -> Option<VoiceAudio> {
        let run = self.run.as_ref()?;
        self.has_reference().then(|| VoiceAudio {
            samples: run(tokens, &self.reference),
            sample_rate_hz: self.sample_rate_hz,
        })
    }
}

impl LoopTtsEngine for PocketTtsEngine {
    fn is_available(&self) -> bool {
        self.run.is_some() && self.has_reference()
    }

    fn synthesize(&self, _text: &str, _language: &str) -> Option<VoiceAudio> {
        // Text has to be tokenised first, which is the caller's job - a
        // tokeniser guessed at here would be a different vocabulary from the
        // model's, and the model would receive noise.
        None
    }
}

/// A Toucan ONNX voice.
pub struct ToucanOnnxTtsEngine {
    run: Option<Box<dyn Fn(&str) -> Vec<f32> + Send + Sync>>,
    sample_rate_hz: u32,
}

impl ToucanOnnxTtsEngine {
    pub fn new(
        run: Option<Box<dyn Fn(&str) -> Vec<f32> + Send + Sync>>,
        sample_rate_hz: u32,
    ) -> Self {
        Self { run, sample_rate_hz }
    }
}

impl LoopTtsEngine for ToucanOnnxTtsEngine {
    fn is_available(&self) -> bool {
        self.run.is_some()
    }

    fn synthesize(&self, text: &str, _language: &str) -> Option<VoiceAudio> {
        let run = self.run.as_ref()?;
        Some(VoiceAudio { samples: run(text), sample_rate_hz: self.sample_rate_hz })
    }
}

/// Transcribes audio.
pub trait LoopTranscriber {
    fn is_available(&self) -> bool;
    fn transcribe(&self, samples: &[f32], sample_rate_hz: u32, language: &str) -> Option<String>;
}

/// Whisper through a host binding.
///
/// THE RATE CHECK IS THE POINT. Whisper wants 16 kHz mono, and feeding it 22050
/// does not fail - it transcribes audio it believes is slower than it is and
/// produces confident nonsense.
pub struct WhisperTranscriber {
    #[allow(clippy::type_complexity)]
    run: Option<Box<dyn Fn(&[f32], &str) -> String + Send + Sync>>,
    language: String,
}

impl WhisperTranscriber {
    pub const REQUIRED_RATE_HZ: u32 = 16_000;

    #[allow(clippy::type_complexity)]
    pub fn new(
        run: Option<Box<dyn Fn(&[f32], &str) -> String + Send + Sync>>,
        language: String,
    ) -> Self {
        Self { run, language }
    }

    /// Downmixes and resamples to what the model needs.
    pub fn prepare(&self, samples: &[f32], sample_rate_hz: u32, channels: u16) -> Vec<f32> {
        let mono = WavIo::to_mono(samples, channels);
        if sample_rate_hz == Self::REQUIRED_RATE_HZ {
            mono
        } else {
            WavIo::resample_linear(&mono, sample_rate_hz, Self::REQUIRED_RATE_HZ)
        }
    }
}

impl LoopTranscriber for WhisperTranscriber {
    fn is_available(&self) -> bool {
        self.run.is_some()
    }

    fn transcribe(&self, samples: &[f32], sample_rate_hz: u32, language: &str) -> Option<String> {
        let run = self.run.as_ref()?;
        let prepared = self.prepare(samples, sample_rate_hz, 1);
        Some(run(
            &prepared,
            if language.is_empty() { &self.language } else { language },
        ))
    }
}

/// The managed binding, which also needs a model file.
pub struct WhisperNetTranscriber {
    inner: WhisperTranscriber,
    pub model_path: String,
}

impl WhisperNetTranscriber {
    #[allow(clippy::type_complexity)]
    pub fn new(
        run: Option<Box<dyn Fn(&[f32], &str) -> String + Send + Sync>>,
        language: String,
        model_path: String,
    ) -> Self {
        Self { inner: WhisperTranscriber::new(run, language), model_path }
    }
}

impl LoopTranscriber for WhisperNetTranscriber {
    /// Needs BOTH a model file and a binding. Either alone is a transcriber that
    /// reports ready and then fails on the first call.
    fn is_available(&self) -> bool {
        self.inner.is_available() && !self.model_path.is_empty()
    }

    fn transcribe(&self, samples: &[f32], sample_rate_hz: u32, language: &str) -> Option<String> {
        self.is_available()
            .then(|| self.inner.transcribe(samples, sample_rate_hz, language))
            .flatten()
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// The loop

/// One exchange: what was heard, and what was said back.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct VoiceExchangeEvent {
    pub heard: String,
    pub said: String,
    pub listen_ms: u64,
    pub think_ms: u64,
    pub speak_ms: u64,
    pub was_barged_in: bool,
}

/// A partial or settled transcript, as the pipeline produces it.
#[derive(Debug, Clone, PartialEq, Default)]
pub struct TranscribedEvent {
    pub text: String,
    /// Whether this is the settled version. A consumer that treats a partial as
    /// final commits to a sentence the recogniser is still revising.
    pub is_final: bool,
    /// `None` when the engine did not say. Zero is a real answer meaning "no
    /// idea".
    pub confidence: Option<f32>,
    pub at_ms: u64,
}

/// Where the time went in one exchange.
#[derive(Debug, Default, Clone)]
pub struct VoiceTrace {
    marks: HashMap<String, u64>,
    spans: Vec<(String, u64)>,
}

impl VoiceTrace {
    pub fn new() -> Self {
        Self::default()
    }

    pub fn start(&mut self, name: &str, now_ms: u64) {
        self.marks.insert(name.to_string(), now_ms);
    }

    pub fn end(&mut self, name: &str, now_ms: u64) -> u64 {
        let Some(started) = self.marks.remove(name) else { return 0 };
        let ms = now_ms.saturating_sub(started);
        self.spans.push((name.to_string(), ms));
        ms
    }

    /// The slowest span, which is what to fix.
    ///
    /// A total tells you it was slow; the breakdown tells you whether it was the
    /// microphone, the model or the voice - and those are three different jobs.
    pub fn slowest(&self) -> Option<(&str, u64)> {
        self.spans
            .iter()
            .max_by_key(|(_, ms)| *ms)
            .map(|(name, ms)| (name.as_str(), *ms))
    }

    pub fn total_ms(&self) -> u64 {
        self.spans.iter().map(|(_, ms)| ms).sum()
    }

    pub fn summary(&self) -> String {
        if self.spans.is_empty() {
            return "nothing timed".into();
        }
        self.spans
            .iter()
            .map(|(name, ms)| format!("{name} {ms}ms"))
            .collect::<Vec<_>>()
            .join(", ")
    }
}

/// Listen, think, speak - and be interruptible throughout.
pub struct VoiceLoop<T: LoopTranscriber, E: LoopTtsEngine, P: AudioPlayer> {
    transcriber: T,
    tts: E,
    player: P,
    respond: Option<Box<dyn Fn(&str) -> String + Send + Sync>>,
    speaking: bool,
    barged_in: bool,
}

impl<T: LoopTranscriber, E: LoopTtsEngine, P: AudioPlayer> VoiceLoop<T, E, P> {
    pub fn new(
        transcriber: T,
        tts: E,
        player: P,
        respond: Option<Box<dyn Fn(&str) -> String + Send + Sync>>,
    ) -> Self {
        Self { transcriber, tts, player, respond, speaking: false, barged_in: false }
    }

    pub fn is_speaking(&self) -> bool {
        self.speaking
    }

    /// Stops the voice immediately. Safe to call when nothing is speaking.
    pub fn barge_in(&mut self) {
        if !self.speaking {
            return;
        }
        self.barged_in = true;
        self.player.stop();
        self.speaking = false;
    }

    pub fn exchange(
        &mut self,
        samples: &[f32],
        sample_rate_hz: u32,
        language: &str,
        now_ms: u64,
    ) -> VoiceExchangeEvent {
        let mut trace = VoiceTrace::new();
        self.barged_in = false;

        trace.start("listen", now_ms);
        let heard = self
            .transcriber
            .transcribe(samples, sample_rate_hz, language)
            .unwrap_or_default();
        let listen_ms = trace.end("listen", now_ms);

        trace.start("think", now_ms);
        let said = match (&self.respond, heard.trim().is_empty()) {
            (Some(respond), false) => respond(&heard),
            _ => String::new(),
        };
        let think_ms = trace.end("think", now_ms);

        trace.start("speak", now_ms);
        if !said.is_empty() && self.tts.is_available() && self.player.is_available() {
            self.speaking = true;
            if let Some(audio) = self.tts.synthesize(&said, language) {
                // Checked AGAIN after synthesis: somebody can barge in while the
                // voice is still being generated, and playing it anyway is
                // exactly the behaviour barge-in exists to prevent.
                if !self.barged_in {
                    self.player.play(&audio);
                }
            }
            self.speaking = false;
        }
        let speak_ms = trace.end("speak", now_ms);

        VoiceExchangeEvent {
            heard,
            said,
            listen_ms,
            think_ms,
            speak_ms,
            was_barged_in: self.barged_in,
        }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Telephony

/// Something that happened during a call.
#[derive(Debug, Clone, PartialEq)]
pub enum SpeechLifecycleEvent {
    /// The caller began talking.
    CallerSpeechStarted { call_id: String, at_ms: u64 },
    /// The caller stopped. NOT the same as end of turn: stopping making noise
    /// and having finished a sentence are different facts.
    CallerSpeechEnded { call_id: String, at_ms: u64 },
    /// A partial transcript. Deltas REPLACE each other for an utterance; a
    /// consumer that appends renders the sentence growing by duplication.
    TranscriptInterim { call_id: String, text: String, at_ms: u64 },
    /// The settled transcript, with a word-level breakdown the first version of
    /// this event did not carry - which is why the type keeps its `_v2` suffix
    /// rather than being renamed and hiding that two shapes exist on the wire.
    TranscriptFinalV2 {
        call_id: String,
        text: String,
        confidence: Option<f32>,
        words: Vec<(String, u64, u64)>,
        at_ms: u64,
    },
    /// The assistant is working. Emitted so a filler can start BEFORE the answer
    /// exists - silence on a phone line reads as the call having dropped.
    AgentThinking { call_id: String, at_ms: u64 },
    AgentSpeakingStarted { call_id: String, at_ms: u64 },
    AgentSpeakingFinished { call_id: String, at_ms: u64 },
    /// Something went wrong.
    SpeechError {
        call_id: String,
        code: String,
        message: String,
        /// Whether the call survives. A recoverable error and a dead call demand
        /// opposite reactions.
        fatal: bool,
    },
}

/// A subscription that can be cancelled.
pub trait SpeechSubscription {
    fn cancel(&mut self);
    fn is_active(&self) -> bool;
}

/// Carries lifecycle events to whoever is listening.
pub trait SpeechLifecycleBus {
    fn publish(&mut self, event: SpeechLifecycleEvent);
    fn drain(&mut self) -> Vec<SpeechLifecycleEvent>;
}

/// The default bus.
#[derive(Debug, Default)]
pub struct InMemorySpeechLifecycleBus {
    events: Vec<SpeechLifecycleEvent>,
    /// A cap, because a long call on a busy line produces thousands of interim
    /// transcripts and nothing consumes them all.
    max_events: usize,
}

impl InMemorySpeechLifecycleBus {
    pub fn new(max_events: usize) -> Self {
        Self { events: Vec::new(), max_events: if max_events == 0 { 500 } else { max_events } }
    }
}

impl SpeechLifecycleBus for InMemorySpeechLifecycleBus {
    fn publish(&mut self, event: SpeechLifecycleEvent) {
        self.events.push(event);
        while self.events.len() > self.max_events {
            self.events.remove(0);
        }
    }

    fn drain(&mut self) -> Vec<SpeechLifecycleEvent> {
        std::mem::take(&mut self.events)
    }
}

/// Carries audio to and from a call.
pub trait MediaStream {
    fn is_open(&self) -> bool;
    fn send(&mut self, pcm: &[u8]) -> bool;
    fn close(&mut self);
}

/// What the assistant says first.
///
/// A PREAMBLE IS NOT A GREETING, it is a disclosure. Somebody who has just been
/// answered by a machine needs to know that within the first sentence, and a
/// preamble that opens with "how can I help" has taken the choice away from
/// them.
pub trait FirstMessagePreamble {
    fn preamble(&self, language: &str) -> String;
}

/// The default preamble.
#[derive(Debug, Default, Clone, Copy)]
pub struct DefaultFirstMessagePreamble;

impl FirstMessagePreamble for DefaultFirstMessagePreamble {
    /// SAYS IT IS A MACHINE, FIRST. Everything else can wait; that cannot.
    fn preamble(&self, language: &str) -> String {
        match language.split(['-', '_']).next().unwrap_or("en") {
            "af" => "Hallo, jy praat met 'n rekenaar. Hoe kan ek help?".into(),
            "zu" => "Sawubona, ukhuluma nomshini. Ngingakusiza ngani?".into(),
            "xh" => "Molo, uthetha nomatshini. Ndingakunceda njani?".into(),
            _ => "Hello, you're speaking to a machine. How can I help?".into(),
        }
    }
}

/// Something to say while the assistant is thinking.
///
/// SILENCE ON A PHONE LINE IS NOT NEUTRAL. A second of it reads as the call
/// having dropped, and two has somebody saying "hello? hello?". A filler is what
/// keeps the line feeling alive.
pub trait ReassuranceFiller {
    /// `None` when the answer arrived quickly enough that nothing is needed -
    /// filling a gap that did not exist makes the assistant sound hesitant.
    fn filler(&self, waited_ms: u64, language: &str) -> Option<String>;
}

/// The default filler.
#[derive(Debug, Clone, Copy)]
pub struct DefaultReassuranceFiller {
    /// Below this, say nothing. Above it, one short phrase - and only one, since
    /// a second filler while still thinking is worse than the silence.
    pub threshold_ms: u64,
}

impl Default for DefaultReassuranceFiller {
    fn default() -> Self {
        Self { threshold_ms: 800 }
    }
}

impl ReassuranceFiller for DefaultReassuranceFiller {
    fn filler(&self, waited_ms: u64, language: &str) -> Option<String> {
        if waited_ms < self.threshold_ms {
            return None;
        }
        Some(
            match language.split(['-', '_']).next().unwrap_or("en") {
                "af" => "Net 'n oomblik...".into(),
                "zu" => "Ake ngibheke...".into(),
                "xh" => "Ndiyajonga...".into(),
                _ => "Let me check...".to_string(),
            },
        )
    }
}

/// One branch of a speculative generation.
#[derive(Debug, Clone, PartialEq)]
pub struct SpeculativeBranch {
    /// What the caller was PREDICTED to say. Speculation starts before they have
    /// finished, so this is a guess and is named as one.
    pub predicted_utterance: String,
    pub response: String,
    pub confidence: f32,
}

/// Starts generating before the caller has finished.
///
/// THE POINT IS LATENCY, and the risk is answering a question nobody asked. A
/// branch is only used when the settled transcript MATCHES what was predicted -
/// close is not good enough, because a near-match is a different question.
pub trait SpeculativeGenerator {
    fn is_available(&self) -> bool;
    fn speculate(&self, interim: &str) -> Vec<SpeculativeBranch>;
    /// Returns the branch whose prediction the final transcript confirms, or
    /// `None` - which means generating properly, having lost nothing but the
    /// speculative work.
    fn resolve(&self, branches: &[SpeculativeBranch], final_text: &str) -> Option<SpeculativeBranch>;
}

/// The default resolver.
#[derive(Debug, Default, Clone, Copy)]
pub struct ExactMatchSpeculativeGenerator;

impl ExactMatchSpeculativeGenerator {
    /// Normalised for comparison: case folded, punctuation dropped, spaces
    /// collapsed. A transcript that differs only in a comma is the same
    /// sentence.
    pub fn normalise(text: &str) -> String {
        text.to_lowercase()
            .chars()
            .map(|c| if c.is_alphanumeric() || c.is_whitespace() { c } else { ' ' })
            .collect::<String>()
            .split_whitespace()
            .collect::<Vec<_>>()
            .join(" ")
    }
}

impl SpeculativeGenerator for ExactMatchSpeculativeGenerator {
    fn is_available(&self) -> bool {
        true
    }

    fn speculate(&self, _interim: &str) -> Vec<SpeculativeBranch> {
        Vec::new()
    }

    fn resolve(
        &self,
        branches: &[SpeculativeBranch],
        final_text: &str,
    ) -> Option<SpeculativeBranch> {
        let wanted = Self::normalise(final_text);
        branches
            .iter()
            .find(|b| Self::normalise(&b.predicted_utterance) == wanted)
            .cloned()
    }
}

/// Hands a call to a person.
///
/// THE HANDOFF CARRIES THE TRANSCRIPT. A caller who has explained their problem
/// to a machine and is then asked to explain it again has been treated worse
/// than if the machine had not answered.
pub trait AgentHandoffOrchestrator {
    fn is_available(&self) -> bool;
    fn hand_off(&self, call_id: &str, transcript: &str, reason: &str) -> Result<String, String>;
}

/// Hands a call to a person WHILE STAYING ON THE LINE.
///
/// A warm transfer means the assistant introduces the caller to whoever picks up
/// before dropping out. A cold transfer - hanging up and hoping - is what makes
/// people distrust an automated line.
pub trait WarmTransferOrchestrator {
    fn is_available(&self) -> bool;
    fn begin(&self, call_id: &str, to_number_e164: &str, summary: &str) -> Result<String, String>;
    /// Only after somebody has actually answered. Completing before that drops
    /// the caller into silence.
    fn complete(&self, transfer_id: &str, answered: bool) -> bool;
}

/// Brings tools in from the tool protocol during a call.
pub trait McpToolImporter {
    fn is_available(&self) -> bool;
    /// Only tools that are allowed AND fast enough to run inside a call. A tool
    /// that takes ten seconds is a tool that empties a phone line.
    fn importable(&self, max_latency_ms: u64) -> Vec<String>;
}

/// The voice loop offered as a tool the model can call.
pub trait VoiceLoopTool {
    fn name(&self) -> &str;
    fn is_available(&self) -> bool;
    fn invoke(&self, arguments: &HashMap<String, String>) -> Result<String, String>;
}

/// Speaks tool progress aloud.
///
/// RATE-LIMITED, and the final update is never dropped. A tool that reports
/// every step would have the assistant narrating a progress bar down a phone
/// line; one that reports nothing leaves silence, which reads as a dropped call.
pub struct SpokenToolProgressSink {
    speak: Option<Box<dyn Fn(&str) + Send + Sync>>,
    min_interval_ms: u64,
    last_spoken_ms: u64,
}

impl SpokenToolProgressSink {
    pub fn new(speak: Option<Box<dyn Fn(&str) + Send + Sync>>, min_interval_ms: u64) -> Self {
        Self {
            speak,
            min_interval_ms: if min_interval_ms == 0 { 4000 } else { min_interval_ms },
            last_spoken_ms: 0,
        }
    }

    pub fn report(&mut self, message: &str, is_final: bool, now_ms: u64) -> bool {
        let Some(speak) = &self.speak else { return false };
        if !is_final && now_ms.saturating_sub(self.last_spoken_ms) < self.min_interval_ms {
            return false;
        }
        self.last_spoken_ms = now_ms;
        speak(message);
        true
    }
}

/// What a dashboard shows about calls.
#[derive(Debug, Clone, PartialEq, Default)]
pub struct DashboardSnapshot {
    pub active_calls: usize,
    pub calls_today: usize,
    pub median_answer_ms: u64,
    pub handoff_rate: f32,
}

/// Supplies dashboard data.
pub trait DashboardDataSource {
    fn snapshot(&self) -> DashboardSnapshot;
}

/// The default source.
#[derive(Debug, Default, Clone)]
pub struct DefaultDashboardDataSource {
    answer_times_ms: Vec<u64>,
    active: usize,
    today: usize,
    handoffs: usize,
}

impl DefaultDashboardDataSource {
    pub fn new() -> Self {
        Self::default()
    }

    pub fn record_answer(&mut self, ms: u64) {
        self.answer_times_ms.push(ms);
        self.today += 1;
    }

    pub fn record_handoff(&mut self) {
        self.handoffs += 1;
    }

    pub fn set_active(&mut self, active: usize) {
        self.active = active;
    }
}

impl DashboardDataSource for DefaultDashboardDataSource {
    fn snapshot(&self) -> DashboardSnapshot {
        // The MEDIAN, not the mean. One call that hung for a minute drags a mean
        // into uselessness and leaves a median where it was.
        let mut sorted = self.answer_times_ms.clone();
        sorted.sort_unstable();
        let median = match sorted.len() {
            0 => 0,
            n if n % 2 == 1 => sorted[n / 2],
            n => (sorted[n / 2 - 1] + sorted[n / 2]) / 2,
        };
        DashboardSnapshot {
            active_calls: self.active,
            calls_today: self.today,
            median_answer_ms: median,
            handoff_rate: if self.today == 0 {
                0.0
            } else {
                self.handoffs as f32 / self.today as f32
            },
        }
    }
}

/// A public address for a device behind a NAT, during development.
pub trait LocalDevTunnel {
    fn is_available(&self) -> bool;
    fn public_url(&self) -> &str;
    fn open(&mut self, local_port: u16) -> Option<String>;
    fn close(&mut self);
}

/// ngrok.
///
/// A tunnel puts a development machine on the public internet, which is why
/// there is no default that starts one.
pub struct NgrokTunnel {
    run: Option<Box<dyn Fn(u16) -> Option<String> + Send + Sync>>,
    url: String,
}

impl NgrokTunnel {
    pub fn new(run: Option<Box<dyn Fn(u16) -> Option<String> + Send + Sync>>) -> Self {
        Self { run, url: String::new() }
    }
}

impl LocalDevTunnel for NgrokTunnel {
    fn is_available(&self) -> bool {
        self.run.is_some()
    }
    fn public_url(&self) -> &str {
        &self.url
    }
    fn open(&mut self, local_port: u16) -> Option<String> {
        self.url = (self.run.as_ref()?)(local_port)?;
        Some(self.url.clone())
    }
    fn close(&mut self) {
        self.url.clear();
    }
}

/// Cloudflare.
pub struct CloudflareTunnel {
    run: Option<Box<dyn Fn(u16) -> Option<String> + Send + Sync>>,
    url: String,
}

impl CloudflareTunnel {
    pub fn new(run: Option<Box<dyn Fn(u16) -> Option<String> + Send + Sync>>) -> Self {
        Self { run, url: String::new() }
    }
}

impl LocalDevTunnel for CloudflareTunnel {
    fn is_available(&self) -> bool {
        self.run.is_some()
    }
    fn public_url(&self) -> &str {
        &self.url
    }
    fn open(&mut self, local_port: u16) -> Option<String> {
        self.url = (self.run.as_ref()?)(local_port)?;
        Some(self.url.clone())
    }
    fn close(&mut self) {
        self.url.clear();
    }
}

/// Wires the telephony surface.
pub struct TelephonyRegistration {
    registered: HashMap<String, String>,
}

impl Default for TelephonyRegistration {
    fn default() -> Self {
        Self::new()
    }
}

impl TelephonyRegistration {
    pub fn new() -> Self {
        Self { registered: HashMap::new() }
    }

    pub fn add(&mut self, name: &str, value: &str) -> &mut Self {
        self.registered.insert(name.to_string(), value.to_string());
        self
    }

    pub fn get(&self, name: &str) -> Option<&String> {
        self.registered.get(name)
    }

    pub fn names(&self) -> Vec<String> {
        let mut out: Vec<String> = self.registered.keys().cloned().collect();
        out.sort();
        out
    }
}
