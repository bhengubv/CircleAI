//! telephony.rs
//!
//! Port of `CircleAI.Telephony/` (3.3.0) — the carrier-agnostic voice-loop
//! surface: the [`ITelephonyCarrier`]/[`ICallSession`] contracts, plus the real
//! DSP + orchestration primitives a production voice agent needs (DTMF tone
//! generation, answering-machine detection, barge-in, cost accounting, sentence
//! chunking, latency percentiles, IVR-loop detection, guardrails, hold-music
//! mixing, stereo call recording, tool-calling + circuit breaker, warm transfer,
//! agent handoff, speculative generation, eval + LLM-judge, and more).
//!
//! C# → Rust map (highlights):
//!   * Enums/records (`CallDirection`, `CallStatus`, `CallMediaFormat`,
//!     `TransferMode`, `CallInfo`, `AudioFrame`, `DtmfEvent`, …) → same-named
//!     types.
//!   * Carrier + session contracts (`ITelephonyCarrier`, `ICallSession`,
//!     `IInboundCallDispatcher`, `IMediaStream`, `IDtmfSendable`) →
//!     `#[async_trait]` traits with an associated `Error: std::error::Error`.
//!   * Pure DSP (`DtmfToneGenerator`, `AnsweringMachineDetector`,
//!     `HoldMusicMixer`, `StereoCallRecorder`) → ported verbatim (arithmetic 1:1).
//!   * Orchestration state machines (`BargeInController`, `IvrLoopDetector`,
//!     `SpeculativeGenerator`, `CircuitBreakerToolRegistry`) → same behaviour,
//!     with the C# `Func<DateTimeOffset>` clock kept as an injected closure.
//!   * The HTTP-backed pieces (`HttpWebhookConsultChannel`, `HttpMcpToolImporter`,
//!     the webhook branch of `DefaultToolCallRegistry`) → the network call is the
//!     injected boundary ([`IHttpJsonClient`]); the request/response *shaping*
//!     logic is ported, and an in-memory client ships for tests.
//!
//! Notes on constructs that did not map 1:1:
//!   * `decimal` money → `f64`; `TimeSpan`/`DateTimeOffset` →
//!     `chrono::Duration`/`chrono::DateTime<Utc>`; `ReadOnlyMemory<byte>`/
//!     `Span<byte>` → `Vec<u8>`/`&[u8]`/`&mut [u8]`.
//!   * `ILogger` is dropped (idiomatic Rust surfaces failures via `Result`).
//!   * `IServiceCollection` DI extensions (`ServiceCollectionExtensions.cs`) are
//!     framework glue, not portable logic; the useful part — the multi-carrier
//!     failover — is ported as [`CarrierFallback`].
//!   * `System.Diagnostics.ActivitySource` (OpenTelemetry) → the lightweight
//!     [`VoiceLoopSpan`] value + [`VoiceLoopTelemetry`] factory; there is no OTel
//!     dependency, so a host observes spans by inspecting the returned value.
//!   * `IAsyncEnumerable<T>` streams (`ReceiveAudioAsync`/`ReceiveDtmfAsync`) →
//!     `drain_*` methods returning the buffered `Vec<T>` on [`TestCallSession`]
//!     (the in-memory session), with the trait exposing pull-style
//!     `receive_audio`/`receive_dtmf`.
//!   * `SpeechLifecycleEvent` (an abstract-record hierarchy dispatched by runtime
//!     type) → the [`SpeechLifecycleEvent`] enum + a typed-filter subscription bus.

use std::collections::HashMap;
use std::convert::Infallible;
use std::f64::consts::PI;
use std::fmt;
use std::sync::atomic::{AtomicI64, Ordering};
use std::sync::{Arc, Mutex};

use async_trait::async_trait;
use chrono::{DateTime, Duration, Utc};
use regex::Regex;

// ═════════════════════════════════════════════════════════════════════════════
// Primitives.cs — enums + value records
// ═════════════════════════════════════════════════════════════════════════════

/// (3.3.0) Call direction.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum CallDirection {
    Inbound,
    Outbound,
}

/// (3.3.0) Call lifecycle states.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum CallStatus {
    /// Carrier accepted the dial but the other end has not picked up yet.
    Ringing,
    /// Both sides connected; media flowing.
    Active,
    /// Caller hung up.
    EndedByCaller,
    /// Callee hung up.
    EndedByCallee,
    /// AI agent (us) ended the call.
    EndedByAgent,
    /// Carrier-detected voicemail / answering machine on outbound dial.
    Voicemail,
    /// Call did not connect (busy, no answer, network).
    Failed,
    /// Call transferred to a human or a different agent.
    Transferred,
}

/// (3.3.0) Audio wire formats supported across carriers.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum CallMediaFormat {
    /// µ-law 8 kHz mono — Twilio/Plivo default, fallback Telnyx.
    Mulaw8000,
    /// A-law 8 kHz mono — some European carriers.
    Alaw8000,
    /// Linear PCM 16-bit 16 kHz mono — Telnyx negotiated path.
    Pcm16000,
    /// Linear PCM 16-bit 24 kHz mono — high-quality WebRTC, OpenAI Realtime.
    Pcm24000,
}

/// (3.3.0) Transfer mode the AI requests from the carrier.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum TransferMode {
    /// Drop the caller into the new line and hang up — fast, no context handover.
    Cold,
    /// Park caller, dial human, brief human verbally, then bridge both.
    Warm,
}

/// (3.3.0) Information about one call. Captured once at call start, immutable.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct CallInfo {
    /// Carrier-supplied unique id (Twilio CallSid, Telnyx call_control_id, …).
    pub call_id: String,
    pub direction: CallDirection,
    /// Caller's phone number in E.164 format (e.g. +27821234567).
    pub from: String,
    /// Called party's phone number in E.164 format.
    pub to: String,
    pub carrier_id: String,
    pub media_format: CallMediaFormat,
    pub started_at_utc: DateTime<Utc>,
}

/// (3.3.0) A snapshot of a call's current state. `cost_so_far` is a per-second
/// cost figure (`decimal` → `f64`).
#[derive(Debug, Clone, PartialEq)]
pub struct CallSnapshot {
    pub info: CallInfo,
    pub status: CallStatus,
    pub duration: Duration,
    pub cost_so_far: f64,
    pub transfer_target: Option<String>,
}

/// (3.3.0) Audio chunk flowing from caller → AI or AI → caller. `pcm` is the raw
/// little-endian PCM payload for `format`.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct AudioFrame {
    pub pcm: Vec<u8>,
    pub format: CallMediaFormat,
    pub offset: Duration,
}

impl AudioFrame {
    pub fn new(pcm: Vec<u8>, format: CallMediaFormat, offset: Duration) -> Self {
        Self { pcm, format, offset }
    }
}

/// (3.3.0) DTMF tone from the caller.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct DtmfEvent {
    /// The digit (0-9, *, #).
    pub digit: char,
    /// How long the caller held it.
    pub duration: Duration,
    /// When (relative to call start).
    pub offset: Duration,
}

/// (3.3.0) Result of a number-provisioning request.
#[derive(Debug, Clone, PartialEq)]
pub struct ProvisionedNumber {
    pub phone_number: String,
    pub carrier_id: String,
    pub provisioned_at_utc: DateTime<Utc>,
    pub monthly_recurring_cost: f64,
}

// ═════════════════════════════════════════════════════════════════════════════
// TelephonyError
// ═════════════════════════════════════════════════════════════════════════════

/// Failure surface for the telephony orchestration + carrier contracts. Covers
/// the C# `ArgumentException`/`InvalidOperationException`/`NotSupportedException`
/// guard rails. Carrier / HTTP backends wire their own error into the trait
/// associated `Error`; this enum is the surface for the in-crate logic.
#[derive(Debug)]
pub enum TelephonyError {
    /// A required argument was null / empty / whitespace / out of range.
    InvalidArgument(String),
    /// An operation was attempted in an unsupported / not-configured state.
    InvalidOperation(String),
    /// A carrier / backend refused the operation.
    NotSupported(String),
}

impl fmt::Display for TelephonyError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            TelephonyError::InvalidArgument(m) => write!(f, "invalid argument: {m}"),
            TelephonyError::InvalidOperation(m) => write!(f, "invalid operation: {m}"),
            TelephonyError::NotSupported(m) => write!(f, "not supported: {m}"),
        }
    }
}

impl std::error::Error for TelephonyError {}

// ═════════════════════════════════════════════════════════════════════════════
// Contracts.cs / IMediaStream.cs / IDtmfSendable.cs — carrier + session surface
// ═════════════════════════════════════════════════════════════════════════════

/// (3.3.0) Optional knobs for an outbound dial.
#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct OutboundDialOptions {
    /// If true, detect voicemail and surface [`CallStatus::Voicemail`].
    pub detect_answering_machine: bool,
    /// How long to ring before treating it as no-answer. Default 30 s.
    pub ring_timeout_seconds: i32,
    /// Optional caller-id override (must be a number you own).
    pub caller_id_override: Option<String>,
    /// Optional list of E.164 numbers to also dial if the primary doesn't answer.
    pub follow_me_numbers: Option<Vec<String>>,
}

impl OutboundDialOptions {
    /// The C# default (`RingTimeoutSeconds = 30`).
    pub fn defaults() -> Self {
        Self {
            detect_answering_machine: false,
            ring_timeout_seconds: 30,
            caller_id_override: None,
            follow_me_numbers: None,
        }
    }
}

/// (3.3.0) Carrier integration — where CircleAI talks to a phone-network
/// operator (Twilio, Telnyx, Plivo, or a SIP gateway).
#[async_trait]
pub trait ITelephonyCarrier {
    type Error: std::error::Error;
    /// The [`ICallSession`] type this carrier produces.
    type Session: ICallSession;

    /// Stable carrier id — "twilio" / "telnyx" / "plivo" / "null".
    fn carrier_id(&self) -> &str;

    /// True when the carrier has the credentials + base addresses it needs.
    fn is_configured(&self) -> bool;

    /// Buy a new phone number for the given ISO country code.
    async fn provision_number(
        &self,
        country_code: &str,
        area_code: Option<&str>,
    ) -> Result<ProvisionedNumber, Self::Error>;

    /// Route inbound calls on a number we own to our WebSocket endpoint.
    async fn configure_inbound_webhook(
        &self,
        phone_number: &str,
        inbound_webhook: &str,
    ) -> Result<(), Self::Error>;

    /// Place an outbound call. `stream_url` is where the carrier streams media.
    async fn dial(
        &self,
        from_number: &str,
        to_number: &str,
        stream_url: &str,
        options: Option<OutboundDialOptions>,
    ) -> Result<Self::Session, Self::Error>;

    /// List the numbers we own on this carrier.
    async fn list_numbers(&self) -> Result<Vec<ProvisionedNumber>, Self::Error>;
}

/// (3.3.0) Live call session. The agent talks to this — carrier-agnostic.
#[async_trait]
pub trait ICallSession: Send + Sync {
    type Error: std::error::Error;

    /// Stable carrier-supplied info captured at call start.
    fn info(&self) -> &CallInfo;

    /// Current lifecycle status.
    fn status(&self) -> CallStatus;

    /// Pull any audio frames that have arrived from the caller.
    async fn receive_audio(&self) -> Result<Vec<AudioFrame>, Self::Error>;

    /// Send an audio frame to the caller.
    async fn send_audio(&self, frame: AudioFrame) -> Result<(), Self::Error>;

    /// Pull any DTMF tones the caller has pressed.
    async fn receive_dtmf(&self) -> Result<Vec<DtmfEvent>, Self::Error>;

    /// Send DTMF tones from the AI side (for navigating other people's menus).
    async fn send_dtmf(&self, digits: &str) -> Result<(), Self::Error>;

    /// Transfer the call to `target_number`.
    async fn transfer(
        &self,
        target_number: &str,
        mode: TransferMode,
        briefing: Option<&str>,
    ) -> Result<(), Self::Error>;

    /// End the call from our side.
    async fn hang_up(&self) -> Result<(), Self::Error>;
}

/// (3.3.0) Inbound webhook dispatcher — materialises an [`ICallSession`] for the
/// agent to attach to when a call arrives.
pub trait IInboundCallDispatcher {
    type Session: ICallSession;

    /// Stable id of the carrier feeding inbound calls into this dispatcher.
    fn carrier_id(&self) -> &str;

    /// Register a handler invoked for each inbound session.
    fn subscribe(&self, handler: Box<dyn Fn(Self::Session) + Send + Sync>) -> TelephonySubscription;
}

/// (3.3.0) Optional sister interface to support carrier-native out-of-band DTMF.
#[async_trait]
pub trait IDtmfSendable {
    type Error: std::error::Error;
    async fn send_dtmf(&self, digits: &str) -> Result<(), Self::Error>;
}

/// A subscription handle. Dropping it invokes the unsubscribe closure.
pub struct TelephonySubscription {
    on_drop: Option<Box<dyn FnOnce() + Send>>,
}

impl TelephonySubscription {
    pub fn new(on_drop: Box<dyn FnOnce() + Send>) -> Self {
        Self { on_drop: Some(on_drop) }
    }
    /// A no-op subscription (the C# `NoopDisposable`).
    pub fn noop() -> Self {
        Self { on_drop: None }
    }
}

impl Drop for TelephonySubscription {
    fn drop(&mut self) {
        if let Some(f) = self.on_drop.take() {
            f();
        }
    }
}

// ═════════════════════════════════════════════════════════════════════════════
// NullImplementations.cs — fail-soft carrier + dispatcher
// ═════════════════════════════════════════════════════════════════════════════

/// (3.3.0) Null carrier — fail-soft on every operation. Produces
/// [`TestCallSession`] purely so the associated `Session` type resolves; every
/// dial/provision fails.
#[derive(Debug, Default, Clone, Copy)]
pub struct NullTelephonyCarrier;

#[async_trait]
impl ITelephonyCarrier for NullTelephonyCarrier {
    type Error = TelephonyError;
    type Session = TestCallSession;

    fn carrier_id(&self) -> &str {
        "null"
    }
    fn is_configured(&self) -> bool {
        false
    }
    async fn provision_number(
        &self,
        _country_code: &str,
        _area_code: Option<&str>,
    ) -> Result<ProvisionedNumber, TelephonyError> {
        Err(TelephonyError::InvalidOperation(
            "Null carrier cannot provision phone numbers. Register a real ITelephonyCarrier (CircleAI.Telephony.Twilio / .Telnyx / .Plivo).".into(),
        ))
    }
    async fn configure_inbound_webhook(
        &self,
        _phone_number: &str,
        _inbound_webhook: &str,
    ) -> Result<(), TelephonyError> {
        Ok(())
    }
    async fn dial(
        &self,
        _from_number: &str,
        _to_number: &str,
        _stream_url: &str,
        _options: Option<OutboundDialOptions>,
    ) -> Result<TestCallSession, TelephonyError> {
        Err(TelephonyError::InvalidOperation(
            "Null carrier cannot place outbound calls. Register a real ITelephonyCarrier.".into(),
        ))
    }
    async fn list_numbers(&self) -> Result<Vec<ProvisionedNumber>, TelephonyError> {
        Ok(Vec::new())
    }
}

/// (3.3.0) Null inbound dispatcher — never fires.
#[derive(Debug, Default, Clone, Copy)]
pub struct NullInboundCallDispatcher;

impl IInboundCallDispatcher for NullInboundCallDispatcher {
    type Session = TestCallSession;
    fn carrier_id(&self) -> &str {
        "null"
    }
    fn subscribe(&self, _handler: Box<dyn Fn(TestCallSession) + Send + Sync>) -> TelephonySubscription {
        TelephonySubscription::noop()
    }
}

// ═════════════════════════════════════════════════════════════════════════════
// TestCallSession.cs — in-memory ICallSession for harnesses
// ═════════════════════════════════════════════════════════════════════════════

struct TestSessionInner {
    inbound_audio: Vec<AudioFrame>,
    inbound_dtmf: Vec<DtmfEvent>,
    outbound_audio: Vec<AudioFrame>,
    outbound_dtmf: Vec<String>,
    status: CallStatus,
    inbound_closed: bool,
}

/// (3.3.0) In-memory [`ICallSession`] for harnesses + unit tests. Inject inbound
/// audio/DTMF, capture outbound audio/DTMF, drive lifecycle on demand.
pub struct TestCallSession {
    info: CallInfo,
    inner: Mutex<TestSessionInner>,
}

impl TestCallSession {
    pub fn new(info: Option<CallInfo>) -> Self {
        let info = info.unwrap_or_else(|| CallInfo {
            call_id: uuid::Uuid::new_v4().simple().to_string(),
            direction: CallDirection::Inbound,
            from: "+15555550100".into(),
            to: "+15555550200".into(),
            carrier_id: "test".into(),
            media_format: CallMediaFormat::Pcm16000,
            started_at_utc: Utc::now(),
        });
        Self {
            info,
            inner: Mutex::new(TestSessionInner {
                inbound_audio: Vec::new(),
                inbound_dtmf: Vec::new(),
                outbound_audio: Vec::new(),
                outbound_dtmf: Vec::new(),
                status: CallStatus::Active,
                inbound_closed: false,
            }),
        }
    }

