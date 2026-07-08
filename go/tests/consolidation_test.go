// consolidation_test.go
//
// Verifies the hierarchical memory-consolidation subsystem ported from
// CircleAI.Memory.Consolidation. Uses a fixed injected clock and hand-built
// EpisodicMemoryEntry lists so every deterministic formula can be asserted
// exactly. Covers: day helpers, full cosine, daily summary produced for a
// completed day + idempotency, today's episodes excluded, the salience/
// topicConcentration formula on a small example, weekly clustering's 2-day
// threshold, high-salience → core promotion, retention pruning, persona-delta
// new-topic detection, full-cosine ranking, and OnDemand running every tier.
// Mirrors the TS pilot suite tests/consolidation.test.ts 1:1.

package circleai_test

import (
	"context"
	"fmt"
	"math"
	"sort"
	"strings"
	"testing"
	"time"

	"github.com/google/uuid"

	circleai "github.com/bhengubv/CircleAI/go"
)

// ── Fixtures ────────────────────────────────────────────────────────────────

type consEntryOpts struct {
	id        string
	recorded  time.Time
	userText  string
	embedding []float32
	tags      map[string]string
}

// consEntry builds an EpisodicMemoryEntry. A label id maps to a stable UUID so
// distinct labels get distinct ids (the salience proxy filters on ID).
func consEntry(o consEntryOpts) circleai.EpisodicMemoryEntry {
	rec := o.recorded
	if rec.IsZero() {
		rec = time.Date(2026, 6, 1, 12, 0, 0, 0, time.UTC)
	}
	ut := o.userText
	if ut == "" {
		ut = "u"
	}
	id := uuid.New()
	if o.id != "" {
		id = uuid.NewSHA1(uuid.NameSpaceOID, []byte("consolidation-test:"+o.id))
	}
	return circleai.EpisodicMemoryEntry{
		ID:            id,
		RecordedAtUTC: rec,
		UserText:      ut,
		AssistantText: "a",
		Embedding:     o.embedding,
		Tags:          o.tags,
	}
}

// fixedClock returns a clock pinned at the given RFC3339 instant.
func fixedClock(rfc3339 string) func() time.Time {
	t, err := time.Parse(time.RFC3339, rfc3339)
	if err != nil {
		panic(err)
	}
	return func() time.Time { return t }
}

type consParts struct {
	episodic     *circleai.InMemoryEpisodicStore
	daily        *circleai.InMemoryDailyMemoryStore
	semantic     *circleai.InMemorySemanticMemoryStore
	personaDelta *circleai.InMemoryPersonaDeltaStore
	core         *circleai.InMemoryCoreMemoryStore
	personaStore *circleai.InMemoryPersonaStore
	summarizer   *circleai.HeuristicSummarizer
	consolidator *circleai.MemoryConsolidator
}

// makeConsolidator wires a consolidator over fresh in-memory stores.
func makeConsolidator(t *testing.T, clock func() time.Time, cfg circleai.MemoryConsolidatorConfig) consParts {
	t.Helper()
	episodic, err := circleai.NewInMemoryEpisodicStore(100000)
	if err != nil {
		t.Fatalf("NewInMemoryEpisodicStore: %v", err)
	}
	daily := circleai.NewInMemoryDailyMemoryStore()
	semantic := circleai.NewInMemorySemanticMemoryStore()
	personaDelta := circleai.NewInMemoryPersonaDeltaStore()
	core := circleai.NewInMemoryCoreMemoryStore()
	personaStore := circleai.NewInMemoryPersonaStore()
	summarizer := circleai.NewHeuristicSummarizer(circleai.HeuristicSummarizerOptions{Clock: clock})
	if cfg.Clock == nil {
		cfg.Clock = clock
	}
	consolidator, err := circleai.NewMemoryConsolidator(episodic, daily, semantic, personaDelta, core, personaStore, summarizer, cfg)
	if err != nil {
		t.Fatalf("NewMemoryConsolidator: %v", err)
	}
	return consParts{episodic, daily, semantic, personaDelta, core, personaStore, summarizer, consolidator}
}

func mustTick(t *testing.T, c *circleai.MemoryConsolidator, kind circleai.SleepKind) circleai.ConsolidationOutcome {
	t.Helper()
	out, err := c.Tick(context.Background(), kind)
	if err != nil {
		t.Fatalf("Tick(%v): %v", kind, err)
	}
	return out
}

func approx(got, want float64) bool { return math.Abs(got-want) < 1e-12 }

// ── Day helpers ───────────────────────────────────────────────────────────

func TestConsolidation_DayHelpers(t *testing.T) {
	t.Run("CivilDateOf uses UTC calendar day", func(t *testing.T) {
		if got := circleai.CivilDateOf(time.Date(2026, 6, 8, 23, 59, 59, 0, time.UTC)).String(); got != "2026-06-08" {
			t.Errorf("got %s want 2026-06-08", got)
		}
		if got := circleai.CivilDateOf(time.Date(2026, 1, 5, 0, 0, 0, 0, time.UTC)).String(); got != "2026-01-05" {
			t.Errorf("got %s want 2026-01-05", got)
		}
	})

	t.Run("MondayOf returns the Monday of the week (Sunday=0)", func(t *testing.T) {
		if got := circleai.NewCivilDate(2026, 6, 8).MondayOf().String(); got != "2026-06-08" {
			t.Errorf("Monday: got %s want 2026-06-08", got)
		}
		if got := circleai.NewCivilDate(2026, 6, 14).MondayOf().String(); got != "2026-06-08" {
			t.Errorf("Sunday: got %s want 2026-06-08", got)
		}
		if got := circleai.NewCivilDate(2026, 6, 10).MondayOf().String(); got != "2026-06-08" {
			t.Errorf("Wednesday: got %s want 2026-06-08", got)
		}
	})

	t.Run("AddDays crosses month boundaries", func(t *testing.T) {
		if got := circleai.NewCivilDate(2026, 6, 1).AddDays(-1).String(); got != "2026-05-31" {
			t.Errorf("got %s want 2026-05-31", got)
		}
		if got := circleai.NewCivilDate(2026, 6, 30).AddDays(1).String(); got != "2026-07-01" {
			t.Errorf("got %s want 2026-07-01", got)
		}
	})

	t.Run("MonthFirstDay yields the first of the month", func(t *testing.T) {
		if got := circleai.NewCivilDate(2026, 6, 17).MonthFirstDay().String(); got != "2026-06-01" {
			t.Errorf("got %s want 2026-06-01", got)
		}
	})
}

