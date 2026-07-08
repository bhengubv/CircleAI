"""test_consolidation.py

Verifies the hierarchical memory-consolidation subsystem ported from
CircleAI.Memory.Consolidation. Uses a fixed injected clock and hand-built
EpisodicMemoryEntry lists so every deterministic formula can be asserted
exactly. Covers: day helpers, full cosine, daily-summary formulas + idempotency,
today-exclusion, the salience/topicConcentration formula, weekly clustering's
2-day threshold, high-salience -> core promotion, retention pruning,
persona-delta new-topic detection, full-cosine ranking in the in-memory stores,
and OnDemand running every tier. Mirrors the TypeScript reference
(consolidation.test.ts) with the same fixtures and exact numbers.
"""
from __future__ import annotations

from datetime import date, datetime, timezone
from typing import Callable, Optional

from circle_ai.memory.consolidation import (
    CoreMemoryKind,
    HeuristicSummarizer,
    InMemoryCoreMemoryStore,
    InMemoryDailyMemoryStore,
    InMemoryPersonaDeltaStore,
    InMemoryPersonaStore,
    InMemorySemanticMemoryStore,
    MemoryConsolidationOptions,
    MemoryConsolidator,
    SemanticMemoryCluster,
    SleepKind,
    cosine_full,
    day_key_of,
    monday_of,
    month_first_day_of,
)
from circle_ai.memory.consolidation import (
    CoreMemory,
    DailyMemorySummary,
)
from circle_ai.memory.episodic_memory import EpisodicMemoryEntry
from circle_ai.memory.in_memory_episodic_store import InMemoryEpisodicStore
from circle_ai.memory.persona_state import PersonaState

# ── Fixtures ──────────────────────────────────────────────────────────────────

_id_counter = 0


def _dt(iso: str) -> datetime:
    """Parse an ISO-8601 UTC string ('...Z') into an aware UTC datetime."""
    return datetime.fromisoformat(iso.replace("Z", "+00:00"))


def entry(**overrides) -> EpisodicMemoryEntry:
    global _id_counter
    eid = overrides.get("id")
    if eid is None:
        eid = f"e{_id_counter}"
        _id_counter += 1
    return EpisodicMemoryEntry(
        id=eid,
        recorded_at_utc=overrides.get("recorded_at_utc", _dt("2026-06-01T12:00:00Z")),
        user_text=overrides.get("user_text", "u"),
        assistant_text=overrides.get("assistant_text", "a"),
        embedding=overrides.get("embedding"),
        tags=overrides.get("tags"),
        app_context=overrides.get("app_context"),
    )


def fixed_clock(iso: str) -> Callable[[], datetime]:
    """Clock fixed at the given instant (2026-06-08 is a Monday) so week math is stable."""
    d = _dt(iso)
    return lambda: d


def daily(day: str, **overrides) -> DailyMemorySummary:
    """Build a DailyMemorySummary with a date parsed from a 'YYYY-MM-DD' string."""
    return DailyMemorySummary(
        day=date.fromisoformat(day),
        summary=overrides.get("summary", ""),
        highlight_entries=overrides.get("highlight_entries", []),
        episode_count=overrides.get("episode_count", 0),
        topic_weights=overrides.get("topic_weights", {}),
        topic_dispersion=overrides.get("topic_dispersion", 0.0),
        salience=overrides.get("salience", 0.0),
    )


def cluster(week: str, **overrides) -> SemanticMemoryCluster:
    return SemanticMemoryCluster(
        week_starting_monday=date.fromisoformat(week),
        topic=overrides.get("topic", ""),
        summary=overrides.get("summary", ""),
        centroid_embedding=overrides.get("centroid_embedding"),
        source_daily_ids=overrides.get("source_daily_ids", []),
        topic_weight=overrides.get("topic_weight", 0.0),
        salience=overrides.get("salience", 0.0),
    )