    /// (3.3.0) Outbound audio frames the AI has emitted, captured for assertions.
    pub fn sent_audio_frames(&self) -> Vec<AudioFrame> {
        self.inner.lock().unwrap().outbound_audio.clone()
    }

    /// (3.3.0) Outbound DTMF strings the AI has emitted.
    pub fn sent_dtmf(&self) -> Vec<String> {
        self.inner.lock().unwrap().outbound_dtmf.clone()
    }

    /// (3.3.0) Inject one inbound audio frame for the AI to consume.
    pub fn inject_inbound_audio(&self, frame: AudioFrame) {
        self.inner.lock().unwrap().inbound_audio.push(frame);
    }

    /// (3.3.0) Inject one inbound DTMF event.
    pub fn inject_inbound_dtmf(&self, ev: DtmfEvent) {
        self.inner.lock().unwrap().inbound_dtmf.push(ev);
    }

    /// (3.3.0) Stop the inbound streams cleanly.
    pub fn end_inbound_streams(&self) {
        self.inner.lock().unwrap().inbound_closed = true;
    }

    /// (3.3.0) Trigger a status change (e.g. caller hangs up).
    pub fn trigger_status_change(&self, new_status: CallStatus) {
        self.inner.lock().unwrap().status = new_status;
    }
}

#[async_trait]
impl ICallSession for TestCallSession {
    type Error = Infallible;

    fn info(&self) -> &CallInfo {
        &self.info
    }
    fn status(&self) -> CallStatus {
        self.inner.lock().unwrap().status
    }
    async fn receive_audio(&self) -> Result<Vec<AudioFrame>, Infallible> {
        let mut inner = self.inner.lock().unwrap();
        Ok(std::mem::take(&mut inner.inbound_audio))
    }
    async fn send_audio(&self, frame: AudioFrame) -> Result<(), Infallible> {
        self.inner.lock().unwrap().outbound_audio.push(frame);
        Ok(())
    }
    async fn receive_dtmf(&self) -> Result<Vec<DtmfEvent>, Infallible> {
        let mut inner = self.inner.lock().unwrap();
        Ok(std::mem::take(&mut inner.inbound_dtmf))
    }
    async fn send_dtmf(&self, digits: &str) -> Result<(), Infallible> {
        self.inner.lock().unwrap().outbound_dtmf.push(digits.to_owned());
        Ok(())
    }
    async fn transfer(
        &self,
        _target_number: &str,
        _mode: TransferMode,
        _briefing: Option<&str>,
    ) -> Result<(), Infallible> {
        self.trigger_status_change(CallStatus::Transferred);
        Ok(())
    }
    async fn hang_up(&self) -> Result<(), Infallible> {
        self.trigger_status_change(CallStatus::EndedByAgent);
        self.end_inbound_streams();
        Ok(())
    }
}

// ═════════════════════════════════════════════════════════════════════════════
// DtmfToneGenerator.cs
// ═════════════════════════════════════════════════════════════════════════════

/// (3.3.0) Stateless DTMF audio generator.
pub struct DtmfToneGenerator;

impl DtmfToneGenerator {
    /// Standard DTMF frequencies (low row, high column) for a digit.
    fn frequencies(digit: char) -> Option<(i32, i32)> {
        let key = digit.to_ascii_uppercase();
        let pair = match key {
            '1' => (697, 1209),
            '2' => (697, 1336),
            '3' => (697, 1477),
            'A' => (697, 1633),
            '4' => (770, 1209),
            '5' => (770, 1336),
            '6' => (770, 1477),
            'B' => (770, 1633),
            '7' => (852, 1209),
            '8' => (852, 1336),
            '9' => (852, 1477),
            'C' => (852, 1633),
            '*' => (941, 1209),
            '0' => (941, 1336),
            '#' => (941, 1477),
            'D' => (941, 1633),
            _ => return None,
        };
        Some(pair)
    }

    /// (3.3.0) Generate one PCM-16 mono buffer for the digit.
    ///
    /// # Panics
    /// Panics (like the C# `ArgumentException`/`ArgumentOutOfRangeException`) on
    /// an unsupported digit or a non-positive sample rate / duration.
    pub fn generate(digit: char, sample_rate_hz: i32, duration_ms: i32, amplitude: f32) -> Vec<u8> {
        assert!(sample_rate_hz > 0, "sample_rate_hz must be positive");
        assert!(duration_ms > 0, "duration_ms must be positive");
        let (low, high) =
            Self::frequencies(digit).unwrap_or_else(|| panic!("Unsupported DTMF digit '{digit}'."));

        let samples = (sample_rate_hz * duration_ms / 1000) as usize;
        let mut buf = vec![0u8; samples * 2];
        for i in 0..samples {
            let t = i as f64 / sample_rate_hz as f64;
            let s = 0.5
                * amplitude as f64
                * ((2.0 * PI * low as f64 * t).sin() + (2.0 * PI * high as f64 * t).sin());
            let sample = (s.clamp(-1.0, 1.0) * i16::MAX as f64) as i16;
            write_i16_le(&mut buf, i * 2, sample);
        }
        buf
    }

    /// The C# defaults (`durationMs = 150`, `amplitude = 0.5`).
    pub fn generate_default(digit: char, sample_rate_hz: i32) -> Vec<u8> {
        Self::generate(digit, sample_rate_hz, 150, 0.5)
    }

    /// (3.3.0) Generate a full string of digits with gap silence between them.
    pub fn generate_sequence(
        digits: &str,
        sample_rate_hz: i32,
        tone_duration_ms: i32,
        inter_digit_gap_ms: i32,
        amplitude: f32,
    ) -> Vec<u8> {
        if digits.is_empty() {
            return Vec::new();
        }
        let gap_samples = (sample_rate_hz * inter_digit_gap_ms / 1000) as usize;
        let gap = vec![0u8; gap_samples * 2];

        let chars: Vec<char> = digits.chars().collect();
        let mut out = Vec::new();
        for (i, &d) in chars.iter().enumerate() {
            let tone = Self::generate(d, sample_rate_hz, tone_duration_ms, amplitude);
            out.extend_from_slice(&tone);
            if i < chars.len() - 1 {
                out.extend_from_slice(&gap);
            }
        }
        out
    }

    /// (3.3.0) Send `digits` over the call via in-band tones.
    pub async fn send_through_session<S: ICallSession>(
        session: &S,
        digits: &str,
        sample_rate_hz: i32,
        tone_duration_ms: i32,
        inter_digit_gap_ms: i32,
    ) -> Result<(), S::Error> {
        if digits.is_empty() {
            return Ok(());
        }
        let pcm = Self::generate_sequence(digits, sample_rate_hz, tone_duration_ms, inter_digit_gap_ms, 0.5);
        let format = match sample_rate_hz {
            8000 => CallMediaFormat::Mulaw8000,
            16000 => CallMediaFormat::Pcm16000,
            24000 => CallMediaFormat::Pcm24000,
            _ => CallMediaFormat::Pcm16000,
        };
        session.send_audio(AudioFrame::new(pcm, format, Duration::zero())).await
    }
}

// ═════════════════════════════════════════════════════════════════════════════
// AnsweringMachineDetector.cs
// ═════════════════════════════════════════════════════════════════════════════

/// (3.3.0) Verdict from the answering-machine detector.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum AmdVerdict {
    Unknown,
    Human,
    AnsweringMachine,
}

/// (3.3.0) Heuristic AMD configuration. `None` fields resolve to the C# defaults.
#[derive(Debug, Clone, Copy, Default)]
pub struct AmdOptions {
    pub human_max_first_utterance_ms: Option<i32>,
    pub human_min_first_utterance_ms: Option<i32>,
    pub max_observation_window: Option<i32>,
    pub silence_frame_threshold_ms: Option<i32>,
}

impl AmdOptions {
    pub fn human_max_first_utterance_ms_or_default(&self) -> i32 {
        self.human_max_first_utterance_ms.unwrap_or(1800)
    }
    pub fn human_min_first_utterance_ms_or_default(&self) -> i32 {
        self.human_min_first_utterance_ms.unwrap_or(300)
    }
    pub fn max_observation_window_or_default(&self) -> i32 {
        self.max_observation_window.unwrap_or(3500)
    }
    pub fn silence_frame_threshold_ms_or_default(&self) -> i32 {
        self.silence_frame_threshold_ms.unwrap_or(250)
    }
}

/// (3.3.0) Frame-by-frame AMD. Feed PCM-16 frames until [`current_verdict`]
/// stabilises.
pub struct AnsweringMachineDetector {
    options: AmdOptions,
    first_utterance_length: Duration,
    accumulated_audio: Duration,
    utterance_in_progress: bool,
    trailing_silence: Duration,
    verdict: AmdVerdict,
}

impl AnsweringMachineDetector {
    pub fn new(options: Option<AmdOptions>) -> Self {
        Self {
            options: options.unwrap_or_default(),
            first_utterance_length: Duration::zero(),
            accumulated_audio: Duration::zero(),
            utterance_in_progress: false,
            trailing_silence: Duration::zero(),
            verdict: AmdVerdict::Unknown,
        }
    }

    pub fn current_verdict(&self) -> AmdVerdict {
        self.verdict
    }

    /// (3.3.0) Feed one frame of PCM-16 mono. Returns the (possibly updated) verdict.
    ///
    /// # Panics
    /// Panics (like the C# `ArgumentOutOfRangeException`) if `sample_rate_hz <= 0`.
    pub fn observe(&mut self, pcm_frame: &[u8], sample_rate_hz: i32) -> AmdVerdict {
        assert!(sample_rate_hz > 0, "sample_rate_hz must be positive");
        if pcm_frame.len() < 2 {
            return self.verdict;
        }

        let frame_ms = 1000.0 * (pcm_frame.len() / 2) as f64 / sample_rate_hz as f64;
        let frame_duration = Duration::milliseconds(frame_ms as i64);
        let is_speech = frame_has_speech(pcm_frame);

        if self.verdict != AmdVerdict::Unknown {
            return self.verdict;
        }

        self.accumulated_audio = self.accumulated_audio + frame_duration;

        if is_speech {
            if !self.utterance_in_progress {
                self.utterance_in_progress = true;
            }
            self.first_utterance_length = self.first_utterance_length + frame_duration;
            self.trailing_silence = Duration::zero();
        } else if self.utterance_in_progress {
            self.trailing_silence = self.trailing_silence + frame_duration;
            if self.trailing_silence.num_milliseconds()
                >= self.options.silence_frame_threshold_ms_or_default() as i64
            {
                self.utterance_in_progress = false;
            }
        }

        // Decide (using floating-ms to match the C# `TotalMilliseconds` compares).
        let first_ms = self.first_utterance_length.num_milliseconds() as f64;
        let max_first = self.options.human_max_first_utterance_ms_or_default() as f64;
        let min_first = self.options.human_min_first_utterance_ms_or_default() as f64;
        if first_ms >= max_first {
            self.verdict = AmdVerdict::AnsweringMachine;
        } else if !self.utterance_in_progress && first_ms >= min_first && first_ms < max_first {
            self.verdict = AmdVerdict::Human;
        } else if self.accumulated_audio.num_milliseconds() as f64
            >= self.options.max_observation_window_or_default() as f64
        {
            self.verdict = if first_ms < min_first {
                AmdVerdict::Unknown
            } else {
                AmdVerdict::AnsweringMachine
            };
        }
        self.verdict
    }

    pub fn reset(&mut self) {
        self.first_utterance_length = Duration::zero();
        self.accumulated_audio = Duration::zero();
        self.utterance_in_progress = false;
        self.trailing_silence = Duration::zero();
        self.verdict = AmdVerdict::Unknown;
    }
}

fn frame_has_speech(pcm: &[u8]) -> bool {
    const ENERGY_THRESHOLD: f32 = 0.012;
    let sample_count = pcm.len() / 2;
    if sample_count == 0 {
        return false;
    }
    let mut sum_squares: f64 = 0.0;
    for i in 0..sample_count {
        let s = read_i16_le(pcm, i * 2) as f64;
        sum_squares += s * s;
    }
    let rms = (sum_squares / sample_count as f64).sqrt() / i16::MAX as f64;
    rms >= ENERGY_THRESHOLD as f64
}

// ═════════════════════════════════════════════════════════════════════════════
// BargeInController.cs
// ═════════════════════════════════════════════════════════════════════════════

/// (3.3.0) State of the AI's current turn.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum BargeInState {
    Speaking,
    Paused,
    Cancelled,
    Resumed,
}

/// (3.3.0) One state transition.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct BargeInTransition {
    pub from: BargeInState,
    pub to: BargeInState,
    pub at: DateTime<Utc>,
    pub reason: String,
}

/// (3.3.0) Configuration for barge-in detection. `None` fields resolve to the C#
/// defaults (pause 100 ms, cancel 600 ms).
#[derive(Debug, Clone, Copy, Default)]
pub struct BargeInOptions {
    pub pause_after: Option<Duration>,
    pub cancel_after: Option<Duration>,
}

impl BargeInOptions {
    pub fn pause_after_or_default(&self) -> Duration {
        self.pause_after.unwrap_or_else(|| Duration::milliseconds(100))
    }
    pub fn cancel_after_or_default(&self) -> Duration {
        self.cancel_after.unwrap_or_else(|| Duration::milliseconds(600))
    }
}

/// (3.3.0) Drives barge-in pause/resume/cancel decisions.
pub struct BargeInController {
    options: BargeInOptions,
    clock: Box<dyn Fn() -> DateTime<Utc> + Send + Sync>,
    state: BargeInState,
    caller_speech_started_at: Option<DateTime<Utc>>,
}

impl BargeInController {
    pub fn new(
        options: Option<BargeInOptions>,
        clock: Option<Box<dyn Fn() -> DateTime<Utc> + Send + Sync>>,
    ) -> Self {
        Self {
            options: options.unwrap_or_default(),
            clock: clock.unwrap_or_else(|| Box::new(Utc::now)),
            state: BargeInState::Speaking,
            caller_speech_started_at: None,
        }
    }

    pub fn state(&self) -> BargeInState {
        self.state
    }

    /// Call when AI playback begins.
    pub fn on_playback_start(&mut self) {
        self.state = BargeInState::Speaking;
        self.caller_speech_started_at = None;
    }

    /// Call on each frame where the VAD reports caller speech.
    pub fn on_caller_speech(&mut self) -> Option<BargeInTransition> {
        let now = (self.clock)();
        if self.state == BargeInState::Cancelled {
            return None;
        }
        let started = match self.caller_speech_started_at {
            None => {
                self.caller_speech_started_at = Some(now);
                return None;
            }
            Some(s) => s,
        };
        let elapsed = now - started;
        if self.state == BargeInState::Speaking && elapsed >= self.options.pause_after_or_default() {
            let t = BargeInTransition {
                from: self.state,
                to: BargeInState::Paused,
                at: now,
                reason: format!("Caller speech {} ms", elapsed.num_milliseconds()),
            };
            self.state = BargeInState::Paused;
            return Some(t);
        }
        if self.state == BargeInState::Paused && elapsed >= self.options.cancel_after_or_default() {
            let t = BargeInTransition {
                from: self.state,
                to: BargeInState::Cancelled,
                at: now,
                reason: format!("Confirmed barge-in after {} ms", elapsed.num_milliseconds()),
            };
            self.state = BargeInState::Cancelled;
            return Some(t);
        }
        None
    }

    /// Call on each frame where VAD reports silence.
    pub fn on_caller_silence(&mut self) -> Option<BargeInTransition> {
        let now = (self.clock)();
        self.caller_speech_started_at = None;
        if self.state == BargeInState::Paused {
            let t = BargeInTransition {
                from: self.state,
                to: BargeInState::Resumed,
                at: now,
                reason: "Caller fell silent after pause".into(),
            };
            self.state = BargeInState::Speaking; // resume
            return Some(t);
        }
        None
    }

    /// Whether the AI should keep emitting audio frames right now.
    pub fn should_emit_audio(&self) -> bool {
        self.state == BargeInState::Speaking
    }

    /// Whether the turn was confirmed barge-in (caller wins, AI should drop).
    pub fn was_barged_in(&self) -> bool {
        self.state == BargeInState::Cancelled
    }
}

// ═════════════════════════════════════════════════════════════════════════════
// FalseInterruptionTracker.cs
// ═════════════════════════════════════════════════════════════════════════════

/// (3.3.0) Counters for false-interruption monitoring.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct InterruptionStats {
    pub total_pause_events: i64,
    pub confirmed_barge_ins: i64,
    pub false_alarms: i64,
    pub false_alarm_rate: f32,
}

