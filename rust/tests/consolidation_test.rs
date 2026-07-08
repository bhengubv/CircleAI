//! consolidation_test.rs
//!
//! Verifies the hierarchical memory-consolidation subsystem ported from
//! CircleAI.Memory.Consolidation. Uses a fixed injected clock and hand-built
//! EpisodicMemoryEntry lists so every deterministic formula can be asserted
//! exactly. Covers: daily summary produced for a completed day + idempotency,
//! today's episodes excluded, the salience/topicConcentration formula on a small
//! example, weekly clustering's 2-day threshold, high-salience → core promotion,
//! retention pruning, persona-delta new-topic detection, and full-cosine ranking
//! in the in-memory stores. Mirrors the TS pilot suite tests/consolidation.test.ts
//! 1:1.

use std::collections::HashMap;
use std::sync::Arc;

use chrono::{DateTime, Utc};
use uuid::Uuid;

use circle_ai::memory::consolidation::{
    add_days, cosine_full, create_core_memory, create_daily_summary, create_semantic_cluster,
    day_key_of, monday_of, month_first_day_of, ClockFn, CoreMemoryInit, CoreMemoryKind,
    DailyMemorySummary, DailyMemorySummaryInit, HeuristicSummarizer, ICoreMemoryStore,
    IDailyMemoryStore, IMemoryConsolidator, IMemorySummarizer, IPersonaDeltaStore,
    ISemanticMemoryStore, InMemoryCoreMemoryStore, InMemoryDailyMemoryStore,
    InMemoryPersonaDeltaStore, InMemoryPersonaStore, InMemorySemanticMemoryStore,
    MemoryConsolidationOptions, MemoryConsolidator, PersonaConsolidationStore, SemanticMemoryClusterInit,
    SleepKind,
};
use circle_ai::memory::episodic::InMemoryEpisodicStore;
use circle_ai::memory::stores::{EpisodicMemoryEntry, PersonaState};

// ── Fixtures ────────────────────────────────────────────────────────────────

fn dt(iso: &str) -> DateTime<Utc> {
    iso.parse::<DateTime<Utc>>().expect("parse iso datetime")
}

/// Builds an entry. `id` is accepted for parity with the TS fixture but a fresh
/// UUID is always assigned (distinct per entry — all the salience-proxy self
/// exclusion needs); no assertion depends on the string id.
#[derive(Default)]
struct EntryOverrides {
    recorded_at_utc: Option<DateTime<Utc>>,
    user_text: Option<String>,
    assistant_text: Option<String>,
    embedding: Option<Vec<f32>>,
    tags: Option<HashMap<String, String>>,
}

fn entry(o: EntryOverrides) -> EpisodicMemoryEntry {
    EpisodicMemoryEntry {
        id: Uuid::new_v4(),
        recorded_at_utc: o.recorded_at_utc.unwrap_or_else(|| dt("2026-06-01T12:00:00Z")),
        user_text: o.user_text.unwrap_or_else(|| "u".to_string()),
        assistant_text: o.assistant_text.unwrap_or_else(|| "a".to_string()),
        app_context: None,
        embedding: o.embedding,
        tags: o.tags,
    }
}

fn tags(pairs: &[(&str, &str)]) -> HashMap<String, String> {
    pairs.iter().map(|(k, v)| (k.to_string(), v.to_string())).collect()
}

/// Clock fixed at the given instant so week/day math is stable.
fn fixed_clock(iso: &str) -> ClockFn {
    let d = dt(iso);
    Arc::new(move || d)
}

/// A daily summary with the given day + topic weights (other fields default).
fn daily_with(
    day: &str,
    episode_count: usize,
    topic_weights: &[(&str, f64)],
    clock: &ClockFn,
) -> DailyMemorySummary {
    let mut init = DailyMemorySummaryInit::for_day(day);
    init.episode_count = episode_count;
    init.topic_weights = topic_weights.iter().map(|(k, v)| (k.to_string(), *v)).collect();
    create_daily_summary(init, clock)
}

struct Parts {
    episodic: Arc<InMemoryEpisodicStore>,
    daily: Arc<InMemoryDailyMemoryStore>,
    semantic: Arc<InMemorySemanticMemoryStore>,
    persona_delta: Arc<InMemoryPersonaDeltaStore>,
    core: Arc<InMemoryCoreMemoryStore>,
    persona_store: Arc<InMemoryPersonaStore>,
    consolidator: MemoryConsolidator,
}

