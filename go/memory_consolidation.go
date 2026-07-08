// memory_consolidation.go
//
// Hierarchical memory consolidation — the "sleep cycle" engine. Ported from
// CircleAI.Memory.Consolidation (C#) — the reference — and mirrors the
// TypeScript pilot (memory/consolidation.ts) 1:1: SleepKind, CoreMemoryKind,
// the tier records (CoreMemory, DailyMemorySummary, SemanticMemoryCluster,
// PersonaDeltaSnapshot), ConsolidationOutcome, MemoryConsolidationOptions, the
// four tier stores (see memory_consolidation_stores.go), the HeuristicSummarizer,
// and the MemoryConsolidator orchestration engine.
//
// Promotes episodic → daily → weekly (semantic) → monthly (persona delta) →
// core, and enforces retention. All time decisions go through an injectable
// clock so tests are deterministic. This is the in-memory port: identical
// algorithms and formulas to the C# reference, no persistence.
//
// C# `DateOnly` is represented here as CivilDate — a dep-free, UTC {year,
// month, day} triple with a total ordering, so the range/idempotency/prune
// comparisons carry over unchanged.

package circleai

import (
	"context"
	"errors"
	"fmt"
	"math"
	"sort"
	"strings"
	"time"

	"github.com/google/uuid"
)

// ---------------------------------------------------------------------------
// CivilDate — DateOnly equivalent (UTC calendar date, dep-free)
// ---------------------------------------------------------------------------

// CivilDate is a UTC calendar date (year, month, day) — the Go stand-in for C#
// DateOnly. Comparable by value (map key) and totally ordered via Compare.
type CivilDate struct {
	Year  int
	Month int // 1..12
	Day   int // 1..31
}

// CivilDateOf returns the UTC calendar day of t.
func CivilDateOf(t time.Time) CivilDate {
	u := t.UTC()
	return CivilDate{Year: u.Year(), Month: int(u.Month()), Day: u.Day()}
}

// NewCivilDate builds a normalised CivilDate (e.g. month 13 → next year).
func NewCivilDate(year, month, day int) CivilDate {
	return CivilDateOf(time.Date(year, time.Month(month), day, 0, 0, 0, 0, time.UTC))
}

// time returns midnight-UTC time.Time for this date.
func (d CivilDate) time() time.Time {
	return time.Date(d.Year, time.Month(d.Month), d.Day, 0, 0, 0, 0, time.UTC)
}

// String formats the date as "YYYY-MM-DD".
func (d CivilDate) String() string {
	return fmt.Sprintf("%04d-%02d-%02d", d.Year, d.Month, d.Day)
}

// Compare returns -1, 0, or +1 as d is before, equal to, or after o.
func (d CivilDate) Compare(o CivilDate) int {
	if d.Year != o.Year {
		if d.Year < o.Year {
			return -1
		}
		return 1
	}
	if d.Month != o.Month {
		if d.Month < o.Month {
			return -1
		}
		return 1
	}
	if d.Day != o.Day {
		if d.Day < o.Day {
			return -1
		}
		return 1
	}
	return 0
}

// AddDays returns the date `days` (may be negative) from d.
func (d CivilDate) AddDays(days int) CivilDate {
	return CivilDateOf(d.time().AddDate(0, 0, days))
}

// MondayOf returns the Monday of the week containing d: d minus ((weekday+6)%7)
// days, with Sunday = 0 (matching C# DayOfWeek).
func (d CivilDate) MondayOf() CivilDate {
	dow := int(d.time().Weekday()) // Sun=0..Sat=6
	delta := (dow + 6) % 7         // Sun=0..Sat=6 → Mon=0..Sun=6
	return d.AddDays(-delta)
}

// MonthFirstDay returns the first day of the month containing d.
func (d CivilDate) MonthFirstDay() CivilDate {
	return CivilDate{Year: d.Year, Month: d.Month, Day: 1}
}

// ---------------------------------------------------------------------------
// SleepKind + CoreMemoryKind
// ---------------------------------------------------------------------------

// SleepKind is which tier of hierarchical consolidation a tick should run.
type SleepKind int

const (
	// SleepDaily collapses the day's episodic entries into a DailyMemorySummary.
	SleepDaily SleepKind = iota
	// SleepWeekly clusters the week's daily summaries into semantic topic groups.
	SleepWeekly
	// SleepMonthly computes the persona delta and writes a PersonaDeltaSnapshot.
	SleepMonthly
	// SleepOnDemand runs whichever tiers have work pending.
	SleepOnDemand
)

// String returns the C#/TS name of the sleep kind.
func (k SleepKind) String() string {
	switch k {
	case SleepDaily:
		return "Daily"
	case SleepWeekly:
		return "Weekly"
	case SleepMonthly:
		return "Monthly"
	case SleepOnDemand:
		return "OnDemand"
	default:
		return "Unknown"
	}
}

// CoreMemoryKind is why a memory was promoted to the core tier.
type CoreMemoryKind int

const (
	// CoreUserAsserted is a fact the user explicitly asked the AI to remember.
	CoreUserAsserted CoreMemoryKind = iota
	// CorePatternInferred is inferred from interaction patterns.
	CorePatternInferred
	// CoreHighSalience is promoted because of extreme salience.
	CoreHighSalience
	// CoreHostProvided is promoted by the host directly.
	CoreHostProvided
)

// String returns the C#/TS name of the core-memory kind.
func (k CoreMemoryKind) String() string {
	switch k {
	case CoreUserAsserted:
		return "UserAsserted"
	case CorePatternInferred:
		return "PatternInferred"
	case CoreHighSalience:
		return "HighSalience"
	case CoreHostProvided:
		return "HostProvided"
	default:
		return "Unknown"
	}
}

// ---------------------------------------------------------------------------
// Tier records
// ---------------------------------------------------------------------------

// CoreMemory is a core memory the AI will not forget. Compact by design.
type CoreMemory struct {
	// ID is the stable identifier.
	ID uuid.UUID
	// CreatedAtUTC is the UTC time the memory was committed to core.
	CreatedAtUTC time.Time
	// LastReinforcedUTC is the UTC time the memory was last reinforced. Mutable.
	LastReinforcedUTC time.Time
	// Statement is a short, dense statement, third-person from the AI's view.
	Statement string
	// Kind is how the memory came to be in core.
	Kind CoreMemoryKind
	// Topic is an optional topic label. nil when unset.
	Topic *string
	// Embedding is the embedding of Statement; nil when unavailable.
	Embedding []float32
	// ReinforcementCount is how many times this memory has been reinforced. Mutable.
	ReinforcementCount int
	// SourceMemoryID traces back to the lower-tier source memory, if one exists.
	SourceMemoryID *uuid.UUID
}