def core(**overrides) -> CoreMemory:
    return CoreMemory(
        statement=overrides.get("statement", ""),
        kind=overrides.get("kind", CoreMemoryKind.UserAsserted),
        topic=overrides.get("topic"),
        embedding=overrides.get("embedding"),
        source_memory_id=overrides.get("source_memory_id"),
    )


def make_consolidator(
    clock: Callable[[], datetime],
    options: Optional[MemoryConsolidationOptions] = None,
):
    """Wire a consolidator over fresh in-memory stores; return the parts."""
    episodic = InMemoryEpisodicStore(100000)
    daily_store = InMemoryDailyMemoryStore()
    semantic = InMemorySemanticMemoryStore()
    persona_delta = InMemoryPersonaDeltaStore()
    core_store = InMemoryCoreMemoryStore()
    persona_store = InMemoryPersonaStore()
    summarizer = HeuristicSummarizer(clock=clock)
    consolidator = MemoryConsolidator(
        episodic,
        daily_store,
        semantic,
        persona_delta,
        core_store,
        persona_store,
        summarizer,
        options,
        clock,
    )
    return {
        "episodic": episodic,
        "daily": daily_store,
        "semantic": semantic,
        "persona_delta": persona_delta,
        "core": core_store,
        "persona_store": persona_store,
        "summarizer": summarizer,
        "consolidator": consolidator,
    }


# ── Day helpers ────────────────────────────────────────────────────────────


def test_day_key_of_uses_utc_calendar_day() -> None:
    assert day_key_of(_dt("2026-06-08T23:59:59Z")) == date(2026, 6, 8)
    assert day_key_of(_dt("2026-01-05T00:00:00Z")) == date(2026, 1, 5)


def test_monday_of_returns_the_monday_of_the_week() -> None:
    assert monday_of(date(2026, 6, 8)) == date(2026, 6, 8)  # Monday -> itself
    assert monday_of(date(2026, 6, 14)) == date(2026, 6, 8)  # Sunday -> prior Monday
    assert monday_of(date(2026, 6, 10)) == date(2026, 6, 8)  # Wednesday -> Monday


def test_month_first_day_of_yields_the_first_of_the_month() -> None:
    assert month_first_day_of(date(2026, 6, 17)) == date(2026, 6, 1)


# ── cosine_full ────────────────────────────────────────────────────────────


def test_cosine_full_identical_orthogonal_and_magnitude_normalised() -> None:
    assert cosine_full([1, 0], [1, 0]) == 1
    assert cosine_full([1, 0], [0, 1]) == 0
    # Not L2-normalised inputs: full cosine still yields 1 for same direction.
    assert abs(cosine_full([3, 0], [7, 0]) - 1) < 1e-12


def test_cosine_full_returns_zero_on_length_mismatch_or_zero_vector() -> None:
    assert cosine_full([1, 0], [1, 0, 0]) == 0
    assert cosine_full([0, 0], [1, 0]) == 0


# ── Daily summarization formulas ────────────────────────────────────────────


async def test_summarize_day_computes_weights_dispersion_concentration_salience() -> None:
    s = HeuristicSummarizer(clock=fixed_clock("2026-06-02T00:00:00Z"))
    # 3 entries: finance x2 (topic tag) + health x1; embeddings [1,0],[0,1],[1,0].
    entries = [
        entry(id="a", embedding=[1, 0], tags={"topic": "finance"}),
        entry(id="b", embedding=[0, 1], tags={"topic": "health"}),
        entry(id="c", embedding=[1, 0], tags={"topic": "finance"}),
    ]
    summary = await s.summarize_day_async(date(2026, 6, 1), entries)

    assert summary.episode_count == 3
    assert summary.topic_weights["finance"] == 2
    assert summary.topic_weights["health"] == 1
    # dispersion = mean(1-cos) over pairs = (1 + 0 + 1)/3 = 2/3
    assert abs(summary.topic_dispersion - 2 / 3) < 1e-12
    # salience = volume(3/30=0.1)*0.4 + disp(2/3)*0.3 + conc(2/3)*0.3 = 0.44
    assert abs(summary.salience - 0.44) < 1e-12
    # summary text shape
    assert summary.summary.startswith("On 2026-06-01 you had 3 exchanges.")
    assert "Top topics: finance, health." in summary.summary