// ── cosineFull (exercised via store search + dispersion) ────────────────────

func TestConsolidation_CosineFull(t *testing.T) {
	// cosineFull is unexported; exercise it through the semantic store's search,
	// which ranks by full cosine to the query centroid.
	sem := circleai.NewInMemorySemanticMemoryStore()
	ctx := context.Background()
	_ = sem.Add(ctx, circleai.NewSemanticCluster(circleai.SemanticMemoryClusterInit{
		WeekStartingMonday: circleai.NewCivilDate(2026, 6, 1), Topic: "same", CentroidEmbedding: []float32{1, 0},
	}))
	_ = sem.Add(ctx, circleai.NewSemanticCluster(circleai.SemanticMemoryClusterInit{
		WeekStartingMonday: circleai.NewCivilDate(2026, 6, 1), Topic: "ortho", CentroidEmbedding: []float32{0, 1},
	}))
	_ = sem.Add(ctx, circleai.NewSemanticCluster(circleai.SemanticMemoryClusterInit{
		WeekStartingMonday: circleai.NewCivilDate(2026, 6, 1), Topic: "scaled", CentroidEmbedding: []float32{7, 0},
	}))

	ranked, err := sem.Search(ctx, []float32{3, 0}, 3)
	if err != nil {
		t.Fatalf("Search: %v", err)
	}
	// Same direction (1 for [1,0] and [7,0]) ranks above orthogonal (0).
	if ranked[0].Topic == "ortho" {
		t.Errorf("orthogonal cluster should not rank first")
	}
	if ranked[2].Topic != "ortho" {
		t.Errorf("orthogonal cluster should rank last, got %s", ranked[2].Topic)
	}
}

// ── Daily summarization formulas ────────────────────────────────────────────

func TestConsolidation_SummarizeDayFormulas(t *testing.T) {
	ctx := context.Background()

	t.Run("computes topic weights, dispersion, topicConcentration and salience exactly", func(t *testing.T) {
		s := circleai.NewHeuristicSummarizer(circleai.HeuristicSummarizerOptions{Clock: fixedClock("2026-06-02T00:00:00Z")})
		// 3 entries: finance×2 + health×1; embeddings [1,0],[0,1],[1,0].
		entries := []circleai.EpisodicMemoryEntry{
			consEntry(consEntryOpts{id: "a", embedding: []float32{1, 0}, tags: map[string]string{"topic": "finance"}}),
			consEntry(consEntryOpts{id: "b", embedding: []float32{0, 1}, tags: map[string]string{"topic": "health"}}),
			consEntry(consEntryOpts{id: "c", embedding: []float32{1, 0}, tags: map[string]string{"topic": "finance"}}),
		}
		summary, err := s.SummarizeDay(ctx, circleai.NewCivilDate(2026, 6, 1), entries)
		if err != nil {
			t.Fatalf("SummarizeDay: %v", err)
		}

		if summary.EpisodeCount != 3 {
			t.Errorf("episodeCount: got %d want 3", summary.EpisodeCount)
		}
		if summary.TopicWeights["finance"] != 2 {
			t.Errorf("finance weight: got %v want 2", summary.TopicWeights["finance"])
		}
		if summary.TopicWeights["health"] != 1 {
			t.Errorf("health weight: got %v want 1", summary.TopicWeights["health"])
		}
		// dispersion = mean(1-cos) over pairs = (1 + 0 + 1)/3 = 2/3
		if !approx(summary.TopicDispersion, 2.0/3.0) {
			t.Errorf("dispersion: got %v want %v", summary.TopicDispersion, 2.0/3.0)
		}
		// salience = volume(0.1)*0.4 + disp(2/3)*0.3 + conc(2/3)*0.3 = 0.44
		if !approx(summary.Salience, 0.44) {
			t.Errorf("salience: got %v want 0.44", summary.Salience)
		}
		if !strings.HasPrefix(summary.Summary, "On 2026-06-01 you had 3 exchanges.") {
			t.Errorf("summary prefix: got %q", summary.Summary)
		}
		if !strings.Contains(summary.Summary, "Top topics: finance, health.") {
			t.Errorf("summary topics: got %q", summary.Summary)
		}
	})

	t.Run("splits pipe-delimited topics and lowercases/trims", func(t *testing.T) {
		s := circleai.NewHeuristicSummarizer(circleai.HeuristicSummarizerOptions{})
		summary, err := s.SummarizeDay(ctx, circleai.NewCivilDate(2026, 6, 1), []circleai.EpisodicMemoryEntry{
			consEntry(consEntryOpts{tags: map[string]string{"topics": "Finance | Health |finance"}}),
		})
		if err != nil {
			t.Fatalf("SummarizeDay: %v", err)
		}
		if summary.TopicWeights["finance"] != 2 {
			t.Errorf("finance: got %v want 2", summary.TopicWeights["finance"])
		}
		if summary.TopicWeights["health"] != 1 {
			t.Errorf("health: got %v want 1", summary.TopicWeights["health"])
		}
	})

	t.Run("uses topicConcentration 0.5 when there are no topics", func(t *testing.T) {
		s := circleai.NewHeuristicSummarizer(circleai.HeuristicSummarizerOptions{})
		summary, err := s.SummarizeDay(ctx, circleai.NewCivilDate(2026, 6, 1), []circleai.EpisodicMemoryEntry{consEntry(consEntryOpts{})})
		if err != nil {
			t.Fatalf("SummarizeDay: %v", err)
		}
		expected := (1.0/30.0)*0.4 + 0*0.3 + 0.5*0.3
		if !approx(summary.Salience, expected) {
			t.Errorf("salience: got %v want %v", summary.Salience, expected)
		}
		if summary.Summary != `On 2026-06-01 you had 1 exchange. Standout moment: "u".` {
			t.Errorf("summary: got %q", summary.Summary)
		}
		if strings.Contains(summary.Summary, "Top topics") {
			t.Errorf("should not contain Top topics: %q", summary.Summary)
		}
	})

	t.Run("returns an empty-day summary for zero entries", func(t *testing.T) {
		s := circleai.NewHeuristicSummarizer(circleai.HeuristicSummarizerOptions{})
		summary, err := s.SummarizeDay(ctx, circleai.NewCivilDate(2026, 6, 1), []circleai.EpisodicMemoryEntry{})
		if err != nil {
			t.Fatalf("SummarizeDay: %v", err)
		}
		if summary.EpisodeCount != 0 {
			t.Errorf("episodeCount: got %d want 0", summary.EpisodeCount)
		}
		if summary.Summary != "No exchanges recorded on 2026-06-01." {
			t.Errorf("summary: got %q", summary.Summary)
		}
	})
}