// CoreMemoryInit mirrors the C# object-initializer defaults for a CoreMemory.
type CoreMemoryInit struct {
	Statement      string
	Kind           CoreMemoryKind
	Topic          *string
	Embedding      []float32
	SourceMemoryID *uuid.UUID
	Clock          func() time.Time
}

// NewCoreMemory builds a CoreMemory with C#-equivalent defaults (new id, now
// timestamps, reinforcementCount 0).
func NewCoreMemory(init CoreMemoryInit) CoreMemory {
	clock := init.Clock
	if clock == nil {
		clock = time.Now
	}
	now := clock().UTC()
	return CoreMemory{
		ID:                 uuid.New(),
		CreatedAtUTC:       now,
		LastReinforcedUTC:  now,
		Statement:          init.Statement,
		Kind:               init.Kind,
		Topic:              init.Topic,
		Embedding:          init.Embedding,
		ReinforcementCount: 0,
		SourceMemoryID:     init.SourceMemoryID,
	}
}

// DailyMemorySummary is a compressed record of a single calendar day's episodic
// memory.
type DailyMemorySummary struct {
	// ID is the stable identifier.
	ID uuid.UUID
	// Day is the calendar day this summary covers (UTC).
	Day CivilDate
	// GeneratedAtUTC is the UTC time the summary was produced.
	GeneratedAtUTC time.Time
	// Summary is a short prose summary of the day's gist.
	Summary string
	// HighlightEntries are the most salient verbatim exchanges (typically 3–5).
	HighlightEntries []EpisodicMemoryEntry
	// EpisodeCount is the total episodic entries collapsed into this summary.
	EpisodeCount int
	// TopicWeights are aggregated topic weights across the day (label → weight).
	TopicWeights map[string]float32
	// TopicDispersion is the mean cosine-distance dispersion of the day (0..1).
	TopicDispersion float64
	// Salience is the salience score 0.0–1.0.
	Salience float64
}

// DailyMemorySummaryInit mirrors the C# object-initializer defaults.
type DailyMemorySummaryInit struct {
	Day              CivilDate
	Summary          string
	HighlightEntries []EpisodicMemoryEntry
	EpisodeCount     int
	TopicWeights     map[string]float32
	TopicDispersion  float64
	Salience         float64
	Clock            func() time.Time
}

// NewDailySummary builds a DailyMemorySummary with C#-equivalent defaults.
func NewDailySummary(init DailyMemorySummaryInit) DailyMemorySummary {
	clock := init.Clock
	if clock == nil {
		clock = time.Now
	}
	tw := init.TopicWeights
	if tw == nil {
		tw = make(map[string]float32)
	}
	return DailyMemorySummary{
		ID:               uuid.New(),
		Day:              init.Day,
		GeneratedAtUTC:   clock().UTC(),
		Summary:          init.Summary,
		HighlightEntries: init.HighlightEntries,
		EpisodeCount:     init.EpisodeCount,
		TopicWeights:     tw,
		TopicDispersion:  init.TopicDispersion,
		Salience:         init.Salience,
	}
}

// SemanticMemoryCluster is a topic-coherent cluster of daily summaries — the
// "semantic memory" tier.
type SemanticMemoryCluster struct {
	// ID is the stable identifier.
	ID uuid.UUID
	// GeneratedAtUTC is the UTC time the cluster was produced.
	GeneratedAtUTC time.Time
	// WeekStartingMonday is the Monday of the week this cluster covers (UTC).
	WeekStartingMonday CivilDate
	// Topic is the dominant topic label.
	Topic string
	// Summary is a short prose summary of the cluster's gist.
	Summary string
	// CentroidEmbedding is the mean of constituent embeddings; nil when unavailable.
	CentroidEmbedding []float32
	// SourceDailyIDs are the ids of the daily summaries that contributed.
	SourceDailyIDs []uuid.UUID
	// TopicWeight is the aggregate weight of the topic across constituent days.
	TopicWeight float32
	// Salience is the salience score 0.0–1.0.
	Salience float64
}

// SemanticMemoryClusterInit mirrors the C# object-initializer defaults.
type SemanticMemoryClusterInit struct {
	WeekStartingMonday CivilDate
	Topic              string
	Summary            string
	CentroidEmbedding  []float32
	SourceDailyIDs     []uuid.UUID
	TopicWeight        float32
	Salience           float64
	Clock              func() time.Time
}

// NewSemanticCluster builds a SemanticMemoryCluster with C#-equivalent defaults.
func NewSemanticCluster(init SemanticMemoryClusterInit) SemanticMemoryCluster {
	clock := init.Clock
	if clock == nil {
		clock = time.Now
	}
	ids := init.SourceDailyIDs
	if ids == nil {
		ids = []uuid.UUID{}
	}
	return SemanticMemoryCluster{
		ID:                 uuid.New(),
		GeneratedAtUTC:     clock().UTC(),
		WeekStartingMonday: init.WeekStartingMonday,
		Topic:              init.Topic,
		Summary:            init.Summary,
		CentroidEmbedding:  init.CentroidEmbedding,
		SourceDailyIDs:     ids,
		TopicWeight:        init.TopicWeight,
		Salience:           init.Salience,
	}
}

// PersonaDeltaSnapshot is the diff between a PersonaState at the start and end
// of a consolidation period.
type PersonaDeltaSnapshot struct {
	// ID is the stable identifier.
	ID uuid.UUID
	// GeneratedAtUTC is the UTC time the delta was captured.
	GeneratedAtUTC time.Time
	// PeriodStart is the start of the period (UTC).
	PeriodStart CivilDate
	// PeriodEnd is the end of the period (UTC).
	PeriodEnd CivilDate
	// UserID is the user identifier.
	UserID string
	// VerbosityBefore is verbosity at period start.
	VerbosityBefore string
	// VerbosityAfter is verbosity at period end.
	VerbosityAfter string
	// FormalityBefore is formality at period start.
	FormalityBefore string
	// FormalityAfter is formality at period end.
	FormalityAfter string
	// NewTopics are topics that emerged in the period (label → accumulated weight).
	NewTopics map[string]float32
	// StrengthenedTopics are topics that gained the most weight (label → delta).
	StrengthenedTopics map[string]float32
	// NewlyDisfavouredTopics are topics the user explicitly down-voted.
	NewlyDisfavouredTopics []string
	// NetSignalDelta is net positive minus negative signals across the period.
	NetSignalDelta int
	// InteractionsInPeriod is the total interactions during the period.
	InteractionsInPeriod int
	// Narrative is a short human-readable narrative of the persona change.
	Narrative string
}