async def test_summarize_day_splits_pipe_topics_and_lowercases_trims() -> None:
    s = HeuristicSummarizer()
    summary = await s.summarize_day_async(
        date(2026, 6, 1),
        [entry(tags={"topics": "Finance | Health |finance"})],
    )
    assert summary.topic_weights["finance"] == 2
    assert summary.topic_weights["health"] == 1


async def test_summarize_day_uses_topic_concentration_half_when_no_topics() -> None:
    s = HeuristicSummarizer()
    # 1 entry, no tags, no embedding -> dispersion 0, volume 1/30, conc 0.5
    summary = await s.summarize_day_async(date(2026, 6, 1), [entry()])
    expected = (1 / 30) * 0.4 + 0 * 0.3 + 0.5 * 0.3
    assert abs(summary.salience - expected) < 1e-12
    # A single entry is always a highlight, so the standout clause is appended
    # (user_text defaults to "u"). No topics -> no "Top topics" clause.
    assert summary.summary == 'On 2026-06-01 you had 1 exchange. Standout moment: "u".'
    assert "Top topics" not in summary.summary


async def test_summarize_day_returns_empty_day_summary_for_zero_entries() -> None:
    s = HeuristicSummarizer()
    summary = await s.summarize_day_async(date(2026, 6, 1), [])
    assert summary.episode_count == 0
    assert summary.summary == "No exchanges recorded on 2026-06-01."


# ── Daily pass: production, idempotency, today-exclusion ─────────────────────


async def test_daily_pass_produces_summary_and_is_idempotent() -> None:
    clock = fixed_clock("2026-06-08T09:00:00Z")  # "today" = 2026-06-08
    c = make_consolidator(clock)
    episodic, daily_store, consolidator = c["episodic"], c["daily"], c["consolidator"]
    await episodic.add_async(
        entry(recorded_at_utc=_dt("2026-06-06T10:00:00Z"), tags={"topic": "x"})
    )
    await episodic.add_async(
        entry(recorded_at_utc=_dt("2026-06-06T11:00:00Z"), tags={"topic": "x"})
    )

    r1 = await consolidator.tick_async(SleepKind.Daily)
    assert r1.daily_summaries_produced == 1
    summary = await daily_store.get_async(date(2026, 6, 6))
    assert summary is not None
    assert summary.episode_count == 2

    # Second tick with no new episodes -> idempotent skip (episode_count matches).
    r2 = await consolidator.tick_async(SleepKind.Daily)
    assert r2.daily_summaries_produced == 0
    assert await daily_store.count_async() == 1


async def test_daily_pass_does_not_summarise_todays_incomplete_day() -> None:
    clock = fixed_clock("2026-06-08T09:00:00Z")
    c = make_consolidator(clock)
    episodic, daily_store, consolidator = c["episodic"], c["daily"], c["consolidator"]
    # Episode recorded "today" -> excluded (day is not < today).
    await episodic.add_async(entry(recorded_at_utc=_dt("2026-06-08T08:00:00Z")))

    r = await consolidator.tick_async(SleepKind.Daily)
    assert r.daily_summaries_produced == 0
    assert await daily_store.count_async() == 0


