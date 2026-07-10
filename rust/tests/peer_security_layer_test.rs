//! peer_security_layer_test.rs
//!
//! Ports `AISecurityLayerService.cs` (`SecurityLayerService`),
//! `DirectivePublisher.cs`, and `AetherIntelligenceService.cs`
//! (`PeerIntelligenceService`).

use std::sync::atomic::{AtomicUsize, Ordering};
use std::sync::{Arc, Mutex};

use std::collections::VecDeque;

use chrono::{Duration, Utc};
use circle_ai::security::{
    DirectivePublisher, IPeerDirectiveConsumer, IPeerIntelligence, IPeerSecurityEventFeed,
    IPeerSecurityLayer, NodeTrustRegistry, PeerDirective, PeerDirectiveKind,
    PeerIntelligenceService, PeerSecurityEvent, PeerSecurityEventKind, PeerThreatLevel,
    SecurityLayerService, SecurityOptions,
};

// ── Directive collector ─────────────────────────────────────────────────────

#[derive(Default)]
struct Collector {
    directives: Mutex<Vec<PeerDirective>>,
}

impl IPeerDirectiveConsumer for Collector {
    fn on_directive(&self, directive: &PeerDirective) {
        self.directives.lock().unwrap().push(directive.clone());
    }
}

fn event(node: &str, kind: PeerSecurityEventKind, level: PeerThreatLevel) -> PeerSecurityEvent {
    PeerSecurityEvent::new(node, kind, level, "test-event", "test", Utc::now())
}

fn build() -> (
    Arc<NodeTrustRegistry>,
    Arc<DirectivePublisher>,
    SecurityLayerService,
) {
    let opts = SecurityOptions::default();
    let reg = Arc::new(NodeTrustRegistry::new(opts.clone()));
    let pub_ = Arc::new(DirectivePublisher::new());
    let svc = SecurityLayerService::new(Arc::clone(&reg), opts, Arc::clone(&pub_));
    (reg, pub_, svc)
}

// ── DirectivePublisher ──────────────────────────────────────────────────────

#[test]
fn publisher_fans_out_and_counts_subscribers() {
    let publisher = DirectivePublisher::new();
    let c1 = Arc::new(Collector::default());
    let c2 = Arc::new(Collector::default());
    let s1 = publisher.subscribe(c1.clone());
    let _s2 = publisher.subscribe(c2.clone());
    assert_eq!(publisher.subscriber_count(), 2);

    let d = PeerDirective {
        kind: PeerDirectiveKind::AvoidNode,
        target_node_id: "n1".into(),
        trust_score: 0.4,
        threat_level: PeerThreatLevel::High,
        reason: "r".into(),
        duration: None,
        issued_at: Utc::now(),
    };
    publisher.publish(&d);
    assert_eq!(c1.directives.lock().unwrap().len(), 1);
    assert_eq!(c2.directives.lock().unwrap().len(), 1);

    // Drop s1 -> unsubscribe.
    drop(s1);
    assert_eq!(publisher.subscriber_count(), 1);
    publisher.publish(&d);
    assert_eq!(c1.directives.lock().unwrap().len(), 1, "unsubscribed");
    assert_eq!(c2.directives.lock().unwrap().len(), 2);
}

#[test]
fn subscription_unsubscribe_is_idempotent() {
    let publisher = DirectivePublisher::new();
    let c = Arc::new(Collector::default());
    let s = publisher.subscribe(c);
    s.unsubscribe();
    assert_eq!(publisher.subscriber_count(), 0);
    // A dropped-after-unsubscribe handle must not panic (no double-remove issue).
}

// ── SecurityLayerService: threshold crossings ───────────────────────────────

#[test]
fn none_level_event_has_no_effect() {
    let (reg, _pub, svc) = build();
    let c = Arc::new(Collector::default());
    let _s = svc.subscribe_to_directives(c.clone());
    svc.handle_peer_event(&event("n1", PeerSecurityEventKind::AuthAttempt, PeerThreatLevel::None));
    assert_eq!(reg.get_trust_score("n1"), 1.0);
    assert!(c.directives.lock().unwrap().is_empty());
}

