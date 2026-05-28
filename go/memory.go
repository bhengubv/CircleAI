// memory.go
//
// AffectState, PersonaState, EpisodicMemoryEntry, FeedbackSignal, Goal,
// related enums, and the five store interfaces.
//
// AffectState math is byte-identical to the C# reference implementation.

package circleai

import (
	"context"
	"strings"
	"time"

	"github.com/google/uuid"
)

// ---------------------------------------------------------------------------
// AffectState
// ---------------------------------------------------------------------------

// AffectState is B!'s current emotional/engagement state — the "HER affect
// layer". Five float32 dimensions, all 0.0–1.0. Persisted per-user and
// injected into the system prompt to shape response tone and initiative.
type AffectState struct {
	// UserID is an opaque user identifier (device ID or hashed phone number).
	// Never contains PII in plaintext.
	UserID string

	// LastUpdatedUTC is the UTC time of the last update to this affect state.
	LastUpdatedUTC time.Time

	// Curiosity: 0 = bored, 1 = fascinated. Drives proactive questions.
	Curiosity float32

	// Engagement: 0 = disengaged, 1 = fully engaged.
	Engagement float32

	// Uncertainty: 0 = confident, 1 = confused. High = ask clarifying questions.
	Uncertainty float32

	// Rapport: 0 = stranger, 1 = deep rapport. Grows slowly over many sessions.
	Rapport float32

	// Energy: 0 = subdued, 1 = energetic.
	Energy float32
}

// NewAffectState creates a default AffectState for the given userID.
func NewAffectState(userID string) AffectState {
	return AffectState{
		UserID:         userID,
		LastUpdatedUTC: time.Now().UTC(),
		Curiosity:      0.5,
		Engagement:     0.5,
		Uncertainty:    0.2,
		Rapport:        0.0,
		Energy:         0.5,
	}
}

// ToSystemPromptHint builds a compact affect hint for injection into the
// system prompt. Only emits lines that deviate meaningfully from neutral (0.5).
func (a *AffectState) ToSystemPromptHint() string {
	var hints []string

	if a.Curiosity > 0.7 {
		hints = append(hints, "You are deeply curious about this topic — ask a follow-up question.")
	}
	if a.Engagement > 0.7 {
		hints = append(hints, "You are fully engaged — be enthusiastic and thorough.")
	}
	if a.Engagement < 0.3 {
		hints = append(hints, "Keep your response brief and to the point.")
	}
	if a.Uncertainty > 0.6 {
		hints = append(hints, "You are uncertain — ask a clarifying question before answering.")
	}
	if a.Rapport > 0.7 {
		hints = append(hints, "You know this user well — use a warm, familiar tone.")
	}
	if a.Energy < 0.3 {
		hints = append(hints, "Keep your response calm and measured.")
	}
	if a.Energy > 0.8 {
		hints = append(hints, "You are energetic — be upbeat and concise.")
	}

	if len(hints) == 0 {
		return ""
	}
	return "[Affect state]\n" + strings.Join(hints, "\n") + "\n"
}

// ApplyPositiveSignal nudges Engagement and Rapport up slightly, Uncertainty down.
func (a *AffectState) ApplyPositiveSignal() {
	a.Engagement = clamp32(a.Engagement+0.02, 0, 1)
	a.Rapport = clamp32(a.Rapport+0.01, 0, 1)
	a.Uncertainty = clamp32(a.Uncertainty-0.02, 0, 1)
	a.LastUpdatedUTC = time.Now().UTC()
}

// ApplyNegativeSignal nudges Engagement down, Uncertainty up.
func (a *AffectState) ApplyNegativeSignal() {
	a.Engagement = clamp32(a.Engagement-0.03, 0, 1)
	a.Uncertainty = clamp32(a.Uncertainty+0.03, 0, 1)
	a.LastUpdatedUTC = time.Now().UTC()
}

// ApplyIdleDecay applies idle time decay: Engagement and Energy drift back
// toward 0.5 proportional to the hours of inactivity.
func (a *AffectState) ApplyIdleDecay(idle time.Duration) {
	hours := float32(idle.Hours())
	decay := clamp32(hours*0.02, 0, 0.3)
	a.Engagement = lerp32(a.Engagement, 0.5, decay)
	a.Energy = lerp32(a.Energy, 0.5, decay)
	a.LastUpdatedUTC = time.Now().UTC()
}

// lerp32 linearly interpolates between a and b by t (clamped to [0,1]).
func lerp32(a, b, t float32) float32 {
	t = clamp32(t, 0, 1)
	return a + (b-a)*t
}