// PersonaDeltaSnapshotInit mirrors the C# object-initializer defaults.
type PersonaDeltaSnapshotInit struct {
	PeriodStart            CivilDate
	PeriodEnd              CivilDate
	UserID                 string
	VerbosityBefore        string
	VerbosityAfter         string
	FormalityBefore        string
	FormalityAfter         string
	NewTopics              map[string]float32
	StrengthenedTopics     map[string]float32
	NewlyDisfavouredTopics []string
	NetSignalDelta         int
	InteractionsInPeriod   int
	Narrative              string
	Clock                  func() time.Time
}

// NewPersonaDelta builds a PersonaDeltaSnapshot with C#-equivalent defaults.
func NewPersonaDelta(init PersonaDeltaSnapshotInit) PersonaDeltaSnapshot {
	clock := init.Clock
	if clock == nil {
		clock = time.Now
	}
	userID := init.UserID
	if userID == "" {
		userID = "default"
	}
	newTopics := init.NewTopics
	if newTopics == nil {
		newTopics = make(map[string]float32)
	}
	strengthened := init.StrengthenedTopics
	if strengthened == nil {
		strengthened = make(map[string]float32)
	}
	disfavoured := init.NewlyDisfavouredTopics
	if disfavoured == nil {
		disfavoured = []string{}
	}
	return PersonaDeltaSnapshot{
		ID:                     uuid.New(),
		GeneratedAtUTC:         clock().UTC(),
		PeriodStart:            init.PeriodStart,
		PeriodEnd:              init.PeriodEnd,
		UserID:                 userID,
		VerbosityBefore:        init.VerbosityBefore,
		VerbosityAfter:         init.VerbosityAfter,
		FormalityBefore:        init.FormalityBefore,
		FormalityAfter:         init.FormalityAfter,
		NewTopics:              newTopics,
		StrengthenedTopics:     strengthened,
		NewlyDisfavouredTopics: disfavoured,
		NetSignalDelta:         init.NetSignalDelta,
		InteractionsInPeriod:   init.InteractionsInPeriod,
		Narrative:              init.Narrative,
	}
}

// ConsolidationOutcome is the outcome of a single consolidator tick.
type ConsolidationOutcome struct {
	Kind                     SleepKind
	DailySummariesProduced   int
	SemanticClustersProduced int
	PersonaDeltasProduced    int
	CorePromotions           int
	EpisodesPruned           int
	DailiesPruned            int
	SemanticsPruned          int
	RanAtUTC                 time.Time
}

// ---------------------------------------------------------------------------
// MemoryConsolidationOptions
// ---------------------------------------------------------------------------

// MemoryConsolidationOptions holds retention windows + core-promotion thresholds.
type MemoryConsolidationOptions struct {
	// EpisodicRetentionDays is days of episodic entries to retain after summarising.
	EpisodicRetentionDays int
	// DailyRetentionDays is days of daily summaries to retain after weekly consolidation.
	DailyRetentionDays int
	// SemanticRetentionDays is days of semantic clusters to retain.
	SemanticRetentionDays int
	// DailyCorePromotionThreshold is the salience threshold for daily → core.
	DailyCorePromotionThreshold float64
	// WeeklyCorePromotionThreshold is the salience threshold for weekly → core.
	WeeklyCorePromotionThreshold float64
}

// DefaultMemoryConsolidationOptions returns the defaults from the C# reference.
func DefaultMemoryConsolidationOptions() MemoryConsolidationOptions {
	return MemoryConsolidationOptions{
		EpisodicRetentionDays:        7,
		DailyRetentionDays:           30,
		SemanticRetentionDays:        365,
		DailyCorePromotionThreshold:  0.80,
		WeeklyCorePromotionThreshold: 0.75,
	}
}

// ---------------------------------------------------------------------------
// cosineFull — FULL cosine (differs from the episodic store's dot-only cosine).
// ---------------------------------------------------------------------------

// cosineFull is the full cosine similarity: dot / (‖a‖·‖b‖). Returns 0 on a
// length mismatch or a near-zero denominator. It does NOT assume the vectors are
// L2-normalised, so it differs from the episodic store's dot-product cosine —
// both are kept. Mirrors CosineSimilarity.Score in the C# reference.
func cosineFull(a, b []float32) float32 {
	if len(a) != len(b) {
		return 0
	}
	var dot, magA, magB float64
	for i := 0; i < len(a); i++ {
		dot += float64(a[i]) * float64(b[i])
		magA += float64(a[i]) * float64(a[i])
		magB += float64(b[i]) * float64(b[i])
	}
	denom := math.Sqrt(magA) * math.Sqrt(magB)
	// C# guards with `denom < double.Epsilon` (the smallest positive value).
	if denom < math.SmallestNonzeroFloat64 {
		return 0
	}
	return float32(dot / denom)
}

// ---------------------------------------------------------------------------
// IMemorySummarizer + HeuristicSummarizer
// ---------------------------------------------------------------------------

// IMemorySummarizer produces the text + scores for each consolidation tier.
type IMemorySummarizer interface {
	// SummarizeDay produces a DailyMemorySummary from the day's episodic entries.
	SummarizeDay(ctx context.Context, day CivilDate, entries []EpisodicMemoryEntry) (DailyMemorySummary, error)
	// ConsolidateWeek produces zero or more SemanticMemoryClusters from a week's dailies.
	ConsolidateWeek(ctx context.Context, weekStartingMonday CivilDate, daysInWeek []DailyMemorySummary) ([]SemanticMemoryCluster, error)
	// DerivePersonaDelta computes the PersonaDeltaSnapshot across the period.
	DerivePersonaDelta(ctx context.Context, before, after PersonaState, daysInPeriod []DailyMemorySummary) (PersonaDeltaSnapshot, error)
}

// HeuristicSummarizer is a no-LLM IMemorySummarizer. It produces summaries
// entirely from structural signals — embedding clustering, topic-weight
// aggregation, length-and-recency salience. Formulas are identical to the C#
// HeuristicSummarizer.
type HeuristicSummarizer struct {
	// HighlightCount is the max high-salience verbatim entries kept per day.
	HighlightCount int
	// MinDaysPerTopicForCluster is the min contributing days a topic needs across a week.
	MinDaysPerTopicForCluster int
	clock                     func() time.Time
}

// HeuristicSummarizerOptions configures a HeuristicSummarizer. Zero values fall
// back to the C# defaults (HighlightCount 5, MinDaysPerTopicForCluster 2, wall clock).
type HeuristicSummarizerOptions struct {
	HighlightCount            int
	MinDaysPerTopicForCluster int
	Clock                     func() time.Time
}

