// security_aethernet_directive_store.go
//
// Ports the CircleAI.Security.AetherNet directive-consumption pipeline:
//
//   MeshDirectiveStore.cs         -> MeshDirectiveStore (implements
//                                    ISecurityDirectiveConsumer; lazy-expiry store)
//   MeshSecurityGate.cs           -> MeshSecurityGate, GateDecision,
//                                    MeshSecurityBlockedError
//   MeshGatedCompanionSession.cs  -> MeshGatedCompanionSession (decorator over
//                                    ICompanionSession that consults the gate)
//
// Flow:
//   AetherNet issues SecurityDirective
//     → MeshDirectiveStore.OnDirective (records + lazily expires)
//     → MeshSecurityGate.Decide ("is this user/node blocked?")
//     → MeshGatedCompanionSession enforces on every message-producing call.

package circleai

import (
	"context"
	"fmt"
	"sync"
	"time"
)

// ─────────────────────────────────────────────────────────────────────────────
// MeshDirectiveStore (MeshDirectiveStore.cs)
// ─────────────────────────────────────────────────────────────────────────────

// MeshDirectiveStore is a thread-safe in-memory registry of security directives
// received from the mesh. It is BOTH the directive sink (implements
// ISecurityDirectiveConsumer) AND the query surface other CircleAI components
// consult before serving a request. Ports MeshDirectiveStore.
//
// Expiry is handled lazily on read — no background timer to leak. Block state
// observes Avoid + Quarantine; Release lifts both.
type MeshDirectiveStore struct {
	mu     sync.Mutex
	byNode map[string][]SecurityDirective
	clock  func() time.Time
}

// NewMeshDirectiveStore constructs a store using time.Now().UTC() as the clock.
// Ports the parameterless MeshDirectiveStore constructor.
func NewMeshDirectiveStore() *MeshDirectiveStore {
	return NewMeshDirectiveStoreWithClock(func() time.Time { return time.Now().UTC() })
}

// NewMeshDirectiveStoreWithClock constructs a store with an explicit clock for
// testing. Ports the MeshDirectiveStore(Func<DateTimeOffset>) constructor.
// Panics if clock is nil (mirrors ArgumentNullException).
func NewMeshDirectiveStoreWithClock(clock func() time.Time) *MeshDirectiveStore {
	if clock == nil {
		panic("clock must not be nil")
	}
	return &MeshDirectiveStore{
		byNode: make(map[string][]SecurityDirective),
		clock:  clock,
	}
}

// OnDirective records (or, for a Release, removes) a directive. Ignores
// directives with no target. Ports MeshDirectiveStore.OnDirective.
func (s *MeshDirectiveStore) OnDirective(directive SecurityDirective) {
	if !directive.HasTarget() {
		return
	}
	nodeID := *directive.TargetNodeID

	s.mu.Lock()
	defer s.mu.Unlock()

	if directive.Kind == SecurityDirectiveKindReleaseNode {
		// Release lifts every Avoid/Quarantine for the node.
		delete(s.byNode, nodeID)
		return
	}
	s.byNode[nodeID] = append(s.byNode[nodeID], directive)
}

// IsBlocked returns true when an unexpired Avoid or Quarantine directive is
// active for the node, with the most recent block's reason text. Expired entries
// are swept during the walk. Ports MeshDirectiveStore.IsBlocked.
func (s *MeshDirectiveStore) IsBlocked(nodeID string) (blocked bool, reason string) {
	if isBlankAether(nodeID) {
		return false, ""
	}
	s.mu.Lock()
	defer s.mu.Unlock()

	list, ok := s.byNode[nodeID]
	if !ok {
		return false, ""
	}
	now := s.clock()

	var latestBlock *SecurityDirective
	kept := list[:0] // reuse backing array; we rebuild the survivors in place
	for i := range list {
		d := list[i]
		if meshDirectiveExpired(d, now) {
			continue
		}
		kept = append(kept, d)
	}
	// kept now holds only unexpired directives. Find the newest block among them.
	for i := range kept {
		d := kept[i]
		if meshDirectiveIsBlockKind(d.Kind) && (latestBlock == nil || d.IssuedAt.After(latestBlock.IssuedAt)) {
			latestBlock = &kept[i]
		}
	}
	if len(kept) == 0 {
		delete(s.byNode, nodeID)
	} else {
		s.byNode[nodeID] = kept
	}

	if latestBlock == nil {
		return false, ""
	}
	return true, latestBlock.Reason
}

