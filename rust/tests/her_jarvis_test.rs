//! her_jarvis_test.rs
//!
//! Verifies the remaining HER/Jarvis companion contracts + their real in-memory
//! implementations. Mirrors the behaviour of the C# reference in
//! `HerJarvisRealImplementations.cs`.

use std::collections::BTreeMap;
use std::sync::Arc;

use chrono::{Duration, Utc};
use circle_ai::companion::her_jarvis::*;

// ── 1. AlwaysOnPresence ─────────────────────────────────────────────────────

#[test]
fn presence_starts_stops_and_counts_heartbeats() {
    let p = HeartbeatAlwaysOnPresence::new();
    assert!(!p.is_running());
    assert_eq!(p.heartbeats(), 0);

    p.start();
    assert!(p.is_running());
    // Start fires the first beat immediately (dueTime = Zero in the C#).
    assert_eq!(p.heartbeats(), 1);
    p.beat();
    p.beat();
    assert_eq!(p.heartbeats(), 3);

    p.stop();
    assert!(!p.is_running());
    // Beats while stopped are ignored (timer disposed).
    p.beat();
    assert_eq!(p.heartbeats(), 3);
}

#[test]
fn presence_start_is_idempotent() {
    let p = HeartbeatAlwaysOnPresence::new();
    p.start();
    p.start();
    // Only the first start seeds a beat.
    assert_eq!(p.heartbeats(), 1);
}

// ── 2. FusedPerception ──────────────────────────────────────────────────────

#[test]
fn fused_perception_publishes_and_drains() {
    let fp = ChannelFusedPerception::new();
    assert!(fp.stream().is_empty());
    fp.publish(FusedPercept::new(
        Utc::now(),
        Some("a cat".into()),
        None,
        Some("hello".into()),
        BTreeMap::new(),
    ));
    let out = fp.stream();
    assert_eq!(out.len(), 1);
    assert_eq!(out[0].vision.as_deref(), Some("a cat"));
    // Drained — a second read is empty.
    assert!(fp.stream().is_empty());
}

// ── 3. IdentitySync ─────────────────────────────────────────────────────────

#[test]
fn identity_sync_returns_deltas_after_cursor() {
    let s = JsonIdentitySync::new();
    s.push("{\"a\":1}");
    s.push("{\"b\":2}");
    let payload = s.pull("0");
    assert_eq!(payload, "{\"cursor\":2,\"deltas\":[{\"a\":1},{\"b\":2}]}");
    // Pull from cursor 1 yields only the second delta.
    let after = s.pull("1");
    assert_eq!(after, "{\"cursor\":2,\"deltas\":[{\"b\":2}]}");
    // Nothing after the tip: cursor stays, deltas empty.
    let empty = s.pull("2");
    assert_eq!(empty, "{\"cursor\":2,\"deltas\":[]}");
}

// ── 4. ContinuousLearner ────────────────────────────────────────────────────

#[test]
fn continuous_learner_folds_ewa_reward() {
    let l = EwaContinuousLearner::new(0.5);
    l.register_feedback("i1", 1.0, "{}");
    assert_eq!(l.average_reward_of("i1"), Some(1.0));
    assert_eq!(l.observations_of("i1"), 1);
    // Second observation folds: 1.0*0.5 + 0.0*0.5 = 0.5.
    l.register_feedback("i1", 0.0, "{}");
    assert!((l.average_reward_of("i1").unwrap() - 0.5).abs() < 1e-9);
    assert_eq!(l.observations_of("i1"), 2);
    assert_eq!(l.average_reward_of("unknown"), None);
}

#[test]
#[should_panic(expected = "alpha out of range")]
fn continuous_learner_rejects_bad_alpha() {
    let _ = EwaContinuousLearner::new(0.0);
}

#[test]
#[should_panic(expected = "interactionId required")]
fn continuous_learner_rejects_blank_id() {
    let l = EwaContinuousLearner::default();
    l.register_feedback("  ", 1.0, "{}");
}

// ── 6. GoalPursuer ──────────────────────────────────────────────────────────