#[test]
fn crossing_elevate_threshold_issues_elevate_monitoring() {
    // Default elevate threshold = 0.75. One IntrusionSignal/High = 0.15*2 = 0.30
    // -> 1.0 -> 0.70, crossing 0.75.
    let (_reg, _pub, svc) = build();
    let c = Arc::new(Collector::default());
    let _s = svc.subscribe_to_directives(c.clone());
    svc.handle_peer_event(&event(
        "n1",
        PeerSecurityEventKind::IntrusionSignal,
        PeerThreatLevel::High,
    ));
    let d = c.directives.lock().unwrap();
    assert_eq!(d.len(), 1);
    assert_eq!(d[0].kind, PeerDirectiveKind::ElevateMonitoring);
    assert_eq!(d[0].threat_level, PeerThreatLevel::Medium);
}

#[test]
fn crossing_quarantine_threshold_issues_quarantine_only() {
    // One big Critical hit that drops below 0.25 in a single event should issue
    // the most-severe directive only (Quarantine), not Avoid/Elevate too.
    let (_reg, _pub, svc) = build();
    let c = Arc::new(Collector::default());
    let _s = svc.subscribe_to_directives(c.clone());
    // IntrusionSignal/Critical = 0.15*3 = 0.45. 1.0 -> 0.55 (not enough).
    // Apply twice: 0.55 -> 0.10 (crosses quarantine).
    svc.handle_peer_event(&event(
        "n1",
        PeerSecurityEventKind::IntrusionSignal,
        PeerThreatLevel::Critical,
    ));
    svc.handle_peer_event(&event(
        "n1",
        PeerSecurityEventKind::IntrusionSignal,
        PeerThreatLevel::Critical,
    ));
    let d = c.directives.lock().unwrap();
    // First event crosses elevate (1.0->0.55 crosses 0.75) -> ElevateMonitoring.
    // Second event 0.55->0.10 crosses BOTH avoid(0.50) and quarantine(0.25);
    // most-severe-wins => Quarantine.
    let kinds: Vec<_> = d.iter().map(|x| x.kind).collect();
    assert_eq!(
        kinds,
        vec![
            PeerDirectiveKind::ElevateMonitoring,
            PeerDirectiveKind::QuarantineNode
        ]
    );
    assert_eq!(d[1].threat_level, PeerThreatLevel::Critical);
}

#[test]
fn no_duplicate_directive_when_staying_below_threshold() {
    // Once below elevate threshold, further small degradations that do not cross
    // a NEW threshold must not re-issue ElevateMonitoring.
    let (_reg, _pub, svc) = build();
    let c = Arc::new(Collector::default());
    let _s = svc.subscribe_to_directives(c.clone());
    // Cross elevate: 0.30 drop.
    svc.handle_peer_event(&event(
        "n1",
        PeerSecurityEventKind::IntrusionSignal,
        PeerThreatLevel::High,
    )); // ->0.70 elevate
        // Small drop staying between avoid(0.50) and elevate(0.75): AuthAttempt/Medium=0.05 ->0.65
    svc.handle_peer_event(&event(
        "n1",
        PeerSecurityEventKind::AuthAttempt,
        PeerThreatLevel::Medium,
    ));
    let d = c.directives.lock().unwrap();
    assert_eq!(d.len(), 1, "only the threshold crossing issues a directive");
}

// ── Posture ─────────────────────────────────────────────────────────────────

