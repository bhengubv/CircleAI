// network_dtn_test.go
//
// Verifies network_dtn.go:
//   - DtnPriority ordinals + String
//   - InMemoryDtnBundleStore: store/get, ordered All, custody, IsExpired,
//     Purge, InFlightTo
//   - DtnSyncChannel: 72h bundle creation, send over first available transport,
//     custody-required flagging for Guaranteed, per-(owner,domain) sequence,
//     store-and-forward delivery when no transport is available,
//     ReceiveDeltas afterSeq filtering

package circleai_test

import (
	"context"
	"testing"
	"time"

	circleai "github.com/bhengubv/CircleAI/go"
)

func TestDtnPriority_Ordinals(t *testing.T) {
	cases := []struct {
		p    circleai.DtnPriority
		ord  int
		name string
	}{
		{circleai.DtnPriorityBulk, 0, "Bulk"},
		{circleai.DtnPriorityNormal, 1, "Normal"},
		{circleai.DtnPriorityExpedited, 2, "Expedited"},
	}
	for _, c := range cases {
		if int(c.p) != c.ord {
			t.Errorf("%s ordinal = %d want %d", c.name, int(c.p), c.ord)
		}
		if c.p.String() != c.name {
			t.Errorf("String = %q want %q", c.p.String(), c.name)
		}
	}
}

func TestDtnBundleStore_CRUDAndExpiry(t *testing.T) {
	s := circleai.NewInMemoryDtnBundleStore()
	now := time.Now().UTC()
	live := circleai.DtnBundle{BundleId: "b2", SourceNodeId: "s", DestinationNodeId: "d1", ExpiresAt: now.Add(time.Hour), CreatedAt: now}
	dead := circleai.DtnBundle{BundleId: "b1", SourceNodeId: "s", DestinationNodeId: "d2", ExpiresAt: now.Add(-time.Hour), CreatedAt: now.Add(-2 * time.Hour)}
	s.Store(live)
	s.Store(dead)

	all := s.All()
	if len(all) != 2 || all[0].BundleId != "b1" || all[1].BundleId != "b2" {
		t.Fatalf("All not ordered by BundleId: %+v", all)
	}
	if got, ok := s.Get("b2"); !ok || got.DestinationNodeId != "d1" {
		t.Errorf("Get(b2) = %+v ok=%v", got, ok)
	}
	if _, ok := s.Get("missing"); ok {
		t.Error("Get(missing) should be false")
	}

	if !s.IsExpired("b1", now) {
		t.Error("b1 should be expired")
	}
	if s.IsExpired("b2", now) {
		t.Error("b2 should not be expired")
	}
	if !s.IsExpired("unknown", now) {
		t.Error("unknown bundle should count as expired")
	}

	// Custody.
	s.AcceptCustody(circleai.DtnCustodyRecord{BundleId: "b2", CustodianNode: "node1", AcceptedAtUtc: now})
	if rec, ok := s.GetCustody("b2"); !ok || rec.CustodianNode != "node1" {
		t.Errorf("custody = %+v ok=%v", rec, ok)
	}

	// InFlightTo filters by destination.
	if inflight := s.InFlightTo("d1"); len(inflight) != 1 || inflight[0].BundleId != "b2" {
		t.Errorf("InFlightTo(d1) = %+v", inflight)
	}

	// Purge removes the dead bundle (and its custody).
	if n := s.Purge(now); n != 1 {
		t.Errorf("Purge removed %d want 1", n)
	}
	if _, ok := s.Get("b1"); ok {
		t.Error("b1 should be purged")
	}
	if _, ok := s.Get("b2"); !ok {
		t.Error("b2 should survive purge")
	}
}

