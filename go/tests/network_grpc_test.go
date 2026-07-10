// network_grpc_test.go
//
// Verifies network_grpc.go:
//   - GrpcChannelState ordinals + String
//   - GrpcRetryPolicy predicates (IsRetryable, BackoffFor cap) + well-known
//     policies (Default/Aggressive/NoRetry)
//   - InMemoryGrpcCallMetrics: register/get channel, state default+set,
//     LogCall id sequencing + RecentCalls ordering
//   - GrpcNetworkTransport: lifecycle + channel-state transitions,
//     same-Target fan-out (loopback + cross-target excluded),
//     buffered-before-subscribe, MaxSendBytes over-budget failure + call log

package circleai_test

import (
	"context"
	"testing"
	"time"

	circleai "github.com/bhengubv/CircleAI/go"
)

func TestGrpcChannelState_Ordinals(t *testing.T) {
	cases := []struct {
		s    circleai.GrpcChannelState
		ord  int
		name string
	}{
		{circleai.GrpcChannelStateIdle, 0, "Idle"},
		{circleai.GrpcChannelStateConnecting, 1, "Connecting"},
		{circleai.GrpcChannelStateReady, 2, "Ready"},
		{circleai.GrpcChannelStateTransientFailure, 3, "TransientFailure"},
		{circleai.GrpcChannelStateShutdown, 4, "Shutdown"},
	}
	for _, c := range cases {
		if int(c.s) != c.ord {
			t.Errorf("%s ordinal = %d want %d", c.name, int(c.s), c.ord)
		}
		if c.s.String() != c.name {
			t.Errorf("String = %q want %q", c.s.String(), c.name)
		}
	}
}

func TestGrpcRetryPolicies_Values(t *testing.T) {
	def := circleai.GrpcRetryPolicyDefault()
	if def.MaxAttempts != 3 || def.InitialBackoff != 100*time.Millisecond || def.MaxBackoff != 2*time.Second || def.Multiplier != 2.0 {
		t.Errorf("Default = %+v", def)
	}
	if !def.IsRetryable("UNAVAILABLE") || !def.IsRetryable("DEADLINE_EXCEEDED") || def.IsRetryable("RESOURCE_EXHAUSTED") {
		t.Error("Default retryable set wrong")
	}
	agg := circleai.GrpcRetryPolicyAggressive()
	if agg.MaxAttempts != 6 || !agg.IsRetryable("RESOURCE_EXHAUSTED") {
		t.Errorf("Aggressive = %+v", agg)
	}
	no := circleai.GrpcRetryPolicyNoRetry()
	if no.MaxAttempts != 1 || len(no.RetryableStatusCodes) != 0 {
		t.Errorf("NoRetry = %+v", no)
	}
}

func TestGrpcRetryPolicy_BackoffFor(t *testing.T) {
	p := circleai.GrpcRetryPolicyDefault() // 100ms, x2, cap 2s
	if got := p.BackoffFor(1); got != 100*time.Millisecond {
		t.Errorf("attempt 1 backoff = %v want 100ms", got)
	}
	if got := p.BackoffFor(2); got != 200*time.Millisecond {
		t.Errorf("attempt 2 backoff = %v want 200ms", got)
	}
	if got := p.BackoffFor(3); got != 400*time.Millisecond {
		t.Errorf("attempt 3 backoff = %v want 400ms", got)
	}
	// Deep attempts cap at MaxBackoff.
	if got := p.BackoffFor(20); got != 2*time.Second {
		t.Errorf("attempt 20 backoff = %v want cap 2s", got)
	}
}

func TestGrpcCallMetrics_LogAndOrder(t *testing.T) {
	m := circleai.NewInMemoryGrpcCallMetrics()
	desc := circleai.GrpcChannelDescriptor{Target: "svc:443", UseTls: true, MaxSendBytes: 1024}
	m.RegisterChannel("c1", desc)
	if got, ok := m.GetChannel("c1"); !ok || got.Target != "svc:443" {
		t.Errorf("GetChannel = %+v ok=%v", got, ok)
	}
	if s := m.State("c1"); s != circleai.GrpcChannelStateIdle {
		t.Errorf("default state = %v want Idle", s)
	}
	m.SetState("c1", circleai.GrpcChannelStateReady)
	if s := m.State("c1"); s != circleai.GrpcChannelStateReady {
		t.Errorf("state after set = %v", s)
	}

	now := time.Now().UTC()
	id1 := m.LogCall(circleai.GrpcCallSummary{Method: "A", Attempts: 1, StatusCode: "OK", AtUtc: now.Add(1 * time.Second)})
	id2 := m.LogCall(circleai.GrpcCallSummary{Method: "B", Attempts: 1, StatusCode: "OK", AtUtc: now.Add(2 * time.Second)})
	if id1 != "grpc-1" || id2 != "grpc-2" {
		t.Errorf("call ids = %q,%q want grpc-1,grpc-2", id1, id2)
	}
	recent := m.RecentCalls(1)
	if len(recent) != 1 || recent[0].Method != "B" {
		t.Errorf("RecentCalls(1) = %+v want [B]", recent)
	}
}