/// (3.3.0) Tracks barge-in transitions and surfaces a false-alarm rate.
pub trait IFalseInterruptionTracker {
    fn record(&self, transition: &BargeInTransition);
    fn get_stats(&self) -> InterruptionStats;
    fn reset(&self);
}

/// (3.3.0) Default in-memory tracker. Thread-safe.
#[derive(Debug, Default)]
pub struct InMemoryFalseInterruptionTracker {
    total_pauses: AtomicI64,
    confirmed: AtomicI64,
    false_alarms: AtomicI64,
}

impl IFalseInterruptionTracker for InMemoryFalseInterruptionTracker {
    fn record(&self, transition: &BargeInTransition) {
        match transition.to {
            BargeInState::Paused => {
                self.total_pauses.fetch_add(1, Ordering::SeqCst);
            }
            BargeInState::Cancelled => {
                self.confirmed.fetch_add(1, Ordering::SeqCst);
            }
            BargeInState::Resumed => {
                self.false_alarms.fetch_add(1, Ordering::SeqCst);
            }
            BargeInState::Speaking => {}
        }
    }
    fn get_stats(&self) -> InterruptionStats {
        let total_pauses = self.total_pauses.load(Ordering::SeqCst);
        let confirmed = self.confirmed.load(Ordering::SeqCst);
        let false_alarms = self.false_alarms.load(Ordering::SeqCst);
        let rate = if total_pauses > 0 {
            false_alarms as f32 / total_pauses as f32
        } else {
            0.0
        };
        InterruptionStats {
            total_pause_events: total_pauses,
            confirmed_barge_ins: confirmed,
            false_alarms,
            false_alarm_rate: rate,
        }
    }
    fn reset(&self) {
        self.total_pauses.store(0, Ordering::SeqCst);
        self.confirmed.store(0, Ordering::SeqCst);
        self.false_alarms.store(0, Ordering::SeqCst);
    }
}

// ═════════════════════════════════════════════════════════════════════════════
// CallCostCalculator.cs
// ═════════════════════════════════════════════════════════════════════════════

/// (3.3.0) Per-unit prices (any consistent currency). `decimal` → `f64`.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct CallPricing {
    pub carrier_per_minute: f64,
    pub stt_per_second: f64,
    pub tts_per_thousand_chars: f64,
    pub llm_input_per_k_token: f64,
    pub llm_output_per_k_token: f64,
}

/// (3.3.0) Breakdown of where the money went.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct CallCostBreakdown {
    pub carrier: f64,
    pub stt: f64,
    pub tts: f64,
    pub llm_input: f64,
    pub llm_output: f64,
    pub total: f64,
}

/// (3.3.0) Tracks cost for one call. Thread-safe via atomics.
pub struct CallCostCalculator {
    pricing: CallPricing,
    carrier_ms: AtomicI64,
    stt_ms: AtomicI64,
    tts_chars: AtomicI64,
    llm_input_tokens: AtomicI64,
    llm_output_tokens: AtomicI64,
}

impl CallCostCalculator {
    pub fn new(pricing: CallPricing) -> Self {
        Self {
            pricing,
            carrier_ms: AtomicI64::new(0),
            stt_ms: AtomicI64::new(0),
            tts_chars: AtomicI64::new(0),
            llm_input_tokens: AtomicI64::new(0),
            llm_output_tokens: AtomicI64::new(0),
        }
    }

    pub fn add_carrier_time(&self, duration: Duration) {
        if duration < Duration::zero() {
            return;
        }
        self.carrier_ms.fetch_add(duration.num_milliseconds(), Ordering::SeqCst);
    }

    pub fn add_stt_time(&self, duration: Duration) {
        if duration < Duration::zero() {
            return;
        }
        self.stt_ms.fetch_add(duration.num_milliseconds(), Ordering::SeqCst);
    }

    pub fn add_tts_characters(&self, chars: i32) {
        if chars <= 0 {
            return;
        }
        self.tts_chars.fetch_add(chars as i64, Ordering::SeqCst);
    }

    pub fn add_llm_tokens(&self, input_tokens: i32, output_tokens: i32) {
        if input_tokens > 0 {
            self.llm_input_tokens.fetch_add(input_tokens as i64, Ordering::SeqCst);
        }
        if output_tokens > 0 {
            self.llm_output_tokens.fetch_add(output_tokens as i64, Ordering::SeqCst);
        }
    }

    pub fn current_breakdown(&self) -> CallCostBreakdown {
        let carrier_min = self.carrier_ms.load(Ordering::SeqCst) as f64 / 60_000.0;
        let stt_sec = self.stt_ms.load(Ordering::SeqCst) as f64 / 1000.0;
        let tts_k = self.tts_chars.load(Ordering::SeqCst) as f64 / 1000.0;
        let llm_input_k = self.llm_input_tokens.load(Ordering::SeqCst) as f64 / 1000.0;
        let llm_output_k = self.llm_output_tokens.load(Ordering::SeqCst) as f64 / 1000.0;

        let carrier = carrier_min * self.pricing.carrier_per_minute;
        let stt = stt_sec * self.pricing.stt_per_second;
        let tts = tts_k * self.pricing.tts_per_thousand_chars;
        let llm_in = llm_input_k * self.pricing.llm_input_per_k_token;
        let llm_out = llm_output_k * self.pricing.llm_output_per_k_token;
        let total = carrier + stt + tts + llm_in + llm_out;

        CallCostBreakdown {
            carrier,
            stt,
            tts,
            llm_input: llm_in,
            llm_output: llm_out,
            total,
        }
    }

    pub fn reset(&self) {
        self.carrier_ms.store(0, Ordering::SeqCst);
        self.stt_ms.store(0, Ordering::SeqCst);
        self.tts_chars.store(0, Ordering::SeqCst);
        self.llm_input_tokens.store(0, Ordering::SeqCst);
        self.llm_output_tokens.store(0, Ordering::SeqCst);
    }
}

// ═════════════════════════════════════════════════════════════════════════════
// SentenceChunker.cs
// ═════════════════════════════════════════════════════════════════════════════

/// (3.3.0) Streaming sentence chunker. Emits whole sentences as soon as they're
/// complete so TTS can start speaking mid-response.
pub struct SentenceChunker {
    buffer: Mutex<String>,
    min_sentence_length: usize,
}

impl SentenceChunker {
    const TERMINAL: [char; 6] = ['.', '!', '?', '。', '！', '？'];

    /// C# default `minSentenceLength = 4`.
    pub fn new(min_sentence_length: usize) -> Self {
        Self {
            buffer: Mutex::new(String::new()),
            min_sentence_length,
        }
    }

    /// (3.3.0) Push a token; receive any complete sentences ready to emit.
    pub fn push_token(&self, token: &str) -> Vec<String> {
        if token.is_empty() {
            return Vec::new();
        }
        let mut buffer = self.buffer.lock().unwrap();
        buffer.push_str(token);
        let mut ready = Vec::new();
        loop {
            match self.extract_next(&buffer) {
                (Some(chunk), kept) => {
                    *buffer = kept;
                    ready.push(chunk);
                }
                (None, _) => break,
            }
        }
        ready
    }

    /// (3.3.0) Flush whatever's buffered as a final fragment.
    pub fn flush(&self) -> String {
        let mut buffer = self.buffer.lock().unwrap();
        std::mem::take(&mut *buffer)
    }

    fn extract_next(&self, buffer: &str) -> (Option<String>, String) {
        let chars: Vec<char> = buffer.chars().collect();
        let n = chars.len();
        let mut search_from = 0usize;
        while search_from < n {
            let idx = match (search_from..n).find(|&i| Self::TERMINAL.contains(&chars[i])) {
                Some(i) => i,
                None => return (None, buffer.to_owned()),
            };
            // Consume trailing whitespace + closing quotes after the punctuation.
            let mut end = idx + 1;
            while end < n
                && (chars[end].is_whitespace()
                    || chars[end] == '"'
                    || chars[end] == '\''
                    || chars[end] == ')')
            {
                end += 1;
            }
            let candidate: String = chars[..end].iter().collect::<String>().trim().to_owned();
            if candidate.chars().count() >= self.min_sentence_length {
                let kept: String = chars[end..].iter().collect();
                return (Some(candidate), kept);
            }
            // Too short — keep extending past this punctuation.
            search_from = end;
        }
        (None, buffer.to_owned())
    }
}

// ═════════════════════════════════════════════════════════════════════════════
// LatencyTracker.cs
// ═════════════════════════════════════════════════════════════════════════════

/// (3.3.0) Well-known voice-loop latency stage keys.
pub struct LatencyStage;

impl LatencyStage {
    pub const ASR_FIRST_WORD: &'static str = "asr.first_word";
    pub const ASR_FINAL: &'static str = "asr.final";
    pub const LLM_FIRST_TOKEN: &'static str = "llm.first_token";
    pub const LLM_FULL_RESPONSE: &'static str = "llm.full_response";
    pub const TTS_FIRST_AUDIO: &'static str = "tts.first_audio";
    pub const TTS_FULL_AUDIO: &'static str = "tts.full_audio";
    pub const END_TO_END: &'static str = "voice_loop.end_to_end";
}

/// (3.3.0) Snapshot of latency for one stage.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct LatencySnapshot {
    pub stage: String,
    pub samples: usize,
    pub min: Duration,
    pub p50: Duration,
    pub p95: Duration,
    pub p99: Duration,
    pub max: Duration,
}

/// (3.3.0) Records latency observations and produces percentiles over a
/// fixed-size sliding window per stage.
pub struct LatencyTracker {
    window_size: usize,
    observations: Mutex<HashMap<String, std::collections::VecDeque<i64>>>,
}

impl LatencyTracker {
    /// C# default `windowSize = 256`.
    pub fn new(window_size: usize) -> Self {
        assert!(window_size > 0, "window_size must be positive");
        Self {
            window_size,
            observations: Mutex::new(HashMap::new()),
        }
    }

    pub fn record(&self, stage: &str, latency: Duration) {
        assert!(!stage.trim().is_empty(), "stage required");
        if latency < Duration::zero() {
            return;
        }
        let mut obs = self.observations.lock().unwrap();
        let queue = obs.entry(stage.to_owned()).or_default();
        queue.push_back(latency.num_milliseconds());
        while queue.len() > self.window_size {
            queue.pop_front();
        }
    }

    pub fn snapshot(&self, stage: &str) -> Option<LatencySnapshot> {
        let obs = self.observations.lock().unwrap();
        let queue = obs.get(stage)?;
        if queue.is_empty() {
            return None;
        }
        let mut sorted: Vec<i64> = queue.iter().copied().collect();
        sorted.sort_unstable();

        let percentile = |p: f64| -> Duration {
            if sorted.is_empty() {
                return Duration::zero();
            }
            let mut idx = (p * sorted.len() as f64).ceil() as isize - 1;
            if idx < 0 {
                idx = 0;
            }
            if idx as usize >= sorted.len() {
                idx = sorted.len() as isize - 1;
            }
            Duration::milliseconds(sorted[idx as usize])
        };

        Some(LatencySnapshot {
            stage: stage.to_owned(),
            samples: sorted.len(),
            min: Duration::milliseconds(sorted[0]),
            p50: percentile(0.50),
            p95: percentile(0.95),
            p99: percentile(0.99),
            max: Duration::milliseconds(sorted[sorted.len() - 1]),
        })
    }

    pub fn snapshot_all(&self) -> Vec<LatencySnapshot> {
        let keys: Vec<String> = {
            let obs = self.observations.lock().unwrap();
            obs.keys().cloned().collect()
        };
        keys.iter().filter_map(|s| self.snapshot(s)).collect()
    }

    pub fn reset(&self, stage: &str) {
        let mut obs = self.observations.lock().unwrap();
        if let Some(queue) = obs.get_mut(stage) {
            queue.clear();
        }
    }

    pub fn reset_all(&self) {
        self.observations.lock().unwrap().clear();
    }
}

// ═════════════════════════════════════════════════════════════════════════════
// IvrLoopDetector.cs
// ═════════════════════════════════════════════════════════════════════════════

/// (3.3.0) One observation in the IVR conversation.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct IvrRound {
    pub speech: String,
    pub dtmf_pressed: Option<String>,
    pub at: DateTime<Utc>,
}

/// (3.3.0) Verdict on IVR navigation health.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct IvrLoopVerdict {
    pub is_looping: bool,
    pub loop_length: i32,
    pub reason: String,
}

/// (3.3.0) Records IVR rounds and surfaces a loop verdict.
pub struct IvrLoopDetector {
    rounds: Mutex<Vec<IvrRound>>,
    max_rounds_to_track: usize,
    min_rounds_for_loop: usize,
    similarity_threshold: f64,
}

impl IvrLoopDetector {
    /// C# defaults: track 32, min 2, similarity 0.85.
    pub fn new(max_rounds_to_track: usize, min_rounds_for_loop: usize, similarity_threshold: f64) -> Self {
        Self {
            rounds: Mutex::new(Vec::new()),
            max_rounds_to_track,
            min_rounds_for_loop,
            similarity_threshold,
        }
    }

    /// (3.3.0) Append one round and return the current verdict.
    pub fn observe(&self, round: IvrRound) -> IvrLoopVerdict {
        let mut rounds = self.rounds.lock().unwrap();
        rounds.push(round);
        while rounds.len() > self.max_rounds_to_track {
            rounds.remove(0);
        }
        self.evaluate(&rounds)
    }

    /// (3.3.0) Current verdict without adding a new round.
    pub fn current_verdict(&self) -> IvrLoopVerdict {
        let rounds = self.rounds.lock().unwrap();
        self.evaluate(&rounds)
    }

    /// (3.3.0) Drop all history.
    pub fn reset(&self) {
        self.rounds.lock().unwrap().clear();
    }

    fn evaluate(&self, rounds: &[IvrRound]) -> IvrLoopVerdict {
        // Strong signal first — same DTMF + similar prompt three times in a row.
        if rounds.len() >= 3 {
            let tail = &rounds[rounds.len() - 3..];
            if tail.iter().all(|r| r.dtmf_pressed == tail[0].dtmf_pressed)
                && tail.iter().all(|r| self.similar_to(&r.speech, &tail[0].speech))
            {
                return IvrLoopVerdict {
                    is_looping: true,
                    loop_length: 1,
                    reason: "Same prompt-and-press triple in a row.".into(),
                };
            }
        }

        if rounds.len() < self.min_rounds_for_loop * 2 {
            return IvrLoopVerdict {
                is_looping: false,
                loop_length: 0,
                reason: "Not enough rounds to evaluate.".into(),
            };
        }

        // Look for a repeating cycle of length L in the last N rounds.
        let mut l = self.min_rounds_for_loop;
        while l <= rounds.len() / 2 {
            let tail = &rounds[rounds.len() - 2 * l..];
            let mut looped = true;
            for i in 0..l {
                if !self.similar_to(&tail[i].speech, &tail[l + i].speech)
                    || tail[i].dtmf_pressed != tail[l + i].dtmf_pressed
                {
                    looped = false;
                    break;
                }
            }
            if looped {
                return IvrLoopVerdict {
                    is_looping: true,
                    loop_length: l as i32,
                    reason: format!("Detected repeating cycle of length {l}."),
                };
            }
            l += 1;
        }
        IvrLoopVerdict {
            is_looping: false,
            loop_length: 0,
            reason: "No loop detected.".into(),
        }
    }

    fn similar_to(&self, a: &str, b: &str) -> bool {
        if a.eq_ignore_ascii_case(b) {
            return true;
        }
        use std::collections::HashSet;
        let set_a: HashSet<String> = a
            .split(' ')
            .filter(|s| !s.is_empty())
            .map(|s| s.to_lowercase())
            .collect();
        let set_b: HashSet<String> = b
            .split(' ')
            .filter(|s| !s.is_empty())
            .map(|s| s.to_lowercase())
            .collect();
        if set_a.is_empty() || set_b.is_empty() {
            return false;
        }
        let inter = set_a.intersection(&set_b).count();
        let union = set_a.union(&set_b).count();
        inter as f64 / union as f64 >= self.similarity_threshold
    }
}

// ═════════════════════════════════════════════════════════════════════════════
// Guardrails.cs
// ═════════════════════════════════════════════════════════════════════════════

/// (3.3.0) What a guardrail does on match.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum GuardrailAction {
    /// Block the turn entirely — the AI says the fallback message instead.
    Replace,
    /// Redact only the matched text.
    Redact,
    /// Pass through but flag in the audit log.
    Warn,
}

/// (3.3.0) One rule the guardrail checks.
#[derive(Debug, Clone)]
pub struct GuardrailRule {
    pub name: String,
    pub pattern: String,
    pub action: GuardrailAction,
    pub replace_with: Option<String>,
    pub fallback_message: Option<String>,
}

/// (3.3.0) Outcome of running guardrails on one text draft.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct GuardrailResult {
    pub final_text: String,
    pub was_modified: bool,
    pub was_blocked: bool,
    pub triggered_rules: Vec<String>,
}