#[test]
fn posture_reflects_active_flag_and_counts() {
    let (_reg, _pub, svc) = build();
    // Before start.
    let p0 = svc.get_posture();
    assert!(!p0.is_active);
    assert_eq!(p0.overall_threat_level, PeerThreatLevel::None);

    svc.start();
    assert!(svc.is_active());

    // Drive one node into quarantine and one into monitored range.
    // n1: two Critical intrusions -> ~0.10 (quarantined).
    svc.handle_peer_event(&event("n1", PeerSecurityEventKind::IntrusionSignal, PeerThreatLevel::Critical));
    svc.handle_peer_event(&event("n1", PeerSecurityEventKind::IntrusionSignal, PeerThreatLevel::Critical));
    // n2: one High intrusion -> 0.70 (monitored, between quarantine and elevate).
    svc.handle_peer_event(&event("n2", PeerSecurityEventKind::IntrusionSignal, PeerThreatLevel::High));

    let p = svc.get_posture();
    assert!(p.is_active);
    assert_eq!(p.quarantined_peer_count, 1);
    assert_eq!(p.monitored_peer_count, 1);
    // Worst score ~0.10 -> Critical.
    assert_eq!(p.overall_threat_level, PeerThreatLevel::Critical);

    svc.stop();
    assert!(!svc.get_posture().is_active);
}

#[test]
fn tick_recovery_only_runs_when_active() {
    let (reg, _pub, svc) = build();
    svc.handle_peer_event(&event("n1", PeerSecurityEventKind::IntrusionSignal, PeerThreatLevel::High)); // ->0.70
    reg.trust_score_updates();

    // Inactive: no recovery.
    svc.tick_recovery(Duration::seconds(100));
    assert!((reg.get_trust_score("n1") - 0.70).abs() < 1e-9);

    svc.start();
    svc.tick_recovery(Duration::seconds(100)); // recovery_rate 0.001 * 100 = 0.1 -> 0.80
    assert!(reg.get_trust_score("n1") > 0.70);
}

// ── PeerIntelligenceService ─────────────────────────────────────────────────

#[test]
fn network_health_empty_is_excellent() {
    let reg = Arc::new(NodeTrustRegistry::new(SecurityOptions::default()));
    let intel = PeerIntelligenceService::new(reg, SecurityOptions::default());
    let h = intel.get_network_health();
    assert_eq!(h.overall_score, 1.0);
    assert_eq!(h.trusted_peer_count, 0);
    assert_eq!(h.suspicious_peer_count, 0);
    assert_eq!(h.summary, "No peers observed.");
}

#[test]
fn network_health_counts_trusted_and_suspicious() {
    let opts = SecurityOptions::default();
    let reg = Arc::new(NodeTrustRegistry::new(opts.clone()));
    // trusted node stays at 1.0.
    reg.get_or_create("good");
    // suspicious node: drop below elevate (0.75).
    reg.apply_degradation(
        &event("bad", PeerSecurityEventKind::IntrusionSignal, PeerThreatLevel::High),
        0.30,
    ); // ->0.70
    let intel = PeerIntelligenceService::new(Arc::clone(&reg), opts);
    let h = intel.get_network_health();
    // 'good' at 1.0 and 'bad' at 0.70 are both > avoid(0.5) -> both trusted.
    // 'bad' at 0.70 <= elevate(0.75) -> suspicious.
    assert_eq!(h.trusted_peer_count, 2);
    assert_eq!(h.suspicious_peer_count, 1);
    assert!((h.overall_score - 0.85).abs() < 1e-9); // (1.0 + 0.70)/2
}

#[test]
fn assess_threat_confidence_and_indicators() {
    let opts = SecurityOptions::default();
    let reg = Arc::new(NodeTrustRegistry::new(opts.clone()));
    // Three auth attempts (brute force indicator) + trust drop.
    for _ in 0..3 {
        reg.apply_degradation(
            &event("n1", PeerSecurityEventKind::AuthAttempt, PeerThreatLevel::Medium),
            0.10,
        );
    }
    // Score now 0.70. deficit 0.30. indicators: repeated-auth-attempts (1) -> +0.10.
    let intel = PeerIntelligenceService::new(Arc::clone(&reg), opts);
    let a = intel.assess_threat("n1");
    assert_eq!(a.node_id, "n1");
    assert_eq!(a.threat_level, PeerThreatLevel::Medium); // 0.70 in (0.50,0.75]
    assert!(a.indicators.contains(&"repeated-auth-attempts".to_string()));
    assert!((a.confidence - 0.40).abs() < 1e-9, "0.30 deficit + 0.10 indicator");
}