async def test_daily_pass_resummarises_when_new_episodes_arrive() -> None:
    clock = fixed_clock("2026-06-08T09:00:00Z")
    c = make_consolidator(clock)
    episodic, daily_store, consolidator = c["episodic"], c["daily"], c["consolidator"]
    await episodic.add_async(
        entry(id="p1", recorded_at_utc=_dt("2026-06-06T10:00:00Z"))
    )
    await consolidator.tick_async(SleepKind.Daily)
    assert (await daily_store.get_async(date(2026, 6, 6))).episode_count == 1

    await episodic.add_async(
        entry(id="p2", recorded_at_utc=_dt("2026-06-06T12:00:00Z"))
    )
    r = await consolidator.tick_async(SleepKind.Daily)
    assert r.daily_summaries_produced == 1
    assert (await daily_store.get_async(date(2026, 6, 6))).episode_count == 2


# ── High-salience daily -> core promotion (>=0.80) ──────────────────────────


async def test_promotes_high_salience_day_to_core() -> None:
    clock = fixed_clock("2026-06-08T09:00:00Z")
    c = make_consolidator(clock)
    episodic, core_store, consolidator = c["episodic"], c["core"], c["consolidator"]

    # 30 entries, single topic 'finance' (conc=1); embeddings 15x[1,0] + 15x[0,1]
    # -> dispersion ~= 0.5172, salience ~= 0.8552 (>= 0.80).
    for i in range(30):
        await episodic.add_async(
            entry(
                id=f"h{i}",
                recorded_at_utc=_dt(f"2026-06-06T{i % 24:02d}:00:00Z"),
                embedding=[1, 0] if i < 15 else [0, 1],
                tags={"topic": "finance"},
            )
        )

    r = await consolidator.tick_async(SleepKind.Daily)
    assert r.daily_summaries_produced == 1
    assert r.core_promotions == 1

    all_core = await core_store.list_all_async()
    assert len(all_core) == 1
    assert all_core[0].kind == CoreMemoryKind.HighSalience
    assert all_core[0].topic == "finance"
    assert (
        all_core[0].statement
        == '"finance" mattered enough on 2026-06-06 to be remembered.'
    )
    # Highlight embedding carried onto the core memory.
    assert all_core[0].embedding is not None


async def test_does_not_promote_a_low_salience_day() -> None:
    clock = fixed_clock("2026-06-08T09:00:00Z")
    c = make_consolidator(clock)
    episodic, core_store, consolidator = c["episodic"], c["core"], c["consolidator"]
    await episodic.add_async(
        entry(recorded_at_utc=_dt("2026-06-06T10:00:00Z"), tags={"topic": "x"})
    )
    r = await consolidator.tick_async(SleepKind.Daily)
    assert r.core_promotions == 0
    assert await core_store.count_async() == 0


# ── Weekly clustering + 2-day threshold ─────────────────────────────────────


async def test_consolidate_week_clusters_only_topics_in_two_or_more_days() -> None:
    s = HeuristicSummarizer(clock=fixed_clock("2026-06-08T00:00:00Z"))
    # Day1: finance=1, health=1 ; Day2: finance=1.
    # finance -> 2 days (weight 2) -> cluster ; health -> 1 day -> excluded.
    day1 = daily("2026-06-01", episode_count=2, topic_weights={"finance": 1, "health": 1})
    day2 = daily("2026-06-02", episode_count=1, topic_weights={"finance": 1})

    clusters = await s.consolidate_week_async(date(2026, 6, 1), [day1, day2])
    assert len(clusters) == 1
    assert clusters[0].topic == "finance"
    assert clusters[0].topic_weight == 2
    # salience = min(1, 2/3 + (2/7)*0.25) = 0.7380952...
    assert abs(clusters[0].salience - (2 / 3 + (2 / 7) * 0.25)) < 1e-12
    assert (
        clusters[0].summary
        == 'Across 2 days this week you returned to "finance" — 3 exchanges in total.'
    )
    assert sorted(clusters[0].source_daily_ids, key=str) == sorted(
        [day1.id, day2.id], key=str
    )