/// (3.3.0) Pre-TTS guardrail engine. Patterns are compiled case-insensitively.
pub struct Guardrails {
    rules: Vec<(GuardrailRule, Regex)>,
    default_fallback: String,
}

impl Guardrails {
    /// C# default fallback: "I'm sorry, I can't help with that right now."
    pub fn new(rules: Vec<GuardrailRule>, default_fallback: &str) -> Self {
        let compiled = rules
            .into_iter()
            .map(|r| {
                let re = Regex::new(&format!("(?i){}", r.pattern))
                    .unwrap_or_else(|e| panic!("invalid guardrail pattern {:?}: {e}", r.pattern));
                (r, re)
            })
            .collect();
        Self {
            rules: compiled,
            default_fallback: default_fallback.to_owned(),
        }
    }

    pub fn with_default_fallback(rules: Vec<GuardrailRule>) -> Self {
        Self::new(rules, "I'm sorry, I can't help with that right now.")
    }

    /// (3.3.0) Run the guardrails against a draft response.
    pub fn apply(&self, draft: &str) -> GuardrailResult {
        if draft.is_empty() {
            return GuardrailResult {
                final_text: String::new(),
                was_modified: false,
                was_blocked: false,
                triggered_rules: Vec::new(),
            };
        }

        let mut triggered = Vec::new();
        let mut text = draft.to_owned();

        for (rule, regex) in &self.rules {
            if !regex.is_match(&text) {
                continue;
            }
            triggered.push(rule.name.clone());

            match rule.action {
                GuardrailAction::Replace => {
                    let replacement = rule
                        .fallback_message
                        .clone()
                        .unwrap_or_else(|| self.default_fallback.clone());
                    return GuardrailResult {
                        final_text: replacement,
                        was_modified: true,
                        was_blocked: true,
                        triggered_rules: triggered,
                    };
                }
                GuardrailAction::Redact => {
                    let with = rule.replace_with.as_deref().unwrap_or("[redacted]");
                    text = regex.replace_all(&text, with).into_owned();
                }
                GuardrailAction::Warn => {}
            }
        }

        let modified = text != draft;
        GuardrailResult {
            final_text: text,
            was_modified: modified,
            was_blocked: false,
            triggered_rules: triggered,
        }
    }
}

/// (3.3.0) Common guardrails out of the box.
pub struct CommonGuardrails;

impl CommonGuardrails {
    /// (3.3.0) Redact 13-19 digit credit-card numbers.
    pub fn credit_card_redactor() -> GuardrailRule {
        GuardrailRule {
            name: "credit-card".into(),
            pattern: r"\b(?:\d[ -]*?){13,19}\b".into(),
            action: GuardrailAction::Redact,
            replace_with: Some("[redacted card number]".into()),
            fallback_message: None,
        }
    }

    /// (3.3.0) Block US SSN-shaped sequences (xxx-xx-xxxx).
    pub fn ssn_blocker() -> GuardrailRule {
        GuardrailRule {
            name: "ssn".into(),
            pattern: r"\b\d{3}-\d{2}-\d{4}\b".into(),
            action: GuardrailAction::Replace,
            replace_with: None,
            fallback_message: Some("For security I can't share that information.".into()),
        }
    }

    /// (3.3.0) Block competitor mentions — supply names per deployment.
    pub fn competitor_mention(competitors: &[&str]) -> GuardrailRule {
        let joined = competitors
            .iter()
            .map(|c| regex::escape(c))
            .collect::<Vec<_>>()
            .join("|");
        GuardrailRule {
            name: "competitor".into(),
            pattern: format!(r"\b(?:{joined})\b"),
            action: GuardrailAction::Replace,
            replace_with: None,
            fallback_message: Some(
                "I can't comment on other providers, but I can help with your account.".into(),
            ),
        }
    }
}

// ═════════════════════════════════════════════════════════════════════════════
// PromptVariableResolver.cs
// ═════════════════════════════════════════════════════════════════════════════

/// (3.3.0) Resolves the value for one prompt variable. Mirrors the C#
/// `PromptVariableProvider` delegate (async).
#[async_trait]
pub trait PromptVariableProvider: Send + Sync {
    async fn resolve(&self, variable_name: &str) -> Option<String>;
}

/// (3.3.0) Render a template with `{{var}}` placeholders against static values +
/// dynamic providers.
pub struct PromptVariableResolver {
    pattern: Regex,
    providers: HashMap<String, Box<dyn PromptVariableProvider>>,
    statics: HashMap<String, String>,
    default_missing: String,
}

impl PromptVariableResolver {
    pub fn new(default_missing: &str) -> Self {
        Self {
            // `[A-Za-z_][A-Za-z0-9_.]*` inside `{{ … }}`.
            pattern: Regex::new(r"\{\{\s*([A-Za-z_][A-Za-z0-9_.]*)\s*\}\}").unwrap(),
            providers: HashMap::new(),
            statics: HashMap::new(),
            default_missing: default_missing.to_owned(),
        }
    }

    /// Register a static value (case-insensitive key).
    pub fn set(&mut self, name: &str, value: &str) -> Result<&mut Self, TelephonyError> {
        if name.trim().is_empty() {
            return Err(TelephonyError::InvalidArgument("name required".into()));
        }
        self.statics.insert(name.to_lowercase(), value.to_owned());
        Ok(self)
    }

    /// Register a dynamic value provider (case-insensitive key).
    pub fn set_provider(
        &mut self,
        name: &str,
        provider: Box<dyn PromptVariableProvider>,
    ) -> Result<&mut Self, TelephonyError> {
        if name.trim().is_empty() {
            return Err(TelephonyError::InvalidArgument("name required".into()));
        }
        self.providers.insert(name.to_lowercase(), provider);
        Ok(self)
    }

    /// Render `template` by substituting every `{{var}}`.
    pub async fn render(&self, template: &str) -> String {
        if template.is_empty() {
            return String::new();
        }
        // Collect the distinct variable names (case-insensitive), resolving each.
        let mut replacements: HashMap<String, String> = HashMap::new();
        for caps in self.pattern.captures_iter(template) {
            let name = caps[1].to_string();
            let key = name.to_lowercase();
            if replacements.contains_key(&key) {
                continue;
            }
            let value = if let Some(v) = self.statics.get(&key) {
                v.clone()
            } else if let Some(p) = self.providers.get(&key) {
                p.resolve(&name).await.unwrap_or_else(|| self.default_missing.clone())
            } else {
                self.default_missing.clone()
            };
            replacements.insert(key, value);
        }
        self.pattern
            .replace_all(template, |caps: &regex::Captures| {
                let key = caps[1].to_lowercase();
                replacements.get(&key).cloned().unwrap_or_default()
            })
            .into_owned()
    }
}

// ═════════════════════════════════════════════════════════════════════════════
// HoldMusicMixer.cs
// ═════════════════════════════════════════════════════════════════════════════

/// (3.3.0) Background audio mixer for hold music. Loops a track and mixes the
/// AI's speech on top, ducking the background when speech arrives.
pub struct HoldMusicMixer {
    background_loop: Vec<u8>,
    background_gain: f32,
    ducked_gain: f32,
    loop_cursor: usize,
}

impl HoldMusicMixer {
    /// C# defaults: `backgroundGain = 0.6`, `duckedGain = 0.15`.
    ///
    /// # Panics
    /// Panics (like the C# `ArgumentException`/`ArgumentOutOfRangeException`) if
    /// the loop is shorter than one PCM-16 sample or a gain is outside `0..=1`.
    pub fn new(background_loop: Vec<u8>, background_gain: f32, ducked_gain: f32) -> Self {
        assert!(
            background_loop.len() >= 2,
            "Background loop must contain at least one PCM-16 sample."
        );
        assert!((0.0..=1.0).contains(&background_gain), "background_gain out of range");
        assert!((0.0..=1.0).contains(&ducked_gain), "ducked_gain out of range");
        Self {
            background_loop,
            background_gain,
            ducked_gain,
            loop_cursor: 0,
        }
    }

    /// Reset the loop cursor to the start.
    pub fn reset(&mut self) {
        self.loop_cursor = 0;
    }

    /// (3.3.0) Mix `speech_frame` on top of looped background into `destination`.
    /// Pass an empty speech buffer to render plain background.
    ///
    /// # Panics
    /// Panics (like the C# `ArgumentException`) if `destination` is shorter than
    /// the speech frame.
    pub fn mix_frame(&mut self, speech_frame: &[u8], destination: &mut [u8]) -> usize {
        if destination.len() < 2 {
            return 0;
        }
        let has_speech = speech_frame.len() >= 2;
        let frame_length = if has_speech { speech_frame.len() } else { destination.len() };
        assert!(
            destination.len() >= frame_length,
            "destination must be at least as long as the speech frame."
        );

        let gain = if has_speech { self.ducked_gain } else { self.background_gain };
        let loop_len = self.background_loop.len();

        let mut i = 0;
        while i < frame_length {
            let speech_sample = if has_speech { read_i16_le(speech_frame, i) } else { 0 };

            // Pull background sample from the loop, wrapping as needed.
            let bg_sample = read_i16_le(&self.background_loop, self.loop_cursor);
            self.loop_cursor = (self.loop_cursor + 2) % loop_len;
            if self.loop_cursor % 2 != 0 {
                self.loop_cursor -= 1; // align to 16-bit boundary
            }

            let mixed = speech_sample as i32 + (bg_sample as f32 * gain) as i32;
            let mixed = mixed.clamp(i16::MIN as i32, i16::MAX as i32);
            write_i16_le(destination, i, mixed as i16);
            i += 2;
        }
        frame_length
    }
}

// ═════════════════════════════════════════════════════════════════════════════
// StereoCallRecorder.cs
// ═════════════════════════════════════════════════════════════════════════════

/// (3.3.0) Interleave caller (left) + agent (right) PCM-16 mono audio into a
/// single in-memory stereo WAV buffer.
///
/// The C# writes to a seekable `Stream` and backfills the 44-byte header on
/// `Finalize`; the Rust port accumulates the interleaved samples in a `Vec<u8>`
/// and produces the complete `.wav` bytes (header + data) via [`finish`].
pub struct StereoCallRecorder {
    sample_rate_hz: i32,
    data: Vec<u8>,
    samples_written: u64,
}

impl StereoCallRecorder {
    /// # Panics
    /// Panics (like the C# `ArgumentOutOfRangeException`) if `sample_rate_hz <= 0`.
    pub fn new(sample_rate_hz: i32) -> Self {
        assert!(sample_rate_hz > 0, "sample_rate_hz must be positive");
        Self {
            sample_rate_hz,
            data: Vec::new(),
            samples_written: 0,
        }
    }

    /// (3.3.0) Write inbound (caller) PCM-16 mono audio → left channel.
    pub fn write_caller_frame(&mut self, pcm_frame: &[u8]) {
        self.write_side(pcm_frame, true);
    }

    /// (3.3.0) Write outbound (agent) PCM-16 mono audio → right channel.
    pub fn write_agent_frame(&mut self, pcm_frame: &[u8]) {
        self.write_side(pcm_frame, false);
    }

    fn write_side(&mut self, pcm_frame: &[u8], is_caller: bool) {
        if pcm_frame.len() < 2 {
            return;
        }
        let samples = pcm_frame.len() / 2;
        for i in 0..samples {
            let mono = read_i16_le(pcm_frame, i * 2);
            let mut stereo = [0u8; 4];
            if is_caller {
                write_i16_le(&mut stereo, 0, mono);
                write_i16_le(&mut stereo, 2, 0);
            } else {
                write_i16_le(&mut stereo, 0, 0);
                write_i16_le(&mut stereo, 2, mono);
            }
            self.data.extend_from_slice(&stereo);
            self.samples_written += 1;
        }
    }

    /// (3.3.0) Produce the finished stereo PCM-16 WAV bytes (header + data).
    pub fn finish(&self) -> Vec<u8> {
        let data_size = (self.samples_written * 4) as u32; // 2 channels × 2 bytes
        let chunk_size = 36 + data_size;
        let mut out = Vec::with_capacity(44 + self.data.len());

        out.extend_from_slice(b"RIFF");
        out.extend_from_slice(&chunk_size.to_le_bytes());
        out.extend_from_slice(b"WAVE");
        out.extend_from_slice(b"fmt ");
        out.extend_from_slice(&16u32.to_le_bytes()); // Subchunk1Size
        out.extend_from_slice(&1u16.to_le_bytes()); // PCM
        out.extend_from_slice(&2u16.to_le_bytes()); // channels
        out.extend_from_slice(&(self.sample_rate_hz as u32).to_le_bytes());
        out.extend_from_slice(&((self.sample_rate_hz * 4) as u32).to_le_bytes()); // byte rate
        out.extend_from_slice(&4u16.to_le_bytes()); // block align
        out.extend_from_slice(&16u16.to_le_bytes()); // bits per sample
        out.extend_from_slice(b"data");
        out.extend_from_slice(&data_size.to_le_bytes());
        out.extend_from_slice(&self.data);
        out
    }
}

// ═════════════════════════════════════════════════════════════════════════════
// ToolCalling.cs — tool definitions + registry (local + injected webhook)
// ═════════════════════════════════════════════════════════════════════════════

/// (3.3.0) Tool definition surfaced to the LLM.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ToolDefinition {
    pub name: String,
    pub description: String,
    pub arguments_json_schema: String,
}

impl ToolDefinition {
    pub fn new(name: &str, description: &str, arguments_json_schema: &str) -> Self {
        Self {
            name: name.to_owned(),
            description: description.to_owned(),
            arguments_json_schema: arguments_json_schema.to_owned(),
        }
    }
}

/// (3.3.0) An invocation of one tool by the model.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ToolInvocation {
    pub call_id: String,
    pub tool_name: String,
    pub arguments_json: String,
}

/// (3.3.0) Result of a tool invocation.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ToolResult {
    pub call_id: String,
    pub succeeded: bool,
    pub result_json: String,
    pub error: Option<String>,
}

impl ToolResult {
    pub fn ok(call_id: &str, result_json: &str) -> Self {
        Self {
            call_id: call_id.to_owned(),
            succeeded: true,
            result_json: result_json.to_owned(),
            error: None,
        }
    }
    pub fn fail(call_id: &str, error: &str) -> Self {
        Self {
            call_id: call_id.to_owned(),
            succeeded: false,
            result_json: "{}".into(),
            error: Some(error.to_owned()),
        }
    }
}

/// (3.3.0) In-process tool handler (the C# `LocalToolHandler` delegate).
#[async_trait]
pub trait LocalToolHandler: Send + Sync {
    async fn invoke(&self, arguments_json: &str) -> Result<String, TelephonyError>;
}

/// (3.3.0) Injected HTTP-JSON boundary for the webhook branch of the registry +
/// the MCP importer + the consult channel. The C# uses `System.Net.Http`; here
/// the network call is the trait, and the request/response *shaping* is ported.
#[async_trait]
pub trait IHttpJsonClient: Send + Sync {
    /// POST `body_json` to `url` with an optional `Authorization` header. Returns
    /// the (status, response-body) pair. Implementations that cannot reach the
    /// network return an `Err`.
    async fn post_json(
        &self,
        url: &str,
        body_json: &str,
        authorization: Option<&str>,
    ) -> Result<(u16, String), TelephonyError>;
}

enum ToolEntry {
    Local(Arc<dyn LocalToolHandler>),
    Webhook(String),
}

/// (3.3.0) Tool registry contract: register local handlers OR webhook URLs; the
/// registry dispatches.
#[async_trait]
pub trait IToolCallRegistry: Send + Sync {
    fn definitions(&self) -> Vec<ToolDefinition>;
    fn register_local(&self, definition: ToolDefinition, handler: Box<dyn LocalToolHandler>);
    fn register_webhook(&self, definition: ToolDefinition, webhook: &str);
    async fn invoke(&self, invocation: &ToolInvocation) -> ToolResult;
}

/// (3.3.0) Default in-memory registry. The webhook branch POSTs via the injected
/// [`IHttpJsonClient`].
pub struct DefaultToolCallRegistry {
    tools: Mutex<HashMap<String, (ToolDefinition, ToolEntry)>>,
    http: Box<dyn IHttpJsonClient>,
}

impl DefaultToolCallRegistry {
    pub fn new(http: Box<dyn IHttpJsonClient>) -> Self {
        Self {
            tools: Mutex::new(HashMap::new()),
            http,
        }
    }

    fn truncate(s: &str, max: usize) -> String {
        if s.chars().count() <= max {
            s.to_owned()
        } else {
            let head: String = s.chars().take(max).collect();
            format!("{head}…")
        }
    }
}

#[async_trait]
impl IToolCallRegistry for DefaultToolCallRegistry {
    fn definitions(&self) -> Vec<ToolDefinition> {
        self.tools.lock().unwrap().values().map(|(d, _)| d.clone()).collect()
    }