/// Wires a consolidator over fresh in-memory stores; returns the parts.
fn make_consolidator(clock: ClockFn, options: Option<MemoryConsolidationOptions>) -> Parts {
    let episodic = Arc::new(InMemoryEpisodicStore::new(100_000).expect("episodic"));
    let daily = Arc::new(InMemoryDailyMemoryStore::new());
    let semantic = Arc::new(InMemorySemanticMemoryStore::new());
    let persona_delta = Arc::new(InMemoryPersonaDeltaStore::new());
    let core = Arc::new(InMemoryCoreMemoryStore::new());
    let persona_store = Arc::new(InMemoryPersonaStore::new());
    let summarizer: Arc<dyn IMemorySummarizer> =
        Arc::new(HeuristicSummarizer::with_clock(clock.clone()));
    let consolidator = MemoryConsolidator::new(
        episodic.clone(),
        daily.clone(),
        semantic.clone(),
        persona_delta.clone(),
        core.clone(),
        persona_store.clone(),
        summarizer,
        options,
        Some(clock),
        None,
    )
    .expect("consolidator");
    Parts {
        episodic,
        daily,
        semantic,
        persona_delta,
        core,
        persona_store,
        consolidator,
    }
}

// ── Day helpers ───────────────────────────────────────────────────────────

#[test]
fn day_key_of_uses_utc_calendar_day() {
    assert_eq!(day_key_of(&dt("2026-06-08T23:59:59Z")), "2026-06-08");
    assert_eq!(day_key_of(&dt("2026-01-05T00:00:00Z")), "2026-01-05");
}

#[test]
fn monday_of_returns_the_monday_of_the_week() {
    assert_eq!(monday_of("2026-06-08"), "2026-06-08"); // Monday → itself
    assert_eq!(monday_of("2026-06-14"), "2026-06-08"); // Sunday → prior Monday
    assert_eq!(monday_of("2026-06-10"), "2026-06-08"); // Wednesday → Monday
}

#[test]
fn add_days_crosses_month_boundaries() {
    assert_eq!(add_days("2026-06-01", -1), "2026-05-31");
    assert_eq!(add_days("2026-06-30", 1), "2026-07-01");
}

#[test]
fn month_first_day_of_yields_the_first_of_the_month() {
    assert_eq!(month_first_day_of("2026-06-17"), "2026-06-01");
}

// ── cosineFull ────────────────────────────────────────────────────────────

#[test]
fn cosine_full_direction_and_magnitude() {
    assert_eq!(cosine_full(&[1.0, 0.0], &[1.0, 0.0]), 1.0);
    assert_eq!(cosine_full(&[1.0, 0.0], &[0.0, 1.0]), 0.0);
    // Not L2-normalised inputs: full cosine still yields 1 for same direction.
    assert!((cosine_full(&[3.0, 0.0], &[7.0, 0.0]) - 1.0).abs() < 1e-12);
}

#[test]
fn cosine_full_returns_0_on_length_mismatch_or_zero_vector() {
    assert_eq!(cosine_full(&[1.0, 0.0], &[1.0, 0.0, 0.0]), 0.0);
    assert_eq!(cosine_full(&[0.0, 0.0], &[1.0, 0.0]), 0.0);
}

// ── Daily summarization formulas ────────────────────────────────────────────

