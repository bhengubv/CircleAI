// aethernet_mesh_capability_test.go
//
// Verifies CircleAI.AetherNet.MeshCapabilityRegistry port
// (aethernet_mesh_capability.go): Upsert replaces per-peer, Remove is
// idempotent, List honours the staleness filter, Find matches model
// (case-insensitive) + min-KV and sorts by spare budget descending, and the
// broadcasters (Null no-op + loopback that feeds the registry).

package circleai_test

import (
	"context"
	"testing"
	"time"

	circleai "github.com/bhengubv/CircleAI/go"
)

func mkAd(peer, model string, freeKv int, at time.Time) circleai.MeshCapabilityAdvertisement {
	return circleai.MeshCapabilityAdvertisement{
		PeerID:              peer,
		ModelID:             model,
		FreeKvTokens:        freeKv,
		Tier:                circleai.DeviceTierPhone,
		ContextWindowTokens: 2048,
		AdvertisedAtUTC:     at,
	}
}

func TestMeshRegistry_UpsertReplacesPerPeer(t *testing.T) {
	reg := circleai.NewInMemoryMeshCapabilityRegistry()
	ctx := context.Background()
	now := time.Now().UTC()

	_ = reg.Upsert(ctx, mkAd("p1", "Qwen3-1.7B-MNN", 1000, now))
	_ = reg.Upsert(ctx, mkAd("p1", "Qwen3-1.7B-MNN", 2000, now)) // replace, not add

	all := reg.List(nil)
	if len(all) != 1 {
		t.Fatalf("upsert should replace: got %d entries want 1", len(all))
	}
	if all[0].FreeKvTokens != 2000 {
		t.Errorf("latest advertisement should win: got %d", all[0].FreeKvTokens)
	}
}

func TestMeshRegistry_RemoveIdempotent(t *testing.T) {
	reg := circleai.NewInMemoryMeshCapabilityRegistry()
	ctx := context.Background()
	_ = reg.Upsert(ctx, mkAd("p1", "m", 10, time.Now().UTC()))

	removed, _ := reg.Remove(ctx, "p1")
	if !removed {
		t.Error("first remove should report true")
	}
	removed, _ = reg.Remove(ctx, "p1")
	if removed {
		t.Error("second remove should report false (idempotent)")
	}
	if len(reg.List(nil)) != 0 {
		t.Error("registry should be empty after remove")
	}
}

func TestMeshRegistry_ListStalenessFilter(t *testing.T) {
	fixed := time.Date(2026, 7, 10, 12, 0, 0, 0, time.UTC)
	reg := circleai.NewInMemoryMeshCapabilityRegistry().WithClock(func() time.Time { return fixed })
	ctx := context.Background()

	_ = reg.Upsert(ctx, mkAd("fresh", "m", 10, fixed.Add(-10*time.Second)))
	_ = reg.Upsert(ctx, mkAd("stale", "m", 10, fixed.Add(-90*time.Second)))

	// No filter → both.
	if got := len(reg.List(nil)); got != 2 {
		t.Errorf("unfiltered list got %d want 2", got)
	}
	// 60s window → only the fresh one.
	window := 60 * time.Second
	got := reg.List(&window)
	if len(got) != 1 || got[0].PeerID != "fresh" {
		t.Errorf("staleness filter got %+v, want only 'fresh'", got)
	}
}

func TestMeshRegistry_FindMatchesAndSorts(t *testing.T) {
	fixed := time.Date(2026, 7, 10, 12, 0, 0, 0, time.UTC)
	reg := circleai.NewInMemoryMeshCapabilityRegistry().WithClock(func() time.Time { return fixed })
	ctx := context.Background()

	_ = reg.Upsert(ctx, mkAd("low", "Qwen3-1.7B-MNN", 500, fixed))
	_ = reg.Upsert(ctx, mkAd("high", "qwen3-1.7b-mnn", 4000, fixed)) // case-insensitive match
	_ = reg.Upsert(ctx, mkAd("mid", "Qwen3-1.7B-MNN", 2000, fixed))
	_ = reg.Upsert(ctx, mkAd("other", "Gemma-2B", 9000, fixed)) // different model

	// min 1000 KV → excludes "low" (500) and "other" (wrong model).
	got := reg.Find("Qwen3-1.7B-MNN", 1000, nil)
	if len(got) != 2 {
		t.Fatalf("Find got %d want 2 (%+v)", len(got), got)
	}
	// Sorted by spare budget DESCENDING → high (4000) before mid (2000).
	if got[0].PeerID != "high" || got[1].PeerID != "mid" {
		t.Errorf("Find ordering wrong: got %s,%s want high,mid", got[0].PeerID, got[1].PeerID)
	}

	// min 0 includes low too (still excludes wrong model).
	got = reg.Find("Qwen3-1.7B-MNN", 0, nil)
	if len(got) != 3 {
		t.Errorf("Find min0 got %d want 3", len(got))
	}
	if got[0].PeerID != "high" || got[2].PeerID != "low" {
		t.Errorf("Find min0 ordering: got %s..%s", got[0].PeerID, got[2].PeerID)
	}
}

func TestMeshRegistry_FindStalenessFilter(t *testing.T) {
	fixed := time.Date(2026, 7, 10, 12, 0, 0, 0, time.UTC)
	reg := circleai.NewInMemoryMeshCapabilityRegistry().WithClock(func() time.Time { return fixed })
	ctx := context.Background()

	_ = reg.Upsert(ctx, mkAd("fresh", "m", 1000, fixed.Add(-5*time.Second)))
	_ = reg.Upsert(ctx, mkAd("stale", "m", 5000, fixed.Add(-120*time.Second)))

	window := 60 * time.Second
	got := reg.Find("m", 0, &window)
	if len(got) != 1 || got[0].PeerID != "fresh" {
		t.Errorf("Find staleness got %+v want only 'fresh'", got)
	}
}

func TestMeshRegistry_UpsertBlankPeerPanics(t *testing.T) {
	reg := circleai.NewInMemoryMeshCapabilityRegistry()
	defer func() {
		if recover() == nil {
			t.Error("expected panic on blank PeerID")
		}
	}()
	_ = reg.Upsert(context.Background(), mkAd("   ", "m", 1, time.Now().UTC()))
}

func TestNullMeshCapabilityBroadcaster_NoOp(t *testing.T) {
	err := circleai.NullMeshCapabilityBroadcasterInstance.Broadcast(context.Background(),
		mkAd("p1", "m", 1, time.Now().UTC()))
	if err != nil {
		t.Errorf("null broadcaster should never error: %v", err)
	}
}

func TestLoopbackBroadcaster_FeedsRegistry(t *testing.T) {
	reg := circleai.NewInMemoryMeshCapabilityRegistry()
	bc := circleai.NewLocalLoopbackMeshCapabilityBroadcaster(reg)
	ctx := context.Background()

	ad := mkAd("me", "Qwen3-1.7B-MNN", 3000, time.Now().UTC())
	if err := bc.Broadcast(ctx, ad); err != nil {
		t.Fatalf("broadcast: %v", err)
	}
	got := reg.Find("Qwen3-1.7B-MNN", 0, nil)
	if len(got) != 1 || got[0].PeerID != "me" {
		t.Errorf("loopback broadcast should land in registry, got %+v", got)
	}
}