    fn register_local(&self, definition: ToolDefinition, handler: Box<dyn LocalToolHandler>) {
        let key = definition.name.to_lowercase();
        assert!(!definition.name.trim().is_empty(), "Tool name is required");
        self.tools
            .lock()
            .unwrap()
            .insert(key, (definition, ToolEntry::Local(Arc::from(handler))));
    }

    fn register_webhook(&self, definition: ToolDefinition, webhook: &str) {
        let key = definition.name.to_lowercase();
        assert!(!definition.name.trim().is_empty(), "Tool name is required");
        self.tools
            .lock()
            .unwrap()
            .insert(key, (definition, ToolEntry::Webhook(webhook.to_owned())));
    }

    async fn invoke(&self, invocation: &ToolInvocation) -> ToolResult {
        // Resolve the entry under the lock, cloning the shared handler / URL out
        // so the `MutexGuard` is dropped before any `.await` (keeps the future Send).
        let key = invocation.tool_name.to_lowercase();
        enum Kind {
            Local(Arc<dyn LocalToolHandler>),
            Webhook(String),
            Missing,
        }
        let kind = {
            let tools = self.tools.lock().unwrap();
            match tools.get(&key) {
                Some((_, ToolEntry::Local(h))) => Kind::Local(Arc::clone(h)),
                Some((_, ToolEntry::Webhook(url))) => Kind::Webhook(url.clone()),
                None => Kind::Missing,
            }
        };

        match kind {
            Kind::Missing => ToolResult::fail(
                &invocation.call_id,
                &format!("Tool '{}' is not registered.", invocation.tool_name),
            ),
            Kind::Local(handler) => {
                match handler.invoke(&invocation.arguments_json).await {
                    Ok(json) => ToolResult::ok(&invocation.call_id, if json.is_empty() { "{}" } else { &json }),
                    Err(e) => ToolResult::fail(&invocation.call_id, &e.to_string()),
                }
            }
            Kind::Webhook(url) => {
                // The C# posts `{call_id, tool, arguments:<parsed JSON>}`; here the
                // arguments are forwarded as a raw JSON value.
                let body = format!(
                    "{{\"call_id\":{},\"tool\":{},\"arguments\":{}}}",
                    json_string(&invocation.call_id),
                    json_string(&invocation.tool_name),
                    if invocation.arguments_json.trim().is_empty() {
                        "{}"
                    } else {
                        &invocation.arguments_json
                    }
                );
                match self.http.post_json(&url, &body, None).await {
                    Ok((status, resp_body)) if (200..300).contains(&status) => ToolResult::ok(
                        &invocation.call_id,
                        if resp_body.trim().is_empty() { "{}" } else { &resp_body },
                    ),
                    Ok((status, resp_body)) => ToolResult::fail(
                        &invocation.call_id,
                        &format!("Webhook {status}: {}", Self::truncate(&resp_body, 240)),
                    ),
                    Err(e) => ToolResult::fail(&invocation.call_id, &e.to_string()),
                }
            }
        }
    }
}

// ═════════════════════════════════════════════════════════════════════════════
// ToolCircuitBreaker.cs — per-tool timeout + breaker decorator
// ═════════════════════════════════════════════════════════════════════════════

/// (3.3.0) Per-tool timeout + breaker thresholds. `None` fields resolve to the
/// C# defaults (timeout 5 s, open-duration 30 s).
#[derive(Debug, Clone, Copy)]
pub struct ToolCallPolicy {
    pub timeout: Option<Duration>,
    pub failure_threshold: i32,
    pub open_duration: Option<Duration>,
}

impl Default for ToolCallPolicy {
    fn default() -> Self {
        Self {
            timeout: None,
            failure_threshold: 3,
            open_duration: None,
        }
    }
}

impl ToolCallPolicy {
    pub fn timeout_or_default(&self) -> Duration {
        self.timeout.unwrap_or_else(|| Duration::seconds(5))
    }
    pub fn open_duration_or_default(&self) -> Duration {
        self.open_duration.unwrap_or_else(|| Duration::seconds(30))
    }
}

/// (3.3.0) Breaker state.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ToolBreakerState {
    Closed,
    Open,
    HalfOpen,
}

#[derive(Default)]
struct BreakerEntry {
    consecutive_failures: i32,
    opened_at: Option<DateTime<Utc>>,
    is_open: bool,
}

impl BreakerEntry {
    fn current_state(&self, now: DateTime<Utc>, open_duration: Duration) -> ToolBreakerState {
        if !self.is_open {
            return ToolBreakerState::Closed;
        }
        match self.opened_at {
            Some(at) if now - at >= open_duration => ToolBreakerState::HalfOpen,
            _ => ToolBreakerState::Open,
        }
    }
    fn record_success(&mut self) {
        self.consecutive_failures = 0;
        self.is_open = false;
    }
    fn record_failure(&mut self, threshold: i32, now: DateTime<Utc>) {
        self.consecutive_failures += 1;
        if self.consecutive_failures >= threshold {
            self.is_open = true;
            self.opened_at = Some(now);
        }
    }
}

/// (3.3.0) Decorates an [`IToolCallRegistry`] with per-tool circuit breakers.
///
/// The wall-clock timeout in the C# uses a `CancellationTokenSource`; because the
/// Rust registry surface is a single `invoke().await` (no cancellation token),
/// the breaker records success/failure based on the inner result — the timeout
/// budget is carried on the policy for a host that wants to enforce it around the
/// call. Breaker state transitions are byte-for-byte identical.
pub struct CircuitBreakerToolRegistry<R: IToolCallRegistry> {
    inner: R,
    default_policy: ToolCallPolicy,
    policies: Mutex<HashMap<String, ToolCallPolicy>>,
    breakers: Mutex<HashMap<String, BreakerEntry>>,
    clock: Box<dyn Fn() -> DateTime<Utc> + Send + Sync>,
}

impl<R: IToolCallRegistry> CircuitBreakerToolRegistry<R> {
    pub fn new(
        inner: R,
        default_policy: Option<ToolCallPolicy>,
        clock: Option<Box<dyn Fn() -> DateTime<Utc> + Send + Sync>>,
    ) -> Self {
        Self {
            inner,
            default_policy: default_policy.unwrap_or_default(),
            policies: Mutex::new(HashMap::new()),
            breakers: Mutex::new(HashMap::new()),
            clock: clock.unwrap_or_else(|| Box::new(Utc::now)),
        }
    }

    /// Override the policy for a specific tool.
    pub fn set_policy(&self, tool_name: &str, policy: ToolCallPolicy) {
        self.policies.lock().unwrap().insert(tool_name.to_lowercase(), policy);
    }

    /// Inspect the current breaker state for a tool.
    pub fn get_state(&self, tool_name: &str) -> ToolBreakerState {
        let now = (self.clock)();
        let open_duration = self.get_policy(tool_name).open_duration_or_default();
        let breakers = self.breakers.lock().unwrap();
        breakers
            .get(&tool_name.to_lowercase())
            .map(|e| e.current_state(now, open_duration))
            .unwrap_or(ToolBreakerState::Closed)
    }

    fn get_policy(&self, tool_name: &str) -> ToolCallPolicy {
        self.policies
            .lock()
            .unwrap()
            .get(&tool_name.to_lowercase())
            .copied()
            .unwrap_or(self.default_policy)
    }
}

#[async_trait]
impl<R: IToolCallRegistry> IToolCallRegistry for CircuitBreakerToolRegistry<R> {
    fn definitions(&self) -> Vec<ToolDefinition> {
        self.inner.definitions()
    }
    fn register_local(&self, definition: ToolDefinition, handler: Box<dyn LocalToolHandler>) {
        self.inner.register_local(definition, handler)
    }
    fn register_webhook(&self, definition: ToolDefinition, webhook: &str) {
        self.inner.register_webhook(definition, webhook)
    }
    async fn invoke(&self, invocation: &ToolInvocation) -> ToolResult {
        let key = invocation.tool_name.to_lowercase();
        let policy = self.get_policy(&invocation.tool_name);
        let now = (self.clock)();

        let state = {
            let mut breakers = self.breakers.lock().unwrap();
            let entry = breakers.entry(key.clone()).or_default();
            entry.current_state(now, policy.open_duration_or_default())
        };
        if state == ToolBreakerState::Open {
            return ToolResult::fail(
                &invocation.call_id,
                &format!(
                    "Tool '{}' is circuit-broken; retry after the breaker resets.",
                    invocation.tool_name
                ),
            );
        }

        let result = self.inner.invoke(invocation).await;
        let now2 = (self.clock)();
        {
            let mut breakers = self.breakers.lock().unwrap();
            let entry = breakers.entry(key).or_default();
            if result.succeeded {
                entry.record_success();
            } else {
                entry.record_failure(policy.failure_threshold, now2);
            }
        }
        result
    }
}

// ═════════════════════════════════════════════════════════════════════════════
// SpeculativeGenerator.cs
// ═════════════════════════════════════════════════════════════════════════════

/// (3.3.0) Function that drives a response generation given a partial transcript
/// (the C# `ResponseGenerator` delegate).
#[async_trait]
pub trait ResponseGenerator: Send + Sync {
    async fn generate(&self, transcript: &str) -> String;
}

/// (3.3.0) Manages speculative-generation branches.
///
/// The C# keeps an in-flight `Task<string>` per branch and cancels superseded
/// ones. Rust futures are inert until awaited and cancellation of a not-yet-
/// awaited future is a no-op, so this port tracks the *committed partial* and,
/// on [`commit`], regenerates against the final transcript — preserving the
/// observable contract (the same generator drives the eventual answer) while
/// dropping the wasted-work optimisation that has no lock-free Rust analogue.
pub struct DefaultSpeculativeGenerator {
    active_partial: Mutex<Option<String>>,
    min_partial_length: usize,
}

impl DefaultSpeculativeGenerator {
    /// C# default `minPartialLength = 8`.
    pub fn new(min_partial_length: usize) -> Self {
        Self {
            active_partial: Mutex::new(None),
            min_partial_length,
        }
    }

    /// The current speculative partial transcript, if any.
    pub fn active_partial(&self) -> Option<String> {
        self.active_partial.lock().unwrap().clone()
    }

    /// Start (or extend) speculation using `partial_transcript`.
    pub fn speculate(&self, partial_transcript: &str) {
        if partial_transcript.trim().is_empty() || partial_transcript.len() < self.min_partial_length {
            return;
        }
        let mut active = self.active_partial.lock().unwrap();
        // If the new partial merely extends the active one, keep the active.
        if let Some(a) = active.as_ref() {
            if partial_transcript.to_lowercase().starts_with(&a.to_lowercase()) {
                return;
            }
        }
        *active = Some(partial_transcript.to_owned());
    }

    /// Commit to a final transcript and return the matching response.
    pub async fn commit<G: ResponseGenerator>(&self, final_transcript: &str, generator: &G) -> String {
        if final_transcript.trim().is_empty() {
            return String::new();
        }
        *self.active_partial.lock().unwrap() = None;
        generator.generate(final_transcript).await
    }

    /// Abort any active speculation.
    pub fn abort(&self) {
        *self.active_partial.lock().unwrap() = None;
    }
}

// ═════════════════════════════════════════════════════════════════════════════
// StreamingToolProgress.cs
// ═════════════════════════════════════════════════════════════════════════════

/// (3.3.0) One progress update from a streaming tool.
#[derive(Debug, Clone, PartialEq)]
pub struct ToolProgressUpdate {
    pub call_id: String,
    pub percent_complete: f32,
    pub status_text: Option<String>,
    pub emitted_at: DateTime<Utc>,
}

/// (3.3.0) The sink a tool pushes progress updates into.
#[async_trait]
pub trait IToolProgressSink: Send + Sync {
    async fn emit(&self, update: &ToolProgressUpdate);
}

/// (3.3.0) Sink that records updates for observability without speaking them.
#[derive(Default)]
pub struct RecordingToolProgressSink {
    updates: Mutex<Vec<ToolProgressUpdate>>,
}

impl RecordingToolProgressSink {
    pub fn updates(&self) -> Vec<ToolProgressUpdate> {
        self.updates.lock().unwrap().clone()
    }
}

#[async_trait]
impl IToolProgressSink for RecordingToolProgressSink {
    async fn emit(&self, update: &ToolProgressUpdate) {
        self.updates.lock().unwrap().push(update.clone());
    }
}

/// (3.3.0) Streaming tool handler — accepts a progress sink it can push into.
#[async_trait]
pub trait StreamingToolHandler: Send + Sync {
    async fn invoke(
        &self,
        arguments_json: &str,
        progress_sink: &(dyn IToolProgressSink + Sync),
    ) -> Result<String, TelephonyError>;
}

/// (3.3.0) Run a streaming tool handler against a progress sink.
pub struct StreamingToolRunner;

impl StreamingToolRunner {
    pub async fn run<H: StreamingToolHandler + ?Sized>(
        invocation: &ToolInvocation,
        handler: &H,
        sink: &(dyn IToolProgressSink + Sync),
    ) -> ToolResult {
        match handler.invoke(&invocation.arguments_json, sink).await {
            Ok(json) => ToolResult::ok(&invocation.call_id, if json.is_empty() { "{}" } else { &json }),
            Err(e) => ToolResult::fail(&invocation.call_id, &e.to_string()),
        }
    }
}

// ═════════════════════════════════════════════════════════════════════════════
// AgentHandoff.cs
// ═════════════════════════════════════════════════════════════════════════════

/// (3.3.0) One AI agent persona that can be handed control of a call.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct CallAgent {
    pub agent_id: String,
    pub display_name: String,
    pub system_prompt: String,
    pub greeting_text: Option<String>,
}

/// (3.3.0) Outcome of a handoff attempt.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct HandoffResult {
    pub succeeded: bool,
    pub failure_reason: Option<String>,
    pub active_agent: Option<CallAgent>,
}

/// (3.3.0) Synthesise the briefing / greeting text to PCM-16 mono (the C#
/// `BriefingSynthesiser` delegate).
#[async_trait]
pub trait BriefingSynthesiser: Send + Sync {
    async fn synthesise(&self, text: &str) -> Vec<u8>;
}

/// (3.3.0) Default in-memory agent-handoff orchestrator. Thread-safe.
pub struct DefaultAgentHandoffOrchestrator {
    agents: Mutex<HashMap<String, CallAgent>>,
    current: Mutex<Option<CallAgent>>,
}

impl DefaultAgentHandoffOrchestrator {
    pub fn new(seed: Option<Vec<CallAgent>>) -> Self {
        let mut agents = HashMap::new();
        if let Some(seed) = seed {
            for a in seed {
                agents.insert(a.agent_id.to_lowercase(), a);
            }
        }
        Self {
            agents: Mutex::new(agents),
            current: Mutex::new(None),
        }
    }

    pub fn current_agent(&self) -> Option<CallAgent> {
        self.current.lock().unwrap().clone()
    }

    pub fn agent_catalog(&self) -> HashMap<String, CallAgent> {
        self.agents.lock().unwrap().clone()
    }

    pub fn register_agent(&self, agent: CallAgent) -> Result<(), TelephonyError> {
        if agent.agent_id.trim().is_empty() {
            return Err(TelephonyError::InvalidArgument("AgentId is required.".into()));
        }
        self.agents.lock().unwrap().insert(agent.agent_id.to_lowercase(), agent);
        Ok(())
    }

    pub fn set_initial_agent(&self, agent_id: &str) -> Result<(), TelephonyError> {
        let agents = self.agents.lock().unwrap();
        let agent = agents.get(&agent_id.to_lowercase()).ok_or_else(|| {
            TelephonyError::InvalidOperation(format!("Agent '{agent_id}' is not registered."))
        })?;
        *self.current.lock().unwrap() = Some(agent.clone());
        Ok(())
    }

    /// Hand the call over to `target_agent_id`; speaks the greeting via `tts`.
    pub async fn handoff<S: ICallSession, T: BriefingSynthesiser>(
        &self,
        session: &S,
        target_agent_id: &str,
        tts: &T,
    ) -> HandoffResult {
        if target_agent_id.trim().is_empty() {
            return HandoffResult {
                succeeded: false,
                failure_reason: Some("targetAgentId is required".into()),
                active_agent: self.current_agent(),
            };
        }

        let (target, is_same) = {
            let mut current = self.current.lock().unwrap();
            let agents = self.agents.lock().unwrap();
            let target = match agents.get(&target_agent_id.to_lowercase()) {
                Some(t) => t.clone(),
                None => {
                    return HandoffResult {
                        succeeded: false,
                        failure_reason: Some(format!("Agent '{target_agent_id}' is not registered.")),
                        active_agent: current.clone(),
                    }
                }
            };
            let is_same = current
                .as_ref()
                .map(|p| p.agent_id.eq_ignore_ascii_case(&target.agent_id))
                .unwrap_or(false);
            if is_same {
                return HandoffResult {
                    succeeded: true,
                    failure_reason: None,
                    active_agent: current.clone(),
                };
            }
            *current = Some(target.clone());
            (target, is_same)
        };
        let _ = is_same;

        if let Some(greeting) = &target.greeting_text {
            if !greeting.trim().is_empty() {
                let audio = tts.synthesise(greeting).await;
                if !audio.is_empty() {
                    let _ = session
                        .send_audio(AudioFrame::new(audio, CallMediaFormat::Pcm24000, Duration::zero()))
                        .await;
                }
            }
        }

        HandoffResult {
            succeeded: true,
            failure_reason: None,
            active_agent: Some(target),
        }
    }
}