// GetActiveDirectives lists every unexpired directive for the node — useful for
// audit/diagnostics. Ports MeshDirectiveStore.GetActiveDirectives.
func (s *MeshDirectiveStore) GetActiveDirectives(nodeID string) []SecurityDirective {
	if isBlankAether(nodeID) {
		return []SecurityDirective{}
	}
	s.mu.Lock()
	defer s.mu.Unlock()

	list, ok := s.byNode[nodeID]
	if !ok {
		return []SecurityDirective{}
	}
	now := s.clock()
	out := make([]SecurityDirective, 0, len(list))
	for _, d := range list {
		if !meshDirectiveExpired(d, now) {
			out = append(out, d)
		}
	}
	return out
}

// TrackedNodeCount returns the number of nodes with at least one tracked
// directive. Ports MeshDirectiveStore.TrackedNodeCount.
func (s *MeshDirectiveStore) TrackedNodeCount() int {
	s.mu.Lock()
	defer s.mu.Unlock()
	return len(s.byNode)
}

func meshDirectiveIsBlockKind(k SecurityDirectiveKind) bool {
	return k == SecurityDirectiveKindAvoidNode || k == SecurityDirectiveKindQuarantineNode
}

func meshDirectiveExpired(d SecurityDirective, now time.Time) bool {
	if d.Duration == nil {
		return false
	}
	return !d.IssuedAt.Add(*d.Duration).After(now) // (IssuedAt + Duration) <= now
}

var _ ISecurityDirectiveConsumer = (*MeshDirectiveStore)(nil)

// ─────────────────────────────────────────────────────────────────────────────
// MeshSecurityGate (MeshSecurityGate.cs)
// ─────────────────────────────────────────────────────────────────────────────

// GateDecision is the decision returned from MeshSecurityGate.Decide. Ports the
// nested GateDecision readonly record struct.
type GateDecision struct {
	IsBlocked bool
	Reason    string
}

// GateDecisionAllowed is the "allow with no reason" decision. Ports
// GateDecision.Allowed.
var GateDecisionAllowed = GateDecision{IsBlocked: false, Reason: ""}

// MeshSecurityGate is the read-only fast-path query surface over a
// MeshDirectiveStore: "is this user/node currently blocked by the mesh?".
// Separating the gate from the store lets consumers depend on the query view
// without the write surface. Ports MeshSecurityGate.
type MeshSecurityGate struct {
	store *MeshDirectiveStore
}

// NewMeshSecurityGate constructs a gate over a store. Panics if store is nil
// (mirrors ArgumentNullException).
func NewMeshSecurityGate(store *MeshDirectiveStore) *MeshSecurityGate {
	if store == nil {
		panic("store must not be nil")
	}
	return &MeshSecurityGate{store: store}
}

// Decide returns a single-shot decision for the given user/node id. The reason
// text comes from the most recent active block directive. Ports
// MeshSecurityGate.Decide.
func (g *MeshSecurityGate) Decide(userOrNodeID string) GateDecision {
	if isBlankAether(userOrNodeID) {
		return GateDecisionAllowed
	}
	if blocked, reason := g.store.IsBlocked(userOrNodeID); blocked {
		return GateDecision{IsBlocked: true, Reason: reason}
	}
	return GateDecisionAllowed
}

// Enforce returns a MeshSecurityBlockedError when a request from a blocked id
// would proceed, else nil. The C# version throws; Go idiom returns an error so
// callers branch explicitly. Ports MeshSecurityGate.Enforce.
func (g *MeshSecurityGate) Enforce(userOrNodeID string) error {
	decision := g.Decide(userOrNodeID)
	if decision.IsBlocked {
		return &MeshSecurityBlockedError{BlockedID: userOrNodeID, Reason: decision.Reason}
	}
	return nil
}

// MeshSecurityBlockedError is returned by MeshSecurityGate.Enforce (and the
// gated session) when the mesh has issued a block directive against the
// requesting id. Ports MeshSecurityBlockedException.
type MeshSecurityBlockedError struct {
	// BlockedID is the id the mesh has blocked.
	BlockedID string
	// Reason is the block reason text.
	Reason string
}

// Error implements the error interface with the same message shape as the C#
// exception.
func (e *MeshSecurityBlockedError) Error() string {
	return fmt.Sprintf("Mesh has blocked '%s': %s", e.BlockedID, e.Reason)
}