// ── Daily pass: production, idempotency, today-exclusion ─────────────────────

func TestConsolidation_DailyPass(t *testing.T) {
	ctx := context.Background()

	t.Run("produces a summary for a completed day and is idempotent on re-tick", func(t *testing.T) {
		clock := fixedClock("2026-06-08T09:00:00Z") // today = 2026-06-08
		p := makeConsolidator(t, clock, circleai.MemoryConsolidatorConfig{})
		mustAdd(t, p.episodic, consEntry(consEntryOpts{recorded: time.Date(2026, 6, 6, 10, 0, 0, 0, time.UTC), tags: map[string]string{"topic": "x"}}))
		mustAdd(t, p.episodic, consEntry(consEntryOpts{recorded: time.Date(2026, 6, 6, 11, 0, 0, 0, time.UTC), tags: map[string]string{"topic": "x"}}))

		r1 := mustTick(t, p.consolidator, circleai.SleepDaily)
		if r1.DailySummariesProduced != 1 {
			t.Fatalf("r1 produced: got %d want 1", r1.DailySummariesProduced)
		}
		summary, err := p.daily.Get(ctx, circleai.NewCivilDate(2026, 6, 6))
		if err != nil || summary == nil {
			t.Fatalf("Get: %v summary=%v", err, summary)
		}
		if summary.EpisodeCount != 2 {
			t.Errorf("episodeCount: got %d want 2", summary.EpisodeCount)
		}

		r2 := mustTick(t, p.consolidator, circleai.SleepDaily)
		if r2.DailySummariesProduced != 0 {
			t.Errorf("r2 produced: got %d want 0", r2.DailySummariesProduced)
		}
		if n, _ := p.daily.Count(ctx); n != 1 {
			t.Errorf("daily count: got %d want 1", n)
		}
	})

	t.Run("does NOT summarise today's (incomplete) day", func(t *testing.T) {
		clock := fixedClock("2026-06-08T09:00:00Z")
		p := makeConsolidator(t, clock, circleai.MemoryConsolidatorConfig{})
		mustAdd(t, p.episodic, consEntry(consEntryOpts{recorded: time.Date(2026, 6, 8, 8, 0, 0, 0, time.UTC)}))

		r := mustTick(t, p.consolidator, circleai.SleepDaily)
		if r.DailySummariesProduced != 0 {
			t.Errorf("produced: got %d want 0", r.DailySummariesProduced)
		}
		if n, _ := p.daily.Count(ctx); n != 0 {
			t.Errorf("daily count: got %d want 0", n)
		}
	})

	t.Run("re-summarises a day when new episodes arrive for it (count mismatch)", func(t *testing.T) {
		clock := fixedClock("2026-06-08T09:00:00Z")
		p := makeConsolidator(t, clock, circleai.MemoryConsolidatorConfig{})
		mustAdd(t, p.episodic, consEntry(consEntryOpts{id: "p1", recorded: time.Date(2026, 6, 6, 10, 0, 0, 0, time.UTC)}))
		mustTick(t, p.consolidator, circleai.SleepDaily)
		s1, _ := p.daily.Get(ctx, circleai.NewCivilDate(2026, 6, 6))
		if s1.EpisodeCount != 1 {
			t.Fatalf("episodeCount: got %d want 1", s1.EpisodeCount)
		}

		mustAdd(t, p.episodic, consEntry(consEntryOpts{id: "p2", recorded: time.Date(2026, 6, 6, 12, 0, 0, 0, time.UTC)}))
		r := mustTick(t, p.consolidator, circleai.SleepDaily)
		if r.DailySummariesProduced != 1 {
			t.Errorf("produced: got %d want 1", r.DailySummariesProduced)
		}
		s2, _ := p.daily.Get(ctx, circleai.NewCivilDate(2026, 6, 6))
		if s2.EpisodeCount != 2 {
			t.Errorf("episodeCount: got %d want 2", s2.EpisodeCount)
		}
	})
}