// ═════════════════════════════════════════════════════════════════════════════
// WarmTransferOrchestrator.cs
// ═════════════════════════════════════════════════════════════════════════════

/// (3.3.0) One warm-transfer request.
pub struct WarmTransferRequest<'a, S: ICallSession> {
    pub source_session: &'a S,
    pub target_number: String,
    pub briefing_text: String,
    pub bridge_stream_url: String,
}

/// (3.3.0) Outcome of a warm transfer.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct WarmTransferResult {
    pub succeeded: bool,
    pub failure_reason: Option<String>,
}

/// (3.3.0) Carrier-agnostic warm-transfer driver: dial target, brief, bridge.
pub struct DefaultWarmTransferOrchestrator<C: ITelephonyCarrier, T: BriefingSynthesiser> {
    carrier: C,
    briefing_tts: T,
}

impl<C: ITelephonyCarrier, T: BriefingSynthesiser> DefaultWarmTransferOrchestrator<C, T>
where
    C::Session: ICallSession,
{
    pub fn new(carrier: C, briefing_tts: T) -> Self {
        Self {
            carrier,
            briefing_tts,
        }
    }

    pub async fn execute<S: ICallSession>(&self, request: WarmTransferRequest<'_, S>) -> WarmTransferResult {
        if request.target_number.trim().is_empty() {
            return WarmTransferResult {
                succeeded: false,
                failure_reason: Some("TargetNumber is required".into()),
            };
        }

        // 1) Dial target on a fresh leg.
        let bridge_leg = match self
            .carrier
            .dial(
                &request.source_session.info().to,
                &request.target_number,
                &request.bridge_stream_url,
                None,
            )
            .await
        {
            Ok(leg) => leg,
            Err(e) => {
                return WarmTransferResult {
                    succeeded: false,
                    failure_reason: Some(format!("Failed to dial target: {e}")),
                }
            }
        };

        // 2) Speak briefing to target.
        let briefing_audio = self.briefing_tts.synthesise(&request.briefing_text).await;
        if !briefing_audio.is_empty()
            && bridge_leg
                .send_audio(AudioFrame::new(briefing_audio, CallMediaFormat::Pcm24000, Duration::zero()))
                .await
                .is_err()
        {
            let _ = bridge_leg.hang_up().await;
            return WarmTransferResult {
                succeeded: false,
                failure_reason: Some("Failed to brief target.".into()),
            };
        }

        // 3) Hand caller off to target — the bridge moment.
        if request
            .source_session
            .transfer(&request.target_number, TransferMode::Cold, None)
            .await
            .is_err()
        {
            let _ = bridge_leg.hang_up().await;
            return WarmTransferResult {
                succeeded: false,
                failure_reason: Some("Failed to bridge caller.".into()),
            };
        }

        // 4) AI leg ends; caller + target stay connected.
        let _ = bridge_leg.hang_up().await;
        WarmTransferResult {
            succeeded: true,
            failure_reason: None,
        }
    }
}

// ═════════════════════════════════════════════════════════════════════════════
// ConsultEscalation.cs — human-in-the-loop consult over injected channels
// ═════════════════════════════════════════════════════════════════════════════

/// (3.3.0) Question the AI asks a human expert.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ConsultRequest {
    pub call_id: String,
    pub question: String,
    pub context_json: String,
    pub urgency: String,
}

/// (3.3.0) Human reply. `confidence = true` ⇒ expert confirmed.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ConsultAnswer {
    pub answer: String,
    pub confidence: bool,
    pub notes: Option<String>,
}

/// (3.3.0) Channel for asking a human expert.
#[async_trait]
pub trait IConsultChannel: Send + Sync {
    fn name(&self) -> &str;
    /// Ask, honouring `timeout`. Returns `None` on no-answer/timeout.
    async fn ask(&self, request: &ConsultRequest, timeout: Duration) -> Option<ConsultAnswer>;
}

/// (3.3.0) Default escalation driver: try channels in order until one answers.
pub struct ConsultEscalator {
    channels: Vec<Box<dyn IConsultChannel>>,
}

impl ConsultEscalator {
    pub fn new(channels: Vec<Box<dyn IConsultChannel>>) -> Self {
        Self { channels }
    }

    /// (3.3.0) Walk channels in order; first non-`None` answer wins.
    pub async fn escalate(
        &self,
        request: &ConsultRequest,
        timeout_per_channel: Duration,
    ) -> Option<ConsultAnswer> {
        for channel in &self.channels {
            if let Some(answer) = channel.ask(request, timeout_per_channel).await {
                return Some(answer);
            }
        }
        None
    }
}

/// (3.3.0) HTTP webhook channel — POSTs the request, parses a JSON reply via the
/// injected [`IHttpJsonClient`]. Expects `{answer, confidence, notes}`.
pub struct HttpWebhookConsultChannel {
    http: Box<dyn IHttpJsonClient>,
    endpoint: String,
    name: String,
}

impl HttpWebhookConsultChannel {
    pub fn new(http: Box<dyn IHttpJsonClient>, endpoint: &str, name: &str) -> Self {
        Self {
            http,
            endpoint: endpoint.to_owned(),
            name: name.to_owned(),
        }
    }
}

#[async_trait]
impl IConsultChannel for HttpWebhookConsultChannel {
    fn name(&self) -> &str {
        &self.name
    }
    async fn ask(&self, request: &ConsultRequest, _timeout: Duration) -> Option<ConsultAnswer> {
        let body = format!(
            "{{\"call_id\":{},\"question\":{},\"context_json\":{},\"urgency\":{}}}",
            json_string(&request.call_id),
            json_string(&request.question),
            json_string(&request.context_json),
            json_string(&request.urgency)
        );
        let (status, resp) = self.http.post_json(&self.endpoint, &body, None).await.ok()?;
        if !(200..300).contains(&status) {
            return None;
        }
        let map = parse_flat_json_object(resp.as_bytes())?;
        let answer = map.get("answer")?.clone();
        if answer.trim().is_empty() {
            return None;
        }
        let confidence = map.get("confidence").map(|v| v == "true").unwrap_or(false);
        let notes = map.get("notes").cloned();
        Some(ConsultAnswer {
            answer,
            confidence,
            notes,
        })
    }
}

// ═════════════════════════════════════════════════════════════════════════════
// McpToolImporter.cs — import remote MCP tools into a registry
// ═════════════════════════════════════════════════════════════════════════════

/// (3.3.0) Description of one MCP tool returned from `tools/list`.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct McpToolDescriptor {
    pub name: String,
    pub description: String,
    pub input_json_schema: String,
}

/// (3.3.0) MCP server descriptor.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct McpServerConfig {
    pub server_endpoint: String,
    pub authorization_header: Option<String>,
    pub tool_name_prefix: Option<String>,
}

/// (3.3.0) HTTP-backed MCP importer. `tools/list` via JSON-RPC over the injected
/// [`IHttpJsonClient`]; each remote tool is registered as a webhook that forwards
/// back to the server's `tools/call`.
pub struct HttpMcpToolImporter {
    http: Box<dyn IHttpJsonClient>,
}

impl HttpMcpToolImporter {
    pub fn new(http: Box<dyn IHttpJsonClient>) -> Self {
        Self { http }
    }

    /// Import tools from `server` into `registry`, returning the imported defs.
    pub async fn import<R: IToolCallRegistry>(
        &self,
        registry: &R,
        server: &McpServerConfig,
    ) -> Vec<ToolDefinition> {
        let list_request = r#"{"jsonrpc":"2.0","id":1,"method":"tools/list","params":{}}"#;
        let (status, body) = match self
            .http
            .post_json(
                &server.server_endpoint,
                list_request,
                server.authorization_header.as_deref(),
            )
            .await
        {
            Ok(r) => r,
            Err(_) => return Vec::new(),
        };
        if !(200..300).contains(&status) {
            return Vec::new();
        }

        // Parse the `result.tools[]` array. To avoid a full JSON dependency, the
        // descriptors are extracted with the light-weight
        // [`extract_mcp_tools`] helper (handles the exact `tools/list` shape).
        let descriptors = extract_mcp_tools(&body);
        let mut imported = Vec::new();
        for d in descriptors {
            if d.name.trim().is_empty() {
                continue;
            }
            let local_name = match &server.tool_name_prefix {
                Some(p) if !p.trim().is_empty() => format!("{p}{}", d.name),
                _ => d.name.clone(),
            };
            let def = ToolDefinition::new(&local_name, &d.description, &d.input_json_schema);
            let invoke_url = append_query(&server.server_endpoint, "remote_tool", &d.name);
            registry.register_webhook(def.clone(), &invoke_url);
            imported.push(def);
        }
        imported
    }
}

/// Append `key=value` to a URL's query string (the C# `AppendQuery`).
fn append_query(base_uri: &str, key: &str, value: &str) -> String {
    let separator = if base_uri.contains('?') { "&" } else { "?" };
    format!("{base_uri}{separator}{key}={}", url_escape(value))
}

/// Percent-encode a query value (RFC 3986 unreserved kept verbatim).
fn url_escape(s: &str) -> String {
    let mut out = String::with_capacity(s.len());
    for b in s.bytes() {
        match b {
            b'A'..=b'Z' | b'a'..=b'z' | b'0'..=b'9' | b'-' | b'_' | b'.' | b'~' => out.push(b as char),
            _ => out.push_str(&format!("%{b:02X}")),
        }
    }
    out
}

// ═════════════════════════════════════════════════════════════════════════════
// VoiceLoopAsTool.cs — expose the voice loop as an external-agent tool
// ═════════════════════════════════════════════════════════════════════════════

/// (3.3.0) Request to make one outbound voice call as a tool invocation.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct VoiceLoopToolRequest {
    pub to_number: String,
    pub goal: String,
    pub context_json: Option<String>,
    pub system_prompt: Option<String>,
    pub max_duration: Option<Duration>,
}

/// (3.3.0) Result of the call returned to the calling agent.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct VoiceLoopToolResult {
    pub goal_achieved: bool,
    pub summary: String,
    pub call_id: String,
    pub duration: Duration,
    pub transcript: String,
    pub structured_output_json: Option<String>,
}

/// (3.3.0) Host-supplied runner that actually places the call (the C#
/// `Func<VoiceLoopToolRequest, …, Task<VoiceLoopToolResult>>`).
#[async_trait]
pub trait VoiceLoopRunner: Send + Sync {
    async fn run(&self, request: &VoiceLoopToolRequest) -> VoiceLoopToolResult;
}

/// (3.3.0) Driver that delegates the actual call to a host-supplied runner.
pub struct VoiceLoopAsTool<R: VoiceLoopRunner> {
    runner: R,
    default_max_duration: Duration,
}

impl<R: VoiceLoopRunner> VoiceLoopAsTool<R> {
    /// C# default `defaultMaxDuration = 5 minutes`.
    pub fn new(runner: R, default_max_duration: Option<Duration>) -> Self {
        Self {
            runner,
            default_max_duration: default_max_duration.unwrap_or_else(|| Duration::minutes(5)),
        }
    }

    pub async fn invoke(&self, request: &VoiceLoopToolRequest) -> Result<VoiceLoopToolResult, TelephonyError> {
        if request.to_number.trim().is_empty() {
            return Err(TelephonyError::InvalidArgument("ToNumber is required.".into()));
        }
        if request.goal.trim().is_empty() {
            return Err(TelephonyError::InvalidArgument("Goal is required.".into()));
        }
        // The C# enforces `MaxDuration` via a linked CTS; the runner boundary owns
        // the timeout here (Rust has no ambient cancellation token). The resolved
        // budget is still surfaced so a host can honour it.
        let _budget = request.max_duration.unwrap_or(self.default_max_duration);
        Ok(self.runner.run(request).await)
    }

    /// (3.3.0) Tool descriptor for use with an [`IToolCallRegistry`].
    pub fn descriptor() -> ToolDefinition {
        ToolDefinition::new(
            "make_voice_call",
            "Place an outbound phone call and follow the supplied goal/script. Returns whether the goal was achieved.",
            r#"{
  "type": "object",
  "properties": {
    "to_number":     { "type": "string", "description": "E.164 destination." },
    "goal":          { "type": "string" },
    "context_json":  { "type": "string", "nullable": true },
    "system_prompt": { "type": "string", "nullable": true },
    "max_duration_seconds": { "type": "integer", "nullable": true }
  },
  "required": ["to_number", "goal"]
}"#,
        )
    }
}

// ═════════════════════════════════════════════════════════════════════════════
// EvalSession.cs + LlmJudge.cs — offline eval harness
// ═════════════════════════════════════════════════════════════════════════════

/// (3.3.0) One scripted turn from a fake caller.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct EvalTurn {
    pub user_transcript: String,
    pub expected_keywords: Option<Vec<String>>,
}

/// (3.3.0) Outcome of one eval turn.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct EvalTurnResult {
    pub assistant_response: String,
    pub missing_keywords: Vec<String>,
    pub latency: Duration,
}

/// (3.3.0) Overall eval result.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct EvalRunResult {
    pub turns: Vec<EvalTurnResult>,
    pub all_keywords_hit: bool,
    pub total_latency: Duration,
}

/// (3.3.0) Runs one turn through the AI under test (the C# `EvalTurnHandler`).
#[async_trait]
pub trait EvalTurnHandler: Send + Sync {
    async fn handle(&self, user_transcript: &str) -> String;
}

/// (3.3.0) Drives an eval session against a real handler.
pub struct EvalSession<H: EvalTurnHandler> {
    handler: H,
    clock: Box<dyn Fn() -> DateTime<Utc> + Send + Sync>,
}

impl<H: EvalTurnHandler> EvalSession<H> {
    pub fn new(handler: H) -> Self {
        Self::with_clock(handler, Box::new(Utc::now))
    }

    pub fn with_clock(handler: H, clock: Box<dyn Fn() -> DateTime<Utc> + Send + Sync>) -> Self {
        Self { handler, clock }
    }

    /// (3.3.0) Run the script and assemble results.
    pub async fn run(&self, script: &[EvalTurn]) -> EvalRunResult {
        let mut results = Vec::with_capacity(script.len());
        let mut total = Duration::zero();
        let mut all_hit = true;
        for turn in script {
            let started = (self.clock)();
            let response = self.handler.handle(&turn.user_transcript).await;
            let elapsed = (self.clock)() - started;
            total = total + elapsed;

            let mut missing = Vec::new();
            if let Some(keywords) = &turn.expected_keywords {
                for kw in keywords {
                    if !response.to_lowercase().contains(&kw.to_lowercase()) {
                        missing.push(kw.clone());
                    }
                }
            }
            if !missing.is_empty() {
                all_hit = false;
            }
            results.push(EvalTurnResult {
                assistant_response: response,
                missing_keywords: missing,
                latency: elapsed,
            });
        }
        EvalRunResult {
            turns: results,
            all_keywords_hit: all_hit,
            total_latency: total,
        }
    }
}

/// (3.3.0) One scoring dimension.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct JudgeDimension {
    pub name: String,
    pub description: String,
}

/// (3.3.0) Result of one judging call.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct JudgeVerdict {
    /// 0..10 per dimension.
    pub scores: HashMap<String, i32>,
    /// pass / borderline / fail.
    pub overall: String,
    pub reasoning: String,
}

/// (3.3.0) Delegate that asks the actual LLM to grade (the C# `JudgeCompletion`).
#[async_trait]
pub trait JudgeCompletion: Send + Sync {
    async fn complete(&self, prompt: &str) -> String;
}

/// (3.3.0) LLM-as-judge driver.
pub struct LlmJudge<C: JudgeCompletion> {
    completion: C,
}

impl<C: JudgeCompletion> LlmJudge<C> {
    pub fn new(completion: C) -> Self {
        Self { completion }
    }

    /// (3.3.0) Build the rubric prompt, ask the judge, parse JSON, return verdict.
    pub async fn judge(
        &self,
        user_utterance: &str,
        assistant_response: &str,
        dimensions: &[JudgeDimension],
    ) -> JudgeVerdict {
        let prompt = Self::build_prompt(user_utterance, assistant_response, dimensions);
        let raw = self.completion.complete(&prompt).await;
        Self::parse_verdict(&raw, dimensions)
    }

