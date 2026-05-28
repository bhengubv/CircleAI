// companion.go
//
// InterfaceKind, CompanionContext, CompanionTurn, CompanionProactiveEvent,
// ICompanionSession, FaceAffectMapper, FaceCompanionBridge.
//
// The Companion is the HER + JARVIS persona — available on every surface,
// with memory and identity that travels with the person.

package circleai

import (
	"context"
	"time"
)

// ---------------------------------------------------------------------------
// InterfaceKind
// ---------------------------------------------------------------------------

// InterfaceKind is the surface on which the Companion session is running.
// Determines sensory capabilities, available UI affordances, and how the
// Companion adapts its communication style.
type InterfaceKind int

const (
	// InterfaceKindMobile is a mobile phone or tablet (MAUI).
	InterfaceKindMobile InterfaceKind = iota

	// InterfaceKindWearable is a smartwatch or fitness band with a small display.
	InterfaceKindWearable

	// InterfaceKindDesktop is a desktop or laptop computer (MAUI or WPF).
	InterfaceKindDesktop

	// InterfaceKindWeb is a browser-based experience (Blazor).
	InterfaceKindWeb

	// InterfaceKindIoT is an embedded IoT device — voice in, voice out, minimal compute.
	InterfaceKindIoT

	// InterfaceKindAmbient is an always-on ambient surface — smart speaker, room display, car.
	InterfaceKindAmbient

	// InterfaceKindHeadless is a programmatic / background / testing context (no UI).
	InterfaceKindHeadless
)

// ---------------------------------------------------------------------------
// CompanionContext
// ---------------------------------------------------------------------------

// CompanionContext is a snapshot of all context injected into the Companion's
// system prompt. Rebuilt at the start of each session and refreshed on request.
type CompanionContext struct {
	// IdentityID is the stable identity driving this session.
	IdentityID string

	// DisplayName is the identity's display name.
	DisplayName string

	// PreferredLanguage is the IETF BCP-47 language preference, or nil.
	PreferredLanguage *string

	// Interface is the surface on which this session is running.
	Interface InterfaceKind

	// PersonaHints is the persona instruction block from PersonaState.ToSystemPromptHint().
	PersonaHints string

	// AffectSummary is the affect instruction block from AffectState.ToSystemPromptHint().
	AffectSummary string

	// RecentMemorySnippets holds short text snippets from recent episodic memory.
	RecentMemorySnippets []string

	// ActiveGoals holds the titles of currently active goals.
	ActiveGoals []string

	// ContextBuiltAt is the UTC time when this context snapshot was built.
	ContextBuiltAt time.Time
}

// ---------------------------------------------------------------------------
// CompanionTurn
// ---------------------------------------------------------------------------

// CompanionTurn is a single turn in the Companion conversation log, held in
// memory for the duration of the session.
type CompanionTurn struct {
	// Role is "user" or "assistant".
	Role string

	// Content is the text of the turn.
	Content string

	// Timestamp is the UTC time of the turn.
	Timestamp time.Time
}

// ---------------------------------------------------------------------------
// CompanionProactiveEvent
// ---------------------------------------------------------------------------

// CompanionProactiveEvent is metadata emitted when the Companion proactively
// initiates contact (e.g. a goal check-in, a mood-triggered nudge, or a
// scheduled reminder).
type CompanionProactiveEvent struct {
	// SessionID is the ID of the session that generated this event.
	SessionID string

	// IdentityID is the identity the event targets.
	IdentityID string

	// Interface is the surface on which the session is running.
	Interface InterfaceKind

	// Message is the proactive message text.
	Message string

	// TriggerName is a short identifier for the rule that fired (e.g. "goal_checkin").
	TriggerName string

	// GeneratedAt is the UTC time when the event was generated.
	GeneratedAt time.Time
}

// ---------------------------------------------------------------------------
// FaceAffectMapper
// ---------------------------------------------------------------------------

// minFaceConfidence is the minimum confidence score required before a facial
// expression is applied to AffectState. Readings below this threshold are
// discarded as unreliable.
const minFaceConfidence = float32(0.5)