#[test]
fn summarize_day_computes_weights_dispersion_concentration_and_salience_exactly() {
    let s = HeuristicSummarizer::with_clock(fixed_clock("2026-06-02T00:00:00Z"));
    // 3 entries: finance×2 (topic tag) + health×1; embeddings [1,0],[0,1],[1,0].
    let entries = vec![
        entry(EntryOverrides {
            embedding: Some(vec![1.0, 0.0]),
            tags: Some(tags(&[("topic", "finance")])),
            ..Default::default()
        }),
        entry(EntryOverrides {
            embedding: Some(vec![0.0, 1.0]),
            tags: Some(tags(&[("topic", "health")])),
            ..Default::default()
        }),
        entry(EntryOverrides {
            embedding: Some(vec![1.0, 0.0]),
            tags: Some(tags(&[("topic", "finance")])),
            ..Default::default()
        }),
    ];
    let summary = s.summarize_day("2026-06-01", &entries).expect("summarize");

    assert_eq!(summary.episode_count, 3);
    assert_eq!(summary.topic_weights.get("finance"), Some(&2.0));
    assert_eq!(summary.topic_weights.get("health"), Some(&1.0));
    // dispersion = mean(1-cos) over pairs = (1 + 0 + 1)/3 = 2/3
    assert!((summary.topic_dispersion - 2.0 / 3.0).abs() < 1e-12);
    // salience = volume(3/30=0.1)*0.4 + disp(2/3)*0.3 + conc(2/3)*0.3 = 0.44
    assert!((summary.salience - 0.44).abs() < 1e-12);
    assert!(summary.summary.starts_with("On 2026-06-01 you had 3 exchanges."));
    assert!(summary.summary.contains("Top topics: finance, health."));
}

#[test]
fn splits_pipe_delimited_topics_and_lowercases_trims() {
    let s = HeuristicSummarizer::new();
    let summary = s
        .summarize_day(
            "2026-06-01",
            &[entry(EntryOverrides {
                tags: Some(tags(&[("topics", "Finance | Health |finance")])),
                ..Default::default()
            })],
        )
        .expect("summarize");
    assert_eq!(summary.topic_weights.get("finance"), Some(&2.0));
    assert_eq!(summary.topic_weights.get("health"), Some(&1.0));
}

#[test]
fn uses_topic_concentration_0_5_when_there_are_no_topics() {
    let s = HeuristicSummarizer::new();
    // 1 entry, no tags, no embedding → dispersion 0, volume 1/30, conc 0.5
    let summary = s
        .summarize_day("2026-06-01", &[entry(EntryOverrides::default())])
        .expect("summarize");
    let expected = (1.0 / 30.0) * 0.4 + 0.0 * 0.3 + 0.5 * 0.3;
    assert!((summary.salience - expected).abs() < 1e-12);
    // A single entry is always a highlight, so the standout clause is appended
    // (userText defaults to "u"). No topics → no "Top topics" clause.
    assert_eq!(
        summary.summary,
        "On 2026-06-01 you had 1 exchange. Standout moment: \"u\"."
    );
    assert!(!summary.summary.contains("Top topics"));
}

#[test]
fn returns_an_empty_day_summary_for_zero_entries() {
    let s = HeuristicSummarizer::new();
    let summary = s.summarize_day("2026-06-01", &[]).expect("summarize");
    assert_eq!(summary.episode_count, 0);
    assert_eq!(summary.summary, "No exchanges recorded on 2026-06-01.");
}

// ── Daily pass: production, idempotency, today-exclusion ─────────────────────

#[test]
fn produces_a_summary_for_a_completed_day_and_is_idempotent_on_re_tick() {
    let clock = fixed_clock("2026-06-08T09:00:00Z"); // "today" = 2026-06-08
    let p = make_consolidator(clock, None);
    p.episodic
        .add_shared(entry(EntryOverrides {
            recorded_at_utc: Some(dt("2026-06-06T10:00:00Z")),
            tags: Some(tags(&[("topic", "x")])),
            ..Default::default()
        }))
        .expect("add");
    p.episodic
        .add_shared(entry(EntryOverrides {
            recorded_at_utc: Some(dt("2026-06-06T11:00:00Z")),
            tags: Some(tags(&[("topic", "x")])),
            ..Default::default()
        }))
        .expect("add");

    let r1 = p.consolidator.tick(SleepKind::Daily).expect("tick");
    assert_eq!(r1.daily_summaries_produced, 1);
    let summary = p.daily.get("2026-06-06").expect("get").expect("some");
    assert_eq!(summary.episode_count, 2);

    // Second tick with no new episodes → idempotent skip (episodeCount matches).
    let r2 = p.consolidator.tick(SleepKind::Daily).expect("tick");
    assert_eq!(r2.daily_summaries_produced, 0);
    assert_eq!(p.daily.count().expect("count"), 1);
}