func TestDtnSyncChannel_SendOverAvailableTransport(t *testing.T) {
	// A live transport receives the DTN-bundle payload.
	fab := circleai.NewInMemoryTransportFabric()
	sender, _ := circleai.NewInMemoryNetworkTransport(circleai.TransportKindWiFi, fab)
	receiver, _ := circleai.NewInMemoryNetworkTransport(circleai.TransportKindWiFi, fab)
	_ = sender.Start(context.Background())
	_ = receiver.Start(context.Background())

	rctx, cancel := context.WithCancel(context.Background())
	defer cancel()
	wire := receiver.Receive(rctx)

	ch := circleai.NewDtnSyncChannel([]circleai.INetworkTransport{sender})
	delta := circleai.SyncDelta{
		OwnerID: "o", SourceDeviceID: "src", TargetDeviceID: "dst",
		DomainKey: "memory.episodic", Payload: []byte("bundle-body"),
		Sequence: 7, DeliveryMode: circleai.SyncDeliveryModeGuaranteed,
	}
	if err := ch.PushDelta(context.Background(), delta); err != nil {
		t.Fatal(err)
	}

	// The payload went out over the wire with the DTN content type.
	got := recvOne(t, wire)
	if string(got.Data) != "bundle-body" {
		t.Errorf("wire payload = %q", string(got.Data))
	}
	if got.ContentType != "application/dtn-bundle" {
		t.Errorf("content type = %q want application/dtn-bundle", got.ContentType)
	}

	// A bundle was stored with custody (Guaranteed) and a ~72h expiry.
	bundles := ch.Store().All()
	if len(bundles) != 1 {
		t.Fatalf("expected 1 stored bundle, got %d", len(bundles))
	}
	if !bundles[0].CustodyRequired {
		t.Error("Guaranteed delivery should require custody")
	}
	ttl := bundles[0].ExpiresAt.Sub(bundles[0].CreatedAt)
	if ttl < 71*time.Hour || ttl > 73*time.Hour {
		t.Errorf("bundle ttl = %v want ~72h", ttl)
	}
	if _, ok := ch.Store().GetCustody(bundles[0].BundleId); !ok {
		t.Error("custody record should be present for Guaranteed bundle")
	}

	// Sequence high-water advanced.
	if seq, _ := ch.GetLastSequence(context.Background(), "o", "memory.episodic"); seq != 7 {
		t.Errorf("last seq = %d want 7", seq)
	}
}

func TestDtnSyncChannel_StoreAndForwardWhenOffline(t *testing.T) {
	// No available transport: the delta is queued and surfaced to ReceiveDeltas.
	fab := circleai.NewInMemoryTransportFabric()
	down, _ := circleai.NewInMemoryNetworkTransport(circleai.TransportKindWiFi, fab)
	// deliberately NOT started -> IsAvailable() == false

	ch := circleai.NewDtnSyncChannel([]circleai.INetworkTransport{down})
	rctx, cancel := context.WithCancel(context.Background())
	defer cancel()
	deltas, errs := ch.ReceiveDeltas(rctx, "owner", 0)

	if err := ch.PushDelta(context.Background(), circleai.SyncDelta{
		OwnerID: "owner", SourceDeviceID: "s", DomainKey: "d",
		Payload: []byte("queued"), Sequence: 1,
	}); err != nil {
		t.Fatal(err)
	}
	got := recvDelta(t, deltas, errs)
	if string(got.Payload) != "queued" {
		t.Errorf("store-and-forward delta = %q", string(got.Payload))
	}
	// A bundle was still stored for later forwarding.
	if len(ch.Store().All()) != 1 {
		t.Errorf("bundle should be stored while offline")
	}
}

func TestDtnSyncChannel_BestEffortNoCustody(t *testing.T) {
	ch := circleai.NewDtnSyncChannel(nil)
	if err := ch.PushDelta(context.Background(), circleai.SyncDelta{
		OwnerID: "o", SourceDeviceID: "s", DomainKey: "d",
		Payload: []byte("x"), Sequence: 1, DeliveryMode: circleai.SyncDeliveryModeBestEffort,
	}); err != nil {
		t.Fatal(err)
	}
	bundles := ch.Store().All()
	if len(bundles) != 1 || bundles[0].CustodyRequired {
		t.Errorf("BestEffort bundle should not require custody: %+v", bundles)
	}
}

func TestDtnSyncChannel_ReceiveAfterSeqFilter(t *testing.T) {
	ch := circleai.NewDtnSyncChannel(nil)
	rctx, cancel := context.WithCancel(context.Background())
	defer cancel()
	deltas, _ := ch.ReceiveDeltas(rctx, "o", 2)

	for _, seq := range []int64{1, 2, 3} {
		_ = ch.PushDelta(context.Background(), circleai.SyncDelta{
			OwnerID: "o", SourceDeviceID: "s", DomainKey: "d",
			Payload: []byte("v"), Sequence: seq,
		})
	}
	first := recvDelta(t, deltas, nil)
	if first.Sequence != 3 {
		t.Errorf("first delivered seq = %d want 3 (1,2 filtered)", first.Sequence)
	}
}