// NewHeuristicSummarizer builds a summarizer with C#-equivalent defaults.
func NewHeuristicSummarizer(opts HeuristicSummarizerOptions) *HeuristicSummarizer {
	hc := opts.HighlightCount
	if hc == 0 {
		hc = 5
	}
	md := opts.MinDaysPerTopicForCluster
	if md == 0 {
		md = 2
	}
	clock := opts.Clock
	if clock == nil {
		clock = time.Now
	}
	return &HeuristicSummarizer{HighlightCount: hc, MinDaysPerTopicForCluster: md, clock: clock}
}

// SummarizeDay collapses a day's episodic entries into a DailyMemorySummary.
func (h *HeuristicSummarizer) SummarizeDay(_ context.Context, day CivilDate, entries []EpisodicMemoryEntry) (DailyMemorySummary, error) {
	if entries == nil {
		return DailyMemorySummary{}, errors.New("entries required")
	}

	if len(entries) == 0 {
		return NewDailySummary(DailyMemorySummaryInit{
			Day:          day,
			Summary:      fmt.Sprintf("No exchanges recorded on %s.", day.String()),
			EpisodeCount: 0,
			Clock:        h.clock,
		}), nil
	}

	topicWeights := aggregateTopicWeights(entries)
	dispersion := meanPairwiseCosineDistance(entries)
	highlights := selectHighlights(entries, h.HighlightCount)
	salience := computeDailySalience(len(entries), topicWeights, dispersion)
	summary := buildDailySummaryText(day, len(entries), topicWeights, highlights)

	return NewDailySummary(DailyMemorySummaryInit{
		Day:              day,
		Summary:          summary,
		HighlightEntries: highlights,
		EpisodeCount:     len(entries),
		TopicWeights:     topicWeights,
		TopicDispersion:  dispersion,
		Salience:         salience,
		Clock:            h.clock,
	}), nil
}

// ConsolidateWeek clusters a week's daily summaries into semantic topic groups.
func (h *HeuristicSummarizer) ConsolidateWeek(_ context.Context, weekStartingMonday CivilDate, daysInWeek []DailyMemorySummary) ([]SemanticMemoryCluster, error) {
	if daysInWeek == nil {
		return nil, errors.New("daysInWeek required")
	}
	if len(daysInWeek) == 0 {
		return []SemanticMemoryCluster{}, nil
	}

	// Tally how many days each topic appeared in and its cumulative weight.
	// Topic labels arrive already lowercased from aggregateTopicWeights (C#
	// compares case-insensitively; the labels are already normalised).
	topicToDays := make(map[string][]DailyMemorySummary)
	topicToWeight := make(map[string]float32)

	for _, d := range daysInWeek {
		for _, topic := range sortedTopicKeys(d.TopicWeights) {
			w := d.TopicWeights[topic]
			topicToDays[topic] = append(topicToDays[topic], d)
			topicToWeight[topic] += w
		}
	}

	var totalWeight float32
	for _, w := range topicToWeight {
		totalWeight += w
	}
	if totalWeight <= 0 {
		totalWeight = 1
	}

	clusters := []SemanticMemoryCluster{}
	for _, topic := range topicsByWeightDesc(topicToWeight) {
		contributingDays := topicToDays[topic]
		if len(contributingDays) < h.MinDaysPerTopicForCluster {
			continue
		}

		centroid := centroidOfHighlights(contributingDays)
		weight := topicToWeight[topic]
		clusterSalience := math.Min(1.0, float64(weight)/float64(totalWeight)+(float64(len(contributingDays))/7.0)*0.25)

		sourceIDs := make([]uuid.UUID, 0, len(contributingDays))
		for _, d := range contributingDays {
			sourceIDs = append(sourceIDs, d.ID)
		}

		clusters = append(clusters, NewSemanticCluster(SemanticMemoryClusterInit{
			WeekStartingMonday: weekStartingMonday,
			Topic:              topic,
			Summary:            buildWeeklyClusterText(topic, contributingDays),
			CentroidEmbedding:  centroid,
			SourceDailyIDs:     sourceIDs,
			TopicWeight:        weight,
			Salience:           clusterSalience,
			Clock:              h.clock,
		}))
	}
	return clusters, nil
}

// DerivePersonaDelta computes the PersonaDeltaSnapshot across the period.
func (h *HeuristicSummarizer) DerivePersonaDelta(_ context.Context, before, after PersonaState, daysInPeriod []DailyMemorySummary) (PersonaDeltaSnapshot, error) {
	if daysInPeriod == nil {
		return PersonaDeltaSnapshot{}, errors.New("daysInPeriod required")
	}

	newTopics := make(map[string]float32)
	strengthened := make(map[string]float32)
	for _, topic := range sortedTopicKeys(after.TopicWeights) {
		afterW := after.TopicWeights[topic]
		beforeW := before.TopicWeights[topic] // zero value when absent
		delta := afterW - beforeW
		if beforeW <= 0 && afterW > 0 {
			newTopics[topic] = afterW
		} else if delta > 0 {
			strengthened[topic] = delta
		}
	}

	var disfavouredNew []string
	for _, t := range sortedSetKeys(after.DisfavouredTopics) {
		if _, ok := before.DisfavouredTopics[t]; !ok {
			disfavouredNew = append(disfavouredNew, t)
		}
	}

	netSignals := (after.PositiveSignals - before.PositiveSignals) -
		(after.NegativeSignals - before.NegativeSignals)
	interactions := after.TotalInteractions - before.TotalInteractions

	var periodStart, periodEnd CivilDate
	if len(daysInPeriod) > 0 {
		periodStart = minDay(daysInPeriod)
		periodEnd = maxDay(daysInPeriod)
	} else {
		periodStart = CivilDateOf(after.LastUpdatedUTC)
		periodEnd = CivilDateOf(after.LastUpdatedUTC)
	}

	narrative := buildPersonaNarrative(before, after, newTopics, strengthened, disfavouredNew,
		netSignals, interactions, periodStart, periodEnd)

	return NewPersonaDelta(PersonaDeltaSnapshotInit{
		UserID:                 after.UserID,
		PeriodStart:            periodStart,
		PeriodEnd:              periodEnd,
		VerbosityBefore:        before.Verbosity,
		VerbosityAfter:         after.Verbosity,
		FormalityBefore:        before.Formality,
		FormalityAfter:         after.Formality,
		NewTopics:              newTopics,
		StrengthenedTopics:     strengthened,
		NewlyDisfavouredTopics: disfavouredNew,
		NetSignalDelta:         netSignals,
		InteractionsInPeriod:   interactions,
		Narrative:              narrative,
		Clock:                  h.clock,
	}), nil
}