#[test]
fn goal_pursuer_registers_and_plans() {
    let g = InMemoryGoalPursuer::new();
    let deadline = Utc::now() + Duration::days(60);
    let goal = g.register("ship v2", deadline);
    assert_eq!(goal.description, "ship v2");
    assert_eq!(goal.progress_fraction, 0.0);
    assert!(goal.plan_json.contains("milestones"));
    // Retrievable by id.
    let fetched = g.current(&goal.id).unwrap();
    assert_eq!(fetched.id, goal.id);
    // Progress updates.
    g.progress(&goal.id, 0.5);
    assert!((g.current(&goal.id).unwrap().progress_fraction - 0.5).abs() < 1e-9);
    // Replan rebuilds the plan without error.
    g.replan(&goal.id);
    assert!(g.current(&goal.id).unwrap().plan_json.contains("milestones"));
}

#[test]
#[should_panic(expected = "deadline must be in the future")]
fn goal_pursuer_rejects_past_deadline() {
    let g = InMemoryGoalPursuer::new();
    g.register("late", Utc::now() - Duration::days(1));
}

// ── 7. EpisodicMemory (HerJarvis) ───────────────────────────────────────────

#[test]
fn tf_episodic_recall_ranks_by_term_overlap() {
    let m = TfEpisodicMemory::new();
    m.record(EpisodeRecord::new(
        "e1",
        Utc::now(),
        "coffee morning",
        "had coffee with alice",
    ));
    m.record(EpisodeRecord::new(
        "e2",
        Utc::now(),
        "gym session",
        "went to the gym",
    ));
    let hits = m.recall("coffee", 10);
    assert_eq!(hits.len(), 1);
    assert_eq!(hits[0].id, "e1");
    // Empty-query returns nothing.
    assert!(m.recall("!", 10).is_empty());
}

#[test]
#[should_panic(expected = "take out of range")]
fn tf_episodic_rejects_zero_take() {
    let m = TfEpisodicMemory::new();
    m.recall("x", 0);
}

// ── 8. VoiceIdentity ────────────────────────────────────────────────────────

/// Builds a deterministic PCM-16 tone buffer of `n` samples at `freq` Hz.
fn tone_pcm16(freq: f64, n: usize, sample_rate: f64) -> Vec<u8> {
    let mut bytes = Vec::with_capacity(n * 2);
    for i in 0..n {
        let t = i as f64 / sample_rate;
        let s = (2.0 * std::f64::consts::PI * freq * t).sin();
        let v = (s * 20000.0) as i16;
        bytes.push((v & 0xFF) as u8);
        bytes.push(((v >> 8) & 0xFF) as u8);
    }
    bytes
}

#[test]
fn voice_identity_matches_enrolled_speaker() {
    let vi = EnergyBandVoiceIdentity::new();
    // Enroll speaker "amy" with a 220 Hz tone.
    let amy = tone_pcm16(220.0, 8000, 16000.0);
    vi.enroll("amy", &amy, 16000);
    // The same tone identifies as amy (self-similarity == 1.0 > 0.85).
    let probe = tone_pcm16(220.0, 8000, 16000.0);
    assert_eq!(vi.identify(&probe, 16000).as_deref(), Some("amy"));
}

#[test]
fn voice_identity_unknown_when_no_enrolments() {
    let vi = EnergyBandVoiceIdentity::new();
    let probe = tone_pcm16(300.0, 8000, 16000.0);
    assert_eq!(vi.identify(&probe, 16000), None);
}

#[test]
#[should_panic(expected = "userId required")]
fn voice_identity_rejects_blank_user() {
    let vi = EnergyBandVoiceIdentity::new();
    vi.enroll("", &[0u8; 800], 16000);
}

// ── 9. CalibratedConfidence ─────────────────────────────────────────────────

#[test]
fn confidence_band_is_within_unit_interval() {
    let c = HistoricalCalibratedConfidence::new();
    let band = c.evaluate("A fairly detailed answer with substance.", "{\"k\":1}");
    assert!(band.lower >= 0.0 && band.upper <= 1.0);
    assert!(band.lower <= band.upper);
}

#[test]
fn confidence_calibrates_from_history() {
    let c = HistoricalCalibratedConfidence::new();
    // Record 5 outcomes so calibration engages.
    for _ in 0..5 {
        c.record_outcome(0.5, true);
    }
    let band = c.evaluate("some answer", "{}");
    // With all-correct nearby outcomes, calibrated ~1.0, band tightens toward 1.
    assert!(band.upper >= band.lower);
    assert!(band.upper <= 1.0);
}