// ApplyFaceAffect updates the AffectState based on the expression and confidence
// in the FacialMetricMatrix. Readings with ConfidenceScore < 0.5 are ignored.
// Exact deltas (must match fixtures/facex_biometric_vectors.json):
//
//	Happy:    Engagement += 0.03, Energy     += 0.02
//	Surprised: Curiosity += 0.04
//	Confused: Uncertainty += 0.05
//	Stressed: Uncertainty += 0.08, Energy    -= 0.05
//	Angry:    Engagement  -= 0.04, Rapport   -= 0.02
//	Neutral / Sad / Unknown: no change
func ApplyFaceAffect(m *FacialMetricMatrix, s *AffectState) {
	if m.ConfidenceScore < minFaceConfidence {
		return
	}
	switch m.Expression {
	case FaceExpressionHappy:
		s.Engagement = clamp32(s.Engagement+0.03, 0, 1)
		s.Energy = clamp32(s.Energy+0.02, 0, 1)
	case FaceExpressionSurprised:
		s.Curiosity = clamp32(s.Curiosity+0.04, 0, 1)
	case FaceExpressionConfused:
		s.Uncertainty = clamp32(s.Uncertainty+0.05, 0, 1)
	case FaceExpressionStressed:
		s.Uncertainty = clamp32(s.Uncertainty+0.08, 0, 1)
		s.Energy = clamp32(s.Energy-0.05, 0, 1)
	case FaceExpressionAngry:
		s.Engagement = clamp32(s.Engagement-0.04, 0, 1)
		s.Rapport = clamp32(s.Rapport-0.02, 0, 1)
	}
	s.LastUpdatedUTC = time.Now().UTC()
}

// ---------------------------------------------------------------------------
// FaceCompanionBridge
// ---------------------------------------------------------------------------

// FaceCompanionBridge bridges the face analytics pipeline and the Companion
// companion layer. It observes a FacialMetricMatrix, applies affect mutations
// via ApplyFaceAffect, and optionally emits a CompanionProactiveEvent when
// uncertainty exceeds the confusion threshold.
type FaceCompanionBridge struct {
	// ConfusionThreshold is the uncertainty level above which a proactive
	// "are you confused?" event is emitted. Default: 0.70.
	ConfusionThreshold float32
}

// NewFaceCompanionBridge returns a FaceCompanionBridge with the default
// confusion threshold of 0.70.
func NewFaceCompanionBridge() FaceCompanionBridge {
	return FaceCompanionBridge{ConfusionThreshold: 0.70}
}

// Observe applies the expression in m to affect s and returns a
// CompanionProactiveEvent when post-update uncertainty exceeds
// ConfusionThreshold. Returns nil when no proactive event is warranted.
func (b *FaceCompanionBridge) Observe(
	m *FacialMetricMatrix,
	s *AffectState,
	sessionID, identityID string,
	surface InterfaceKind,
) *CompanionProactiveEvent {
	ApplyFaceAffect(m, s)
	// Both conditions must hold — a high Uncertainty score alone (from
	// prior interactions) does not trigger a face-driven proactive event.
	isConfusedExpr := m.Expression == FaceExpressionConfused || m.Expression == FaceExpressionStressed
	if s.Uncertainty >= b.ConfusionThreshold && isConfusedExpr {
		return &CompanionProactiveEvent{
			SessionID:   sessionID,
			IdentityID:  identityID,
			Interface:   surface,
			Message:     "I notice you might be finding this a bit tricky. Would you like me to slow down or explain it differently?",
			TriggerName: "face.confusion_detected",
			GeneratedAt: time.Now().UTC(),
		}
	}
	return nil
}

// ---------------------------------------------------------------------------
// ICompanionSession
// ---------------------------------------------------------------------------

// ICompanionSession is a Companion conversation session. It combines identity
// awareness, cross-device memory, language adaptation, affect sensing, and
// proactive reasoning into a single coherent interface.
//
// Callers must call Close when finished with the session to release resources.
type ICompanionSession interface {
	// SessionID is the stable unique identifier for this session.
	SessionID() string

	// IdentityID is the authenticated identity driving this session.
	IdentityID() string

	// Interface is the surface on which this session is running.
	Interface() InterfaceKind

	// Send sends a message to the Companion and receives a complete reply.
	// Context enrichment (identity, memory, persona, affect, language) is
	// applied automatically.
	Send(ctx context.Context, message string) (string, error)

	// Stream streams the Companion's reply token-by-token for low-latency
	// rendering. The returned channel is closed when the stream ends.
	// The error channel receives at most one error and is closed after.
	Stream(ctx context.Context, message string) (<-chan string, <-chan error)

	// Agent runs in agentic mode: sends the instruction, detects tool calls
	// in the reply, executes them, and re-prompts until the model produces
	// a plain-text answer.
	Agent(ctx context.Context, instruction string) (string, error)

	// GetContext returns the most recent CompanionContext snapshot.
	GetContext() CompanionContext

	// RefreshContext refreshes the context from backing stores.
	RefreshContext(ctx context.Context) error

	// History returns the in-session conversation history (not persisted).
	History() []CompanionTurn

	// SignalFeedback signals satisfaction with the last reply.
	SignalFeedback(ctx context.Context, positive bool, note *string) error

	// ProactiveEvents returns a channel on which proactive events are delivered.
	// The channel is never nil; it is closed when the session is closed.
	ProactiveEvents() <-chan CompanionProactiveEvent

	// Close releases all resources held by the session.
	Close() error
}