async def test_consolidate_week_returns_no_clusters_when_all_single_day() -> None:
    s = HeuristicSummarizer()
    clusters = await s.consolidate_week_async(
        date(2026, 6, 1),
        [
            daily("2026-06-01", topic_weights={"a": 1}),
            daily("2026-06-02", topic_weights={"b": 1}),
        ],
    )
    assert len(clusters) == 0


async def test_consolidate_week_computes_centroid_as_mean_of_highlights() -> None:
    s = HeuristicSummarizer()
    h1 = entry(id="h1", embedding=[2, 0])
    h2 = entry(id="h2", embedding=[0, 4])
    day1 = daily("2026-06-01", topic_weights={"t": 1}, highlight_entries=[h1])
    day2 = daily("2026-06-02", topic_weights={"t": 1}, highlight_entries=[h2])
    clusters = await s.consolidate_week_async(date(2026, 6, 1), [day1, day2])
    assert len(clusters) == 1
    assert clusters[0].centroid_embedding == [1, 2]  # ([2,0]+[0,4])/2


async def test_weekly_pass_clusters_last_completed_week_and_is_idempotent() -> None:
    # "today" Monday 2026-06-08 -> thisMonday 06-08, lastMonday 06-01..lastSunday 06-07.
    clock = fixed_clock("2026-06-08T09:00:00Z")
    c = make_consolidator(clock)
    daily_store, semantic, consolidator = c["daily"], c["semantic"], c["consolidator"]
    await daily_store.upsert_async(
        daily("2026-06-01", episode_count=2, topic_weights={"finance": 1})
    )
    await daily_store.upsert_async(
        daily("2026-06-03", episode_count=1, topic_weights={"finance": 1})
    )

    r1 = await consolidator.tick_async(SleepKind.Weekly)
    assert r1.semantic_clusters_produced == 1
    assert await semantic.count_async() == 1

    r2 = await consolidator.tick_async(SleepKind.Weekly)
    assert r2.semantic_clusters_produced == 0  # get_week non-empty -> skip
    assert await semantic.count_async() == 1


# ── Retention pruning ───────────────────────────────────────────────────────


async def test_prunes_episodic_older_than_7_days_on_daily_pass() -> None:
    clock = fixed_clock("2026-06-08T09:00:00Z")
    c = make_consolidator(clock)
    episodic, consolidator = c["episodic"], c["consolidator"]
    # cutoff = now - 7 days = 2026-06-01T09:00:00Z
    await episodic.add_async(
        entry(id="old", recorded_at_utc=_dt("2026-05-20T00:00:00Z"))
    )
    await episodic.add_async(
        entry(id="fresh", recorded_at_utc=_dt("2026-06-06T00:00:00Z"))
    )

    r = await consolidator.tick_async(SleepKind.Daily)
    assert r.episodes_pruned == 1
    assert await episodic.count_async() == 1
    remaining = await episodic.get_recent_async(10)
    assert remaining[0].id == "fresh"


async def test_prunes_daily_summaries_older_than_30_days_on_weekly_pass() -> None:
    clock = fixed_clock("2026-06-08T09:00:00Z")
    c = make_consolidator(clock)
    daily_store, consolidator = c["daily"], c["consolidator"]
    # cutoff = 2026-06-08 - 30 = 2026-05-09. Older day is pruned.
    await daily_store.upsert_async(daily("2026-04-01"))  # < cutoff -> pruned
    await daily_store.upsert_async(daily("2026-06-03"))  # kept

    r = await consolidator.tick_async(SleepKind.Weekly)
    assert r.dailies_pruned == 1
    assert await daily_store.get_async(date(2026, 4, 1)) is None
    assert await daily_store.get_async(date(2026, 6, 3)) is not None