// ── Summarizer helpers — topic + dispersion ─────────────────────────────────

// aggregateTopicWeights builds topic weights from "topic" (+1) and pipe-split
// "topics" (each +1), lowercased/trimmed.
func aggregateTopicWeights(entries []EpisodicMemoryEntry) map[string]float32 {
	weights := make(map[string]float32)
	for _, e := range entries {
		if e.Tags == nil {
			continue
		}
		if t, ok := e.Tags["topic"]; ok && strings.TrimSpace(t) != "" {
			accumulateTopic(weights, t, 1)
		}
		if multi, ok := e.Tags["topics"]; ok && strings.TrimSpace(multi) != "" {
			for _, p := range strings.Split(multi, "|") {
				if p == "" {
					continue // RemoveEmptyEntries
				}
				accumulateTopic(weights, p, 1)
			}
		}
	}
	return weights
}

func accumulateTopic(dict map[string]float32, topic string, weight float32) {
	key := strings.ToLower(strings.TrimSpace(topic))
	if key == "" {
		return
	}
	dict[key] += weight
}

// meanPairwiseCosineDistance is the mean over all pairs of (1 - clamp(fullCosine,
// -1, 1)); 0 when fewer than 2 embedded entries.
func meanPairwiseCosineDistance(entries []EpisodicMemoryEntry) float64 {
	var withEmbeddings []EpisodicMemoryEntry
	for _, e := range entries {
		if hasEmbedding(e) {
			withEmbeddings = append(withEmbeddings, e)
		}
	}
	if len(withEmbeddings) < 2 {
		return 0
	}

	var total float64
	pairs := 0
	for i := 0; i < len(withEmbeddings); i++ {
		for j := i + 1; j < len(withEmbeddings); j++ {
			sim := cosineFull(withEmbeddings[i].Embedding, withEmbeddings[j].Embedding)
			total += 1.0 - clampFloat(float64(sim), -1.0, 1.0)
			pairs++
		}
	}
	if pairs == 0 {
		return 0
	}
	return clampFloat(total/float64(pairs), 0.0, 1.0)
}

// selectHighlights returns the top-count entries by salience proxy (or all when
// ≤count), re-sorted by time ascending.
func selectHighlights(entries []EpisodicMemoryEntry, count int) []EpisodicMemoryEntry {
	if len(entries) <= count {
		out := make([]EpisodicMemoryEntry, len(entries))
		copy(out, entries)
		sortByTimeAsc(out)
		return out
	}

	type scored struct {
		entry EpisodicMemoryEntry
		score float64
	}
	scoredEntries := make([]scored, 0, len(entries))
	for _, e := range entries {
		scoredEntries = append(scoredEntries, scored{entry: e, score: entrySalienceProxy(e, entries)})
	}
	// OrderByDescending(score).ThenByDescending(recordedAt).
	sort.SliceStable(scoredEntries, func(i, j int) bool {
		if scoredEntries[i].score != scoredEntries[j].score {
			return scoredEntries[i].score > scoredEntries[j].score
		}
		return scoredEntries[i].entry.RecordedAtUTC.After(scoredEntries[j].entry.RecordedAtUTC)
	})
	top := make([]EpisodicMemoryEntry, 0, count)
	for i := 0; i < count; i++ {
		top = append(top, scoredEntries[i].entry)
	}
	sortByTimeAsc(top)
	return top
}

func entrySalienceProxy(entry EpisodicMemoryEntry, all []EpisodicMemoryEntry) float64 {
	lengthScore := math.Min(1.0, float64(len(entry.UserText)+len(entry.AssistantText))/800.0)
	uniquenessScore := 0.5
	if hasEmbedding(entry) {
		var others []EpisodicMemoryEntry
		for _, e := range all {
			if e.ID != entry.ID && hasEmbedding(e) {
				others = append(others, e)
			}
		}
		if len(others) > 0 {
			var sum float64
			for _, e := range others {
				sum += float64(cosineFull(entry.Embedding, e.Embedding))
			}
			meanSim := sum / float64(len(others))
			uniquenessScore = 1.0 - clampFloat(meanSim, -1.0, 1.0)
		}
	}
	return lengthScore*0.6 + uniquenessScore*0.4
}

// computeDailySalience = volume·0.4 + dispersion·0.3 + topicConcentration·0.3.
func computeDailySalience(episodeCount int, topicWeights map[string]float32, dispersion float64) float64 {
	volumeScore := math.Min(1.0, float64(episodeCount)/30.0)
	var topicConcentration float64
	if len(topicWeights) == 0 {
		topicConcentration = 0.5
	} else {
		maxW := float32(math.Inf(-1))
		var sumW float32
		for _, w := range topicWeights {
			if w > maxW {
				maxW = w
			}
			sumW += w
		}
		topicConcentration = math.Min(1.0, float64(maxW)/math.Max(1, float64(sumW)))
	}
	return volumeScore*0.4 + dispersion*0.3 + topicConcentration*0.3
}

// centroidOfHighlights is the mean of all highlight embeddings across
// contributing days; nil when none.
func centroidOfHighlights(days []DailyMemorySummary) []float32 {
	var allEmbeddings [][]float32
	for _, d := range days {
		for _, e := range d.HighlightEntries {
			if hasEmbedding(e) {
				allEmbeddings = append(allEmbeddings, e.Embedding)
			}
		}
	}
	if len(allEmbeddings) == 0 {
		return nil
	}
	dim := len(allEmbeddings[0])
	centroid := make([]float32, dim)
	for _, e := range allEmbeddings {
		for i := 0; i < dim && i < len(e); i++ {
			centroid[i] += e[i]
		}
	}
	for i := 0; i < dim; i++ {
		centroid[i] /= float32(len(allEmbeddings))
	}
	return centroid
}

// ── Summarizer helpers — text builders ──────────────────────────────────────

func buildDailySummaryText(day CivilDate, count int, topics map[string]float32, highlights []EpisodicMemoryEntry) string {
	topTopics := topNKeys(topics, 3)

	topicsClause := ""
	if len(topTopics) > 0 {
		topicsClause = " Top topics: " + strings.Join(topTopics, ", ") + "."
	}

	highlightClause := ""
	if len(highlights) > 0 {
		highlightClause = " Standout moment: \"" + truncate(highlights[0].UserText, 120) + "\"."
	}

	exchangeWord := "exchanges."
	if count == 1 {
		exchangeWord = "exchange."
	}
	return fmt.Sprintf("On %s you had %d %s", day.String(), count, exchangeWord) + topicsClause + highlightClause
}