// ── High-salience daily → core promotion (≥0.80) ────────────────────────────

func TestConsolidation_CorePromotionFromHighSalienceDay(t *testing.T) {
	ctx := context.Background()

	t.Run("promotes a day whose salience >= 0.80 to a HighSalience core memory", func(t *testing.T) {
		clock := fixedClock("2026-06-08T09:00:00Z")
		p := makeConsolidator(t, clock, circleai.MemoryConsolidatorConfig{})

		// 30 entries, single topic 'finance' (conc=1); embeddings 15×[1,0] + 15×[0,1]
		// → dispersion ≈ 0.5172, salience ≈ 0.8552 (>= 0.80).
		for i := 0; i < 30; i++ {
			emb := []float32{1, 0}
			if i >= 15 {
				emb = []float32{0, 1}
			}
			mustAdd(t, p.episodic, consEntry(consEntryOpts{
				id:        fmt.Sprintf("h%d", i),
				recorded:  time.Date(2026, 6, 6, i%24, 0, 0, 0, time.UTC),
				embedding: emb,
				tags:      map[string]string{"topic": "finance"},
			}))
		}

		r := mustTick(t, p.consolidator, circleai.SleepDaily)
		if r.DailySummariesProduced != 1 {
			t.Fatalf("produced: got %d want 1", r.DailySummariesProduced)
		}
		if r.CorePromotions != 1 {
			t.Fatalf("corePromotions: got %d want 1", r.CorePromotions)
		}

		all, err := p.core.ListAll(ctx)
		if err != nil {
			t.Fatalf("ListAll: %v", err)
		}
		if len(all) != 1 {
			t.Fatalf("core len: got %d want 1", len(all))
		}
		if all[0].Kind != circleai.CoreHighSalience {
			t.Errorf("kind: got %v want HighSalience", all[0].Kind)
		}
		if all[0].Topic == nil || *all[0].Topic != "finance" {
			t.Errorf("topic: got %v want finance", all[0].Topic)
		}
		if all[0].Statement != `"finance" mattered enough on 2026-06-06 to be remembered.` {
			t.Errorf("statement: got %q", all[0].Statement)
		}
		if all[0].Embedding == nil {
			t.Errorf("highlight embedding should be carried onto the core memory")
		}
	})

	t.Run("does NOT promote a low-salience day", func(t *testing.T) {
		clock := fixedClock("2026-06-08T09:00:00Z")
		p := makeConsolidator(t, clock, circleai.MemoryConsolidatorConfig{})
		mustAdd(t, p.episodic, consEntry(consEntryOpts{recorded: time.Date(2026, 6, 6, 10, 0, 0, 0, time.UTC), tags: map[string]string{"topic": "x"}}))
		r := mustTick(t, p.consolidator, circleai.SleepDaily)
		if r.CorePromotions != 0 {
			t.Errorf("corePromotions: got %d want 0", r.CorePromotions)
		}
		if n, _ := p.core.Count(ctx); n != 0 {
			t.Errorf("core count: got %d want 0", n)
		}
	})
}

// ── Weekly clustering + 2-day threshold ─────────────────────────────────────

