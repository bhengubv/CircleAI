// sync_engine_test.go
//
// Verifies CompanionStateSyncEngine + InProcessCompanionStateChannel/Hub
// (ported from CompanionStateSyncEngine.cs + InProcessCompanionStateChannel.cs):
//   - WriteLocal stamps a version, hashes the payload (SHA-256 hex), and stores it.
//   - A live Push propagates a write to a peer engine (event-driven).
//   - A late-joining peer converges via Announce → Request → Push after SyncNow.
//   - Tombstones propagate and win.
//   - Convergence terminates (no infinite Announce loop).

package circleai_test

import (
	"context"
	"crypto/sha256"
	"encoding/hex"
	"testing"

	circleai "github.com/bhengubv/CircleAI/go"
)

type engNode struct {
	channel *circleai.InProcessCompanionStateChannel
	store   *circleai.InMemorySyncableEntryStore
	engine  *circleai.CompanionStateSyncEngine
}

func newEngNode(t *testing.T, hub *circleai.InProcessSyncHub, nodeID string, nodeShort int64) engNode {
	t.Helper()
	ch, err := circleai.NewInProcessCompanionStateChannel(hub, nodeID)
	if err != nil {
		t.Fatalf("channel: %v", err)
	}
	store := circleai.NewInMemorySyncableEntryStore()
	clk := circleai.MustNewHybridLogicalClock(nodeShort)
	eng, err := circleai.NewCompanionStateSyncEngine(ch, store, clk, nil)
	if err != nil {
		t.Fatalf("engine: %v", err)
	}
	return engNode{channel: ch, store: store, engine: eng}
}

func TestSyncEngine_WriteLocal_StampsAndHashes(t *testing.T) {
	ctx := context.Background()
	hub := circleai.NewInProcessSyncHub()
	a := newEngNode(t, hub, "A", 1)
	defer a.channel.Close()

	entry, err := a.engine.WriteLocal(ctx, "PersonaState", "user-1", "hello", false)
	if err != nil {
		t.Fatalf("WriteLocal: %v", err)
	}
	if entry.Version == 0 {
		t.Error("version should be stamped")
	}
	if entry.SourceNodeID != "A" {
		t.Errorf("source node: got %q want A", entry.SourceNodeID)
	}
	sum := sha256.Sum256([]byte("hello"))
	if entry.ContentHash != hex.EncodeToString(sum[:]) {
		t.Errorf("content hash mismatch: got %q", entry.ContentHash)
	}
	got, _ := a.store.Get(ctx, "PersonaState", "user-1")
	if got == nil || got.Payload != "hello" {
		t.Errorf("entry not stored: %+v", got)
	}
}

func TestSyncEngine_LivePushPropagates(t *testing.T) {
	ctx := context.Background()
	hub := circleai.NewInProcessSyncHub()
	a := newEngNode(t, hub, "A", 1)
	b := newEngNode(t, hub, "B", 2)
	defer a.channel.Close()
	defer b.channel.Close()

	if err := a.engine.Start(ctx); err != nil {
		t.Fatalf("A start: %v", err)
	}
	if err := b.engine.Start(ctx); err != nil {
		t.Fatalf("B start: %v", err)
	}

	if _, err := a.engine.WriteLocal(ctx, "PersonaState", "user-1", `{"v":1}`, false); err != nil {
		t.Fatalf("WriteLocal: %v", err)
	}

	// B should have received the push and applied it.
	got, _ := b.store.Get(ctx, "PersonaState", "user-1")
	if got == nil || got.Payload != `{"v":1}` {
		t.Errorf("B did not receive push: %+v", got)
	}
}

func TestSyncEngine_LateJoiner_ConvergesViaAnnounce(t *testing.T) {
	ctx := context.Background()
	hub := circleai.NewInProcessSyncHub()
	a := newEngNode(t, hub, "A", 1)
	b := newEngNode(t, hub, "B", 2)
	defer a.channel.Close()
	defer b.channel.Close()

	// A writes BEFORE B is listening → B misses the live push.
	_ = a.engine.Start(ctx)
	_, _ = a.engine.WriteLocal(ctx, "CoreMemory", "m-1", "payloadA", false)
	_, _ = a.engine.WriteLocal(ctx, "CoreMemory", "m-2", "payloadB", false)

	// Now B starts and A re-announces.
	_ = b.engine.Start(ctx)
	if got, _ := b.store.Get(ctx, "CoreMemory", "m-1"); got != nil {
		t.Fatal("precondition: B should not yet have A's data")
	}

	if err := a.engine.SyncNow(ctx); err != nil {
		t.Fatalf("SyncNow: %v", err)
	}

	for _, id := range []string{"m-1", "m-2"} {
		got, _ := b.store.Get(ctx, "CoreMemory", id)
		if got == nil {
			t.Errorf("B did not converge for %s", id)
		}
	}
}

func TestSyncEngine_TombstonePropagates(t *testing.T) {
	ctx := context.Background()
	hub := circleai.NewInProcessSyncHub()
	a := newEngNode(t, hub, "A", 1)
	b := newEngNode(t, hub, "B", 2)
	defer a.channel.Close()
	defer b.channel.Close()
	_ = a.engine.Start(ctx)
	_ = b.engine.Start(ctx)

	_, _ = a.engine.WriteLocal(ctx, "ConversationState", "s-1", "live", false)
	if got, _ := b.store.Get(ctx, "ConversationState", "s-1"); got == nil || got.IsTombstone {
		t.Fatal("B should have the live entry")
	}

	_, _ = a.engine.WriteLocal(ctx, "ConversationState", "s-1", "", true)
	got, _ := b.store.Get(ctx, "ConversationState", "s-1")
	if got == nil || !got.IsTombstone {
		t.Errorf("tombstone should have propagated: %+v", got)
	}
}

func TestSyncEngine_DisposedRejectsWrites(t *testing.T) {
	ctx := context.Background()
	hub := circleai.NewInProcessSyncHub()
	a := newEngNode(t, hub, "A", 1)
	defer a.channel.Close()
	if err := a.engine.Close(); err != nil {
		t.Fatalf("close: %v", err)
	}
	if _, err := a.engine.WriteLocal(ctx, "T", "1", "x", false); err == nil {
		t.Error("write after close should error")
	}
	if err := a.engine.SyncNow(ctx); err == nil {
		t.Error("SyncNow after close should error")
	}
}
