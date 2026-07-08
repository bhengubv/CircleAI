//! voice_listener_test.rs
//!
//! Verifies VoiceCompanionListener: a transcription raises UtteranceDetected,
//! forwards to the session-send closure, and raises ResponseReady with the reply.
//! Session failures never raise ResponseReady. Mirrors the C#
//! VoiceCompanionListener behaviour.

use std::sync::atomic::{AtomicBool, AtomicUsize, Ordering};
use std::sync::{Arc, Mutex};

use chrono::Utc;
use circle_ai::companion::voice_listener::*;

/// A pipeline that just tracks start/stop.
struct FakePipeline {
    started: Arc<AtomicBool>,
    stopped: Arc<AtomicBool>,
}
impl IVoicePipeline for FakePipeline {
    fn start(&self) {
        self.started.store(true, Ordering::SeqCst);
    }
    fn stop(&self) {
        self.stopped.store(true, Ordering::SeqCst);
    }
}

#[test]
fn transcription_forwards_and_raises_response() {
    let started = Arc::new(AtomicBool::new(false));
    let stopped = Arc::new(AtomicBool::new(false));
    let pipeline = Arc::new(FakePipeline {
        started: started.clone(),
        stopped: stopped.clone(),
    });
    let send: SessionSendFn = Arc::new(|text: &str| Ok(format!("reply to: {text}")));
    let listener = VoiceCompanionListener::new(pipeline, send);

    let utterances = Arc::new(Mutex::new(Vec::<String>::new()));
    let responses = Arc::new(Mutex::new(Vec::<(String, String)>::new()));
    let u2 = utterances.clone();
    let r2 = responses.clone();
    listener.on_utterance_detected(Box::new(move |e| u2.lock().unwrap().push(e.text.clone())));
    listener.on_response_ready(Box::new(move |e| {
        r2.lock()
            .unwrap()
            .push((e.original_utterance.clone(), e.text.clone()))
    }));

    listener.start();
    assert!(started.load(Ordering::SeqCst));

    listener.on_transcribed(
        TranscriptionResult {
            text: "hey b, what's the time".into(),
            confidence: 0.92,
        },
        Utc::now(),
    );

    assert_eq!(utterances.lock().unwrap().len(), 1);
    let resp = responses.lock().unwrap();
    assert_eq!(resp.len(), 1);
    assert_eq!(resp[0].0, "hey b, what's the time");
    assert_eq!(resp[0].1, "reply to: hey b, what's the time");
}

#[test]
fn session_failure_raises_utterance_but_not_response() {
    let pipeline = Arc::new(FakePipeline {
        started: Arc::new(AtomicBool::new(false)),
        stopped: Arc::new(AtomicBool::new(false)),
    });
    let send: SessionSendFn = Arc::new(|_| Err("model crashed".to_string()));
    let listener = VoiceCompanionListener::new(pipeline, send);

    let utter_count = Arc::new(AtomicUsize::new(0));
    let resp_count = Arc::new(AtomicUsize::new(0));
    let u2 = utter_count.clone();
    let r2 = resp_count.clone();
    listener.on_utterance_detected(Box::new(move |_| {
        u2.fetch_add(1, Ordering::SeqCst);
    }));
    listener.on_response_ready(Box::new(move |_| {
        r2.fetch_add(1, Ordering::SeqCst);
    }));

    listener.on_transcribed(
        TranscriptionResult {
            text: "hi".into(),
            confidence: 1.0,
        },
        Utc::now(),
    );

    // Utterance detected fired; response ready did not (failure swallowed).
    assert_eq!(utter_count.load(Ordering::SeqCst), 1);
    assert_eq!(resp_count.load(Ordering::SeqCst), 0);
}

#[test]
fn disposed_listener_ignores_transcriptions_and_stops_pipeline() {
    let stopped = Arc::new(AtomicBool::new(false));
    let pipeline = Arc::new(FakePipeline {
        started: Arc::new(AtomicBool::new(false)),
        stopped: stopped.clone(),
    });
    let send: SessionSendFn = Arc::new(|t: &str| Ok(t.to_string()));
    let listener = VoiceCompanionListener::new(pipeline, send);

    let resp_count = Arc::new(AtomicUsize::new(0));
    let r2 = resp_count.clone();
    listener.on_response_ready(Box::new(move |_| {
        r2.fetch_add(1, Ordering::SeqCst);
    }));

    listener.dispose();
    assert!(stopped.load(Ordering::SeqCst));

    listener.on_transcribed(
        TranscriptionResult {
            text: "after dispose".into(),
            confidence: 1.0,
        },
        Utc::now(),
    );
    assert_eq!(resp_count.load(Ordering::SeqCst), 0);
}

#[test]
fn start_stop_drive_the_pipeline() {
    let started = Arc::new(AtomicBool::new(false));
    let stopped = Arc::new(AtomicBool::new(false));
    let pipeline = Arc::new(FakePipeline {
        started: started.clone(),
        stopped: stopped.clone(),
    });
    let send: SessionSendFn = Arc::new(|t: &str| Ok(t.to_string()));
    let listener = VoiceCompanionListener::new(pipeline, send);
    listener.start();
    listener.stop();
    assert!(started.load(Ordering::SeqCst));
    assert!(stopped.load(Ordering::SeqCst));
}