#[test]
fn does_not_summarise_todays_incomplete_day() {
    let clock = fixed_clock("2026-06-08T09:00:00Z");
    let p = make_consolidator(clock, None);
    // Episode recorded "today" → excluded (day is not < today).
    p.episodic
        .add_shared(entry(EntryOverrides {
            recorded_at_utc: Some(dt("2026-06-08T08:00:00Z")),
            ..Default::default()
        }))
        .expect("add");

    let r = p.consolidator.tick(SleepKind::Daily).expect("tick");
    assert_eq!(r.daily_summaries_produced, 0);
    assert_eq!(p.daily.count().expect("count"), 0);
}

#[test]
fn re_summarises_a_day_when_new_episodes_arrive_count_mismatch() {
    let clock = fixed_clock("2026-06-08T09:00:00Z");
    let p = make_consolidator(clock, None);
    p.episodic
        .add_shared(entry(EntryOverrides {
            recorded_at_utc: Some(dt("2026-06-06T10:00:00Z")),
            ..Default::default()
        }))
        .expect("add");
    p.consolidator.tick(SleepKind::Daily).expect("tick");
    assert_eq!(p.daily.get("2026-06-06").unwrap().unwrap().episode_count, 1);

    p.episodic
        .add_shared(entry(EntryOverrides {
            recorded_at_utc: Some(dt("2026-06-06T12:00:00Z")),
            ..Default::default()
        }))
        .expect("add");
    let r = p.consolidator.tick(SleepKind::Daily).expect("tick");
    assert_eq!(r.daily_summaries_produced, 1);
    assert_eq!(p.daily.get("2026-06-06").unwrap().unwrap().episode_count, 2);
}

// ── High-salience daily → core promotion (≥0.80) ────────────────────────────

#[test]
fn promotes_a_day_whose_salience_ge_0_80_to_a_high_salience_core_memory() {
    let clock = fixed_clock("2026-06-08T09:00:00Z");
    let p = make_consolidator(clock, None);

    // 30 entries, single topic 'finance' (conc=1); embeddings 15×[1,0] + 15×[0,1]
    // → dispersion ≈ 0.5172, salience ≈ 0.8552 (≥ 0.80).
    for i in 0..30 {
        let hour = format!("{:02}", i % 24);
        p.episodic
            .add_shared(entry(EntryOverrides {
                recorded_at_utc: Some(dt(&format!("2026-06-06T{hour}:00:00Z"))),
                embedding: Some(if i < 15 { vec![1.0, 0.0] } else { vec![0.0, 1.0] }),
                tags: Some(tags(&[("topic", "finance")])),
                ..Default::default()
            }))
            .expect("add");
    }

    let r = p.consolidator.tick(SleepKind::Daily).expect("tick");
    assert_eq!(r.daily_summaries_produced, 1);
    assert_eq!(r.core_promotions, 1);

    let all = p.core.list_all().expect("list");
    assert_eq!(all.len(), 1);
    assert_eq!(all[0].kind, CoreMemoryKind::HighSalience);
    assert_eq!(all[0].topic.as_deref(), Some("finance"));
    assert_eq!(
        all[0].statement,
        "\"finance\" mattered enough on 2026-06-06 to be remembered."
    );
    // Highlight embedding carried onto the core memory.
    assert!(all[0].embedding.is_some());
}

#[test]
fn does_not_promote_a_low_salience_day() {
    let clock = fixed_clock("2026-06-08T09:00:00Z");
    let p = make_consolidator(clock, None);
    p.episodic
        .add_shared(entry(EntryOverrides {
            recorded_at_utc: Some(dt("2026-06-06T10:00:00Z")),
            tags: Some(tags(&[("topic", "x")])),
            ..Default::default()
        }))
        .expect("add");
    let r = p.consolidator.tick(SleepKind::Daily).expect("tick");
    assert_eq!(r.core_promotions, 0);
    assert_eq!(p.core.count().expect("count"), 0);
}

// ── Weekly clustering + 2-day threshold ─────────────────────────────────────