#[test]
fn confidence_hedges_lower_the_score() {
    let c = HistoricalCalibratedConfidence::new();
    let confident = c.evaluate("The capital is Paris and that is certain.", "{}");
    let hedged = c.evaluate("Maybe perhaps it might possibly be Paris.", "{}");
    // Hedge words push the raw (hence calibrated, with < 5 history) score down.
    assert!(hedged.lower <= confident.lower + 1e-9);
}

// ── 11. EmotionSensor ───────────────────────────────────────────────────────

#[test]
fn emotion_sensor_detects_joy() {
    let s = KeywordEmotionSensor::new();
    let f = s.sense("{\"text\":\"I am so happy and excited, this is wonderful\"}");
    assert_eq!(f.label, "joy");
    assert!(f.valence > 0.0);
    assert!(f.arousal > 0.0);
}

#[test]
fn emotion_sensor_neutral_when_no_keywords() {
    let s = KeywordEmotionSensor::new();
    let f = s.sense("{\"text\":\"the meeting is at noon\"}");
    assert_eq!(f.label, "neutral");
    assert_eq!(f.arousal, 0.0);
    assert_eq!(f.valence, 0.0);
}

// ── 12. SkillAcquisition ────────────────────────────────────────────────────

#[test]
fn skill_acquisition_names_from_demo_and_lists_sorted() {
    let s = DemoStoreSkillAcquisition::new();
    s.acquire("{\"name\":\"zed-skill\"}");
    s.acquire("{\"name\":\"alpha-skill\"}");
    let list = s.list();
    assert_eq!(list.len(), 2);
    // Ordered by name.
    assert_eq!(list[0].name, "alpha-skill");
    assert_eq!(list[1].name, "zed-skill");
    // A nameless demo falls back to skill-<id6>.
    let anon = s.acquire("{}");
    assert!(anon.name.starts_with("skill-"));
}

// ── 15. PersonalKnowledgeGraph ──────────────────────────────────────────────

#[test]
fn knowledge_graph_upserts_nodes_and_neighbours() {
    let g = AdjacencyPersonalKnowledgeGraph::new();
    g.upsert_node(KnowledgeNode::new("p1", "person", "Alice", BTreeMap::new()));
    g.upsert_node(KnowledgeNode::new("c1", "company", "Acme", BTreeMap::new()));
    g.upsert_relation(KnowledgeRelation::new("p1", "c1", "works_at"));
    let neigh = g.neighbours("p1");
    assert_eq!(neigh.len(), 1);
    assert_eq!(neigh[0].id, "c1");
    // Dedup: same (to, relation) upserted twice → still one edge.
    g.upsert_relation(KnowledgeRelation::new("p1", "c1", "works_at"));
    assert_eq!(g.neighbours("p1").len(), 1);
    // Unknown node → no neighbours.
    assert!(g.neighbours("nope").is_empty());
}

// ── 16. LiveWorldKnowledge ──────────────────────────────────────────────────

#[test]
fn live_world_knowledge_delivers_to_subscribers() {
    let b = TopicLiveWorldKnowledge::new();
    // Register the topic by subscribing once (drains nothing yet).
    assert!(b.subscribe(&["markets".to_string()]).is_empty());
    b.publish(WorldFact::new("markets", "{\"idx\":100}", Utc::now()));
    // A fact published to an unsubscribed topic is dropped.
    b.publish(WorldFact::new("weather", "{}", Utc::now()));
    let got = b.subscribe(&["markets".to_string()]);
    assert_eq!(got.len(), 1);
    assert_eq!(got[0].topic, "markets");
    // Drained.
    assert!(b.subscribe(&["markets".to_string()]).is_empty());
}

// ── 17. BioSignalStream ─────────────────────────────────────────────────────

#[test]
fn bio_signal_stream_publishes_and_drains() {
    let s = ChannelBioSignalStream::new();
    s.publish(BioSignal::new("hr", 72.0, Utc::now()));
    s.publish(BioSignal::new("spo2", 98.0, Utc::now()));
    let out = s.stream();
    assert_eq!(out.len(), 2);
    assert_eq!(out[0].kind, "hr");
    assert!(s.stream().is_empty());
}

// ── 18. PhysicalActuator ────────────────────────────────────────────────────

