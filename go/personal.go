// personal.go
//
// Ports CircleAI.Personal:
//   UserConsentToken.cs        -> ConsentScope, UserConsentToken (+ IsValidFor)
//   ConsentGuard.cs            -> ConsentGuardRequire
//   PersonalDomainContext.cs   -> PersonalDomainContext
//   PersonalCompanionAdapter.cs-> PersonalCompanionAdapter
//
// Personal is the daily-life assistant vertical. Every Personal adapter is
// gated on a UserConsentToken signed by the user's UhidKeyRing (signature
// validation lives outside this package). The Calendar/Contacts/Email surfaces
// are represented by the ConsentScope values (CalendarRead/Write, EmailRead/
// Draft, ContactsRead); no separate adapter modules ship in the C# source.

package circleai

import (
	"context"
	"errors"
	"time"

	"github.com/google/uuid"
)

// ---------------------------------------------------------------------------
// ConsentScope
// ---------------------------------------------------------------------------

// ConsentScope is the set of scopes a UserConsentToken may grant. Ports the
// ConsentScope enum (stable ordinals). EmailDraft covers creating drafts;
// sending email is intentionally not exposed.
type ConsentScope int

const (
	// ConsentScopeCalendarRead — read calendar events.
	ConsentScopeCalendarRead ConsentScope = iota
	// ConsentScopeCalendarWrite — create, update, or delete calendar events.
	ConsentScopeCalendarWrite
	// ConsentScopeEmailRead — read inbox messages.
	ConsentScopeEmailRead
	// ConsentScopeEmailDraft — create draft replies. Does NOT grant send.
	ConsentScopeEmailDraft
	// ConsentScopeContactsRead — read the user's contacts.
	ConsentScopeContactsRead
)

// ---------------------------------------------------------------------------
// UserConsentToken
// ---------------------------------------------------------------------------

// UserConsentToken authorises a specific set of ConsentScopes against a Personal
// adapter. Ports the UserConsentToken record. The Signature is preserved
// verbatim for the caller to verify externally.
type UserConsentToken struct {
	// ID is the stable identifier for this token.
	ID uuid.UUID

	// UhidIdentityId is the Uhid identity this token is bound to.
	UhidIdentityId string

	// Scopes are the granted scopes.
	Scopes []ConsentScope

	// GrantedAt is the UTC time the user granted consent.
	GrantedAt time.Time

	// ExpiresAt is the UTC time after which this token is no longer valid.
	ExpiresAt time.Time

	// Signature is the detached signature from the user's UhidKeyRing.
	Signature []byte
}

// IsValidFor reports whether scope is granted and now is before ExpiresAt.
// Ports UserConsentToken.IsValidFor.
func (t UserConsentToken) IsValidFor(scope ConsentScope, now time.Time) bool {
	for _, s := range t.Scopes {
		if s == scope {
			return now.Before(t.ExpiresAt)
		}
	}
	return false
}

// ---------------------------------------------------------------------------
// ConsentGuard
// ---------------------------------------------------------------------------

// ErrConsentDenied is returned by ConsentGuardRequire when a token does not
// grant the requested scope or has expired. Mirrors the C#
// UnauthorizedAccessException thrown by ConsentGuard.Require.
var ErrConsentDenied = errors.New("consent denied")

// ConsentGuardRequire returns a non-nil error when consent does not grant scope
// or has expired (checked against time.Now().UTC()). Ports ConsentGuard.Require;
// the C# void-throws, the Go port returns the condition as an error wrapping
// ErrConsentDenied.
func ConsentGuardRequire(consent UserConsentToken, scope ConsentScope) error {
	if !consent.IsValidFor(scope, time.Now().UTC()) {
		return &consentDeniedError{tokenID: consent.ID, scope: scope}
	}
	return nil
}

type consentDeniedError struct {
	tokenID uuid.UUID
	scope   ConsentScope
}

func (e *consentDeniedError) Error() string {
	return "Consent token " + e.tokenID.String() + " does not grant scope " +
		consentScopeName(e.scope) + " or has expired."
}

func (e *consentDeniedError) Unwrap() error { return ErrConsentDenied }

func consentScopeName(s ConsentScope) string {
	switch s {
	case ConsentScopeCalendarRead:
		return "CalendarRead"
	case ConsentScopeCalendarWrite:
		return "CalendarWrite"
	case ConsentScopeEmailRead:
		return "EmailRead"
	case ConsentScopeEmailDraft:
		return "EmailDraft"
	case ConsentScopeContactsRead:
		return "ContactsRead"
	default:
		return "Unknown"
	}
}

// ---------------------------------------------------------------------------
// PersonalDomainContext
// ---------------------------------------------------------------------------

// PersonalDomainContext holds the static domain framing for the Personal
// vertical. Ports the PersonalDomainContext static class.
var PersonalDomainContext = struct {
	// SystemPromptSnippet is prepended to messages routed through the adapter.
	SystemPromptSnippet string
	// ComplianceFlags lists the compliance regimes this domain observes.
	ComplianceFlags []string
	// SuggestedTools lists the tools this domain typically uses.
	SuggestedTools []string
}{
	SystemPromptSnippet: "[DOMAIN: Personal] You are Circle, a personal life assistant. Help with daily planning, goal setting, decision making, life admin (insurance, subscriptions, tasks), journaling prompts, and personal organisation. Be warm, encouraging, and non-judgmental. Remember context across conversations. Compliance: POPIA.",
	ComplianceFlags:     []string{"POPIA"},
	SuggestedTools:      []string{"calendar", "task_manager", "document_editor", "web_search"},
}