#[test]
fn clusters_only_topics_appearing_in_ge_2_days_salience_per_formula() {
    let clock = fixed_clock("2026-06-08T00:00:00Z");
    let s = HeuristicSummarizer::with_clock(clock.clone());
    // Day1: finance=1, health=1 ; Day2: finance=1.
    // finance → 2 days (weight 2) → cluster ; health → 1 day → excluded.
    let day1 = daily_with("2026-06-01", 2, &[("finance", 1.0), ("health", 1.0)], &clock);
    let day2 = daily_with("2026-06-02", 1, &[("finance", 1.0)], &clock);

    let clusters = s
        .consolidate_week("2026-06-01", &[day1.clone(), day2.clone()])
        .expect("consolidate");
    assert_eq!(clusters.len(), 1);
    assert_eq!(clusters[0].topic, "finance");
    assert_eq!(clusters[0].topic_weight, 2.0);
    // salience = min(1, 2/3 + (2/7)*0.25) = 0.7380952…
    assert!((clusters[0].salience - (2.0 / 3.0 + (2.0 / 7.0) * 0.25)).abs() < 1e-12);
    assert_eq!(
        clusters[0].summary,
        "Across 2 days this week you returned to \"finance\" — 3 exchanges in total."
    );
    let mut got: Vec<String> = clusters[0].source_daily_ids.clone();
    got.sort();
    let mut want = vec![day1.id.clone(), day2.id.clone()];
    want.sort();
    assert_eq!(got, want);
}

#[test]
fn returns_no_clusters_when_every_topic_is_single_day() {
    let clock = fixed_clock("2026-06-08T00:00:00Z");
    let s = HeuristicSummarizer::with_clock(clock.clone());
    let clusters = s
        .consolidate_week(
            "2026-06-01",
            &[
                daily_with("2026-06-01", 0, &[("a", 1.0)], &clock),
                daily_with("2026-06-02", 0, &[("b", 1.0)], &clock),
            ],
        )
        .expect("consolidate");
    assert_eq!(clusters.len(), 0);
}

#[test]
fn computes_the_centroid_as_the_mean_of_highlight_embeddings() {
    let clock = fixed_clock("2026-06-08T00:00:00Z");
    let s = HeuristicSummarizer::with_clock(clock.clone());
    let h1 = entry(EntryOverrides {
        embedding: Some(vec![2.0, 0.0]),
        ..Default::default()
    });
    let h2 = entry(EntryOverrides {
        embedding: Some(vec![0.0, 4.0]),
        ..Default::default()
    });
    let mut i1 = DailyMemorySummaryInit::for_day("2026-06-01");
    i1.topic_weights = [("t".to_string(), 1.0)].into_iter().collect();
    i1.highlight_entries = vec![h1];
    let day1 = create_daily_summary(i1, &clock);
    let mut i2 = DailyMemorySummaryInit::for_day("2026-06-02");
    i2.topic_weights = [("t".to_string(), 1.0)].into_iter().collect();
    i2.highlight_entries = vec![h2];
    let day2 = create_daily_summary(i2, &clock);

    let clusters = s.consolidate_week("2026-06-01", &[day1, day2]).expect("consolidate");
    assert_eq!(clusters.len(), 1);
    assert_eq!(clusters[0].centroid_embedding, Some(vec![1.0, 2.0])); // ([2,0]+[0,4])/2
}

#[test]
fn weekly_pass_clusters_the_last_completed_week_and_is_idempotent() {
    // "today" Monday 2026-06-08 → thisMonday 06-08, lastMonday 06-01..lastSunday 06-07.
    let clock = fixed_clock("2026-06-08T09:00:00Z");
    let p = make_consolidator(clock.clone(), None);
    p.daily
        .upsert(daily_with("2026-06-01", 2, &[("finance", 1.0)], &clock))
        .expect("upsert");
    p.daily
        .upsert(daily_with("2026-06-03", 1, &[("finance", 1.0)], &clock))
        .expect("upsert");

    let r1 = p.consolidator.tick(SleepKind::Weekly).expect("tick");
    assert_eq!(r1.semantic_clusters_produced, 1);
    assert_eq!(p.semantic.count().expect("count"), 1);

    let r2 = p.consolidator.tick(SleepKind::Weekly).expect("tick");
    assert_eq!(r2.semantic_clusters_produced, 0); // getWeek non-empty → skip
    assert_eq!(p.semantic.count().expect("count"), 1);
}

// ── Retention pruning ───────────────────────────────────────────────────────