#[test]
fn physical_actuator_dispatches_to_registered_device() {
    let a = RegistryPhysicalActuator::new();
    a.register_device(
        "lamp",
        Arc::new(|cmd: &PhysicalCommand| {
            if cmd.action == "on" {
                PhysicalCommandResult::ok()
            } else {
                PhysicalCommandResult::fail("unsupported")
            }
        }),
    );
    let ok = a.invoke(&PhysicalCommand::new("lamp", "on", BTreeMap::new()));
    assert!(ok.succeeded);
    let bad = a.invoke(&PhysicalCommand::new("lamp", "explode", BTreeMap::new()));
    assert!(!bad.succeeded);
    // Unknown device fails cleanly.
    let unknown = a.invoke(&PhysicalCommand::new("ghost", "on", BTreeMap::new()));
    assert!(!unknown.succeeded);
    assert!(unknown.error.unwrap().contains("Unknown device"));
}

// ── 19. AgentPeerNetwork ────────────────────────────────────────────────────

#[test]
fn agent_peer_network_delivers_to_mailbox() {
    let net = MailboxAgentPeerNetwork::new();
    net.send(AgentToAgentMessage::new("a", "b", "ping", Utc::now()));
    net.send(AgentToAgentMessage::new("c", "b", "pong", Utc::now()));
    let msgs = net.receive("b");
    assert_eq!(msgs.len(), 2);
    // Drained.
    assert!(net.receive("b").is_empty());
    // Other agent has no mail.
    assert!(net.receive("a").is_empty());
}

// ── 20. FederatedFineTuner ──────────────────────────────────────────────────

#[test]
fn fine_tuner_runs_injected_trainer_and_tracks_status() {
    let trainer: TrainerFn = Arc::new(|_base, _data| Ok(1.0));
    let ft = InMemoryFederatedFineTuner::new(Some(trainer));
    let job = ft.start("qwen3", "/data/train.jsonl");
    let status = ft.status(&job);
    assert_eq!(status.job_id, job);
    assert!((status.progress - 1.0).abs() < 1e-9);
    assert_eq!(status.error, None);
    // Unknown job.
    assert_eq!(ft.status("nope").error.as_deref(), Some("unknown job"));
}

#[test]
fn fine_tuner_records_trainer_error() {
    let trainer: TrainerFn = Arc::new(|_, _| Err("out of memory".to_string()));
    let ft = InMemoryFederatedFineTuner::new(Some(trainer));
    let job = ft.start("m", "/p");
    assert_eq!(ft.status(&job).error.as_deref(), Some("out of memory"));
}

#[test]
#[should_panic(expected = "baseModel required")]
fn fine_tuner_rejects_blank_model() {
    let ft = InMemoryFederatedFineTuner::default();
    ft.start("  ", "/p");
}

// ── 21. FirstTokenOptimizer ─────────────────────────────────────────────────

#[test]
fn first_token_optimizer_reports_p50() {
    let o = SlidingP50FirstTokenOptimizer::new(100, 8);
    assert_eq!(o.current().current_p50_ms, 0);
    for v in [10, 20, 30, 40, 50] {
        o.record_first_token_latency(v);
    }
    let b = o.current();
    assert_eq!(b.target_ms, 100);
    // Sorted [10,20,30,40,50], p50 = index len/2 = 2 → 30.
    assert_eq!(b.current_p50_ms, 30);
}

#[test]
fn first_token_optimizer_evicts_beyond_window() {
    let o = SlidingP50FirstTokenOptimizer::new(100, 3);
    for v in [1, 2, 3, 4, 5] {
        o.record_first_token_latency(v);
    }
    // Only [3,4,5] remain, p50 = index 1 → 4.
    assert_eq!(o.current().current_p50_ms, 4);
}

// ── 22. CryptoDelegation ────────────────────────────────────────────────────

#[test]
fn crypto_delegation_issues_and_verifies() {
    let d = HmacCryptoDelegation::new("issuer-x", b"secret-key".to_vec());
    let cred = d.issue("subject-1", "read:memory", Duration::hours(1));
    assert_eq!(cred.issuer, "issuer-x");
    assert_eq!(cred.subject_id, "subject-1");
    assert!(!cred.signature.is_empty());
    assert!(d.verify(&cred));
}