func TestConsolidation_ConsolidateWeek(t *testing.T) {
	ctx := context.Background()

	t.Run("clusters only topics appearing in >= 2 days, salience per formula", func(t *testing.T) {
		s := circleai.NewHeuristicSummarizer(circleai.HeuristicSummarizerOptions{Clock: fixedClock("2026-06-08T00:00:00Z")})
		day1 := circleai.NewDailySummary(circleai.DailyMemorySummaryInit{
			Day: circleai.NewCivilDate(2026, 6, 1), EpisodeCount: 2,
			TopicWeights: map[string]float32{"finance": 1, "health": 1},
		})
		day2 := circleai.NewDailySummary(circleai.DailyMemorySummaryInit{
			Day: circleai.NewCivilDate(2026, 6, 2), EpisodeCount: 1,
			TopicWeights: map[string]float32{"finance": 1},
		})

		clusters, err := s.ConsolidateWeek(ctx, circleai.NewCivilDate(2026, 6, 1), []circleai.DailyMemorySummary{day1, day2})
		if err != nil {
			t.Fatalf("ConsolidateWeek: %v", err)
		}
		if len(clusters) != 1 {
			t.Fatalf("clusters len: got %d want 1", len(clusters))
		}
		if clusters[0].Topic != "finance" {
			t.Errorf("topic: got %q want finance", clusters[0].Topic)
		}
		if clusters[0].TopicWeight != 2 {
			t.Errorf("topicWeight: got %v want 2", clusters[0].TopicWeight)
		}
		// salience = min(1, 2/3 + (2/7)*0.25)
		want := 2.0/3.0 + (2.0/7.0)*0.25
		if !approx(clusters[0].Salience, want) {
			t.Errorf("salience: got %v want %v", clusters[0].Salience, want)
		}
		if clusters[0].Summary != `Across 2 days this week you returned to "finance" — 3 exchanges in total.` {
			t.Errorf("summary: got %q", clusters[0].Summary)
		}
		gotIDs := []string{clusters[0].SourceDailyIDs[0].String(), clusters[0].SourceDailyIDs[1].String()}
		sort.Strings(gotIDs)
		wantIDs := []string{day1.ID.String(), day2.ID.String()}
		sort.Strings(wantIDs)
		if gotIDs[0] != wantIDs[0] || gotIDs[1] != wantIDs[1] {
			t.Errorf("sourceDailyIds: got %v want %v", gotIDs, wantIDs)
		}
	})

	t.Run("returns no clusters when every topic is single-day", func(t *testing.T) {
		s := circleai.NewHeuristicSummarizer(circleai.HeuristicSummarizerOptions{})
		clusters, err := s.ConsolidateWeek(ctx, circleai.NewCivilDate(2026, 6, 1), []circleai.DailyMemorySummary{
			circleai.NewDailySummary(circleai.DailyMemorySummaryInit{Day: circleai.NewCivilDate(2026, 6, 1), TopicWeights: map[string]float32{"a": 1}}),
			circleai.NewDailySummary(circleai.DailyMemorySummaryInit{Day: circleai.NewCivilDate(2026, 6, 2), TopicWeights: map[string]float32{"b": 1}}),
		})
		if err != nil {
			t.Fatalf("ConsolidateWeek: %v", err)
		}
		if len(clusters) != 0 {
			t.Errorf("clusters len: got %d want 0", len(clusters))
		}
	})

	t.Run("computes the centroid as the mean of highlight embeddings", func(t *testing.T) {
		s := circleai.NewHeuristicSummarizer(circleai.HeuristicSummarizerOptions{})
		h1 := consEntry(consEntryOpts{id: "h1", embedding: []float32{2, 0}})
		h2 := consEntry(consEntryOpts{id: "h2", embedding: []float32{0, 4}})
		day1 := circleai.NewDailySummary(circleai.DailyMemorySummaryInit{
			Day: circleai.NewCivilDate(2026, 6, 1), TopicWeights: map[string]float32{"t": 1}, HighlightEntries: []circleai.EpisodicMemoryEntry{h1},
		})
		day2 := circleai.NewDailySummary(circleai.DailyMemorySummaryInit{
			Day: circleai.NewCivilDate(2026, 6, 2), TopicWeights: map[string]float32{"t": 1}, HighlightEntries: []circleai.EpisodicMemoryEntry{h2},
		})
		clusters, err := s.ConsolidateWeek(ctx, circleai.NewCivilDate(2026, 6, 1), []circleai.DailyMemorySummary{day1, day2})
		if err != nil {
			t.Fatalf("ConsolidateWeek: %v", err)
		}
		if len(clusters) != 1 {
			t.Fatalf("clusters len: got %d want 1", len(clusters))
		}
		if len(clusters[0].CentroidEmbedding) != 2 || clusters[0].CentroidEmbedding[0] != 1 || clusters[0].CentroidEmbedding[1] != 2 {
			t.Errorf("centroid: got %v want [1 2]", clusters[0].CentroidEmbedding)
		}
	})
}

func TestConsolidation_WeeklyPass(t *testing.T) {
	ctx := context.Background()

	t.Run("clusters the last completed week and is idempotent", func(t *testing.T) {
		clock := fixedClock("2026-06-08T09:00:00Z")
		p := makeConsolidator(t, clock, circleai.MemoryConsolidatorConfig{})
		_ = p.daily.Upsert(ctx, circleai.NewDailySummary(circleai.DailyMemorySummaryInit{Day: circleai.NewCivilDate(2026, 6, 1), EpisodeCount: 2, TopicWeights: map[string]float32{"finance": 1}}))
		_ = p.daily.Upsert(ctx, circleai.NewDailySummary(circleai.DailyMemorySummaryInit{Day: circleai.NewCivilDate(2026, 6, 3), EpisodeCount: 1, TopicWeights: map[string]float32{"finance": 1}}))

		r1 := mustTick(t, p.consolidator, circleai.SleepWeekly)
		if r1.SemanticClustersProduced != 1 {
			t.Fatalf("r1 produced: got %d want 1", r1.SemanticClustersProduced)
		}
		if n, _ := p.semantic.Count(ctx); n != 1 {
			t.Errorf("semantic count: got %d want 1", n)
		}

		r2 := mustTick(t, p.consolidator, circleai.SleepWeekly)
		if r2.SemanticClustersProduced != 0 {
			t.Errorf("r2 produced: got %d want 0", r2.SemanticClustersProduced)
		}
		if n, _ := p.semantic.Count(ctx); n != 1 {
			t.Errorf("semantic count: got %d want 1", n)
		}
	})
}

// ── Retention pruning ───────────────────────────────────────────────────────