async def test_prunes_semantic_clusters_older_than_365_days_on_monthly_pass() -> None:
    clock = fixed_clock("2026-06-08T09:00:00Z")
    c = make_consolidator(clock)
    semantic, consolidator = c["semantic"], c["consolidator"]
    # cutoff = 2026-06-08 - 365 = 2025-06-08.
    await semantic.add_async(cluster("2024-01-01", topic="t"))
    await semantic.add_async(cluster("2026-05-04", topic="t"))

    r = await consolidator.tick_async(SleepKind.Monthly)
    assert r.semantics_pruned == 1
    assert await semantic.count_async() == 1


# ── Monthly persona-delta ───────────────────────────────────────────────────


async def test_monthly_derives_delta_detecting_new_topic_and_is_idempotent() -> None:
    # "today" 2026-06-08 -> previous month = May 2026 (2026-05-01..2026-05-31).
    clock = fixed_clock("2026-06-08T09:00:00Z")
    c = make_consolidator(clock)
    daily_store = c["daily"]
    persona_delta = c["persona_delta"]
    persona_store = c["persona_store"]
    consolidator = c["consolidator"]

    # A daily summary inside May so the month has data.
    await daily_store.upsert_async(daily("2026-05-15", episode_count=4))

    # Persona "after" has a topic the fresh "before" lacks -> newTopic.
    after = PersonaState()
    after.user_id = "default"
    after.topic_weights = {"finance": 3}
    after.total_interactions = 10
    after.positive_signals = 6
    after.negative_signals = 1
    await persona_store.save_async(after)

    r1 = await consolidator.tick_async(SleepKind.Monthly)
    assert r1.persona_deltas_produced == 1
    deltas = await persona_delta.get_for_user_async("default")
    assert len(deltas) == 1
    assert deltas[0].new_topics["finance"] == 3
    assert deltas[0].period_start == date(2026, 5, 15)
    assert deltas[0].period_end == date(2026, 5, 15)
    assert "New interests appeared: finance." in deltas[0].narrative

    # Second monthly tick -> idempotent (delta already exists for May).
    r2 = await consolidator.tick_async(SleepKind.Monthly)
    assert r2.persona_deltas_produced == 0
    assert len(await persona_delta.get_for_user_async("default")) == 1


async def test_monthly_produces_no_delta_when_previous_month_has_no_dailies() -> None:
    clock = fixed_clock("2026-06-08T09:00:00Z")
    c = make_consolidator(clock)
    consolidator, persona_delta = c["consolidator"], c["persona_delta"]
    r = await consolidator.tick_async(SleepKind.Monthly)
    assert r.persona_deltas_produced == 0
    assert await persona_delta.count_async() == 0


async def test_derive_persona_delta_separates_new_from_strengthened() -> None:
    s = HeuristicSummarizer()
    before = PersonaState()
    before.topic_weights = {"finance": 2}
    before.positive_signals = 1
    before.negative_signals = 1
    before.total_interactions = 5
    before.verbosity = "balanced"

    after = PersonaState()
    after.topic_weights = {"finance": 5, "travel": 3}  # finance strengthened(+3), travel new
    after.positive_signals = 7
    after.negative_signals = 2
    after.total_interactions = 20
    after.verbosity = "detailed"

    day = daily("2026-05-10")
    delta = await s.derive_persona_delta_async(before, after, [day])

    assert delta.new_topics["travel"] == 3
    assert ("finance" in delta.new_topics) is False
    assert delta.strengthened_topics["finance"] == 3
    # netSignals = (7-1) - (2-1) = 5 ; interactions = 20-5 = 15
    assert delta.net_signal_delta == 5
    assert delta.interactions_in_period == 15
    assert "Preferred verbosity shifted from balanced to detailed." in delta.narrative
    assert "Net feedback was positive (+5)." in delta.narrative


# ── OnDemand runs every tier ────────────────────────────────────────────────


