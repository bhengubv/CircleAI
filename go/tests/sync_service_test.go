// sync_service_test.go
//
// Verifies MemorySyncService + SyncPrimitives (ported from MemorySyncService.cs
// and SyncPrimitives.cs):
//   - PushMemoryDelta builds a broadcast SyncDelta (empty target, source =
//     local device, mode preserved) and pushes it through the channel.
//   - The receive loop applies episodic-domain deltas to the local store and
//     skips deltas that this device authored (echo suppression).
//   - VersionVector merge/dominance and LastWriterWins behave per the reference.

package circleai_test

import (
	"context"
	"testing"
	"time"

	circleai "github.com/bhengubv/CircleAI/go"
)

// fakeSyncChannel captures pushed deltas and streams injected inbound deltas.
type fakeSyncChannel struct {
	pushed  []circleai.SyncDelta
	inbound chan circleai.SyncDelta
	errs    chan error
}

func newFakeSyncChannel() *fakeSyncChannel {
	return &fakeSyncChannel{
		inbound: make(chan circleai.SyncDelta, 16),
		errs:    make(chan error, 1),
	}
}

func (c *fakeSyncChannel) PushDelta(_ context.Context, delta circleai.SyncDelta) error {
	c.pushed = append(c.pushed, delta)
	return nil
}

func (c *fakeSyncChannel) ReceiveDeltas(_ context.Context, _ string, _ int64) (<-chan circleai.SyncDelta, <-chan error) {
	return c.inbound, c.errs
}

func (c *fakeSyncChannel) GetLastSequence(context.Context, string, string) (int64, error) {
	return 0, nil
}

func TestMemorySyncService_PushBuildsBroadcastDelta(t *testing.T) {
	ctx := context.Background()
	ch := newFakeSyncChannel()
	store := circleai.NewInMemoryEpisodicStoreDefault()
	svc, err := circleai.NewMemorySyncService(ch, store, "device-A")
	if err != nil {
		t.Fatalf("ctor: %v", err)
	}

	payload := []byte("delta-bytes")
	if err := svc.PushMemoryDelta(ctx, "owner-1", circleai.SyncDomainKeys.Persona, payload, circleai.SyncDeliveryModeUrgent); err != nil {
		t.Fatalf("PushMemoryDelta: %v", err)
	}
	if len(ch.pushed) != 1 {
		t.Fatalf("expected 1 pushed delta, got %d", len(ch.pushed))
	}
	d := ch.pushed[0]
	if d.OwnerID != "owner-1" {
		t.Errorf("owner: got %q", d.OwnerID)
	}
	if d.SourceDeviceID != "device-A" {
		t.Errorf("source: got %q", d.SourceDeviceID)
	}
	if d.TargetDeviceID != "" {
		t.Errorf("target should be empty (broadcast): got %q", d.TargetDeviceID)
	}
	if d.DomainKey != "persona" {
		t.Errorf("domain: got %q", d.DomainKey)
	}
	if d.DeliveryMode != circleai.SyncDeliveryModeUrgent {
		t.Errorf("mode: got %v", d.DeliveryMode)
	}
	if string(d.Payload) != "delta-bytes" {
		t.Errorf("payload: got %q", string(d.Payload))
	}
	if d.Sequence == 0 {
		t.Error("sequence should be a unix-ms timestamp, got 0")
	}
}