func TestConsolidation_Retention(t *testing.T) {
	ctx := context.Background()

	t.Run("prunes episodic entries older than 7 days on the daily pass", func(t *testing.T) {
		clock := fixedClock("2026-06-08T09:00:00Z")
		p := makeConsolidator(t, clock, circleai.MemoryConsolidatorConfig{})
		// cutoff = now - 7 days = 2026-06-01T09:00:00Z
		mustAdd(t, p.episodic, consEntry(consEntryOpts{id: "old", recorded: time.Date(2026, 5, 20, 0, 0, 0, 0, time.UTC)}))
		mustAdd(t, p.episodic, consEntry(consEntryOpts{id: "fresh", recorded: time.Date(2026, 6, 6, 0, 0, 0, 0, time.UTC)}))

		r := mustTick(t, p.consolidator, circleai.SleepDaily)
		if r.EpisodesPruned != 1 {
			t.Errorf("episodesPruned: got %d want 1", r.EpisodesPruned)
		}
		if n, _ := p.episodic.Count(ctx); n != 1 {
			t.Errorf("episodic count: got %d want 1", n)
		}
		remaining, _ := p.episodic.GetRecent(ctx, 10)
		if remaining[0].ID != uuid.NewSHA1(uuid.NameSpaceOID, []byte("consolidation-test:fresh")) {
			t.Errorf("remaining[0] should be fresh")
		}
	})

	t.Run("prunes daily summaries older than 30 days on the weekly pass", func(t *testing.T) {
		clock := fixedClock("2026-06-08T09:00:00Z")
		p := makeConsolidator(t, clock, circleai.MemoryConsolidatorConfig{})
		// cutoff = 2026-06-08 - 30 = 2026-05-09. Older day is pruned.
		_ = p.daily.Upsert(ctx, circleai.NewDailySummary(circleai.DailyMemorySummaryInit{Day: circleai.NewCivilDate(2026, 4, 1)}))
		_ = p.daily.Upsert(ctx, circleai.NewDailySummary(circleai.DailyMemorySummaryInit{Day: circleai.NewCivilDate(2026, 6, 3)}))

		r := mustTick(t, p.consolidator, circleai.SleepWeekly)
		if r.DailiesPruned != 1 {
			t.Errorf("dailiesPruned: got %d want 1", r.DailiesPruned)
		}
		if s, _ := p.daily.Get(ctx, circleai.NewCivilDate(2026, 4, 1)); s != nil {
			t.Errorf("2026-04-01 should be pruned")
		}
		if s, _ := p.daily.Get(ctx, circleai.NewCivilDate(2026, 6, 3)); s == nil {
			t.Errorf("2026-06-03 should be kept")
		}
	})

	t.Run("prunes semantic clusters older than 365 days on the monthly pass", func(t *testing.T) {
		clock := fixedClock("2026-06-08T09:00:00Z")
		p := makeConsolidator(t, clock, circleai.MemoryConsolidatorConfig{})
		// cutoff = 2026-06-08 - 365 = 2025-06-08.
		_ = p.semantic.Add(ctx, circleai.NewSemanticCluster(circleai.SemanticMemoryClusterInit{WeekStartingMonday: circleai.NewCivilDate(2024, 1, 1), Topic: "t"}))
		_ = p.semantic.Add(ctx, circleai.NewSemanticCluster(circleai.SemanticMemoryClusterInit{WeekStartingMonday: circleai.NewCivilDate(2026, 5, 4), Topic: "t"}))

		r := mustTick(t, p.consolidator, circleai.SleepMonthly)
		if r.SemanticsPruned != 1 {
			t.Errorf("semanticsPruned: got %d want 1", r.SemanticsPruned)
		}
		if n, _ := p.semantic.Count(ctx); n != 1 {
			t.Errorf("semantic count: got %d want 1", n)
		}
	})
}

// ── Monthly persona-delta ───────────────────────────────────────────────────

func TestConsolidation_MonthlyPersonaDelta(t *testing.T) {
	ctx := context.Background()

	t.Run("derives a delta detecting a new topic and is idempotent by month", func(t *testing.T) {
		clock := fixedClock("2026-06-08T09:00:00Z") // previous month = May 2026
		p := makeConsolidator(t, clock, circleai.MemoryConsolidatorConfig{})

		_ = p.daily.Upsert(ctx, circleai.NewDailySummary(circleai.DailyMemorySummaryInit{Day: circleai.NewCivilDate(2026, 5, 15), EpisodeCount: 4}))

		after := circleai.NewPersonaState("default")
		after.TopicWeights = map[string]float32{"finance": 3}
		after.TotalInteractions = 10
		after.PositiveSignals = 6
		after.NegativeSignals = 1
		if err := p.personaStore.Save(ctx, after); err != nil {
			t.Fatalf("Save: %v", err)
		}

		r1 := mustTick(t, p.consolidator, circleai.SleepMonthly)
		if r1.PersonaDeltasProduced != 1 {
			t.Fatalf("r1 deltas: got %d want 1", r1.PersonaDeltasProduced)
		}
		deltas, _ := p.personaDelta.GetForUser(ctx, "default")
		if len(deltas) != 1 {
			t.Fatalf("deltas len: got %d want 1", len(deltas))
		}
		if deltas[0].NewTopics["finance"] != 3 {
			t.Errorf("newTopics finance: got %v want 3", deltas[0].NewTopics["finance"])
		}
		if deltas[0].PeriodStart.String() != "2026-05-15" {
			t.Errorf("periodStart: got %s want 2026-05-15", deltas[0].PeriodStart.String())
		}
		if deltas[0].PeriodEnd.String() != "2026-05-15" {
			t.Errorf("periodEnd: got %s want 2026-05-15", deltas[0].PeriodEnd.String())
		}
		if !strings.Contains(deltas[0].Narrative, "New interests appeared: finance.") {
			t.Errorf("narrative: got %q", deltas[0].Narrative)
		}

		r2 := mustTick(t, p.consolidator, circleai.SleepMonthly)
		if r2.PersonaDeltasProduced != 0 {
			t.Errorf("r2 deltas: got %d want 0", r2.PersonaDeltasProduced)
		}
		deltas2, _ := p.personaDelta.GetForUser(ctx, "default")
		if len(deltas2) != 1 {
			t.Errorf("deltas len after re-tick: got %d want 1", len(deltas2))
		}
	})

	t.Run("produces no delta when the previous month has no daily summaries", func(t *testing.T) {
		clock := fixedClock("2026-06-08T09:00:00Z")
		p := makeConsolidator(t, clock, circleai.MemoryConsolidatorConfig{})
		r := mustTick(t, p.consolidator, circleai.SleepMonthly)
		if r.PersonaDeltasProduced != 0 {
			t.Errorf("deltas: got %d want 0", r.PersonaDeltasProduced)
		}
		if n, _ := p.personaDelta.Count(ctx); n != 0 {
			t.Errorf("personaDelta count: got %d want 0", n)
		}
	})
}

