// companion_session_factory.go
//
// Ported from CircleAI.Companion (CompanionSessionFactory.cs) — the C# reference.
// Creates per-identity, per-surface Companion sessions, resolving the display
// name + preferred language from an optional identity provider (the C# factory
// pulls these from IIdentityProvider.GetCurrentIdentityAsync).
//
//   - ICompanionSessionFactory   (contract)
//   - CompanionSessionFactory    (default impl)
//
// The C# factory resolves every backing service from an IServiceProvider DI
// container. Go has no ambient container, so the collaborators the session needs
// are held on the factory (set once at construction) — the idiomatic equivalent
// of "resolve all available backing services from DI". Per-call, the factory
// still fills identity-derived fields exactly as C# does.

package circleai

import (
	"context"
	"errors"

	"github.com/google/uuid"
)

// newCompanionSessionID mints a fresh session id (dashless UUID, matching the
// C# Guid "n" style used elsewhere in this tree).
func newCompanionSessionID() string {
	return removeDashes(uuid.New().String())
}

// removeDashes strips '-' from a UUID string.
func removeDashes(s string) string {
	b := make([]byte, 0, len(s))
	for i := 0; i < len(s); i++ {
		if s[i] != '-' {
			b = append(b, s[i])
		}
	}
	return string(b)
}

// ICompanionSessionFactory creates per-identity, per-surface Companion sessions.
// Ported from the C# ICompanionSessionFactory.
type ICompanionSessionFactory interface {
	// Create builds a new session for identityID on the given interface surface,
	// resolving available backing collaborators.
	Create(ctx context.Context, identityID string, iface InterfaceKind) (ICompanionSession, error)
}

// CompanionSessionFactoryDeps holds the collaborators the factory injects into
// every session it creates. This stands in for the C# IServiceProvider resolves:
// generator, episodic, and recall are required (a session cannot be built
// without them); the rest are optional and default to nil.
type CompanionSessionFactoryDeps struct {
	// Generator is the chat generator (required).
	Generator IChatGenerator
	// Episodic is the episodic memory store (required).
	Episodic IEpisodicMemoryStore
	// Recall is the fused-recall service (required).
	Recall IRecall
	// Encoder is the background graph/belief encoder (optional).
	Encoder *CompanionMemoryEncoder
	// Beliefs holds the user's own facts (optional).
	Beliefs *SelfBeliefStore
	// Embedder computes query embeddings for associative recall (optional).
	Embedder EmbedderFunc
	// PersonaHints is a static persona hint block (optional).
	PersonaHints string
	// AffectSummary is a static affect hint block (optional).
	AffectSummary string
	// ActiveGoals seeds the session's active-goal titles (optional).
	ActiveGoals []string
	// RecallTopK is how many memories to recall per turn (0 → default 5).
	RecallTopK int
	// AppContext is stamped onto persisted episodes (optional).
	AppContext *string
}

// CompanionSessionFactory is the default ICompanionSessionFactory. Ported from
// the C# CompanionSessionFactory.
type CompanionSessionFactory struct {
	deps     CompanionSessionFactoryDeps
	identity IIdentityProvider // optional; nil → identityID used as display name.
	// newSessionID mints a session id per Create call. Overridable in tests.
	newSessionID func() string
}

// NewCompanionSessionFactory builds a factory. generator/episodic/recall in deps
// are required. identity may be nil.
func NewCompanionSessionFactory(deps CompanionSessionFactoryDeps, identity IIdentityProvider) (*CompanionSessionFactory, error) {
	if deps.Generator == nil {
		return nil, errors.New("generator required")
	}
	if deps.Episodic == nil {
		return nil, errors.New("episodic required")
	}
	if deps.Recall == nil {
		return nil, errors.New("recall required")
	}
	return &CompanionSessionFactory{
		deps:         deps,
		identity:     identity,
		newSessionID: newCompanionSessionID,
	}, nil
}

// Create builds a session for identityID. It resolves the display name and
// preferred language from the identity provider when one is available, exactly
// like the C# CreateAsync.
func (f *CompanionSessionFactory) Create(ctx context.Context, identityID string, iface InterfaceKind) (ICompanionSession, error) {
	if identityID == "" {
		return nil, errors.New("identityId required")
	}

	displayName := identityID
	var preferredLang *string

	if f.identity != nil {
		resolved, err := f.identity.GetCurrentIdentity(ctx)
		if err != nil {
			return nil, err
		}
		if resolved != nil {
			displayName = resolved.DisplayName
			preferredLang = resolved.PreferredLanguage
		}
	}

	return NewCompanionSession(f.deps.Generator, f.deps.Episodic, f.deps.Recall, CompanionSessionOptions{
		SessionID:         f.newSessionID(),
		IdentityID:        identityID,
		Interface:         iface,
		DisplayName:       displayName,
		PreferredLanguage: preferredLang,
		PersonaHints:      f.deps.PersonaHints,
		AffectSummary:     f.deps.AffectSummary,
		ActiveGoals:       f.deps.ActiveGoals,
		RecallTopK:        f.deps.RecallTopK,
		AppContext:        f.deps.AppContext,
		Encoder:           f.deps.Encoder,
		Beliefs:           f.deps.Beliefs,
		Embedder:          f.deps.Embedder,
	})
}

var _ ICompanionSessionFactory = (*CompanionSessionFactory)(nil)