func buildWeeklyClusterText(topic string, contributingDays []DailyMemorySummary) string {
	totalEpisodes := 0
	for _, d := range contributingDays {
		totalEpisodes += d.EpisodeCount
	}
	return fmt.Sprintf("Across %d days this week you returned to \"%s\" — %d exchanges in total.",
		len(contributingDays), topic, totalEpisodes)
}

func buildPersonaNarrative(before, after PersonaState, newTopics, strengthened map[string]float32, disfavoured []string, netSignals, interactions int, periodStart, periodEnd CivilDate) string {
	var parts []string
	parts = append(parts, fmt.Sprintf("Between %s and %s, %d interactions were recorded.",
		periodStart.String(), periodEnd.String(), interactions))
	if len(newTopics) > 0 {
		parts = append(parts, "New interests appeared: "+strings.Join(topNKeys(newTopics, 3), ", ")+".")
	}
	if len(strengthened) > 0 {
		parts = append(parts, "Existing interests deepened around "+strings.Join(topNKeys(strengthened, 3), ", ")+".")
	}
	if len(disfavoured) > 0 {
		parts = append(parts, "Topics now avoided: "+strings.Join(disfavoured, ", ")+".")
	}
	if before.Verbosity != after.Verbosity {
		parts = append(parts, fmt.Sprintf("Preferred verbosity shifted from %s to %s.", before.Verbosity, after.Verbosity))
	}
	if before.Formality != after.Formality {
		parts = append(parts, fmt.Sprintf("Preferred tone shifted from %s to %s.", before.Formality, after.Formality))
	}
	if netSignals != 0 {
		if netSignals > 0 {
			parts = append(parts, fmt.Sprintf("Net feedback was positive (+%d).", netSignals))
		} else {
			parts = append(parts, fmt.Sprintf("Net feedback was negative (%d).", netSignals))
		}
	}
	return strings.Join(parts, " ")
}

// ── Shared small helpers ────────────────────────────────────────────────────

// topNKeys returns the keys of m ordered by value desc, top-n. Ties are broken
// by key ascending for deterministic ordering.
func topNKeys(m map[string]float32, n int) []string {
	keys := topicsByWeightDesc(m)
	if n < len(keys) {
		keys = keys[:n]
	}
	return keys
}

// topicsByWeightDesc returns the keys of m ordered by value desc, ties by key asc.
func topicsByWeightDesc(m map[string]float32) []string {
	keys := make([]string, 0, len(m))
	for k := range m {
		keys = append(keys, k)
	}
	sort.SliceStable(keys, func(i, j int) bool {
		if m[keys[i]] != m[keys[j]] {
			return m[keys[i]] > m[keys[j]]
		}
		return keys[i] < keys[j]
	})
	return keys
}

// sortedTopicKeys returns the keys of m sorted ascending (deterministic iteration).
func sortedTopicKeys(m map[string]float32) []string {
	keys := make([]string, 0, len(m))
	for k := range m {
		keys = append(keys, k)
	}
	sort.Strings(keys)
	return keys
}

// sortedSetKeys returns the keys of a set sorted ascending.
func sortedSetKeys(m map[string]struct{}) []string {
	keys := make([]string, 0, len(m))
	for k := range m {
		keys = append(keys, k)
	}
	sort.Strings(keys)
	return keys
}

func truncate(s string, max int) string {
	if s == "" {
		return ""
	}
	if len(s) <= max {
		return s
	}
	return strings.TrimRight(s[:max], " \t\n\r\f\v") + "…"
}

func hasEmbedding(e EpisodicMemoryEntry) bool {
	return e.Embedding != nil && len(e.Embedding) > 0
}

func sortByTimeAsc(entries []EpisodicMemoryEntry) {
	sort.SliceStable(entries, func(i, j int) bool {
		return entries[i].RecordedAtUTC.Before(entries[j].RecordedAtUTC)
	})
}

func minDay(days []DailyMemorySummary) CivilDate {
	m := days[0].Day
	for _, d := range days {
		if d.Day.Compare(m) < 0 {
			m = d.Day
		}
	}
	return m
}

func maxDay(days []DailyMemorySummary) CivilDate {
	m := days[0].Day
	for _, d := range days {
		if d.Day.Compare(m) > 0 {
			m = d.Day
		}
	}
	return m
}

// ---------------------------------------------------------------------------
// IMemoryConsolidator + MemoryConsolidator
// ---------------------------------------------------------------------------

// IMemoryConsolidator promotes lower-tier memory into higher tiers and enforces
// retention.
type IMemoryConsolidator interface {
	// Tick runs the consolidation pass for the given kind. SleepOnDemand runs
	// every tier with work pending. Returns the breakdown of produced/pruned.
	Tick(ctx context.Context, kind SleepKind) (ConsolidationOutcome, error)
}

// MemoryConsolidator is the default IMemoryConsolidator implementation.
type MemoryConsolidator struct {
	episodic     IEpisodicMemoryStore
	daily        IDailyMemoryStore
	semantic     ISemanticMemoryStore
	personaDelta IPersonaDeltaStore
	core         ICoreMemoryStore
	personaStore IPersonaStore
	summarizer   IMemorySummarizer
	options      MemoryConsolidationOptions
	clock        func() time.Time
	userID       string
}

// MemoryConsolidatorConfig holds the optional knobs for a MemoryConsolidator.
// Zero values fall back to the C# defaults (default options, wall clock, "default"
// user).
type MemoryConsolidatorConfig struct {
	Options *MemoryConsolidationOptions
	Clock   func() time.Time
	UserID  string
}

// NewMemoryConsolidator wires a consolidator over the four tier stores, the
// persona store, and a summarizer. All stores + the summarizer are required.
func NewMemoryConsolidator(
	episodic IEpisodicMemoryStore,
	daily IDailyMemoryStore,
	semantic ISemanticMemoryStore,
	personaDelta IPersonaDeltaStore,
	core ICoreMemoryStore,
	personaStore IPersonaStore,
	summarizer IMemorySummarizer,
	cfg MemoryConsolidatorConfig,
) (*MemoryConsolidator, error) {
	if episodic == nil {
		return nil, errors.New("episodic required")
	}
	if daily == nil {
		return nil, errors.New("daily required")
	}
	if semantic == nil {
		return nil, errors.New("semantic required")
	}
	if personaDelta == nil {
		return nil, errors.New("personaDelta required")
	}
	if core == nil {
		return nil, errors.New("core required")
	}
	if personaStore == nil {
		return nil, errors.New("personaStore required")
	}
	if summarizer == nil {
		return nil, errors.New("summarizer required")
	}
	options := DefaultMemoryConsolidationOptions()
	if cfg.Options != nil {
		options = *cfg.Options
	}
	clock := cfg.Clock
	if clock == nil {
		clock = time.Now
	}
	userID := cfg.UserID
	if userID == "" {
		userID = "default"
	}
	return &MemoryConsolidator{
		episodic:     episodic,
		daily:        daily,
		semantic:     semantic,
		personaDelta: personaDelta,
		core:         core,
		personaStore: personaStore,
		summarizer:   summarizer,
		options:      options,
		clock:        clock,
		userID:       userID,
	}, nil
}