func TestGrpcTransport_LifecycleAndState(t *testing.T) {
	fab := circleai.NewGrpcFabric(nil)
	desc := circleai.GrpcChannelDescriptor{Target: "svc:443"}
	tr, err := circleai.NewGrpcNetworkTransport(desc, fab, circleai.GrpcRetryPolicy{})
	if err != nil {
		t.Fatal(err)
	}
	if tr.Kind() != circleai.TransportKindGrpc {
		t.Errorf("Kind = %v", tr.Kind())
	}
	if tr.IsAvailable() {
		t.Error("not available before Start")
	}
	if fab.Metrics.State("svc:443") != circleai.GrpcChannelStateIdle {
		t.Error("channel should start Idle")
	}
	if err := tr.Send(context.Background(), circleai.NewNetworkPayload(nil, "")); err == nil {
		t.Error("Send before Start should error")
	}
	_ = tr.Start(context.Background())
	if !tr.IsAvailable() {
		t.Error("available after Start")
	}
	if fab.Metrics.State("svc:443") != circleai.GrpcChannelStateReady {
		t.Error("channel should be Ready after Start")
	}
	_ = tr.Stop(context.Background())
	if fab.Metrics.State("svc:443") != circleai.GrpcChannelStateShutdown {
		t.Error("channel should be Shutdown after Stop")
	}
}

func TestGrpcTransport_TargetScopedFanOut(t *testing.T) {
	fab := circleai.NewGrpcFabric(nil)
	a, _ := circleai.NewGrpcNetworkTransport(circleai.GrpcChannelDescriptor{Target: "svcX"}, fab, circleai.GrpcRetryPolicy{})
	b, _ := circleai.NewGrpcNetworkTransport(circleai.GrpcChannelDescriptor{Target: "svcX"}, fab, circleai.GrpcRetryPolicy{})
	other, _ := circleai.NewGrpcNetworkTransport(circleai.GrpcChannelDescriptor{Target: "svcY"}, fab, circleai.GrpcRetryPolicy{})
	for _, tr := range []*circleai.GrpcNetworkTransport{a, b, other} {
		_ = tr.Start(context.Background())
	}
	rctx, cancel := context.WithCancel(context.Background())
	defer cancel()
	bStream := b.Receive(rctx)
	aStream := a.Receive(rctx)
	otherStream := other.Receive(rctx)

	if err := a.Send(context.Background(), circleai.NewNetworkPayload([]byte("call"), "")); err != nil {
		t.Fatal(err)
	}
	if got := string(recvOne(t, bStream).Data); got != "call" {
		t.Errorf("same-target peer got %q", got)
	}
	expectNoPayload(t, aStream)     // loopback excluded
	expectNoPayload(t, otherStream) // different Target excluded
}

func TestGrpcTransport_BufferedBeforeSubscribe(t *testing.T) {
	fab := circleai.NewGrpcFabric(nil)
	a, _ := circleai.NewGrpcNetworkTransport(circleai.GrpcChannelDescriptor{Target: "t"}, fab, circleai.GrpcRetryPolicy{})
	b, _ := circleai.NewGrpcNetworkTransport(circleai.GrpcChannelDescriptor{Target: "t"}, fab, circleai.GrpcRetryPolicy{})
	_ = a.Start(context.Background())
	_ = b.Start(context.Background())
	if err := a.Send(context.Background(), circleai.NewNetworkPayload([]byte("early"), "")); err != nil {
		t.Fatal(err)
	}
	rctx, cancel := context.WithCancel(context.Background())
	defer cancel()
	if got := string(recvOne(t, b.Receive(rctx)).Data); got != "early" {
		t.Errorf("buffered frame lost: %q", got)
	}
}

func TestGrpcTransport_MaxSendBytesRejected(t *testing.T) {
	fab := circleai.NewGrpcFabric(nil)
	desc := circleai.GrpcChannelDescriptor{Target: "svc", MaxSendBytes: 4}
	tr, _ := circleai.NewGrpcNetworkTransport(desc, fab, circleai.GrpcRetryPolicyAggressive())
	_ = tr.Start(context.Background())

	// Over budget -> error, logged as RESOURCE_EXHAUSTED.
	if err := tr.Send(context.Background(), circleai.NewNetworkPayload([]byte("toolong"), "")); err == nil {
		t.Error("over-budget send should error")
	}
	recent := fab.Metrics.RecentCalls(10)
	if len(recent) == 0 || recent[0].StatusCode != "RESOURCE_EXHAUSTED" {
		t.Errorf("expected RESOURCE_EXHAUSTED call log, got %+v", recent)
	}
	// Within budget -> ok.
	if err := tr.Send(context.Background(), circleai.NewNetworkPayload([]byte("ok"), "")); err != nil {
		t.Errorf("within-budget send should succeed: %v", err)
	}
}

func TestGrpcTransport_RequiresTarget(t *testing.T) {
	fab := circleai.NewGrpcFabric(nil)
	if _, err := circleai.NewGrpcNetworkTransport(circleai.GrpcChannelDescriptor{Target: ""}, fab, circleai.GrpcRetryPolicy{}); err == nil {
		t.Error("empty target should be rejected")
	}
	if _, err := circleai.NewGrpcNetworkTransport(circleai.GrpcChannelDescriptor{Target: "x"}, nil, circleai.GrpcRetryPolicy{}); err == nil {
		t.Error("nil fabric should be rejected")
	}
}