// ---------------------------------------------------------------------------
// PersonalCompanionAdapter
// ---------------------------------------------------------------------------

// PersonalCompanionAdapter decorates an ICompanionSession, injecting the
// Personal domain framing into every message and adding personal-assistant
// helper flows. Ports PersonalCompanionAdapter. It implements ICompanionSession
// so it can be used anywhere a session is expected.
type PersonalCompanionAdapter struct {
	inner ICompanionSession
}

// NewPersonalCompanionAdapter wraps an inner session. inner must not be nil.
func NewPersonalCompanionAdapter(inner ICompanionSession) *PersonalCompanionAdapter {
	if inner == nil {
		panic("inner session is required")
	}
	return &PersonalCompanionAdapter{inner: inner}
}

// SessionID returns the inner session's ID.
func (a *PersonalCompanionAdapter) SessionID() string { return a.inner.SessionID() }

// IdentityID returns the inner session's identity.
func (a *PersonalCompanionAdapter) IdentityID() string { return a.inner.IdentityID() }

// Interface returns the inner session's surface.
func (a *PersonalCompanionAdapter) Interface() InterfaceKind { return a.inner.Interface() }

// History returns the inner session's history.
func (a *PersonalCompanionAdapter) History() []CompanionTurn { return a.inner.History() }

// GetContext returns the inner session's context snapshot.
func (a *PersonalCompanionAdapter) GetContext() CompanionContext { return a.inner.GetContext() }

// RefreshContext refreshes the inner session's context.
func (a *PersonalCompanionAdapter) RefreshContext(ctx context.Context) error {
	return a.inner.RefreshContext(ctx)
}

// SignalFeedback forwards feedback to the inner session.
func (a *PersonalCompanionAdapter) SignalFeedback(ctx context.Context, positive bool, note *string) error {
	return a.inner.SignalFeedback(ctx, positive, note)
}

// ProactiveEvents returns the inner session's proactive-event channel.
func (a *PersonalCompanionAdapter) ProactiveEvents() <-chan CompanionProactiveEvent {
	return a.inner.ProactiveEvents()
}

// Close closes the inner session.
func (a *PersonalCompanionAdapter) Close() error { return a.inner.Close() }

// Send sends a domain-framed message and returns the reply.
func (a *PersonalCompanionAdapter) Send(ctx context.Context, message string) (string, error) {
	return a.inner.Send(ctx, a.frame(message))
}

// Stream streams the reply to a domain-framed message.
func (a *PersonalCompanionAdapter) Stream(ctx context.Context, message string) (<-chan string, <-chan error) {
	return a.inner.Stream(ctx, a.frame(message))
}

// Agent runs the domain-framed message in agentic mode.
func (a *PersonalCompanionAdapter) Agent(ctx context.Context, instruction string) (string, error) {
	return a.inner.Agent(ctx, a.frame(instruction))
}

func (a *PersonalCompanionAdapter) frame(m string) string {
	return PersonalDomainContext.SystemPromptSnippet + "\n\n" + m
}

// SetGoal asks the assistant to turn a goal into a SMART goal with milestones.
func (a *PersonalCompanionAdapter) SetGoal(ctx context.Context, goal string) (string, error) {
	return a.inner.Agent(ctx, "Help me set a SMART goal for: "+goal+". Break it into weekly milestones and suggest how to track progress.")
}

// MakeDecision asks the assistant to weigh options via a pros/cons framework.
func (a *PersonalCompanionAdapter) MakeDecision(ctx context.Context, decision, options string) (string, error) {
	return a.inner.Agent(ctx, "Help me decide: "+decision+". Options: "+options+". Use a pros/cons framework, identify the most important criteria, and give a clear recommendation.")
}

// SetWeeklyIntentions asks the assistant to set weekly intentions.
func (a *PersonalCompanionAdapter) SetWeeklyIntentions(ctx context.Context, longTermGoals, thisWeekContext string) (string, error) {
	return a.inner.Agent(ctx, "Set 3 weekly intentions aligned to: "+longTermGoals+". Context this week: "+thisWeekContext+". Each: outcome + one daily anchor.")
}

// DraftDifficultMessage asks the assistant to draft an NVC-style message.
func (a *PersonalCompanionAdapter) DraftDifficultMessage(ctx context.Context, recipient, topic, outcomeWanted string) (string, error) {
	return a.inner.Agent(ctx, "Draft a difficult message to "+recipient+" about: "+topic+". Outcome: "+outcomeWanted+". NVC-style: observation, feeling, need, request.")
}

// DesignRoutineHabit asks the assistant to design a sustainable habit routine.
func (a *PersonalCompanionAdapter) DesignRoutineHabit(ctx context.Context, habit, currentLifestyle string) (string, error) {
	return a.inner.Agent(ctx, "Design a sustainable routine for habit: "+habit+". Current lifestyle: "+currentLifestyle+". Cue, action, reward, slip recovery.")
}

// ReviewWeek asks the assistant to lead a weekly review.
func (a *PersonalCompanionAdapter) ReviewWeek(ctx context.Context, accomplishments, challenges string) (string, error) {
	return a.inner.Agent(ctx, "Lead a week review. Accomplishments: "+accomplishments+". Challenges: "+challenges+". Surface insight + one experiment for next week.")
}

var _ ICompanionSession = (*PersonalCompanionAdapter)(nil)