async def test_on_demand_runs_daily_weekly_and_monthly_in_one_tick() -> None:
    clock = fixed_clock("2026-06-08T09:00:00Z")
    c = make_consolidator(clock)
    episodic = c["episodic"]
    daily_store = c["daily"]
    semantic = c["semantic"]
    persona_store = c["persona_store"]
    persona_delta = c["persona_delta"]
    consolidator = c["consolidator"]

    # Daily fuel: a completed day earlier this week.
    await episodic.add_async(
        entry(recorded_at_utc=_dt("2026-06-06T10:00:00Z"), tags={"topic": "finance"})
    )
    await episodic.add_async(
        entry(recorded_at_utc=_dt("2026-06-06T11:00:00Z"), tags={"topic": "finance"})
    )
    # Weekly fuel: dailies inside last week (06-01..06-07) with a repeated topic.
    await daily_store.upsert_async(
        daily("2026-06-01", episode_count=2, topic_weights={"finance": 1})
    )
    await daily_store.upsert_async(
        daily("2026-06-02", episode_count=1, topic_weights={"finance": 1})
    )
    # Monthly fuel: a daily inside May + a persona.
    await daily_store.upsert_async(daily("2026-05-20", episode_count=3))
    p = PersonaState()
    p.topic_weights = {"finance": 2}
    p.total_interactions = 6
    await persona_store.save_async(p)

    r = await consolidator.tick_async(SleepKind.OnDemand)
    assert r.kind == SleepKind.OnDemand
    assert r.daily_summaries_produced >= 1
    assert r.semantic_clusters_produced >= 1
    assert r.persona_deltas_produced == 1
    assert r.ran_at_utc == clock()
    assert await semantic.count_async() >= 1
    assert len(await persona_delta.get_for_user_async("default")) == 1


# ── In-memory store cosine ranking + ordering ───────────────────────────────


async def test_core_store_ranks_by_full_cosine_to_query_centroid() -> None:
    core_store = InMemoryCoreMemoryStore()
    await core_store.add_async(core(statement="x", embedding=[1, 0]))
    await core_store.add_async(core(statement="y", embedding=[0, 1]))
    await core_store.add_async(core(statement="diag", embedding=[1, 1]))

    ranked = await core_store.search_async([1, 0], 3)
    assert ranked[0].statement == "x"  # cos 1
    assert ranked[2].statement == "y"  # cos 0
    # 'diag' cos([1,1],[1,0]) = 0.707 -> middle
    assert ranked[1].statement == "diag"


async def test_core_store_falls_back_to_reinforcement_order_when_query_none() -> None:
    core_store = InMemoryCoreMemoryStore()
    a = core(statement="a")
    b = core(statement="b")
    await core_store.add_async(a)
    await core_store.add_async(b)
    await core_store.reinforce_async(b.id)
    await core_store.reinforce_async(b.id)

    top = await core_store.search_async(None, 2)
    assert top[0].statement == "b"  # more reinforced first
    assert top[0].reinforcement_count == 2


async def test_semantic_store_get_week_orders_by_weight_search_by_cosine() -> None:
    sem = InMemorySemanticMemoryStore()
    await sem.add_async(
        cluster("2026-06-01", topic="low", topic_weight=1, centroid_embedding=[0, 1])
    )
    await sem.add_async(
        cluster("2026-06-01", topic="high", topic_weight=5, centroid_embedding=[1, 0])
    )

    week = await sem.get_week_async(date(2026, 6, 1))
    assert [c.topic for c in week] == ["high", "low"]

    ranked = await sem.search_async([1, 0], 2)
    assert ranked[0].topic == "high"  # centroid [1,0] cos 1


async def test_daily_store_get_range_returns_day_ordered_inclusive() -> None:
    daily_store = InMemoryDailyMemoryStore()
    await daily_store.upsert_async(daily("2026-06-03"))
    await daily_store.upsert_async(daily("2026-06-01"))
    await daily_store.upsert_async(daily("2026-06-10"))

    rng = await daily_store.get_range_async(date(2026, 6, 1), date(2026, 6, 5))
    assert [c.day for c in rng] == [date(2026, 6, 1), date(2026, 6, 3)]