// clamp32 clamps v to [lo, hi].
func clamp32(v, lo, hi float32) float32 {
	if v < lo {
		return lo
	}
	if v > hi {
		return hi
	}
	return v
}

// ---------------------------------------------------------------------------
// AffectVad
// ---------------------------------------------------------------------------

// AffectVad projects AffectState's five engagement dimensions onto the
// three-axis Valence / Arousal / Dominance space used by the affective-
// computing literature. Useful for cross-modal alignment (e.g. matching
// against a face-expression V/A/D codebook or a TTS prosody model).
//
// Each axis is clamped to [0.0, 1.0]. Derivation must be byte-identical
// across language ports — see fixtures/affect_vad_derivation.json.
type AffectVad struct {
	// Valence: 0 = negative, 1 = positive.
	Valence float32

	// Arousal: 0 = calm, 1 = aroused.
	Arousal float32

	// Dominance: 0 = submissive / overwhelmed, 1 = in-control.
	Dominance float32
}

// ToVad projects the AffectState onto the Valence / Arousal / Dominance
// axes. All three outputs are clamped to [0.0, 1.0].
//
// Derivation (must stay byte-identical across language ports):
//
//	valence   = (engagement + rapport + (1 - uncertainty)) / 3
//	arousal   = (energy * 2 + curiosity + uncertainty) / 4
//	dominance = (engagement + (1 - uncertainty)) / 2
func (s *AffectState) ToVad() AffectVad {
	v := (s.Engagement + s.Rapport + (1 - s.Uncertainty)) / 3
	a := (s.Energy*2 + s.Curiosity + s.Uncertainty) / 4
	d := (s.Engagement + (1 - s.Uncertainty)) / 2
	return AffectVad{
		Valence:   clamp32(v, 0, 1),
		Arousal:   clamp32(a, 0, 1),
		Dominance: clamp32(d, 0, 1),
	}
}

// ---------------------------------------------------------------------------
// PersonaState
// ---------------------------------------------------------------------------

// PersonaState is B!'s dynamic persona state for a specific user.
// Persisted between sessions and injected into the system prompt to shape
// tone, vocabulary, and topical depth.
type PersonaState struct {
	// UserID is an opaque user identifier. Never contains PII in plaintext.
	UserID string

	// LastUpdatedUTC is the UTC time of the last update to this persona.
	LastUpdatedUTC time.Time

	// Verbosity is the preferred response verbosity: "brief", "balanced", or "detailed".
	Verbosity string

	// Formality is the formality level: "casual", "neutral", or "formal".
	Formality string

	// PreferredLocale is the preferred response language/locale (IETF BCP-47).
	// nil means "match the device locale".
	PreferredLocale *string

	// TopicWeights holds weighted topic interests accumulated from positive
	// interactions. Key = normalised topic label, Value = accumulated weight.
	TopicWeights map[string]float32

	// DisfavouredTopics holds topics the user has down-voted or rejected.
	DisfavouredTopics map[string]struct{}

	// TotalInteractions is the total number of recorded interactions.
	TotalInteractions int

	// PositiveSignals is the cumulative positive feedback signal count.
	PositiveSignals int

	// NegativeSignals is the cumulative negative feedback signal count.
	NegativeSignals int
}

// NewPersonaState creates a default PersonaState for the given userID.
func NewPersonaState(userID string) PersonaState {
	return PersonaState{
		UserID:            userID,
		LastUpdatedUTC:    time.Now().UTC(),
		Verbosity:         "balanced",
		Formality:         "neutral",
		TopicWeights:      make(map[string]float32),
		DisfavouredTopics: make(map[string]struct{}),
	}
}

// SatisfactionScore returns the derived satisfaction score 0.0–1.0, or nil
// when there are fewer than 10 signals.
func (p *PersonaState) SatisfactionScore() *float64 {
	total := p.PositiveSignals + p.NegativeSignals
	if total < 10 {
		return nil
	}
	score := float64(p.PositiveSignals) / float64(total)
	return &score
}