    fn build_prompt(user: &str, assistant: &str, dims: &[JudgeDimension]) -> String {
        let mut rubric = String::new();
        rubric.push_str("You are an evaluation judge. Score the assistant's reply across the rubric below.\n");
        rubric.push_str("Reply ONLY in this JSON shape:\n");
        rubric.push_str(
            "{ \"scores\": { \"<dim_name>\": <0-10>, ... }, \"overall\": \"pass|borderline|fail\", \"reasoning\": \"<one paragraph>\" }\n",
        );
        rubric.push('\n');
        rubric.push_str("Rubric:\n");
        for d in dims {
            rubric.push_str(&format!("- {}: {}\n", d.name, d.description));
        }
        rubric.push('\n');
        rubric.push_str("User utterance:\n");
        rubric.push_str(user);
        rubric.push('\n');
        rubric.push('\n');
        rubric.push_str("Assistant reply:\n");
        rubric.push_str(assistant);
        rubric
    }

    fn parse_verdict(raw: &str, dims: &[JudgeDimension]) -> JudgeVerdict {
        // Extract the JSON blob, then read `scores.<dim>` / `overall` / `reasoning`.
        let trimmed = extract_json(raw);
        // The judge JSON is nested (`scores` is an object), so use the small nested
        // reader `read_judge_json`; on any failure fall back to all-zero / borderline.
        match read_judge_json(&trimmed, dims) {
            Some(v) => v,
            None => {
                let mut scores = HashMap::new();
                for d in dims {
                    scores.insert(d.name.clone(), 0);
                }
                JudgeVerdict {
                    scores,
                    overall: "borderline".into(),
                    reasoning: "Judge response could not be parsed.".into(),
                }
            }
        }
    }
}

/// (3.3.0) Tolerate models that wrap JSON in prose or fenced code blocks (the C#
/// `ExtractJson`).
fn extract_json(raw: &str) -> String {
    let start = raw.find('{');
    let end = raw.rfind('}');
    match (start, end) {
        (Some(s), Some(e)) if e > s => raw[s..=e].to_owned(),
        _ => raw.to_owned(),
    }
}

// ═════════════════════════════════════════════════════════════════════════════
// SpeechLifecycleEvents.cs
// ═════════════════════════════════════════════════════════════════════════════

/// (3.3.0) One speech-lifecycle event (the C# abstract-record hierarchy).
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum SpeechLifecycleEvent {
    CallerSpeechStarted { call_id: String, at: DateTime<Utc> },
    CallerSpeechEnded { call_id: String, at: DateTime<Utc> },
    TranscriptInterim { call_id: String, at: DateTime<Utc>, text: String },
    TranscriptFinalV2 { call_id: String, at: DateTime<Utc>, text: String },
    AgentThinking { call_id: String, at: DateTime<Utc> },
    AgentSpeakingStarted { call_id: String, at: DateTime<Utc> },
    AgentSpeakingFinished { call_id: String, at: DateTime<Utc>, spoken_duration: Duration },
    SpeechError { call_id: String, at: DateTime<Utc>, stage: String, message: String },
}

/// Discriminator for typed subscription (the C# `typeof(TEvent)`).
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub enum SpeechEventKind {
    CallerSpeechStarted,
    CallerSpeechEnded,
    TranscriptInterim,
    TranscriptFinalV2,
    AgentThinking,
    AgentSpeakingStarted,
    AgentSpeakingFinished,
    SpeechError,
}

impl SpeechLifecycleEvent {
    pub fn kind(&self) -> SpeechEventKind {
        match self {
            SpeechLifecycleEvent::CallerSpeechStarted { .. } => SpeechEventKind::CallerSpeechStarted,
            SpeechLifecycleEvent::CallerSpeechEnded { .. } => SpeechEventKind::CallerSpeechEnded,
            SpeechLifecycleEvent::TranscriptInterim { .. } => SpeechEventKind::TranscriptInterim,
            SpeechLifecycleEvent::TranscriptFinalV2 { .. } => SpeechEventKind::TranscriptFinalV2,
            SpeechLifecycleEvent::AgentThinking { .. } => SpeechEventKind::AgentThinking,
            SpeechLifecycleEvent::AgentSpeakingStarted { .. } => SpeechEventKind::AgentSpeakingStarted,
            SpeechLifecycleEvent::AgentSpeakingFinished { .. } => SpeechEventKind::AgentSpeakingFinished,
            SpeechLifecycleEvent::SpeechError { .. } => SpeechEventKind::SpeechError,
        }
    }
}

type SpeechHandler = Box<dyn Fn(&SpeechLifecycleEvent) + Send + Sync>;

/// (3.3.0) Speech lifecycle pub/sub. A `None` filter subscribes to every event
/// (the C# `SpeechLifecycleEvent` base subscription).
pub struct InMemorySpeechLifecycleBus {
    subscribers: Mutex<HashMap<i64, (Option<SpeechEventKind>, SpeechHandler)>>,
    next_handle: AtomicI64,
}

impl InMemorySpeechLifecycleBus {
    pub fn new() -> Self {
        Self {
            subscribers: Mutex::new(HashMap::new()),
            next_handle: AtomicI64::new(0),
        }
    }

    /// Subscribe to a specific event kind, or to all with `None`.
    pub fn subscribe(&self, kind: Option<SpeechEventKind>, handler: SpeechHandler) -> i64 {
        let id = self.next_handle.fetch_add(1, Ordering::SeqCst) + 1;
        self.subscribers.lock().unwrap().insert(id, (kind, handler));
        id
    }

    /// Remove a subscription by its handle.
    pub fn unsubscribe(&self, id: i64) {
        self.subscribers.lock().unwrap().remove(&id);
    }

    /// Publish one event to every matching subscriber.
    pub fn publish(&self, ev: &SpeechLifecycleEvent) {
        let subs = self.subscribers.lock().unwrap();
        let kind = ev.kind();
        for (filter, handler) in subs.values() {
            if filter.is_none() || filter == &Some(kind) {
                handler(ev);
            }
        }
    }
}

impl Default for InMemorySpeechLifecycleBus {
    fn default() -> Self {
        Self::new()
    }
}

// ═════════════════════════════════════════════════════════════════════════════
// DashboardData.cs
// ═════════════════════════════════════════════════════════════════════════════

/// (3.3.0) One row in the live-calls panel.
#[derive(Debug, Clone, PartialEq)]
pub struct LiveCallRow {
    pub call_id: String,
    pub carrier: String,
    pub from: String,
    pub to: String,
    pub status: CallStatus,
    pub started_at_utc: DateTime<Utc>,
    pub duration: Duration,
    pub cost_so_far: f64,
}

/// (3.3.0) One row in the recent-calls panel.
#[derive(Debug, Clone, PartialEq)]
pub struct RecentCallRow {
    pub call_id: String,
    pub carrier: String,
    pub from: String,
    pub to: String,
    pub final_status: CallStatus,
    pub ended_at_utc: DateTime<Utc>,
    pub duration: Duration,
    pub total_cost: f64,
}

/// (3.3.0) Agent health summary row.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct AgentHealthRow {
    pub agent_label: String,
    pub health: String,
    pub consecutive_failures: i32,
}

/// (3.3.0) Top-of-page summary card.
#[derive(Debug, Clone, PartialEq)]
pub struct DashboardSummary {
    pub live_call_count: i32,
    pub current_spend_usd: f64,
    pub calls_last_24h: i32,
    pub pause_false_alarm_rate: f32,
}

/// (3.3.0) Full dashboard snapshot.
#[derive(Debug, Clone, PartialEq)]
pub struct DashboardSnapshot {
    pub summary: DashboardSummary,
    pub live_calls: Vec<LiveCallRow>,
    pub recent_calls: Vec<RecentCallRow>,
    pub agent_health: Vec<AgentHealthRow>,
    pub latency_by_stage: Vec<LatencySnapshot>,
}

/// (3.3.0) Dashboard data source: compose live + recent + health + latency feeds.
#[async_trait]
pub trait IDashboardDataSource: Send + Sync {
    async fn snapshot(&self) -> DashboardSnapshot;
}

// ═════════════════════════════════════════════════════════════════════════════
// FirstMessagePreamble.cs + ReassuranceFiller.cs — timing-driven speech helpers
// ═════════════════════════════════════════════════════════════════════════════

/// (3.3.0) Configuration for the first-message preamble.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct FirstMessagePreambleOptions {
    /// Template with `{{var}}` placeholders.
    pub template: String,
    /// If the model responds before this elapses, skip the preamble. Default 250 ms.
    pub max_latency: Option<Duration>,
}

impl FirstMessagePreambleOptions {
    pub fn max_latency_or_default(&self) -> Duration {
        self.max_latency.unwrap_or_else(|| Duration::milliseconds(250))
    }
}

/// (3.3.0) Phrases the reassurance filler rotates through.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ReassuranceVocabulary {
    pub short_fillers: Vec<String>,
    pub long_fillers: Vec<String>,
}

impl ReassuranceVocabulary {
    /// (3.3.0) Sensible English defaults.
    pub fn default_vocabulary() -> Self {
        Self {
            short_fillers: vec![
                "One moment.".into(),
                "Let me check.".into(),
                "Give me a sec.".into(),
                "Just a moment.".into(),
            ],
            long_fillers: vec![
                "Still looking that up for you.".into(),
                "This is taking a bit longer than usual — bear with me.".into(),
                "Almost there — still pulling that information.".into(),
                "Thanks for your patience, I'm checking that now.".into(),
            ],
        }
    }

    /// The rotated short filler at rotation index `n` (0-based).
    pub fn next_short(&self, n: usize) -> String {
        if self.short_fillers.is_empty() {
            "One moment.".into()
        } else {
            self.short_fillers[n % self.short_fillers.len()].clone()
        }
    }

    /// The rotated long filler at rotation index `n` (0-based).
    pub fn next_long(&self, n: usize) -> String {
        if self.long_fillers.is_empty() {
            "Almost there.".into()
        } else {
            self.long_fillers[n % self.long_fillers.len()].clone()
        }
    }
}

/// (3.3.0) Configuration for the reassurance filler driver.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ReassuranceFillerOptions {
    pub short_filler_after: Option<Duration>,
    pub long_filler_every: Option<Duration>,
    pub vocabulary: Option<ReassuranceVocabulary>,
}

impl Default for ReassuranceFillerOptions {
    fn default() -> Self {
        Self {
            short_filler_after: None,
            long_filler_every: None,
            vocabulary: None,
        }
    }
}

impl ReassuranceFillerOptions {
    pub fn short_filler_after_or_default(&self) -> Duration {
        self.short_filler_after.unwrap_or_else(|| Duration::milliseconds(600))
    }
    pub fn long_filler_every_or_default(&self) -> Duration {
        self.long_filler_every.unwrap_or_else(|| Duration::seconds(3))
    }
    pub fn vocabulary_or_default(&self) -> ReassuranceVocabulary {
        self.vocabulary.clone().unwrap_or_else(ReassuranceVocabulary::default_vocabulary)
    }
}

// ═════════════════════════════════════════════════════════════════════════════
// Telemetry.cs — OpenTelemetry span factory (no OTel dependency)
// ═════════════════════════════════════════════════════════════════════════════

/// (3.3.0) A lightweight voice-loop span. The C# uses
/// `System.Diagnostics.Activity`; here spans are plain values a host records.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct VoiceLoopSpan {
    pub name: String,
    pub kind: SpanKind,
    pub tags: Vec<(String, Option<String>)>,
    pub outcome: Option<SpanOutcome>,
}

/// Span kind, mirroring `ActivityKind`.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum SpanKind {
    Internal,
    Client,
}

/// Terminal status a span was tagged with (the C# `RecordOutcome`).
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum SpanOutcome {
    Ok,
    Error(String),
}

/// (3.3.0) Voice-loop span factory. Names + tags match the C# `VoiceLoopTelemetry`.
pub struct VoiceLoopTelemetry;

impl VoiceLoopTelemetry {
    /// ActivitySource name CircleAI uses for voice-loop spans.
    pub const SOURCE_NAME: &'static str = "CircleAI.Telephony.VoiceLoop";

    pub fn start_turn(call_id: &str) -> VoiceLoopSpan {
        VoiceLoopSpan {
            name: "voice_loop.turn".into(),
            kind: SpanKind::Internal,
            tags: vec![("call.id".into(), Some(call_id.to_owned()))],
            outcome: None,
        }
    }

    pub fn start_asr(backend: &str) -> VoiceLoopSpan {
        VoiceLoopSpan {
            name: "voice_loop.asr".into(),
            kind: SpanKind::Client,
            tags: vec![("backend".into(), Some(backend.to_owned()))],
            outcome: None,
        }
    }

    pub fn start_llm(provider: &str, model: &str) -> VoiceLoopSpan {
        VoiceLoopSpan {
            name: "voice_loop.llm".into(),
            kind: SpanKind::Client,
            tags: vec![
                ("provider".into(), Some(provider.to_owned())),
                ("model".into(), Some(model.to_owned())),
            ],
            outcome: None,
        }
    }

    pub fn start_tts(backend: &str, voice_id: Option<&str>) -> VoiceLoopSpan {
        VoiceLoopSpan {
            name: "voice_loop.tts".into(),
            kind: SpanKind::Client,
            tags: vec![
                ("backend".into(), Some(backend.to_owned())),
                ("voice".into(), voice_id.map(str::to_owned)),
            ],
            outcome: None,
        }
    }

    /// Tag a span with its outcome (the C# `RecordOutcome`).
    pub fn record_outcome(span: &mut VoiceLoopSpan, success: bool, error_reason: Option<&str>) {
        span.tags
            .push(("outcome".into(), Some(if success { "success" } else { "failure" }.to_owned())));
        if !success {
            if let Some(reason) = error_reason {
                span.tags.push(("error.message".into(), Some(reason.to_owned())));
                span.outcome = Some(SpanOutcome::Error(reason.to_owned()));
                return;
            }
        }
        if success {
            span.outcome = Some(SpanOutcome::Ok);
        }
    }
}

// ═════════════════════════════════════════════════════════════════════════════
// LocalDevTunnel.cs
// ═════════════════════════════════════════════════════════════════════════════

/// (3.3.0) Resolves a public, internet-reachable URL that maps to a local port.
#[async_trait]
pub trait ILocalDevTunnel: Send + Sync {
    fn provider_id(&self) -> &str;
    fn is_available(&self) -> bool;
    async fn get_public_url(&self, local_port: u16) -> Result<String, TelephonyError>;
}

/// (3.3.0) DI-default that fails — host wires a real tunnel.
#[derive(Debug, Default, Clone, Copy)]
pub struct NullLocalDevTunnel;

#[async_trait]
impl ILocalDevTunnel for NullLocalDevTunnel {
    fn provider_id(&self) -> &str {
        "null"
    }
    fn is_available(&self) -> bool {
        false
    }
    async fn get_public_url(&self, _local_port: u16) -> Result<String, TelephonyError> {
        Err(TelephonyError::InvalidOperation(
            "No local-dev tunnel is configured. Register a CloudflareTunnel / NgrokTunnel / StaticTunnel.".into(),
        ))
    }
}

/// (3.3.0) Static-URL tunnel — caller supplies the public URL up front (best CI).
pub struct StaticLocalDevTunnel {
    public_url: String,
}

impl StaticLocalDevTunnel {
    pub fn new(public_url: &str) -> Result<Self, TelephonyError> {
        if !(public_url.starts_with("http://") || public_url.starts_with("https://")) {
            return Err(TelephonyError::InvalidArgument("publicUrl must be absolute.".into()));
        }
        Ok(Self {
            public_url: public_url.to_owned(),
        })
    }
}

#[async_trait]
impl ILocalDevTunnel for StaticLocalDevTunnel {
    fn provider_id(&self) -> &str {
        "static"
    }
    fn is_available(&self) -> bool {
        true
    }
    async fn get_public_url(&self, _local_port: u16) -> Result<String, TelephonyError> {
        Ok(self.public_url.clone())
    }
}

// ═════════════════════════════════════════════════════════════════════════════
// PhoneNumberProvisioner.cs — buy + configure + persist orchestration
// ═════════════════════════════════════════════════════════════════════════════

/// (3.3.0) Persistence contract for assigned numbers.
#[async_trait]
pub trait IProvisionedNumberStore: Send + Sync {
    async fn save(&self, number: ProvisionedNumber);
    async fn list(&self) -> Vec<ProvisionedNumber>;
    async fn find(&self, phone_number: &str) -> Option<ProvisionedNumber>;
    async fn remove(&self, phone_number: &str);
}

/// (3.3.0) Default in-memory store. Thread-safe.
#[derive(Default)]
pub struct InMemoryProvisionedNumberStore {
    by_number: Mutex<HashMap<String, ProvisionedNumber>>,
}