#[test]
fn prunes_episodic_entries_older_than_7_days_on_the_daily_pass() {
    let clock = fixed_clock("2026-06-08T09:00:00Z");
    let p = make_consolidator(clock, None);
    // cutoff = now - 7 days = 2026-06-01T09:00:00Z
    p.episodic
        .add_shared(entry(EntryOverrides {
            recorded_at_utc: Some(dt("2026-05-20T00:00:00Z")),
            ..Default::default()
        }))
        .expect("add");
    p.episodic
        .add_shared(entry(EntryOverrides {
            recorded_at_utc: Some(dt("2026-06-06T00:00:00Z")),
            ..Default::default()
        }))
        .expect("add");

    let r = p.consolidator.tick(SleepKind::Daily).expect("tick");
    assert_eq!(r.episodes_pruned, 1);
    assert_eq!(p.episodic.count_shared().expect("count"), 1);
    let remaining = p.episodic.get_recent_shared(10).expect("recent");
    assert_eq!(remaining[0].recorded_at_utc, dt("2026-06-06T00:00:00Z"));
}

#[test]
fn prunes_daily_summaries_older_than_30_days_on_the_weekly_pass() {
    let clock = fixed_clock("2026-06-08T09:00:00Z");
    let p = make_consolidator(clock.clone(), None);
    // cutoff = 2026-06-08 - 30 = 2026-05-09. Older day is pruned.
    p.daily
        .upsert(create_daily_summary(DailyMemorySummaryInit::for_day("2026-04-01"), &clock))
        .expect("upsert"); // < cutoff → pruned
    p.daily
        .upsert(create_daily_summary(DailyMemorySummaryInit::for_day("2026-06-03"), &clock))
        .expect("upsert"); // kept

    let r = p.consolidator.tick(SleepKind::Weekly).expect("tick");
    assert_eq!(r.dailies_pruned, 1);
    assert!(p.daily.get("2026-04-01").expect("get").is_none());
    assert!(p.daily.get("2026-06-03").expect("get").is_some());
}

#[test]
fn prunes_semantic_clusters_older_than_365_days_on_the_monthly_pass() {
    let clock = fixed_clock("2026-06-08T09:00:00Z");
    let p = make_consolidator(clock.clone(), None);
    // cutoff = 2026-06-08 - 365 = 2025-06-08.
    p.semantic
        .add(create_semantic_cluster(
            SemanticMemoryClusterInit::new("2024-01-01", "t"),
            &clock,
        ))
        .expect("add");
    p.semantic
        .add(create_semantic_cluster(
            SemanticMemoryClusterInit::new("2026-05-04", "t"),
            &clock,
        ))
        .expect("add");

    let r = p.consolidator.tick(SleepKind::Monthly).expect("tick");
    assert_eq!(r.semantics_pruned, 1);
    assert_eq!(p.semantic.count().expect("count"), 1);
}

// ── Monthly persona-delta ───────────────────────────────────────────────────

#[test]
fn derives_a_delta_detecting_a_new_topic_and_is_idempotent_by_month() {
    // "today" 2026-06-08 → previous month = May 2026 (2026-05-01..2026-05-31).
    let clock = fixed_clock("2026-06-08T09:00:00Z");
    let p = make_consolidator(clock.clone(), None);

    // A daily summary inside May so the month has data.
    p.daily
        .upsert(daily_with("2026-05-15", 4, &[], &clock))
        .expect("upsert");

    // Persona "after" has a topic the fresh "before" lacks → newTopic.
    let mut after = PersonaState::new("default");
    after.topic_weights = [("finance".to_string(), 3.0f32)].into_iter().collect();
    after.total_interactions = 10;
    after.positive_signals = 6;
    after.negative_signals = 1;
    p.persona_store.save(&after).expect("save");

    let r1 = p.consolidator.tick(SleepKind::Monthly).expect("tick");
    assert_eq!(r1.persona_deltas_produced, 1);
    let deltas = p.persona_delta.get_for_user("default").expect("deltas");
    assert_eq!(deltas.len(), 1);
    assert_eq!(deltas[0].new_topics.get("finance"), Some(&3.0));
    assert_eq!(deltas[0].period_start, "2026-05-15");
    assert_eq!(deltas[0].period_end, "2026-05-15");
    assert!(deltas[0].narrative.contains("New interests appeared: finance."));

    // Second monthly tick → idempotent (delta already exists for May).
    let r2 = p.consolidator.tick(SleepKind::Monthly).expect("tick");
    assert_eq!(r2.persona_deltas_produced, 0);
    assert_eq!(p.persona_delta.get_for_user("default").expect("deltas").len(), 1);
}