func TestConsolidation_DerivePersonaDelta(t *testing.T) {
	ctx := context.Background()

	t.Run("separates new topics from strengthened ones and computes signal deltas", func(t *testing.T) {
		s := circleai.NewHeuristicSummarizer(circleai.HeuristicSummarizerOptions{})
		before := circleai.NewPersonaState("default")
		before.TopicWeights = map[string]float32{"finance": 2}
		before.PositiveSignals = 1
		before.NegativeSignals = 1
		before.TotalInteractions = 5
		before.Verbosity = "balanced"

		after := circleai.NewPersonaState("default")
		after.TopicWeights = map[string]float32{"finance": 5, "travel": 3} // finance +3, travel new
		after.PositiveSignals = 7
		after.NegativeSignals = 2
		after.TotalInteractions = 20
		after.Verbosity = "detailed"

		day := circleai.NewDailySummary(circleai.DailyMemorySummaryInit{Day: circleai.NewCivilDate(2026, 5, 10)})
		delta, err := s.DerivePersonaDelta(ctx, before, after, []circleai.DailyMemorySummary{day})
		if err != nil {
			t.Fatalf("DerivePersonaDelta: %v", err)
		}

		if delta.NewTopics["travel"] != 3 {
			t.Errorf("newTopics travel: got %v want 3", delta.NewTopics["travel"])
		}
		if _, ok := delta.NewTopics["finance"]; ok {
			t.Errorf("finance should not be a new topic")
		}
		if delta.StrengthenedTopics["finance"] != 3 {
			t.Errorf("strengthened finance: got %v want 3", delta.StrengthenedTopics["finance"])
		}
		// netSignals = (7-1) - (2-1) = 5 ; interactions = 20-5 = 15
		if delta.NetSignalDelta != 5 {
			t.Errorf("netSignalDelta: got %d want 5", delta.NetSignalDelta)
		}
		if delta.InteractionsInPeriod != 15 {
			t.Errorf("interactionsInPeriod: got %d want 15", delta.InteractionsInPeriod)
		}
		if !strings.Contains(delta.Narrative, "Preferred verbosity shifted from balanced to detailed.") {
			t.Errorf("narrative verbosity: got %q", delta.Narrative)
		}
		if !strings.Contains(delta.Narrative, "Net feedback was positive (+5).") {
			t.Errorf("narrative feedback: got %q", delta.Narrative)
		}
	})
}

// ── OnDemand runs every tier ────────────────────────────────────────────────

func TestConsolidation_OnDemand(t *testing.T) {
	ctx := context.Background()

	t.Run("runs daily, weekly and monthly passes in one tick", func(t *testing.T) {
		clock := fixedClock("2026-06-08T09:00:00Z")
		p := makeConsolidator(t, clock, circleai.MemoryConsolidatorConfig{})

		// Daily fuel: a completed day earlier this week.
		mustAdd(t, p.episodic, consEntry(consEntryOpts{recorded: time.Date(2026, 6, 6, 10, 0, 0, 0, time.UTC), tags: map[string]string{"topic": "finance"}}))
		mustAdd(t, p.episodic, consEntry(consEntryOpts{recorded: time.Date(2026, 6, 6, 11, 0, 0, 0, time.UTC), tags: map[string]string{"topic": "finance"}}))
		// Weekly fuel: dailies inside last week (06-01..06-07) with a repeated topic.
		_ = p.daily.Upsert(ctx, circleai.NewDailySummary(circleai.DailyMemorySummaryInit{Day: circleai.NewCivilDate(2026, 6, 1), EpisodeCount: 2, TopicWeights: map[string]float32{"finance": 1}}))
		_ = p.daily.Upsert(ctx, circleai.NewDailySummary(circleai.DailyMemorySummaryInit{Day: circleai.NewCivilDate(2026, 6, 2), EpisodeCount: 1, TopicWeights: map[string]float32{"finance": 1}}))
		// Monthly fuel: a daily inside May + a persona.
		_ = p.daily.Upsert(ctx, circleai.NewDailySummary(circleai.DailyMemorySummaryInit{Day: circleai.NewCivilDate(2026, 5, 20), EpisodeCount: 3}))
		persona := circleai.NewPersonaState("default")
		persona.TopicWeights = map[string]float32{"finance": 2}
		persona.TotalInteractions = 6
		_ = p.personaStore.Save(ctx, persona)

		r := mustTick(t, p.consolidator, circleai.SleepOnDemand)
		if r.Kind != circleai.SleepOnDemand {
			t.Errorf("kind: got %v want OnDemand", r.Kind)
		}
		if r.DailySummariesProduced < 1 {
			t.Errorf("dailySummariesProduced: got %d want >=1", r.DailySummariesProduced)
		}
		if r.SemanticClustersProduced < 1 {
			t.Errorf("semanticClustersProduced: got %d want >=1", r.SemanticClustersProduced)
		}
		if r.PersonaDeltasProduced != 1 {
			t.Errorf("personaDeltasProduced: got %d want 1", r.PersonaDeltasProduced)
		}
		if !r.RanAtUTC.Equal(clock().UTC()) {
			t.Errorf("ranAtUtc: got %v want %v", r.RanAtUTC, clock().UTC())
		}
		if n, _ := p.semantic.Count(ctx); n < 1 {
			t.Errorf("semantic count: got %d want >=1", n)
		}
		if d, _ := p.personaDelta.GetForUser(ctx, "default"); len(d) != 1 {
			t.Errorf("persona deltas: got %d want 1", len(d))
		}
	})
}