#[async_trait]
impl IProvisionedNumberStore for InMemoryProvisionedNumberStore {
    async fn save(&self, number: ProvisionedNumber) {
        self.by_number
            .lock()
            .unwrap()
            .insert(number.phone_number.to_lowercase(), number);
    }
    async fn list(&self) -> Vec<ProvisionedNumber> {
        self.by_number.lock().unwrap().values().cloned().collect()
    }
    async fn find(&self, phone_number: &str) -> Option<ProvisionedNumber> {
        self.by_number.lock().unwrap().get(&phone_number.to_lowercase()).cloned()
    }
    async fn remove(&self, phone_number: &str) {
        self.by_number.lock().unwrap().remove(&phone_number.to_lowercase());
    }
}

/// (3.3.0) Service that buys + configures + persists phone numbers from any
/// carrier behind [`ITelephonyCarrier`].
pub struct PhoneNumberProvisioner<C: ITelephonyCarrier, S: IProvisionedNumberStore> {
    carrier: C,
    store: S,
}

impl<C: ITelephonyCarrier, S: IProvisionedNumberStore> PhoneNumberProvisioner<C, S> {
    pub fn new(carrier: C, store: S) -> Self {
        Self { carrier, store }
    }

    /// (3.3.0) Buy a number, wire its inbound webhook, persist it, return it.
    pub async fn provision(
        &self,
        country_code: &str,
        inbound_webhook: &str,
        area_code: Option<&str>,
    ) -> Result<ProvisionedNumber, ProvisionError<C::Error>> {
        if country_code.trim().is_empty() {
            return Err(ProvisionError::Telephony(TelephonyError::InvalidArgument(
                "countryCode is required".into(),
            )));
        }
        if !(inbound_webhook.starts_with("http://") || inbound_webhook.starts_with("https://")) {
            return Err(ProvisionError::Telephony(TelephonyError::InvalidArgument(
                "inboundWebhook must be an absolute URI".into(),
            )));
        }

        let provisioned = self
            .carrier
            .provision_number(country_code, area_code)
            .await
            .map_err(ProvisionError::Carrier)?;
        self.carrier
            .configure_inbound_webhook(&provisioned.phone_number, inbound_webhook)
            .await
            .map_err(ProvisionError::Carrier)?;
        self.store.save(provisioned.clone()).await;
        Ok(provisioned)
    }

    /// (3.3.0) The provisioned numbers we know about, locally + via the carrier.
    pub async fn list(&self) -> Result<Vec<ProvisionedNumber>, C::Error> {
        let stored = self.store.list().await;
        let carrier_numbers = self.carrier.list_numbers().await?;
        let mut merged: HashMap<String, ProvisionedNumber> = HashMap::new();
        for n in stored {
            merged.insert(n.phone_number.to_lowercase(), n);
        }
        for n in carrier_numbers {
            merged.insert(n.phone_number.to_lowercase(), n);
        }
        Ok(merged.into_values().collect())
    }
}

/// Error surface for [`PhoneNumberProvisioner::provision`].
#[derive(Debug)]
pub enum ProvisionError<E> {
    Telephony(TelephonyError),
    Carrier(E),
}

impl<E: fmt::Display> fmt::Display for ProvisionError<E> {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            ProvisionError::Telephony(e) => write!(f, "{e}"),
            ProvisionError::Carrier(e) => write!(f, "carrier error: {e}"),
        }
    }
}

impl<E: std::error::Error> std::error::Error for ProvisionError<E> {}

// ═════════════════════════════════════════════════════════════════════════════
// ServiceCollectionExtensions.cs — the portable part: multi-carrier failover
// ═════════════════════════════════════════════════════════════════════════════

/// (3.3.0) Multi-carrier failover — picks the first configured carrier. Ported
/// from the DI helper's `CarrierFallback`; the carriers must share a `Session`
/// type. Trait objects are boxed behind [`ITelephonyCarrier`].
pub struct CarrierFallback<E, S>
where
    E: std::error::Error,
    S: ICallSession,
{
    carriers: Vec<Box<dyn ITelephonyCarrier<Error = E, Session = S> + Send + Sync>>,
}

impl<E, S> CarrierFallback<E, S>
where
    E: std::error::Error,
    S: ICallSession,
{
    pub fn new(carriers: Vec<Box<dyn ITelephonyCarrier<Error = E, Session = S> + Send + Sync>>) -> Self {
        Self { carriers }
    }

    fn pick(&self) -> Option<&(dyn ITelephonyCarrier<Error = E, Session = S> + Send + Sync)> {
        self.carriers
            .iter()
            .find(|c| c.is_configured())
            .map(|b| b.as_ref())
    }
}

#[async_trait]
impl<E, S> ITelephonyCarrier for CarrierFallback<E, S>
where
    E: std::error::Error + Send + Sync + 'static,
    S: ICallSession + Send + 'static,
{
    type Error = TelephonyError;
    type Session = S;

    fn carrier_id(&self) -> &str {
        // The C# returns a formatted `fallback(N)`; a `&str` return can't own that
        // dynamic string, so a stable label is used instead.
        "fallback"
    }
    fn is_configured(&self) -> bool {
        self.carriers.iter().any(|c| c.is_configured())
    }
    async fn provision_number(
        &self,
        country_code: &str,
        area_code: Option<&str>,
    ) -> Result<ProvisionedNumber, TelephonyError> {
        let carrier = self
            .pick()
            .ok_or_else(|| TelephonyError::InvalidOperation("No configured carrier.".into()))?;
        carrier
            .provision_number(country_code, area_code)
            .await
            .map_err(|e| TelephonyError::InvalidOperation(e.to_string()))
    }
    async fn configure_inbound_webhook(
        &self,
        phone_number: &str,
        inbound_webhook: &str,
    ) -> Result<(), TelephonyError> {
        let carrier = self
            .pick()
            .ok_or_else(|| TelephonyError::InvalidOperation("No configured carrier.".into()))?;
        carrier
            .configure_inbound_webhook(phone_number, inbound_webhook)
            .await
            .map_err(|e| TelephonyError::InvalidOperation(e.to_string()))
    }
    async fn dial(
        &self,
        from_number: &str,
        to_number: &str,
        stream_url: &str,
        options: Option<OutboundDialOptions>,
    ) -> Result<S, TelephonyError> {
        let carrier = self
            .pick()
            .ok_or_else(|| TelephonyError::InvalidOperation("No configured carrier.".into()))?;
        carrier
            .dial(from_number, to_number, stream_url, options)
            .await
            .map_err(|e| TelephonyError::InvalidOperation(e.to_string()))
    }
    async fn list_numbers(&self) -> Result<Vec<ProvisionedNumber>, TelephonyError> {
        match self.pick() {
            Some(carrier) => carrier
                .list_numbers()
                .await
                .map_err(|e| TelephonyError::InvalidOperation(e.to_string())),
            None => Ok(Vec::new()),
        }
    }
}

// ═════════════════════════════════════════════════════════════════════════════
// In-memory HTTP-JSON client for tests
// ═════════════════════════════════════════════════════════════════════════════

/// A scripted [`IHttpJsonClient`] for tests: returns a fixed `(status, body)` for
/// every POST and records the requests it saw. Mirrors how the C# tests fake
/// `HttpClient`.
#[derive(Default)]
pub struct InMemoryHttpJsonClient {
    response_status: u16,
    response_body: String,
    requests: Mutex<Vec<(String, String, Option<String>)>>,
}

impl InMemoryHttpJsonClient {
    pub fn new(response_status: u16, response_body: &str) -> Self {
        Self {
            response_status,
            response_body: response_body.to_owned(),
            requests: Mutex::new(Vec::new()),
        }
    }

    /// The `(url, body, authorization)` tuples seen so far.
    pub fn requests(&self) -> Vec<(String, String, Option<String>)> {
        self.requests.lock().unwrap().clone()
    }
}

#[async_trait]
impl IHttpJsonClient for InMemoryHttpJsonClient {
    async fn post_json(
        &self,
        url: &str,
        body_json: &str,
        authorization: Option<&str>,
    ) -> Result<(u16, String), TelephonyError> {
        self.requests.lock().unwrap().push((
            url.to_owned(),
            body_json.to_owned(),
            authorization.map(str::to_owned),
        ));
        Ok((self.response_status, self.response_body.clone()))
    }
}

// ═════════════════════════════════════════════════════════════════════════════
// Shared helpers
// ═════════════════════════════════════════════════════════════════════════════

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

/// Minimal JSON string encoder (escapes `"` `\` and control chars).
fn json_string(s: &str) -> String {
    let mut out = String::with_capacity(s.len() + 2);
    out.push('"');
    for c in s.chars() {
        match c {
            '"' => out.push_str("\\\""),
            '\\' => out.push_str("\\\\"),
            '\n' => out.push_str("\\n"),
            '\r' => out.push_str("\\r"),
            '\t' => out.push_str("\\t"),
            c if (c as u32) < 0x20 => out.push_str(&format!("\\u{:04x}", c as u32)),
            c => out.push(c),
        }
    }
    out.push('"');
    out
}

/// Parses a *flat* JSON object (string / scalar values, one level deep) into a
/// `String → String` map. Sufficient for the consult-reply / MCP-descriptor
/// shapes these types read; not a general JSON parser. Returns `None` on
/// malformed input.
fn parse_flat_json_object(bytes: &[u8]) -> Option<HashMap<String, String>> {
    let s = std::str::from_utf8(bytes).ok()?;
    let s = s.trim();
    let s = s.strip_prefix('{')?.strip_suffix('}')?;
    let chars: Vec<char> = s.chars().collect();
    let n = chars.len();
    let mut i = 0usize;
    let mut map = HashMap::new();

    let skip_ws = |i: &mut usize| {
        while *i < n && chars[*i].is_whitespace() {
            *i += 1;
        }
    };
    let parse_string = |i: &mut usize| -> Option<String> {
        if *i >= n || chars[*i] != '"' {
            return None;
        }
        *i += 1;
        let mut out = String::new();
        while *i < n {
            let c = chars[*i];
            *i += 1;
            match c {
                '"' => return Some(out),
                '\\' => {
                    if *i >= n {
                        return None;
                    }
                    let e = chars[*i];
                    *i += 1;
                    match e {
                        '"' => out.push('"'),
                        '\\' => out.push('\\'),
                        '/' => out.push('/'),
                        'n' => out.push('\n'),
                        'r' => out.push('\r'),
                        't' => out.push('\t'),
                        'u' => {
                            if *i + 4 > n {
                                return None;
                            }
                            let hex: String = chars[*i..*i + 4].iter().collect();
                            *i += 4;
                            let cp = u32::from_str_radix(&hex, 16).ok()?;
                            out.push(char::from_u32(cp)?);
                        }
                        _ => return None,
                    }
                }
                c => out.push(c),
            }
        }
        None
    };

    loop {
        skip_ws(&mut i);
        if i >= n {
            break;
        }
        let key = parse_string(&mut i)?;
        skip_ws(&mut i);
        if i >= n || chars[i] != ':' {
            return None;
        }
        i += 1;
        skip_ws(&mut i);
        if i >= n {
            return None;
        }
        let value = if chars[i] == '"' {
            parse_string(&mut i)?
        } else {
            let start = i;
            while i < n && chars[i] != ',' && chars[i] != '}' && !chars[i].is_whitespace() {
                i += 1;
            }
            chars[start..i].iter().collect::<String>()
        };
        map.insert(key, value);
        skip_ws(&mut i);
        if i < n && chars[i] == ',' {
            i += 1;
            continue;
        }
        break;
    }
    Some(map)
}

/// Extracts the `result.tools[]` array of an MCP `tools/list` response into
/// descriptors. Reads each object's `name` / `description` string fields and
/// captures the raw `inputSchema` sub-object. Best-effort; returns `[]` on a
/// shape it can't read.
fn extract_mcp_tools(body: &str) -> Vec<McpToolDescriptor> {
    // Find the `"tools"` array opening bracket after a `"result"`.
    let tools_key = match body.find("\"tools\"") {
        Some(i) => i,
        None => return Vec::new(),
    };
    let after = &body[tools_key..];
    let arr_start = match after.find('[') {
        Some(i) => tools_key + i,
        None => return Vec::new(),
    };
    let bytes: Vec<char> = body.chars().collect();
    let arr_start_char = body[..arr_start].chars().count();
    let mut out = Vec::new();

    // Walk the array, extracting each top-level `{ … }` object.
    let n = bytes.len();
    let mut i = arr_start_char + 1;
    while i < n {
        // Skip to the next object start or array end.
        while i < n && bytes[i] != '{' && bytes[i] != ']' {
            i += 1;
        }
        if i >= n || bytes[i] == ']' {
            break;
        }
        // Capture the balanced object (respecting strings + escapes).
        let obj_start = i;
        let mut depth = 0i32;
        let mut in_str = false;
        let mut escaped = false;
        while i < n {
            let c = bytes[i];
            if in_str {
                if escaped {
                    escaped = false;
                } else if c == '\\' {
                    escaped = true;
                } else if c == '"' {
                    in_str = false;
                }
            } else {
                match c {
                    '"' => in_str = true,
                    '{' => depth += 1,
                    '}' => {
                        depth -= 1;
                        if depth == 0 {
                            i += 1;
                            break;
                        }
                    }
                    _ => {}
                }
            }
            i += 1;
        }
        let obj: String = bytes[obj_start..i].iter().collect();
        let name = extract_json_string_field(&obj, "name").unwrap_or_default();
        let description = extract_json_string_field(&obj, "description").unwrap_or_default();
        let input_schema =
            extract_json_object_field(&obj, "inputSchema").unwrap_or_else(|| "{}".to_owned());
        out.push(McpToolDescriptor {
            name,
            description,
            input_json_schema: input_schema,
        });
    }
    out
}

/// Reads a top-level `"key":"value"` string field out of a small JSON object.
fn extract_json_string_field(obj: &str, key: &str) -> Option<String> {
    let needle = format!("\"{key}\"");
    let key_pos = obj.find(&needle)?;
    let rest = &obj[key_pos + needle.len()..];
    let colon = rest.find(':')?;
    let after: Vec<char> = rest[colon + 1..].chars().collect();
    let mut i = 0;
    while i < after.len() && after[i].is_whitespace() {
        i += 1;
    }
    if i >= after.len() || after[i] != '"' {
        return None;
    }
    i += 1;
    let mut out = String::new();
    let mut escaped = false;
    while i < after.len() {
        let c = after[i];
        if escaped {
            match c {
                'n' => out.push('\n'),
                'r' => out.push('\r'),
                't' => out.push('\t'),
                other => out.push(other),
            }
            escaped = false;
        } else if c == '\\' {
            escaped = true;
        } else if c == '"' {
            return Some(out);
        } else {
            out.push(c);
        }
        i += 1;
    }
    None
}

/// Reads a top-level `"key":{ … }` object field out of a small JSON object,
/// returning its raw text (balanced braces).
fn extract_json_object_field(obj: &str, key: &str) -> Option<String> {
    let needle = format!("\"{key}\"");
    let key_pos = obj.find(&needle)?;
    let rest: Vec<char> = obj[key_pos + needle.len()..].chars().collect();
    let mut i = 0;
    // Advance to the colon.
    while i < rest.len() && rest[i] != ':' {
        i += 1;
    }
    i += 1;
    while i < rest.len() && rest[i].is_whitespace() {
        i += 1;
    }
    if i >= rest.len() || rest[i] != '{' {
        return None;
    }
    let start = i;
    let mut depth = 0i32;
    let mut in_str = false;
    let mut escaped = false;
    while i < rest.len() {
        let c = rest[i];
        if in_str {
            if escaped {
                escaped = false;
            } else if c == '\\' {
                escaped = true;
            } else if c == '"' {
                in_str = false;
            }
        } else {
            match c {
                '"' => in_str = true,
                '{' => depth += 1,
                '}' => {
                    depth -= 1;
                    if depth == 0 {
                        return Some(rest[start..=i].iter().collect());
                    }
                }
                _ => {}
            }
        }
        i += 1;
    }
    None
}

/// Reads the judge JSON: `scores` (nested object of `<dim>:<int>`), `overall`
/// (string), `reasoning` (string). Returns `None` if `scores` is absent or not
/// an object; missing per-dimension scores default to 0 (matching the C#).
fn read_judge_json(json: &str, dims: &[JudgeDimension]) -> Option<JudgeVerdict> {
    let scores_obj = extract_json_object_field(json, "scores")?;
    let scores_map = parse_flat_json_object(scores_obj.as_bytes()).unwrap_or_default();
    let mut scores = HashMap::new();
    for d in dims {
        let v = scores_map
            .get(&d.name)
            .and_then(|s| s.trim().parse::<i32>().ok())
            .unwrap_or(0);
        scores.insert(d.name.clone(), v);
    }
    let overall = extract_json_string_field(json, "overall").unwrap_or_else(|| "borderline".into());
    let reasoning = extract_json_string_field(json, "reasoning").unwrap_or_default();
    Some(JudgeVerdict {
        scores,
        overall,
        reasoning,
    })
}