#[test]
fn assess_threat_unknown_node_is_clean() {
    let intel = PeerIntelligenceService::new(
        Arc::new(NodeTrustRegistry::new(SecurityOptions::default())),
        SecurityOptions::default(),
    );
    let a = intel.assess_threat("ghost");
    assert_eq!(a.threat_level, PeerThreatLevel::None);
    assert_eq!(a.confidence, 0.0);
    assert!(a.indicators.is_empty());
}

#[test]
fn routing_advice_direct_when_trusted() {
    let opts = SecurityOptions::default();
    let reg = Arc::new(NodeTrustRegistry::new(opts.clone()));
    reg.get_or_create("dest"); // 1.0
    let intel = PeerIntelligenceService::new(Arc::clone(&reg), opts);
    let adv = intel.get_routing_advice("dest");
    assert_eq!(adv.recommended_path, vec!["dest".to_string()]);
    assert!(adv.avoid_node_ids.is_empty());
    assert_eq!(adv.confidence, 1.0);
    assert!(adv.reasoning.contains("trusted"));
}

#[test]
fn routing_advice_no_path_when_quarantined() {
    let opts = SecurityOptions::default();
    let reg = Arc::new(NodeTrustRegistry::new(opts.clone()));
    // Drop dest below avoid(0.5) and below 0.25.
    reg.apply_degradation(
        &event("dest", PeerSecurityEventKind::IntrusionSignal, PeerThreatLevel::Critical),
        0.9,
    ); // ->0.10
    let intel = PeerIntelligenceService::new(Arc::clone(&reg), opts);
    let adv = intel.get_routing_advice("dest");
    assert!(adv.recommended_path.is_empty());
    assert!(adv.avoid_node_ids.contains(&"dest".to_string()));
    assert!(adv.reasoning.contains("quarantined"));
}

#[test]
fn stream_trust_scores_drains_backlog() {
    let opts = SecurityOptions::default();
    let reg = Arc::new(NodeTrustRegistry::new(opts.clone()));
    reg.apply_degradation(&event("n1", PeerSecurityEventKind::AuthAttempt, PeerThreatLevel::Medium), 0.1);
    reg.apply_degradation(&event("n2", PeerSecurityEventKind::AuthAttempt, PeerThreatLevel::Medium), 0.1);
    let intel = PeerIntelligenceService::new(Arc::clone(&reg), opts);
    let updates = intel.stream_trust_scores();
    assert_eq!(updates.len(), 2);
    assert!(intel.stream_trust_scores().is_empty());
}

// ── Concurrency smoke: publish from many threads ────────────────────────────

#[test]
fn concurrent_events_do_not_deadlock() {
    let (reg, _pub, svc) = build();
    let svc = Arc::new(svc);
    let counter = Arc::new(AtomicUsize::new(0));

    struct Counting(Arc<AtomicUsize>);
    impl IPeerDirectiveConsumer for Counting {
        fn on_directive(&self, _d: &PeerDirective) {
            self.0.fetch_add(1, Ordering::SeqCst);
        }
    }
    let _s = svc.subscribe_to_directives(Arc::new(Counting(counter.clone())));

    let mut handles = Vec::new();
    for t in 0..8 {
        let svc = Arc::clone(&svc);
        handles.push(std::thread::spawn(move || {
            for i in 0..50 {
                let node = format!("node-{}", (t * 50 + i) % 10);
                svc.handle_peer_event(&PeerSecurityEvent::new(
                    node,
                    PeerSecurityEventKind::IntrusionSignal,
                    PeerThreatLevel::Critical,
                    "flood",
                    "test",
                    Utc::now(),
                ));
            }
        }));
    }
    for h in handles {
        h.join().unwrap();
    }
    // Every one of the 10 nodes should be fully quarantined and directives fired.
    assert!(counter.load(Ordering::SeqCst) >= 10);
    for n in 0..10 {
        assert!(reg.get_trust_score(&format!("node-{n}")) <= 0.25);
    }
}