// Tick runs the consolidation pass for the given kind.
func (c *MemoryConsolidator) Tick(ctx context.Context, kind SleepKind) (ConsolidationOutcome, error) {
	now := c.clock().UTC()
	var dailies, clusters, deltas, corePromoted int
	var episodesPruned, dailiesPruned, semanticsPruned int

	if kind == SleepDaily || kind == SleepOnDemand {
		produced, promotedFromDaily, err := c.runDaily(ctx, now)
		if err != nil {
			return ConsolidationOutcome{}, err
		}
		dailies = produced
		corePromoted += promotedFromDaily
		p, err := c.pruneEpisodic(ctx, now)
		if err != nil {
			return ConsolidationOutcome{}, err
		}
		episodesPruned += p
	}

	if kind == SleepWeekly || kind == SleepOnDemand {
		produced, promotedFromWeekly, err := c.runWeekly(ctx, now)
		if err != nil {
			return ConsolidationOutcome{}, err
		}
		clusters = produced
		corePromoted += promotedFromWeekly
		p, err := c.pruneDailies(ctx, now)
		if err != nil {
			return ConsolidationOutcome{}, err
		}
		dailiesPruned += p
	}

	if kind == SleepMonthly || kind == SleepOnDemand {
		produced, err := c.runMonthly(ctx, now)
		if err != nil {
			return ConsolidationOutcome{}, err
		}
		deltas = produced
		p, err := c.pruneSemantics(ctx, now)
		if err != nil {
			return ConsolidationOutcome{}, err
		}
		semanticsPruned += p
	}

	return ConsolidationOutcome{
		Kind:                     kind,
		DailySummariesProduced:   dailies,
		SemanticClustersProduced: clusters,
		PersonaDeltasProduced:    deltas,
		CorePromotions:           corePromoted,
		EpisodesPruned:           episodesPruned,
		DailiesPruned:            dailiesPruned,
		SemanticsPruned:          semanticsPruned,
		RanAtUTC:                 now,
	}, nil
}

// ── Daily pass ─────────────────────────────────────────────────────────────

func (c *MemoryConsolidator) runDaily(ctx context.Context, now time.Time) (int, int, error) {
	recent, err := c.episodic.GetRecent(ctx, maxInt)
	if err != nil {
		return 0, 0, err
	}
	if len(recent) == 0 {
		return 0, 0, nil
	}

	// Group episodes by their calendar day (UTC).
	today := CivilDateOf(now)
	byDay := make(map[CivilDate][]EpisodicMemoryEntry)
	for _, e := range recent {
		key := CivilDateOf(e.RecordedAtUTC)
		byDay[key] = append(byDay[key], e)
	}

	produced := 0
	promoted := 0
	for _, day := range sortedDayKeys(byDay) {
		group := byDay[day]
		if !(day.Compare(today) < 0) {
			continue // only fully completed days
		}

		existing, err := c.daily.Get(ctx, day)
		if err != nil {
			return 0, 0, err
		}
		if existing != nil && existing.EpisodeCount == len(group) {
			continue // idempotent skip — already consolidated this day
		}

		ordered := make([]EpisodicMemoryEntry, len(group))
		copy(ordered, group)
		sortByTimeAsc(ordered)
		summary, err := c.summarizer.SummarizeDay(ctx, day, ordered)
		if err != nil {
			return 0, 0, err
		}
		if err := c.daily.Upsert(ctx, summary); err != nil {
			return 0, 0, err
		}
		produced++

		if summary.Salience >= c.options.DailyCorePromotionThreshold {
			p, err := c.promoteDailyToCore(ctx, summary)
			if err != nil {
				return 0, 0, err
			}
			promoted += p
		}
	}
	return produced, promoted, nil
}

// ── Weekly pass ────────────────────────────────────────────────────────────

func (c *MemoryConsolidator) runWeekly(ctx context.Context, now time.Time) (int, int, error) {
	today := CivilDateOf(now)
	thisMonday := today.MondayOf()
	lastMonday := thisMonday.AddDays(-7)
	lastSunday := lastMonday.AddDays(6)

	lastWeek, err := c.daily.GetRange(ctx, lastMonday, lastSunday)
	if err != nil {
		return 0, 0, err
	}
	if len(lastWeek) == 0 {
		return 0, 0, nil
	}

	// Idempotency: if we already have clusters for this week, skip.
	existing, err := c.semantic.GetWeek(ctx, lastMonday)
	if err != nil {
		return 0, 0, err
	}
	if len(existing) > 0 {
		return 0, 0, nil
	}

	clusters, err := c.summarizer.ConsolidateWeek(ctx, lastMonday, lastWeek)
	if err != nil {
		return 0, 0, err
	}
	promoted := 0
	for _, cl := range clusters {
		if err := c.semantic.Add(ctx, cl); err != nil {
			return 0, 0, err
		}
		if cl.Salience >= c.options.WeeklyCorePromotionThreshold {
			p, err := c.promoteClusterToCore(ctx, cl)
			if err != nil {
				return 0, 0, err
			}
			promoted += p
		}
	}
	return len(clusters), promoted, nil
}

// ── Monthly pass ───────────────────────────────────────────────────────────

func (c *MemoryConsolidator) runMonthly(ctx context.Context, now time.Time) (int, error) {
	today := CivilDateOf(now)
	// Consider the most recently completed full month.
	firstOfThisMonth := today.MonthFirstDay()
	lastMonthEnd := firstOfThisMonth.AddDays(-1)
	lastMonthStart := lastMonthEnd.MonthFirstDay()

	// Idempotency: skip if we already have a delta whose PeriodStart falls in
	// the previous month (compared by month-year, not exact dates).
	existingDeltas, err := c.personaDelta.GetForUser(ctx, c.userID)
	if err != nil {
		return 0, err
	}
	for _, d := range existingDeltas {
		if d.PeriodStart.Year == lastMonthStart.Year && d.PeriodStart.Month == lastMonthStart.Month {
			return 0, nil
		}
	}

	days, err := c.daily.GetRange(ctx, lastMonthStart, lastMonthEnd)
	if err != nil {
		return 0, err
	}
	if len(days) == 0 {
		return 0, nil
	}

	after, err := c.personaStore.Load(ctx, c.userID)
	if err != nil {
		return 0, err
	}

	// For "before", reconstruct from the most recent prior delta if one exists;
	// otherwise treat as a fresh persona.
	var priors []PersonaDeltaSnapshot
	for _, d := range existingDeltas {
		if d.PeriodEnd.Compare(lastMonthStart) < 0 {
			priors = append(priors, d)
		}
	}
	// OrderByDescending(PeriodEnd), then FirstOrDefault.
	sort.SliceStable(priors, func(i, j int) bool { return priors[i].PeriodEnd.Compare(priors[j].PeriodEnd) > 0 })

	var before PersonaState
	if len(priors) == 0 {
		before = NewPersonaState(c.userID)
	} else {
		before = reconstructPersonaBefore(after, days, priors[0])
	}

	delta, err := c.summarizer.DerivePersonaDelta(ctx, before, after, days)
	if err != nil {
		return 0, err
	}
	if err := c.personaDelta.Add(ctx, delta); err != nil {
		return 0, err
	}
	return 1, nil
}