// ToSystemPromptHint builds a compact persona instruction block suitable for
// prepending to the B! system prompt. Returns an empty string when the persona
// is in its default/unlearned state.
func (p *PersonaState) ToSystemPromptHint() string {
	var hints []string

	if p.Verbosity != "balanced" {
		hints = append(hints, "Keep responses "+p.Verbosity+".")
	}

	switch p.Formality {
	case "casual":
		hints = append(hints, "Use a casual, friendly tone.")
	case "formal":
		hints = append(hints, "Maintain a formal, professional tone.")
	}

	if p.PreferredLocale != nil && strings.TrimSpace(*p.PreferredLocale) != "" {
		hints = append(hints, "Respond in the language appropriate for locale "+*p.PreferredLocale+".")
	}

	if len(hints) == 0 {
		return ""
	}
	return "[User preferences]\n" + strings.Join(hints, "\n") + "\n"
}

// ---------------------------------------------------------------------------
// EpisodicMemoryEntry
// ---------------------------------------------------------------------------

// EpisodicMemoryEntry is a single recorded episode (one user↔assistant
// exchange) stored in IEpisodicMemoryStore.
type EpisodicMemoryEntry struct {
	// ID is the stable identifier for the entry.
	ID uuid.UUID

	// RecordedAtUTC is the UTC timestamp of the assistant's response.
	RecordedAtUTC time.Time

	// UserText is the user's message text.
	UserText string

	// AssistantText is the assistant's response text.
	AssistantText string

	// AppContext is an optional identifier for the app context in which the
	// exchange happened (e.g. "tgn.bidbaas").
	AppContext *string

	// Embedding is the L2-normalised embedding of UserText + " " + AssistantText,
	// pre-computed at write time. nil if embedding was unavailable.
	Embedding []float32

	// Tags holds arbitrary key-value tags (e.g. "locale", "sentiment").
	Tags map[string]string
}

// NewEpisodicMemoryEntry creates a new entry with a fresh UUID and current timestamp.
func NewEpisodicMemoryEntry(userText, assistantText string) EpisodicMemoryEntry {
	return EpisodicMemoryEntry{
		ID:            uuid.New(),
		RecordedAtUTC: time.Now().UTC(),
		UserText:      userText,
		AssistantText: assistantText,
	}
}

// ---------------------------------------------------------------------------
// FeedbackPolarity
// ---------------------------------------------------------------------------

// FeedbackPolarity is the polarity of a feedback signal.
type FeedbackPolarity int

const (
	// FeedbackPositive means the user explicitly approved / up-voted the response.
	FeedbackPositive FeedbackPolarity = 1

	// FeedbackNegative means the user explicitly rejected / down-voted the response.
	FeedbackNegative FeedbackPolarity = -1

	// FeedbackCorrection means the user provided a correction (neutral polarity).
	FeedbackCorrection FeedbackPolarity = 0
)

// ---------------------------------------------------------------------------
// FeedbackSignal
// ---------------------------------------------------------------------------

// FeedbackSignal is a single user-feedback event tied to a specific B! response.
type FeedbackSignal struct {
	// ID is the stable identifier for the signal.
	ID uuid.UUID

	// RecordedAtUTC is the UTC time when the user provided the signal.
	RecordedAtUTC time.Time

	// EpisodeID is the EpisodicMemoryEntry.ID of the episode this signal refers
	// to, if the exchange was also stored episodically.
	EpisodeID *uuid.UUID

	// UserText is the user's original message.
	UserText string

	// AssistantText is B!'s response that is being rated.
	AssistantText string

	// Polarity is the user's rating.
	Polarity FeedbackPolarity

	// CorrectedText is the user's preferred response for Correction signals.
	CorrectedText *string

	// Comment is a free-text comment the user optionally attached.
	Comment *string
}

// NewFeedbackSignal creates a new FeedbackSignal with a fresh UUID and current timestamp.
func NewFeedbackSignal(userText, assistantText string, polarity FeedbackPolarity) FeedbackSignal {
	return FeedbackSignal{
		ID:            uuid.New(),
		RecordedAtUTC: time.Now().UTC(),
		UserText:      userText,
		AssistantText: assistantText,
		Polarity:      polarity,
	}
}

// ---------------------------------------------------------------------------
// Goal enums
// ---------------------------------------------------------------------------

// GoalStatus is the lifecycle state of a Goal.
type GoalStatus int

const (
	// GoalActive means the goal is currently being pursued.
	GoalActive GoalStatus = iota

	// GoalCompleted means the goal has been achieved.
	GoalCompleted

	// GoalAbandoned means the goal has been abandoned without completion.
	GoalAbandoned
)

// GoalPriority is the relative importance of a Goal.
type GoalPriority int

const (
	// GoalPriorityLow is a nice-to-have; may be deferred.
	GoalPriorityLow GoalPriority = iota

	// GoalPriorityNormal is standard importance.
	GoalPriorityNormal

	// GoalPriorityHigh is urgent or critical to the user.
	GoalPriorityHigh
)