// ── In-memory store cosine ranking + ordering ───────────────────────────────

func TestConsolidation_StoreRankingAndOrdering(t *testing.T) {
	ctx := context.Background()

	t.Run("CoreMemoryStore ranks by full cosine to the query centroid", func(t *testing.T) {
		core := circleai.NewInMemoryCoreMemoryStore()
		_ = core.Add(ctx, circleai.NewCoreMemory(circleai.CoreMemoryInit{Statement: "x", Embedding: []float32{1, 0}}))
		_ = core.Add(ctx, circleai.NewCoreMemory(circleai.CoreMemoryInit{Statement: "y", Embedding: []float32{0, 1}}))
		_ = core.Add(ctx, circleai.NewCoreMemory(circleai.CoreMemoryInit{Statement: "diag", Embedding: []float32{1, 1}}))

		ranked, err := core.Search(ctx, []float32{1, 0}, 3)
		if err != nil {
			t.Fatalf("Search: %v", err)
		}
		if ranked[0].Statement != "x" {
			t.Errorf("ranked[0]: got %q want x", ranked[0].Statement)
		}
		if ranked[2].Statement != "y" {
			t.Errorf("ranked[2]: got %q want y", ranked[2].Statement)
		}
		if ranked[1].Statement != "diag" {
			t.Errorf("ranked[1]: got %q want diag", ranked[1].Statement)
		}
	})

	t.Run("CoreMemoryStore falls back to reinforcement order when query is nil", func(t *testing.T) {
		core := circleai.NewInMemoryCoreMemoryStore()
		a := circleai.NewCoreMemory(circleai.CoreMemoryInit{Statement: "a"})
		b := circleai.NewCoreMemory(circleai.CoreMemoryInit{Statement: "b"})
		_ = core.Add(ctx, a)
		_ = core.Add(ctx, b)
		_ = core.Reinforce(ctx, b.ID)
		_ = core.Reinforce(ctx, b.ID)

		top, err := core.Search(ctx, nil, 2)
		if err != nil {
			t.Fatalf("Search: %v", err)
		}
		if top[0].Statement != "b" {
			t.Errorf("top[0]: got %q want b", top[0].Statement)
		}
		if top[0].ReinforcementCount != 2 {
			t.Errorf("reinforcementCount: got %d want 2", top[0].ReinforcementCount)
		}
	})

	t.Run("SemanticMemoryStore.getWeek orders by topicWeight desc; search ranks by centroid cosine", func(t *testing.T) {
		sem := circleai.NewInMemorySemanticMemoryStore()
		_ = sem.Add(ctx, circleai.NewSemanticCluster(circleai.SemanticMemoryClusterInit{WeekStartingMonday: circleai.NewCivilDate(2026, 6, 1), Topic: "low", TopicWeight: 1, CentroidEmbedding: []float32{0, 1}}))
		_ = sem.Add(ctx, circleai.NewSemanticCluster(circleai.SemanticMemoryClusterInit{WeekStartingMonday: circleai.NewCivilDate(2026, 6, 1), Topic: "high", TopicWeight: 5, CentroidEmbedding: []float32{1, 0}}))

		week, err := sem.GetWeek(ctx, circleai.NewCivilDate(2026, 6, 1))
		if err != nil {
			t.Fatalf("GetWeek: %v", err)
		}
		if len(week) != 2 || week[0].Topic != "high" || week[1].Topic != "low" {
			t.Errorf("getWeek order: got %v want [high low]", []string{week[0].Topic, week[1].Topic})
		}

		ranked, err := sem.Search(ctx, []float32{1, 0}, 2)
		if err != nil {
			t.Fatalf("Search: %v", err)
		}
		if ranked[0].Topic != "high" {
			t.Errorf("ranked[0]: got %q want high", ranked[0].Topic)
		}
	})

	t.Run("DailyMemoryStore.getRange returns day-ordered inclusive results", func(t *testing.T) {
		daily := circleai.NewInMemoryDailyMemoryStore()
		_ = daily.Upsert(ctx, circleai.NewDailySummary(circleai.DailyMemorySummaryInit{Day: circleai.NewCivilDate(2026, 6, 3)}))
		_ = daily.Upsert(ctx, circleai.NewDailySummary(circleai.DailyMemorySummaryInit{Day: circleai.NewCivilDate(2026, 6, 1)}))
		_ = daily.Upsert(ctx, circleai.NewDailySummary(circleai.DailyMemorySummaryInit{Day: circleai.NewCivilDate(2026, 6, 10)}))

		rng, err := daily.GetRange(ctx, circleai.NewCivilDate(2026, 6, 1), circleai.NewCivilDate(2026, 6, 5))
		if err != nil {
			t.Fatalf("GetRange: %v", err)
		}
		if len(rng) != 2 || rng[0].Day.String() != "2026-06-01" || rng[1].Day.String() != "2026-06-03" {
			got := []string{}
			for _, d := range rng {
				got = append(got, d.Day.String())
			}
			t.Errorf("getRange: got %v want [2026-06-01 2026-06-03]", got)
		}
	})
}