// ─────────────────────────────────────────────────────────────────────────────
// MeshGatedCompanionSession (MeshGatedCompanionSession.cs)
// ─────────────────────────────────────────────────────────────────────────────

// MeshGatedCompanionSession wraps an inner ICompanionSession and enforces the
// mesh's "block this user" directives via MeshSecurityGate on every
// message-producing call (Send, Stream, Agent). Unguarded calls (context /
// history / feedback) pass straight through — gating them would punish beyond
// the "stop the chat" intent. Ports MeshGatedCompanionSession.
type MeshGatedCompanionSession struct {
	inner ICompanionSession
	gate  *MeshSecurityGate
}

// NewMeshGatedCompanionSession constructs the decorator. Panics if inner or gate
// is nil (mirrors ArgumentNullException).
func NewMeshGatedCompanionSession(inner ICompanionSession, gate *MeshSecurityGate) *MeshGatedCompanionSession {
	if inner == nil {
		panic("inner must not be nil")
	}
	if gate == nil {
		panic("gate must not be nil")
	}
	return &MeshGatedCompanionSession{inner: inner, gate: gate}
}

// ── Pass-through identity / properties ──────────────────────────────────────

// SessionID passes through to the inner session.
func (s *MeshGatedCompanionSession) SessionID() string { return s.inner.SessionID() }

// IdentityID passes through to the inner session.
func (s *MeshGatedCompanionSession) IdentityID() string { return s.inner.IdentityID() }

// Interface passes through to the inner session.
func (s *MeshGatedCompanionSession) Interface() InterfaceKind { return s.inner.Interface() }

// History passes through to the inner session.
func (s *MeshGatedCompanionSession) History() []CompanionTurn { return s.inner.History() }

// ProactiveEvents passes through to the inner session.
func (s *MeshGatedCompanionSession) ProactiveEvents() <-chan CompanionProactiveEvent {
	return s.inner.ProactiveEvents()
}

// ── Guarded entry points ────────────────────────────────────────────────────

// Send enforces the gate against the session's IdentityID before delegating.
// Returns a MeshSecurityBlockedError instead of reaching the generator when the
// identity is blocked. Ports SendAsync.
func (s *MeshGatedCompanionSession) Send(ctx context.Context, message string) (string, error) {
	if err := s.gate.Enforce(s.IdentityID()); err != nil {
		return "", err
	}
	return s.inner.Send(ctx, message)
}

// Stream enforces the gate before delegating. When the identity is blocked, it
// returns closed channels carrying a single MeshSecurityBlockedError on the
// error channel — the Go analogue of the C# decorator throwing before the first
// yield. Ports StreamAsync.
func (s *MeshGatedCompanionSession) Stream(ctx context.Context, message string) (<-chan string, <-chan error) {
	if err := s.gate.Enforce(s.IdentityID()); err != nil {
		tokens := make(chan string)
		errs := make(chan error, 1)
		close(tokens)
		errs <- err
		close(errs)
		return tokens, errs
	}
	return s.inner.Stream(ctx, message)
}

// Agent enforces the gate before delegating. Ports AgentAsync.
func (s *MeshGatedCompanionSession) Agent(ctx context.Context, instruction string) (string, error) {
	if err := s.gate.Enforce(s.IdentityID()); err != nil {
		return "", err
	}
	return s.inner.Agent(ctx, instruction)
}

// ── Unguarded pass-through ──────────────────────────────────────────────────

// GetContext passes through — diagnostic/metadata call, never gated.
func (s *MeshGatedCompanionSession) GetContext() CompanionContext { return s.inner.GetContext() }

// RefreshContext passes through — diagnostic/metadata call, never gated.
func (s *MeshGatedCompanionSession) RefreshContext(ctx context.Context) error {
	return s.inner.RefreshContext(ctx)
}

// SignalFeedback passes through — metadata call, never gated.
func (s *MeshGatedCompanionSession) SignalFeedback(ctx context.Context, positive bool, note *string) error {
	return s.inner.SignalFeedback(ctx, positive, note)
}

// Close passes through to the inner session (mirrors DisposeAsync delegation).
func (s *MeshGatedCompanionSession) Close() error { return s.inner.Close() }

var _ ICompanionSession = (*MeshGatedCompanionSession)(nil)