func TestMemorySyncService_ReceiveAppliesEpisodicAndSkipsEcho(t *testing.T) {
	ctx := context.Background()
	ch := newFakeSyncChannel()
	store := circleai.NewInMemoryEpisodicStoreDefault()
	svc, err := circleai.NewMemorySyncService(ch, store, "device-A")
	if err != nil {
		t.Fatalf("ctor: %v", err)
	}
	if err := svc.StartReceiving(ctx, "owner-1"); err != nil {
		t.Fatalf("StartReceiving: %v", err)
	}
	defer svc.StopReceiving(ctx)

	// Echo from our own device — must be skipped.
	echoEntry := circleai.NewEpisodicMemoryEntry("mine", "reply")
	echoPayload, _ := circleai.EncodeEpisodicDelta(echoEntry)
	ch.inbound <- circleai.SyncDelta{
		OwnerID:        "owner-1",
		SourceDeviceID: "device-A",
		DomainKey:      circleai.SyncDomainKeys.MemoryEpisodic,
		Payload:        echoPayload,
	}

	// Real delta from a peer device — must be applied.
	peerEntry := circleai.NewEpisodicMemoryEntry("hello from B", "sure")
	peerPayload, _ := circleai.EncodeEpisodicDelta(peerEntry)
	ch.inbound <- circleai.SyncDelta{
		OwnerID:        "owner-1",
		SourceDeviceID: "device-B",
		DomainKey:      circleai.SyncDomainKeys.MemoryEpisodic,
		Payload:        peerPayload,
	}

	// Poll for the applied entry.
	deadline := time.Now().Add(2 * time.Second)
	var count int
	for time.Now().Before(deadline) {
		count, _ = store.Count(ctx)
		if count >= 1 {
			break
		}
		time.Sleep(5 * time.Millisecond)
	}
	if count != 1 {
		t.Fatalf("expected exactly 1 applied entry (echo skipped), got %d", count)
	}
	recent, _ := store.GetRecent(ctx, 1)
	if len(recent) != 1 || recent[0].UserText != "hello from B" {
		t.Errorf("applied entry mismatch: %+v", recent)
	}
}

func TestMemorySyncService_ReceiveStopsOnChannelClose(t *testing.T) {
	ctx := context.Background()
	ch := newFakeSyncChannel()
	store := circleai.NewInMemoryEpisodicStoreDefault()
	svc, _ := circleai.NewMemorySyncService(ch, store, "device-A")
	if err := svc.StartReceiving(ctx, "owner-1"); err != nil {
		t.Fatalf("StartReceiving: %v", err)
	}
	close(ch.inbound) // stream ends
	// StopReceiving must return promptly (the loop already exited).
	if err := svc.StopReceiving(ctx); err != nil {
		t.Fatalf("StopReceiving: %v", err)
	}
}

func TestMemorySyncService_ConstructorValidation(t *testing.T) {
	store := circleai.NewInMemoryEpisodicStoreDefault()
	if _, err := circleai.NewMemorySyncService(nil, store, "d"); err == nil {
		t.Error("nil channel should error")
	}
	if _, err := circleai.NewMemorySyncService(newFakeSyncChannel(), nil, "d"); err == nil {
		t.Error("nil store should error")
	}
	if _, err := circleai.NewMemorySyncService(newFakeSyncChannel(), store, ""); err == nil {
		t.Error("blank device id should error")
	}
}

func TestSyncReconciliation_MergeAndDominance(t *testing.T) {
	a := circleai.NewVersionVector(map[string]int64{"n1": 3, "n2": 5})
	b := circleai.NewVersionVector(map[string]int64{"n2": 2, "n3": 7})

	merged := circleai.MergeVersionVectors(a, b)
	if merged.Clocks["n1"] != 3 || merged.Clocks["n2"] != 5 || merged.Clocks["n3"] != 7 {
		t.Errorf("merge: got %+v", merged.Clocks)
	}

	// a dominates a strict subset with lower/equal values.
	lower := circleai.NewVersionVector(map[string]int64{"n1": 1, "n2": 5})
	if !circleai.VersionVectorADominatesB(a, lower) {
		t.Error("a should dominate lower")
	}
	// Neither dominates when they diverge (a has n2 higher-or-equal but b has n3).
	if circleai.VersionVectorADominatesB(a, b) {
		t.Error("a should not dominate b (concurrent)")
	}
	// Equal vectors: no strict-greater key → not dominating.
	if circleai.VersionVectorADominatesB(a, a) {
		t.Error("equal vectors should not dominate")
	}
}

func TestSyncReconciliation_LastWriterWins(t *testing.T) {
	early := time.Date(2026, 7, 8, 10, 0, 0, 0, time.UTC)
	late := early.Add(time.Hour)

	at, val := circleai.LastWriterWins(late, "late", early, "early")
	if val != "late" || !at.Equal(late) {
		t.Errorf("later should win: got %q @ %v", val, at)
	}
	// Tie favours the first argument (>= semantics).
	at2, val2 := circleai.LastWriterWins(early, "a", early, "b")
	if val2 != "a" || !at2.Equal(early) {
		t.Errorf("tie should favour first: got %q @ %v", val2, at2)
	}
}