// ── Core promotions ──────────────────────────────────────────────────────

func (c *MemoryConsolidator) promoteDailyToCore(ctx context.Context, summary DailyMemorySummary) (int, error) {
	// FirstOrDefault on TopicWeights.OrderByDescending — nil Topic when empty.
	var topTopic *string
	topWeight := float32(math.Inf(-1))
	for _, k := range topicsByWeightDesc(summary.TopicWeights) {
		v := summary.TopicWeights[k]
		if v > topWeight {
			topWeight = v
			kk := k
			topTopic = &kk
		}
	}

	var statement string
	if topTopic == nil {
		statement = fmt.Sprintf("On %s an unusually meaningful day was recorded.", summary.Day.String())
	} else {
		statement = fmt.Sprintf("\"%s\" mattered enough on %s to be remembered.", *topTopic, summary.Day.String())
	}

	var embedding []float32
	for _, h := range summary.HighlightEntries {
		if h.Embedding != nil && len(h.Embedding) > 0 {
			embedding = h.Embedding
			break
		}
	}

	sourceID := summary.ID
	memory := NewCoreMemory(CoreMemoryInit{
		Statement:      statement,
		Kind:           CoreHighSalience,
		Topic:          topTopic,
		Embedding:      embedding,
		SourceMemoryID: &sourceID,
		Clock:          c.clock,
	})
	if err := c.core.Add(ctx, memory); err != nil {
		return 0, err
	}
	return 1, nil
}

func (c *MemoryConsolidator) promoteClusterToCore(ctx context.Context, cluster SemanticMemoryCluster) (int, error) {
	topic := cluster.Topic
	sourceID := cluster.ID
	memory := NewCoreMemory(CoreMemoryInit{
		Statement:      fmt.Sprintf("\"%s\" has been a recurring theme (week of %s).", cluster.Topic, cluster.WeekStartingMonday.String()),
		Kind:           CorePatternInferred,
		Topic:          &topic,
		Embedding:      cluster.CentroidEmbedding,
		SourceMemoryID: &sourceID,
		Clock:          c.clock,
	})
	if err := c.core.Add(ctx, memory); err != nil {
		return 0, err
	}
	return 1, nil
}

// ── Retention ────────────────────────────────────────────────────────────

func (c *MemoryConsolidator) pruneEpisodic(ctx context.Context, now time.Time) (int, error) {
	cutoff := now.AddDate(0, 0, -c.options.EpisodicRetentionDays)
	return c.episodic.PruneOlderThan(ctx, cutoff)
}

func (c *MemoryConsolidator) pruneDailies(ctx context.Context, now time.Time) (int, error) {
	cutoff := CivilDateOf(now).AddDays(-c.options.DailyRetentionDays)
	return c.daily.PruneOlderThan(ctx, cutoff)
}

func (c *MemoryConsolidator) pruneSemantics(ctx context.Context, now time.Time) (int, error) {
	cutoff := CivilDateOf(now).AddDays(-c.options.SemanticRetentionDays)
	return c.semantic.PruneOlderThan(ctx, cutoff)
}

// reconstructPersonaBefore approximates the persona at the start of the period
// by subtracting the in-period gains from the current persona. Conservative —
// when in doubt it shows no change. Faithful port of ReconstructPersonaBeforeAsync.
func reconstructPersonaBefore(after PersonaState, daysInPeriod []DailyMemorySummary, prior PersonaDeltaSnapshot) PersonaState {
	before := NewPersonaState(after.UserID)
	before.Verbosity = prior.VerbosityAfter
	before.Formality = prior.FormalityAfter
	before.PreferredLocale = after.PreferredLocale
	episodeSum := 0
	for _, d := range daysInPeriod {
		episodeSum += d.EpisodeCount
	}
	before.TotalInteractions = after.TotalInteractions - episodeSum
	before.PositiveSignals = maxInt2(0, after.PositiveSignals-clampPositive(prior.NetSignalDelta))
	before.NegativeSignals = after.NegativeSignals

	// Carry over topic weights minus the strongest in-period gains.
	before.TopicWeights = make(map[string]float32)
	for topic, w := range after.TopicWeights {
		if delta, ok := prior.StrengthenedTopics[topic]; ok {
			before.TopicWeights[topic] = maxFloat32(0, w-delta)
		} else {
			before.TopicWeights[topic] = w
		}
	}
	before.DisfavouredTopics = make(map[string]struct{})
	for t := range after.DisfavouredTopics {
		before.DisfavouredTopics[t] = struct{}{}
	}
	return before
}

// ---------------------------------------------------------------------------
// small numeric helpers
// ---------------------------------------------------------------------------

// maxInt is int.MaxValue equivalent — the "give me everything" GetRecent count.
const maxInt = int(^uint(0) >> 1)

func clampPositive(v int) int {
	if v < 0 {
		return 0
	}
	return v
}

func maxInt2(a, b int) int {
	if a > b {
		return a
	}
	return b
}

func maxFloat32(a, b float32) float32 {
	if a > b {
		return a
	}
	return b
}

// sortedDayKeys returns the keys of m sorted ascending for deterministic iteration.
func sortedDayKeys(m map[CivilDate][]EpisodicMemoryEntry) []CivilDate {
	keys := make([]CivilDate, 0, len(m))
	for k := range m {
		keys = append(keys, k)
	}
	sort.SliceStable(keys, func(i, j int) bool { return keys[i].Compare(keys[j]) < 0 })
	return keys
}

// Compile-time assertions.
var (
	_ IMemorySummarizer   = (*HeuristicSummarizer)(nil)
	_ IMemoryConsolidator = (*MemoryConsolidator)(nil)
)