// ── IPeerSecurityEventFeed contract ─────────────────────────────────────────
//
// In C# `IPeerSecurityEventFeed` is a pure interface implemented by
// transport-adapter packages (WiFi/BLE/Aether bridges), so — matching the
// spec — the Rust port ships the trait with no concrete impl inside the module.
// This test provides a local buffered implementation (the same pattern the
// suite uses for `IPeerDirectiveConsumer`) and verifies the `pump` contract:
// buffered events drain into a handler and wire straight into the security
// layer, degrading trust and issuing directives.

/// A deterministic in-test event source: transports would translate their native
/// events into `PeerSecurityEvent`s and buffer them here; `pump` hands the
/// currently-buffered batch to the handler and reports how many it delivered.
struct BufferedFeed {
    transport_id: String,
    buffer: Mutex<VecDeque<PeerSecurityEvent>>,
}

impl BufferedFeed {
    fn new(transport_id: &str) -> Self {
        Self {
            transport_id: transport_id.to_string(),
            buffer: Mutex::new(VecDeque::new()),
        }
    }

    fn enqueue(&self, e: PeerSecurityEvent) {
        self.buffer.lock().unwrap().push_back(e);
    }
}

impl IPeerSecurityEventFeed for BufferedFeed {
    fn transport_id(&self) -> &str {
        &self.transport_id
    }

    fn pump(&self, handler: &mut dyn FnMut(PeerSecurityEvent)) -> usize {
        let batch: Vec<PeerSecurityEvent> = self.buffer.lock().unwrap().drain(..).collect();
        let n = batch.len();
        for e in batch {
            handler(e);
        }
        n
    }
}

#[test]
fn event_feed_pumps_buffered_events_into_layer() {
    let (reg, _pub, svc) = build();
    let c = Arc::new(Collector::default());
    let _s = svc.subscribe_to_directives(c.clone());

    let feed = BufferedFeed::new("wifi");
    assert_eq!(feed.transport_id(), "wifi");

    // Two Critical intrusions on the same node → ~0.10 → crosses quarantine.
    feed.enqueue(event("n1", PeerSecurityEventKind::IntrusionSignal, PeerThreatLevel::Critical));
    feed.enqueue(event("n1", PeerSecurityEventKind::IntrusionSignal, PeerThreatLevel::Critical));

    // Drive the feed into the security layer, exactly as a transport adapter would.
    let delivered = feed.pump(&mut |e| svc.handle_peer_event(&e));
    assert_eq!(delivered, 2);

    // Events took effect: trust degraded past quarantine, directives issued.
    assert!(reg.get_trust_score("n1") <= 0.25);
    let kinds: Vec<_> = c.directives.lock().unwrap().iter().map(|d| d.kind).collect();
    assert_eq!(
        kinds,
        vec![
            PeerDirectiveKind::ElevateMonitoring,
            PeerDirectiveKind::QuarantineNode
        ]
    );

    // Buffer is now empty — a second pump delivers nothing.
    assert_eq!(feed.pump(&mut |_| {}), 0);
}

#[test]
fn event_feed_empty_pump_is_noop() {
    let (reg, _pub, svc) = build();
    let feed = BufferedFeed::new("ble");
    let delivered = feed.pump(&mut |e| svc.handle_peer_event(&e));
    assert_eq!(delivered, 0);
    assert_eq!(reg.all_node_ids().len(), 0);
}