#[test]
fn produces_no_delta_when_the_previous_month_has_no_daily_summaries() {
    let clock = fixed_clock("2026-06-08T09:00:00Z");
    let p = make_consolidator(clock, None);
    let r = p.consolidator.tick(SleepKind::Monthly).expect("tick");
    assert_eq!(r.persona_deltas_produced, 0);
    assert_eq!(p.persona_delta.count().expect("count"), 0);
}

#[test]
fn derive_persona_delta_separates_new_from_strengthened_and_computes_signal_deltas() {
    let s = HeuristicSummarizer::new();
    let mut before = PersonaState::new("default");
    before.topic_weights = [("finance".to_string(), 2.0f32)].into_iter().collect();
    before.positive_signals = 1;
    before.negative_signals = 1;
    before.total_interactions = 5;
    before.verbosity = "balanced".to_string();

    let mut after = PersonaState::new("default");
    // finance strengthened(+3), travel new
    after.topic_weights = [
        ("finance".to_string(), 5.0f32),
        ("travel".to_string(), 3.0f32),
    ]
    .into_iter()
    .collect();
    after.positive_signals = 7;
    after.negative_signals = 2;
    after.total_interactions = 20;
    after.verbosity = "detailed".to_string();

    let clock = fixed_clock("2026-06-08T00:00:00Z");
    let day = create_daily_summary(DailyMemorySummaryInit::for_day("2026-05-10"), &clock);
    let delta = s.derive_persona_delta(&before, &after, &[day]).expect("delta");

    assert_eq!(delta.new_topics.get("travel"), Some(&3.0));
    assert_eq!(delta.new_topics.contains_key("finance"), false);
    assert_eq!(delta.strengthened_topics.get("finance"), Some(&3.0));
    // netSignals = (7-1) - (2-1) = 5 ; interactions = 20-5 = 15
    assert_eq!(delta.net_signal_delta, 5);
    assert_eq!(delta.interactions_in_period, 15);
    assert!(delta
        .narrative
        .contains("Preferred verbosity shifted from balanced to detailed."));
    assert!(delta.narrative.contains("Net feedback was positive (+5)."));
}

// ── OnDemand runs every tier ────────────────────────────────────────────────

#[test]
fn on_demand_runs_daily_weekly_and_monthly_passes_in_one_tick() {
    let clock = fixed_clock("2026-06-08T09:00:00Z");
    let p = make_consolidator(clock.clone(), None);

    // Daily fuel: a completed day earlier this week.
    p.episodic
        .add_shared(entry(EntryOverrides {
            recorded_at_utc: Some(dt("2026-06-06T10:00:00Z")),
            tags: Some(tags(&[("topic", "finance")])),
            ..Default::default()
        }))
        .expect("add");
    p.episodic
        .add_shared(entry(EntryOverrides {
            recorded_at_utc: Some(dt("2026-06-06T11:00:00Z")),
            tags: Some(tags(&[("topic", "finance")])),
            ..Default::default()
        }))
        .expect("add");
    // Weekly fuel: dailies inside last week (06-01..06-07) with a repeated topic.
    p.daily
        .upsert(daily_with("2026-06-01", 2, &[("finance", 1.0)], &clock))
        .expect("upsert");
    p.daily
        .upsert(daily_with("2026-06-02", 1, &[("finance", 1.0)], &clock))
        .expect("upsert");
    // Monthly fuel: a daily inside May + a persona.
    p.daily
        .upsert(daily_with("2026-05-20", 3, &[], &clock))
        .expect("upsert");
    let mut persona = PersonaState::new("default");
    persona.topic_weights = [("finance".to_string(), 2.0f32)].into_iter().collect();
    persona.total_interactions = 6;
    p.persona_store.save(&persona).expect("save");

    let r = p.consolidator.tick(SleepKind::OnDemand).expect("tick");
    assert_eq!(r.kind, SleepKind::OnDemand);
    assert!(r.daily_summaries_produced >= 1);
    assert!(r.semantic_clusters_produced >= 1);
    assert_eq!(r.persona_deltas_produced, 1);
    assert_eq!(r.ran_at_utc, clock());
    assert!(p.semantic.count().expect("count") >= 1);
    assert_eq!(p.persona_delta.get_for_user("default").expect("deltas").len(), 1);
}

