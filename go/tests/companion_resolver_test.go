// companion_resolver_test.go
//
// Verifies InMemoryCompanionSessionResolver + the companion-turn endpoint
// (ported from InMemoryCompanionSessionResolver.cs / CompanionEndpoint.cs):
//   - Resolve caches one session per (sessionId, identityId) and single-flights
//     construction (factory runs once per key).
//   - Blank ids resolve to (nil, nil).
//   - A failed construction does not poison the cache (next call retries).
//   - HandleCompanionTurn: non-stream reply, streaming deltas, auth + missing-
//     field + session-not-found paths.

package circleai_test

import (
	"context"
	"errors"
	"sync"
	"testing"

	circleai "github.com/bhengubv/CircleAI/go"
)

// ── fakes ─────────────────────────────────────────────────────────────────────

// fakeSession implements circleai.ICompanionSession for endpoint/resolver tests.
type fakeSession struct {
	id       string
	identity string
	turns    int
}

func (s *fakeSession) SessionID() string                { return s.id }
func (s *fakeSession) IdentityID() string               { return s.identity }
func (s *fakeSession) Interface() circleai.InterfaceKind { return circleai.InterfaceKindWeb }
func (s *fakeSession) Send(_ context.Context, message string) (string, error) {
	s.turns++
	return "reply:" + message, nil
}
func (s *fakeSession) Stream(_ context.Context, message string) (<-chan string, <-chan error) {
	out := make(chan string, 2)
	errc := make(chan error, 1)
	out <- "chunk-a "
	out <- "chunk-b"
	close(out)
	close(errc)
	return out, errc
}
func (s *fakeSession) Agent(_ context.Context, instruction string) (string, error) {
	return "agent:" + instruction, nil
}
func (s *fakeSession) GetContext() circleai.CompanionContext     { return circleai.CompanionContext{} }
func (s *fakeSession) RefreshContext(context.Context) error      { return nil }
func (s *fakeSession) History() []circleai.CompanionTurn         { return make([]circleai.CompanionTurn, s.turns) }
func (s *fakeSession) SignalFeedback(context.Context, bool, *string) error { return nil }
func (s *fakeSession) ProactiveEvents() <-chan circleai.CompanionProactiveEvent {
	ch := make(chan circleai.CompanionProactiveEvent)
	close(ch)
	return ch
}
func (s *fakeSession) Close() error { return nil }

// countingFactory records how many times Create runs and can fail on demand.
type countingFactory struct {
	mu      sync.Mutex
	calls   int
	failNext bool
}

func (f *countingFactory) Create(_ context.Context, identityID string, iface circleai.InterfaceKind) (circleai.ICompanionSession, error) {
	f.mu.Lock()
	f.calls++
	fail := f.failNext
	f.failNext = false
	f.mu.Unlock()
	if fail {
		return nil, errors.New("construction failed")
	}
	return &fakeSession{id: "s", identity: identityID}, nil
}

func (f *countingFactory) callCount() int {
	f.mu.Lock()
	defer f.mu.Unlock()
	return f.calls
}

// ── resolver ──────────────────────────────────────────────────────────────────

func TestCompanionResolver_CachesAndSingleFlights(t *testing.T) {
	ctx := context.Background()
	factory := &countingFactory{}
	resolver, err := circleai.NewInMemoryCompanionSessionResolver(factory, circleai.InterfaceKindWeb)
	if err != nil {
		t.Fatalf("ctor: %v", err)
	}

	s1, err := resolver.Resolve(ctx, "sess1", "id1")
	if err != nil || s1 == nil {
		t.Fatalf("resolve 1: s=%v err=%v", s1, err)
	}
	s2, _ := resolver.Resolve(ctx, "sess1", "id1")
	if s1 != s2 {
		t.Error("same key should return the cached session instance")
	}
	if factory.callCount() != 1 {
		t.Errorf("factory should run once per key, ran %d", factory.callCount())
	}
	if resolver.CachedSessionCount() != 1 {
		t.Errorf("cache count: got %d", resolver.CachedSessionCount())
	}

	// Different key → new construction.
	if _, err := resolver.Resolve(ctx, "sess2", "id1"); err != nil {
		t.Fatalf("resolve 2: %v", err)
	}
	if factory.callCount() != 2 {
		t.Errorf("distinct key should construct again, calls=%d", factory.callCount())
	}
}

