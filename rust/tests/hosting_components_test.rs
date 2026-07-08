//! hosting_components_test.rs
//!
//! Verifies the smaller hosting building blocks: thermal state machine, memory
//! pressure source, tool catalog search, generative-UI JSON parser, predictive
//! warmup histogram, and the observer bridges (push + aether). Mirrors the
//! respective C# types.

use std::sync::{Arc, Mutex};

use chrono::{Duration, TimeZone, Utc};

use circle_ai::hosting::generative_ui::{
    JsonRenderParser, UiCatalogs,
};
use circle_ai::hosting::memory_pressure::{
    IMemoryPressureSource, ManualMemoryPressureSource, MemoryPressureLevel,
};
use circle_ai::hosting::thermal::{
    classify_kelvin, classify_milli_celsius, IThermalSampler, IThermalThrottleService,
    ManualThermalSampler, ThermalState, ThermalThrottleService,
};
use circle_ai::hosting::tool_catalog::{
    import_from, IToolCatalog, IToolProvider, InMemoryToolCatalog, ToolDescriptor,
};
use circle_ai::hosting::warmup::{HistogramRequestPredictor, IRequestPredictor};

// ── Thermal ─────────────────────────────────────────────────────────────────

#[test]
fn thermal_classifies_thresholds() {
    assert_eq!(classify_milli_celsius(50_000), ThermalState::Normal);
    assert_eq!(classify_milli_celsius(80_000), ThermalState::Serious);
    assert_eq!(classify_milli_celsius(95_000), ThermalState::Critical);
    assert_eq!(classify_kelvin(0.0), ThermalState::Unknown);
    assert_eq!(classify_kelvin(300.0), ThermalState::Normal);
    assert_eq!(classify_kelvin(350.0), ThermalState::Serious);
    assert_eq!(classify_kelvin(365.0), ThermalState::Critical);
}

#[test]
fn thermal_service_fires_state_changed_and_pauses() {
    let sampler = ManualThermalSampler::new(ThermalState::Normal);
    let svc = ThermalThrottleService::new(sampler);

    let fires: Arc<Mutex<Vec<ThermalState>>> = Arc::new(Mutex::new(Vec::new()));
    let f2 = Arc::clone(&fires);
    svc.on_state_changed(Arc::new(move |s| f2.lock().unwrap().push(s)));

    svc.start_monitoring();
    // Started Normal → not paused.
    assert!(!svc.should_pause_inference());
    assert_eq!(svc.current_state(), ThermalState::Normal);
    // One transition Unknown→Normal on start.
    assert_eq!(fires.lock().unwrap().as_slice(), &[ThermalState::Normal]);
}

#[test]
fn thermal_poll_transitions_only_on_change() {
    let sampler = ManualThermalSampler::new(ThermalState::Normal);
    let svc = ThermalThrottleService::new(sampler);
    let count = Arc::new(Mutex::new(0));
    let c2 = Arc::clone(&count);
    svc.on_state_changed(Arc::new(move |_| *c2.lock().unwrap() += 1));
    svc.start_monitoring(); // Unknown→Normal (1)
    svc.poll_once(); // Normal→Normal (no fire)
    assert_eq!(*count.lock().unwrap(), 1);
}

/// A sampler the test can drive after construction.
#[derive(Clone)]
struct Cell(Arc<Mutex<ThermalState>>);
impl IThermalSampler for Cell {
    fn sample(&self) -> ThermalState {
        *self.0.lock().unwrap()
    }
}

#[test]
fn thermal_should_pause_when_serious_or_critical() {
    let cell = Arc::new(Mutex::new(ThermalState::Normal));
    let svc = ThermalThrottleService::new(Cell(cell.clone()));
    svc.start_monitoring();
    *cell.lock().unwrap() = ThermalState::Serious;
    svc.poll_once();
    assert!(svc.should_pause_inference());
}

// ── Memory pressure ─────────────────────────────────────────────────────────

#[test]
fn memory_pressure_raises_transitions_only() {
    let src = ManualMemoryPressureSource::new();
    let seen: Arc<Mutex<Vec<(MemoryPressureLevel, MemoryPressureLevel)>>> =
        Arc::new(Mutex::new(Vec::new()));
    let s2 = Arc::clone(&seen);
    let _sub = src.subscribe(Arc::new(move |old, new| s2.lock().unwrap().push((old, new))));

    assert_eq!(src.current(), MemoryPressureLevel::Normal);
    src.raise(MemoryPressureLevel::Trim);
    src.raise(MemoryPressureLevel::Trim); // same level — no fire
    src.raise(MemoryPressureLevel::Critical);

    let seen = seen.lock().unwrap();
    assert_eq!(
        seen.as_slice(),
        &[
            (MemoryPressureLevel::Normal, MemoryPressureLevel::Trim),
            (MemoryPressureLevel::Trim, MemoryPressureLevel::Critical),
        ]
    );
}