// ── In-memory store cosine ranking + ordering ───────────────────────────────

#[test]
fn core_memory_store_ranks_by_full_cosine_to_the_query_centroid() {
    let clock = fixed_clock("2026-06-08T00:00:00Z");
    let core = InMemoryCoreMemoryStore::new();
    core.add(create_core_memory(
        CoreMemoryInit {
            statement: "x".to_string(),
            embedding: Some(vec![1.0, 0.0]),
            ..Default::default()
        },
        &clock,
    ))
    .expect("add");
    core.add(create_core_memory(
        CoreMemoryInit {
            statement: "y".to_string(),
            embedding: Some(vec![0.0, 1.0]),
            ..Default::default()
        },
        &clock,
    ))
    .expect("add");
    core.add(create_core_memory(
        CoreMemoryInit {
            statement: "diag".to_string(),
            embedding: Some(vec![1.0, 1.0]),
            ..Default::default()
        },
        &clock,
    ))
    .expect("add");

    let ranked = core.search(Some(&[1.0, 0.0]), 3).expect("search");
    assert_eq!(ranked[0].statement, "x"); // cos 1
    assert_eq!(ranked[2].statement, "y"); // cos 0
    // 'diag' cos([1,1],[1,0]) = 0.707 → middle
    assert_eq!(ranked[1].statement, "diag");
}

#[test]
fn core_memory_store_falls_back_to_reinforcement_order_when_query_is_null() {
    let clock = fixed_clock("2026-06-08T00:00:00Z");
    let core = InMemoryCoreMemoryStore::new();
    let a = create_core_memory(
        CoreMemoryInit { statement: "a".to_string(), ..Default::default() },
        &clock,
    );
    let b = create_core_memory(
        CoreMemoryInit { statement: "b".to_string(), ..Default::default() },
        &clock,
    );
    let b_id = b.id.clone();
    core.add(a).expect("add");
    core.add(b).expect("add");
    core.reinforce(&b_id).expect("reinforce");
    core.reinforce(&b_id).expect("reinforce");

    let top = core.search(None, 2).expect("search");
    assert_eq!(top[0].statement, "b"); // more reinforced first
    assert_eq!(top[0].reinforcement_count, 2);
}

#[test]
fn semantic_store_get_week_orders_by_weight_desc_search_ranks_by_centroid_cosine() {
    let clock = fixed_clock("2026-06-08T00:00:00Z");
    let sem = InMemorySemanticMemoryStore::new();
    let mut low = SemanticMemoryClusterInit::new("2026-06-01", "low");
    low.topic_weight = 1.0;
    low.centroid_embedding = Some(vec![0.0, 1.0]);
    sem.add(create_semantic_cluster(low, &clock)).expect("add");
    let mut high = SemanticMemoryClusterInit::new("2026-06-01", "high");
    high.topic_weight = 5.0;
    high.centroid_embedding = Some(vec![1.0, 0.0]);
    sem.add(create_semantic_cluster(high, &clock)).expect("add");

    let week = sem.get_week("2026-06-01").expect("week");
    let topics: Vec<String> = week.iter().map(|c| c.topic.clone()).collect();
    assert_eq!(topics, vec!["high".to_string(), "low".to_string()]);

    let ranked = sem.search(Some(&[1.0, 0.0]), 2).expect("search");
    assert_eq!(ranked[0].topic, "high"); // centroid [1,0] cos 1
}

#[test]
fn daily_store_get_range_returns_day_ordered_inclusive_results() {
    let clock = fixed_clock("2026-06-08T00:00:00Z");
    let daily = InMemoryDailyMemoryStore::new();
    daily
        .upsert(create_daily_summary(DailyMemorySummaryInit::for_day("2026-06-03"), &clock))
        .expect("upsert");
    daily
        .upsert(create_daily_summary(DailyMemorySummaryInit::for_day("2026-06-01"), &clock))
        .expect("upsert");
    daily
        .upsert(create_daily_summary(DailyMemorySummaryInit::for_day("2026-06-10"), &clock))
        .expect("upsert");

    let range = daily.get_range("2026-06-01", "2026-06-05").expect("range");
    let days: Vec<String> = range.iter().map(|d| d.day.clone()).collect();
    assert_eq!(days, vec!["2026-06-01".to_string(), "2026-06-03".to_string()]);
}
