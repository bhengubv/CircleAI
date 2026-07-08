// server_companion_resolver.go
//
// Ports CircleAI.Inference.Server.Endpoints.ICompanionSessionResolver
// (CompanionEndpoint.cs) and
// CircleAI.Inference.Server.Hosting.InMemoryCompanionSessionResolver
// (InMemoryCompanionSessionResolver.cs).
//
// The /v1/companion/turn endpoint resolves an ICompanionSession from this
// resolver. The in-memory default caches one session per (sessionId, identityId)
// pair and constructs misses via ICompanionSessionFactory. Construction is
// single-flighted per key so the factory runs at most once per tuple even under
// concurrent resolution, and a failed construction does not poison the cache.

package circleai

import (
	"context"
	"strings"
	"sync"
)

// ICompanionSessionResolver resolves an ICompanionSession for a (sessionId,
// identityId) pair. Ports CircleAI.Inference.Server.Endpoints.ICompanionSessionResolver.
type ICompanionSessionResolver interface {
	Resolve(ctx context.Context, sessionID, identityID string) (ICompanionSession, error)
}

// companionSessionSlot is a single-flight cache slot: the first resolver runs
// the factory; concurrent resolvers wait on ready and share the result.
type companionSessionSlot struct {
	once    sync.Once
	ready   chan struct{}
	session ICompanionSession
	err     error
}

// InMemoryCompanionSessionResolver caches one ICompanionSession per (sessionId,
// identityId) pair and constructs misses via ICompanionSessionFactory. Ports
// CircleAI.Inference.Server.Hosting.InMemoryCompanionSessionResolver.
type InMemoryCompanionSessionResolver struct {
	factory          ICompanionSessionFactory
	defaultInterface InterfaceKind

	mu       sync.Mutex
	sessions map[string]*companionSessionSlot
}

// NewInMemoryCompanionSessionResolver builds the resolver. defaultInterface is
// stamped onto sessions created via this resolver (C# defaults to Web because
// the HTTP-fronted server is the canonical entry point).
func NewInMemoryCompanionSessionResolver(factory ICompanionSessionFactory, defaultInterface InterfaceKind) (*InMemoryCompanionSessionResolver, error) {
	if factory == nil {
		return nil, errNilCompanionFactory
	}
	return &InMemoryCompanionSessionResolver{
		factory:          factory,
		defaultInterface: defaultInterface,
		sessions:         make(map[string]*companionSessionSlot),
	}, nil
}

var errNilCompanionFactory = &companionResolverError{"factory is required"}

type companionResolverError struct{ msg string }

func (e *companionResolverError) Error() string { return e.msg }

// Resolve returns the cached-or-constructed session for (sessionId, identityId).
// Returns (nil, nil) when either id is blank — mirroring the C# early return.
// Ports ResolveAsync (single-flight + poison-free failure handling).
func (r *InMemoryCompanionSessionResolver) Resolve(ctx context.Context, sessionID, identityID string) (ICompanionSession, error) {
	if strings.TrimSpace(sessionID) == "" || strings.TrimSpace(identityID) == "" {
		return nil, nil
	}
	key := sessionID + "\x00" + identityID

	r.mu.Lock()
	slot, ok := r.sessions[key]
	if !ok {
		slot = &companionSessionSlot{ready: make(chan struct{})}
		r.sessions[key] = slot
	}
	r.mu.Unlock()

	slot.once.Do(func() {
		// Construction uses a background context (not the caller's) so a caller
		// cancelling mid-construction doesn't cancel the shared build.
		slot.session, slot.err = r.factory.Create(context.Background(), identityID, r.defaultInterface)
		close(slot.ready)
		if slot.err != nil {
			// A failed construction must not poison the cache — drop the slot.
			r.mu.Lock()
			if r.sessions[key] == slot {
				delete(r.sessions, key)
			}
			r.mu.Unlock()
		}
	})

	select {
	case <-slot.ready:
	case <-ctx.Done():
		return nil, ctx.Err()
	}
	if slot.err != nil {
		return nil, slot.err
	}
	if err := ctx.Err(); err != nil {
		return nil, err
	}
	return slot.session, nil
}

// CachedSessionCount reports the number of currently cached sessions. Diagnostics only.
func (r *InMemoryCompanionSessionResolver) CachedSessionCount() int {
	r.mu.Lock()
	defer r.mu.Unlock()
	return len(r.sessions)
}

var _ ICompanionSessionResolver = (*InMemoryCompanionSessionResolver)(nil)