#[test]
fn memory_pressure_unsubscribe_stops_events() {
    let src = ManualMemoryPressureSource::new();
    let count = Arc::new(Mutex::new(0));
    let c2 = Arc::clone(&count);
    let sub = src.subscribe(Arc::new(move |_, _| *c2.lock().unwrap() += 1));
    src.raise(MemoryPressureLevel::Trim);
    sub.unsubscribe();
    src.raise(MemoryPressureLevel::Critical);
    assert_eq!(*count.lock().unwrap(), 1);
}

// ── Tool catalog ────────────────────────────────────────────────────────────

struct StaticProvider;
impl IToolProvider for StaticProvider {
    fn provider_id(&self) -> &str {
        "local"
    }
    fn discover(&self) -> Vec<ToolDescriptor> {
        vec![
            ToolDescriptor::new("gmail.send", "Send an email via Gmail", "gmail")
                .with_tags(vec!["email".to_string(), "communication".to_string()]),
            ToolDescriptor::new("github.issue", "Open a GitHub issue", "github")
                .with_tags(vec!["dev".to_string()]),
        ]
    }
    fn is_available(&self) -> bool {
        true
    }
}

#[test]
fn tool_catalog_import_and_search() {
    let catalog = InMemoryToolCatalog::new();
    let n = import_from(&catalog, &StaticProvider);
    assert_eq!(n, 2);
    assert_eq!(catalog.count(), 2);

    let hits = catalog.search("email", 5);
    assert_eq!(hits.len(), 1);
    assert_eq!(hits[0].name, "gmail.send");

    // Provider filter is case-insensitive.
    assert_eq!(catalog.list_by_provider("GITHUB").len(), 1);
}

#[test]
fn tool_catalog_upsert_is_idempotent_and_case_insensitive_keys() {
    let catalog = InMemoryToolCatalog::new();
    catalog.upsert(ToolDescriptor::new("Foo.Bar", "desc", "p"));
    catalog.upsert(ToolDescriptor::new("foo.bar", "desc2", "p"));
    assert_eq!(catalog.count(), 1);
    assert_eq!(catalog.get("FOO.BAR").unwrap().description, "desc2");
    assert!(catalog.remove("foo.bar"));
    assert_eq!(catalog.count(), 0);
}

// ── Generative UI ───────────────────────────────────────────────────────────

#[test]
fn json_render_parser_accepts_valid_card() {
    let catalog = UiCatalogs::default_catalog();
    let json = r#"{"kind":"card","properties":{"title":"Hi"},"children":[{"kind":"textBlock","properties":{"text":"body"}}]}"#;
    let root = JsonRenderParser::parse(json, &catalog, true).unwrap();
    assert_eq!(root.kind, "card");
    assert_eq!(root.children.as_ref().unwrap().len(), 1);
    assert_eq!(root.children.as_ref().unwrap()[0].kind, "textBlock");
}

#[test]
fn json_render_parser_strict_rejects_unknown_kind() {
    let catalog = UiCatalogs::default_catalog();
    let err = JsonRenderParser::parse(r#"{"kind":"wormhole"}"#, &catalog, true);
    assert!(err.is_err());
}

#[test]
fn json_render_parser_lenient_downgrades_unknown_to_textblock() {
    let catalog = UiCatalogs::default_catalog();
    let root = JsonRenderParser::parse(r#"{"kind":"wormhole"}"#, &catalog, false).unwrap();
    assert_eq!(root.kind, "textBlock");
}

#[test]
fn json_render_parser_strict_rejects_disallowed_property() {
    let catalog = UiCatalogs::default_catalog();
    // 'button' does not allow children.
    let err = JsonRenderParser::parse(
        r#"{"kind":"button","properties":{"label":"x","action":"y"},"children":[{"kind":"textBlock","properties":{"text":"t"}}]}"#,
        &catalog,
        true,
    );
    assert!(err.is_err());
}

// ── Predictive warmup histogram ─────────────────────────────────────────────

#[test]
fn histogram_predictor_forecasts_after_learning() {
    let p = HistogramRequestPredictor::with_default_history();
    // Record arrivals at 09:00 across many days.
    for day in 1..=30 {
        let t = Utc.with_ymd_and_hms(2026, 6, day, 9, 0, 0).unwrap();
        p.record_arrival(t);
    }
    assert_eq!(p.observed_arrivals(), 30);

    // Forecast a 1-minute window at 09:00 → high probability.
    let now = Utc.with_ymd_and_hms(2026, 7, 8, 9, 0, 0).unwrap();
    let fc = p.predict(now, Duration::minutes(1));
    assert!(fc.probability_of_arrival > 0.5, "prob was {}", fc.probability_of_arrival);
    assert!(fc.confidence > 0.0);

    // A window at 03:00 (never any arrivals) → zero probability.
    let quiet = Utc.with_ymd_and_hms(2026, 7, 8, 3, 0, 0).unwrap();
    let fc2 = p.predict(quiet, Duration::minutes(1));
    assert!(fc2.probability_of_arrival < 1e-9);
}

#[test]
fn histogram_predictor_zero_when_no_data() {
    let p = HistogramRequestPredictor::with_default_history();
    let fc = p.predict(Utc::now(), Duration::minutes(1));
    assert_eq!(fc.probability_of_arrival, 0.0);
    assert_eq!(fc.expected_count, 0.0);
    assert_eq!(fc.confidence, 0.0);
}
