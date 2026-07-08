//! voice_listener.rs
//!
//! `IVoiceListener` + `VoiceCompanionListener` — bridges the voice pipeline with
//! the Companion session. Ported from `IVoiceListener.cs` +
//! `VoiceCompanionListener.cs`: when the wake word fires and the user speaks, a
//! transcription is forwarded to the session and the Companion's reply is
//! surfaced via the `ResponseReady` event.
//!
//! The C# uses .NET events (multicast delegates) and a concrete `VoicePipeline`.
//! The Rust port models an event as a registered list of handler closures, the
//! pipeline as an injected [`IVoicePipeline`] seam whose transcriptions drive the
//! listener via [`VoiceCompanionListener::on_transcribed`], and the session as an
//! injected "send" closure (the C# calls `ICompanionSession.SendAsync`). This
//! keeps the event-bridge semantics — utterance-detected then response-ready,
//! never blocking the pipeline on failure — intact and testable.

use std::sync::{Arc, Mutex};

use chrono::{DateTime, Utc};

/// Arguments raised when a user utterance has been transcribed and forwarded.
#[derive(Debug, Clone, PartialEq)]
pub struct UtteranceDetectedEventArgs {
    /// Transcribed text of the user's utterance.
    pub text: String,
    /// Transcription confidence in `[0.0, 1.0]`.
    pub confidence: f32,
    /// UTC timestamp when the transcription completed.
    pub detected_at: DateTime<Utc>,
}

/// Arguments raised when the Companion has produced a reply to a voice utterance.
#[derive(Debug, Clone, PartialEq)]
pub struct ResponseReadyEventArgs {
    /// The Companion's reply text.
    pub text: String,
    /// The utterance that triggered this response.
    pub original_utterance: String,
    /// UTC timestamp when the Companion completed the reply.
    pub completed_at: DateTime<Utc>,
}

/// A transcription result produced by the pipeline.
#[derive(Debug, Clone, PartialEq)]
pub struct TranscriptionResult {
    pub text: String,
    pub confidence: f32,
}

/// The voice pipeline seam. A host wires the real wake-word + ASR pipeline; the
/// listener only needs start/stop and a way to be driven by transcriptions.
pub trait IVoicePipeline: Send + Sync {
    /// Begins listening for the wake word.
    fn start(&self);
    /// Stops listening.
    fn stop(&self);
}

/// The Companion "send" seam — forwards an utterance and returns the reply (or an
/// error, which the listener swallows like the C# try/catch). Mirrors
/// `ICompanionSession.SendAsync`.
pub type SessionSendFn = Arc<dyn Fn(&str) -> Result<String, String> + Send + Sync>;

type UtteranceHandler = Box<dyn Fn(&UtteranceDetectedEventArgs) + Send + Sync>;
type ResponseHandler = Box<dyn Fn(&ResponseReadyEventArgs) + Send + Sync>;

/// Bridges a voice pipeline with a Companion session.
pub trait IVoiceListener: Send + Sync {
    /// Registers a handler for the `UtteranceDetected` event.
    fn on_utterance_detected(&self, handler: UtteranceHandler);
    /// Registers a handler for the `ResponseReady` event.
    fn on_response_ready(&self, handler: ResponseHandler);
    /// Begins listening for the wake word.
    fn start(&self);
    /// Stops listening and cancels any in-flight activation.
    fn stop(&self);
}

/// Concrete [`IVoiceListener`] that wires a [`IVoicePipeline`] to a Companion
/// session-send closure. 1:1 with the C# `VoiceCompanionListener` behaviour.
pub struct VoiceCompanionListener {
    pipeline: Arc<dyn IVoicePipeline>,
    send: SessionSendFn,
    utterance_handlers: Arc<Mutex<Vec<UtteranceHandler>>>,
    response_handlers: Arc<Mutex<Vec<ResponseHandler>>>,
    disposed: Arc<Mutex<bool>>,
}

impl VoiceCompanionListener {
    /// Wires the pipeline to the session-send closure.
    pub fn new(pipeline: Arc<dyn IVoicePipeline>, send: SessionSendFn) -> Self {
        Self {
            pipeline,
            send,
            utterance_handlers: Arc::new(Mutex::new(Vec::new())),
            response_handlers: Arc::new(Mutex::new(Vec::new())),
            disposed: Arc::new(Mutex::new(false)),
        }
    }

    /// Drives one transcription through the bridge: raise `UtteranceDetected`,
    /// forward to the session, and (on success) raise `ResponseReady`. A no-op
    /// once disposed. Session failures are swallowed (the C# traces + continues),
    /// so a failing turn never raises `ResponseReady`.
    pub fn on_transcribed(&self, result: TranscriptionResult, completed_at: DateTime<Utc>) {
        if *self.disposed.lock().unwrap() {
            return;
        }

        let detected = UtteranceDetectedEventArgs {
            text: result.text.clone(),
            confidence: result.confidence,
            detected_at: completed_at,
        };
        for h in self.utterance_handlers.lock().unwrap().iter() {
            h(&detected);
        }

        match (self.send)(&result.text) {
            Ok(reply) => {
                if *self.disposed.lock().unwrap() {
                    return;
                }
                let ready = ResponseReadyEventArgs {
                    text: reply,
                    original_utterance: result.text.clone(),
                    completed_at: Utc::now(),
                };
                for h in self.response_handlers.lock().unwrap().iter() {
                    h(&ready);
                }
            }
            Err(_e) => {
                // Mirror the C# catch: trace + continue; never crash the pipeline.
            }
        }
    }

    /// Detaches the pipeline and stops raising events (mirrors `DisposeAsync`).
    pub fn dispose(&self) {
        let mut disposed = self.disposed.lock().unwrap();
        if *disposed {
            return;
        }
        *disposed = true;
        drop(disposed);
        self.pipeline.stop();
    }
}

impl IVoiceListener for VoiceCompanionListener {
    fn on_utterance_detected(&self, handler: UtteranceHandler) {
        self.utterance_handlers.lock().unwrap().push(handler);
    }

    fn on_response_ready(&self, handler: ResponseHandler) {
        self.response_handlers.lock().unwrap().push(handler);
    }

    fn start(&self) {
        if *self.disposed.lock().unwrap() {
            return;
        }
        self.pipeline.start();
    }

    fn stop(&self) {
        if *self.disposed.lock().unwrap() {
            return;
        }
        self.pipeline.stop();
    }
}