func TestCompanionResolver_BlankIdsAndPoisonFree(t *testing.T) {
	ctx := context.Background()
	factory := &countingFactory{}
	resolver, _ := circleai.NewInMemoryCompanionSessionResolver(factory, circleai.InterfaceKindWeb)

	// Blank ids → (nil, nil), no construction.
	if s, err := resolver.Resolve(ctx, "", "id"); s != nil || err != nil {
		t.Errorf("blank session id should be (nil,nil), got (%v,%v)", s, err)
	}
	if s, err := resolver.Resolve(ctx, "s", ""); s != nil || err != nil {
		t.Errorf("blank identity id should be (nil,nil), got (%v,%v)", s, err)
	}

	// Failed construction must not poison the cache.
	factory.failNext = true
	if _, err := resolver.Resolve(ctx, "k", "id"); err == nil {
		t.Fatal("expected construction failure")
	}
	if resolver.CachedSessionCount() != 0 {
		t.Errorf("failed construction should not stay cached, count=%d", resolver.CachedSessionCount())
	}
	// Retry succeeds.
	if s, err := resolver.Resolve(ctx, "k", "id"); s == nil || err != nil {
		t.Errorf("retry after failure should succeed, got (%v,%v)", s, err)
	}

	// Nil factory → error.
	if _, err := circleai.NewInMemoryCompanionSessionResolver(nil, circleai.InterfaceKindWeb); err == nil {
		t.Error("nil factory should error")
	}
}

// ── companion endpoint ────────────────────────────────────────────────────────

func companionServer(t *testing.T, resolver circleai.ICompanionSessionResolver) *circleai.InferenceServerHandlers {
	t.Helper()
	counters := circleai.NewServerCounters()
	return circleai.NewInferenceServerHandlers(circleai.InferenceServerHandlers{
		Registry:  circleai.NewInferenceServerModelRegistry(),
		Admission: circleai.NewAdmissionControl(4, counters),
		Counters:  counters,
		Resolver:  resolver,
	})
}

func TestHandleCompanionTurn(t *testing.T) {
	ctx := context.Background()
	resolver, _ := circleai.NewInMemoryCompanionSessionResolver(&countingFactory{}, circleai.InterfaceKindWeb)
	h := companionServer(t, resolver)

	// Non-stream reply.
	res := h.HandleCompanionTurn(ctx, authOK(), circleai.CompanionTurnRequest{
		SessionID: "s1", IdentityID: "id1", Message: "hi there",
	})
	if res.StatusCode != 200 {
		t.Fatalf("turn status: %d body=%+v", res.StatusCode, res.Body)
	}
	resp := res.Body.(circleai.CompanionTurnResponse)
	if resp.Reply != "reply:hi there" {
		t.Errorf("reply: got %q", resp.Reply)
	}

	// Streaming.
	streamRes := h.HandleCompanionTurn(ctx, authOK(), circleai.CompanionTurnRequest{
		SessionID: "s1", IdentityID: "id1", Message: "go", Stream: true,
	})
	if !streamRes.DoneTerminator || len(streamRes.StreamFrames) != 2 {
		t.Errorf("stream should emit 2 delta frames + terminator, got %d frames done=%v",
			len(streamRes.StreamFrames), streamRes.DoneTerminator)
	}

	// Missing fields → 400.
	if r := h.HandleCompanionTurn(ctx, authOK(), circleai.CompanionTurnRequest{SessionID: "s"}); r.StatusCode != 400 {
		t.Errorf("missing fields should be 400, got %d", r.StatusCode)
	}
	// Unauthorized → 401.
	if r := h.HandleCompanionTurn(ctx, circleai.AuthResult{Outcome: circleai.AuthNoResult}, circleai.CompanionTurnRequest{SessionID: "s", IdentityID: "i", Message: "m"}); r.StatusCode != 401 {
		t.Errorf("unauthorized should be 401, got %d", r.StatusCode)
	}
}

func TestHandleCompanionTurn_SessionNotFound(t *testing.T) {
	// A resolver that returns (nil, nil) → 404 session_not_found.
	h := companionServer(t, nilResolver{})
	res := h.HandleCompanionTurn(context.Background(), authOK(), circleai.CompanionTurnRequest{
		SessionID: "s", IdentityID: "i", Message: "m",
	})
	if res.StatusCode != 404 {
		t.Errorf("nil session should be 404, got %d", res.StatusCode)
	}
}

type nilResolver struct{}

func (nilResolver) Resolve(context.Context, string, string) (circleai.ICompanionSession, error) {
	return nil, nil
}