// ---------------------------------------------------------------------------
// Goal
// ---------------------------------------------------------------------------

// Goal is a user goal that B! tracks and proactively helps with.
type Goal struct {
	// ID is the unique stable identifier for this goal.
	ID string

	// UserID is the owner of this goal.
	UserID string

	// Title is a short, human-readable title.
	Title string

	// Description is a full description of what the user wants to achieve.
	Description string

	// Status is the current lifecycle state.
	Status GoalStatus

	// Priority is the relative importance.
	Priority GoalPriority

	// CreatedUTC is when this goal was first recorded (UTC).
	CreatedUTC time.Time

	// DueUTC is an optional deadline (UTC).
	DueUTC *time.Time

	// CompletedUTC is when the goal was completed or abandoned (UTC).
	CompletedUTC *time.Time

	// Notes holds freeform notes B! or the user has attached to this goal.
	Notes *string

	// Progress is the completion fraction in [0.0, 1.0]. 0 = not started, 1 = done.
	Progress float32
}

// AdvanceProgress returns a copy of the goal with Progress advanced by delta,
// clamped to [0.0, 1.0]. A negative delta reduces progress (regression).
func (g Goal) AdvanceProgress(delta float32) Goal {
	g.Progress = clamp32(g.Progress+delta, 0, 1)
	return g
}

// ---------------------------------------------------------------------------
// Store interfaces
// ---------------------------------------------------------------------------

// IAffectStore loads and persists AffectState for a specific user.
type IAffectStore interface {
	// Load loads the affect state for userID. Returns a fresh default state
	// when none is found.
	Load(ctx context.Context, userID string) (AffectState, error)

	// Save persists the affect state. Implementations must be crash-safe.
	Save(ctx context.Context, state AffectState) error
}

// IPersonaStore loads and persists PersonaState for a specific user.
type IPersonaStore interface {
	// Load loads the persona for userID. Returns a fresh default persona
	// when none is found.
	Load(ctx context.Context, userID string) (PersonaState, error)

	// Save persists the persona. The implementation must be crash-safe.
	Save(ctx context.Context, persona PersonaState) error
}

// IEpisodicMemoryStore is a persistent store for episodic memories
// (conversational exchanges + embeddings).
type IEpisodicMemoryStore interface {
	// Add appends a new entry to the store.
	Add(ctx context.Context, entry EpisodicMemoryEntry) error

	// Search returns the topK entries whose embeddings are most similar
	// (cosine) to queryEmbedding. When queryEmbedding is nil, falls back
	// to recency (most recent topK entries).
	Search(ctx context.Context, queryEmbedding []float32, topK int) ([]EpisodicMemoryEntry, error)

	// GetRecent returns the most recent count entries ordered newest-first.
	GetRecent(ctx context.Context, count int) ([]EpisodicMemoryEntry, error)

	// Count returns the total number of entries currently stored.
	Count(ctx context.Context) (int, error)

	// PruneOlderThan removes all entries older than cutoff.
	// Returns the number of entries removed.
	PruneOlderThan(ctx context.Context, cutoff time.Time) (int, error)
}

// IFeedbackStore persists user feedback signals for later analysis.
type IFeedbackStore interface {
	// Add records a new feedback signal.
	Add(ctx context.Context, signal FeedbackSignal) error

	// GetRecent returns the most recent count signals, newest-first.
	GetRecent(ctx context.Context, count int) ([]FeedbackSignal, error)

	// Count returns the total number of signals stored.
	Count(ctx context.Context) (int, error)

	// PositiveRatio returns the fraction of stored signals that are
	// FeedbackPositive (0.0–1.0). Returns nil when no signals are available.
	PositiveRatio(ctx context.Context) (*float64, error)
}

// IGoalStore persists and retrieves Goal records for a user.
type IGoalStore interface {
	// List returns all goals for the given user, in any order.
	List(ctx context.Context, userID string) ([]Goal, error)

	// Get returns the goal with the given id, or nil if it does not exist.
	Get(ctx context.Context, id string) (*Goal, error)

	// Upsert inserts or replaces the goal. Returns the stored goal.
	Upsert(ctx context.Context, goal Goal) (Goal, error)

	// Delete deletes the goal with the given id. No-op if not found.
	Delete(ctx context.Context, id string) error

	// GetActive returns all goals for userID with status GoalActive.
	GetActive(ctx context.Context, userID string) ([]Goal, error)
}