#[test]
fn crypto_delegation_rejects_tampered_or_expired() {
    let d = HmacCryptoDelegation::new("issuer-x", b"secret-key".to_vec());
    let cred = d.issue("s", "scope", Duration::hours(1));

    // Tampered scope → signature no longer matches.
    let mut tampered = cred.clone();
    tampered.scope = "admin".to_string();
    assert!(!d.verify(&tampered));

    // Wrong issuer → rejected.
    let other = HmacCryptoDelegation::new("issuer-y", b"secret-key".to_vec());
    assert!(!other.verify(&cred));

    // Expired → rejected.
    let expired = d.issue("s", "scope", Duration::seconds(1));
    let mut past = expired.clone();
    past.expires_at_utc = Utc::now() - Duration::seconds(1);
    assert!(!d.verify(&past));
}

#[test]
fn crypto_delegation_base64_roundtrip_survives_verify() {
    // A second signer with the same key must verify the first's credential
    // (proves the base64 signature encoding round-trips deterministically).
    let key = b"shared-delegation-key".to_vec();
    let a = HmacCryptoDelegation::new("iss", key.clone());
    let b = HmacCryptoDelegation::new("iss", key);
    let cred = a.issue("subj", "scope:x", Duration::hours(2));
    assert!(b.verify(&cred));
}

// ── 23. CodeGenerationLoop ──────────────────────────────────────────────────

#[test]
fn code_gen_loop_defaults_pass_on_balanced_output() {
    let loop_ = SyntaxCheckingCodeGenerationLoop::default();
    let job = loop_.run("add two numbers");
    assert!(!job.id.is_empty());
    // Default generator returns "return 0;" (balanced) → tests pass.
    assert!(job.tests_pass);
    assert!(job.deploy_hint.is_some());
}

#[test]
fn code_gen_loop_fails_on_unbalanced_snippet() {
    let generator: CodeGeneratorFn = Arc::new(|_| "void f() { return; ".to_string());
    let loop_ = SyntaxCheckingCodeGenerationLoop::new(Some(generator), None, None);
    let job = loop_.run("broken");
    assert!(!job.tests_pass);
    assert_eq!(job.deploy_hint, None);
}

#[test]
#[should_panic(expected = "prompt required")]
fn code_gen_loop_rejects_blank_prompt() {
    let loop_ = SyntaxCheckingCodeGenerationLoop::default();
    loop_.run("   ");
}

// ── 24. SelfImprovementLoop (tracking default) ──────────────────────────────

#[test]
fn tracking_self_improvement_records_new_best() {
    let run_bench: RunBenchFn = Arc::new(|_| 0.7);
    let loop_ = TrackingSelfImprovementLoop::new(Some(run_bench), None);
    let v1 = loop_.cycle("suite");
    assert!((v1.new_bench_score - 0.7).abs() < 1e-9);
    assert_eq!(v1.improvements_applied, "new best");
    assert!((loop_.best_score_for("suite") - 0.7).abs() < 1e-9);
    // Re-run at the same score → no regression, not a new best.
    let v2 = loop_.cycle("suite");
    assert_eq!(v2.improvements_applied, "no regression");
}

#[test]
fn tracking_self_improvement_proposes_on_regression() {
    use std::sync::atomic::{AtomicUsize, Ordering};
    let calls = Arc::new(AtomicUsize::new(0));
    let calls2 = calls.clone();
    // First cycle scores 0.8 (best); second scores 0.2 (regression).
    let run_bench: RunBenchFn = Arc::new(move |_| {
        if calls2.fetch_add(1, Ordering::SeqCst) == 0 {
            0.8
        } else {
            0.2
        }
    });
    let proposer: ProposeImprovementFn =
        Arc::new(|_id, score| format!("rollback (was {score:.1})"));
    let loop_ = TrackingSelfImprovementLoop::new(Some(run_bench), Some(proposer));
    assert_eq!(loop_.cycle("s").improvements_applied, "new best");
    let regressed = loop_.cycle("s");
    assert!(regressed.improvements_applied.starts_with("rollback"));
    // Best score is preserved at 0.8.
    assert!((loop_.best_score_for("s") - 0.8).abs() < 1e-9);
}

#[test]
#[should_panic(expected = "benchSuiteId required")]
fn tracking_self_improvement_rejects_blank_suite() {
    let loop_ = TrackingSelfImprovementLoop::default();
    loop_.cycle("  ");
}
