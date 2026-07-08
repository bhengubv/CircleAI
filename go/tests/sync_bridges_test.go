// sync_bridges_test.go
//
// Verifies the three sync bridges (ported from PersonaStateSyncBridge.cs,
// LoraAdapterSyncBridge.cs, CompanionConversationSyncBridge.cs):
//   - PersonaStateSyncBridge.Save persists locally AND pushes a decodable entry.
//   - LoraAdapterSyncBridge.Publish base64-encodes a file; TryWrite round-trips
//     the bytes to a destination path.
//   - CompanionConversationSyncBridge publishes deltas and terminates via
//     tombstone; TryDecode round-trips the delta and rejects tombstones.

package circleai_test

import (
	"context"
	"os"
	"path/filepath"
	"testing"
	"time"

	circleai "github.com/bhengubv/CircleAI/go"
)

// wireBridgeEngine builds an engine over a fresh in-process channel + store.
func wireBridgeEngine(t *testing.T, nodeID string) (*circleai.CompanionStateSyncEngine, *circleai.InMemorySyncableEntryStore, func()) {
	t.Helper()
	hub := circleai.NewInProcessSyncHub()
	ch, err := circleai.NewInProcessCompanionStateChannel(hub, nodeID)
	if err != nil {
		t.Fatalf("channel: %v", err)
	}
	store := circleai.NewInMemorySyncableEntryStore()
	eng, err := circleai.NewCompanionStateSyncEngine(ch, store, circleai.MustNewHybridLogicalClock(1), nil)
	if err != nil {
		t.Fatalf("engine: %v", err)
	}
	return eng, store, func() { ch.Close() }
}

func TestPersonaStateSyncBridge_SaveAndDecode(t *testing.T) {
	ctx := context.Background()
	eng, store, done := wireBridgeEngine(t, "A")
	defer done()
	personaStore := circleai.NewInMemoryPersonaStore()

	bridge, err := circleai.NewPersonaStateSyncBridge(personaStore, eng)
	if err != nil {
		t.Fatalf("bridge: %v", err)
	}

	loc := "en-ZA"
	persona := circleai.NewPersonaState("user-42")
	persona.Verbosity = "brief"
	persona.Formality = "casual"
	persona.PreferredLocale = &loc
	persona.TopicWeights["finance"] = 2.5
	persona.DisfavouredTopics["gossip"] = struct{}{}
	persona.PositiveSignals = 7

	if err := bridge.Save(ctx, persona); err != nil {
		t.Fatalf("Save: %v", err)
	}

	// Persisted locally.
	loaded, _ := personaStore.Load(ctx, "user-42")
	if loaded.Verbosity != "brief" {
		t.Errorf("persona store: got verbosity %q", loaded.Verbosity)
	}

	// Pushed as a decodable syncable entry.
	entry, _ := store.Get(ctx, circleai.PersonaStateEntityType, "user-42")
	if entry == nil {
		t.Fatal("persona not pushed to sync store")
	}
	decoded, ok := circleai.TryDecodePersonaState(*entry)
	if !ok {
		t.Fatal("decode failed")
	}
	if decoded.UserID != "user-42" || decoded.Verbosity != "brief" || decoded.Formality != "casual" {
		t.Errorf("decoded mismatch: %+v", decoded)
	}
	if decoded.PreferredLocale == nil || *decoded.PreferredLocale != "en-ZA" {
		t.Errorf("locale round-trip: %v", decoded.PreferredLocale)
	}
	if decoded.TopicWeights["finance"] != 2.5 {
		t.Errorf("topic weight round-trip: %v", decoded.TopicWeights)
	}
	if _, ok := decoded.DisfavouredTopics["gossip"]; !ok {
		t.Errorf("disfavoured round-trip: %v", decoded.DisfavouredTopics)
	}
	if decoded.PositiveSignals != 7 {
		t.Errorf("positive signals round-trip: %d", decoded.PositiveSignals)
	}
}

func TestLoraAdapterSyncBridge_PublishAndWrite(t *testing.T) {
	ctx := context.Background()
	eng, store, done := wireBridgeEngine(t, "A")
	defer done()

	bridge, err := circleai.NewLoraAdapterSyncBridge(eng)
	if err != nil {
		t.Fatalf("bridge: %v", err)
	}

	dir := t.TempDir()
	src := filepath.Join(dir, "adapter.bin")
	original := []byte{0x00, 0x01, 0x02, 0xDE, 0xAD, 0xBE, 0xEF}
	if err := os.WriteFile(src, original, 0o644); err != nil {
		t.Fatalf("write src: %v", err)
	}

	if err := bridge.Publish(ctx, "personal-42", src, 128); err != nil {
		t.Fatalf("Publish: %v", err)
	}

	entry, _ := store.Get(ctx, circleai.LoraAdapterEntityType, "personal-42")
	if entry == nil {
		t.Fatal("adapter not pushed")
	}

	dst := filepath.Join(dir, "nested", "out.bin")
	snap, ok, err := circleai.TryWriteLoraAdapter(*entry, dst)
	if err != nil {
		t.Fatalf("TryWrite: %v", err)
	}
	if !ok {
		t.Fatal("TryWrite returned not-ok for a valid adapter entry")
	}
	if snap.AdapterID != "personal-42" || snap.StepCount != 128 {
		t.Errorf("snapshot fields: %+v", snap)
	}
	written, err := os.ReadFile(dst)
	if err != nil {
		t.Fatalf("read dst: %v", err)
	}
	if string(written) != string(original) {
		t.Errorf("bytes round-trip mismatch: got %v want %v", written, original)
	}

	// Wrong entity type → not ok.
	other := circleai.SyncableEntry{EntityType: "PersonaState", Payload: "{}"}
	if _, ok, _ := circleai.TryWriteLoraAdapter(other, dst); ok {
		t.Error("non-adapter entry should not decode as adapter")
	}
	// Tombstone → not ok.
	tomb := circleai.SyncableEntry{EntityType: circleai.LoraAdapterEntityType, IsTombstone: true}
	if _, ok, _ := circleai.TryWriteLoraAdapter(tomb, dst); ok {
		t.Error("tombstone adapter entry should not decode")
	}
}

func TestCompanionConversationSyncBridge_PublishTerminateDecode(t *testing.T) {
	ctx := context.Background()
	eng, store, done := wireBridgeEngine(t, "A")
	defer done()

	bridge, err := circleai.NewCompanionConversationSyncBridge(eng)
	if err != nil {
		t.Fatalf("bridge: %v", err)
	}

	started := time.Date(2026, 7, 8, 10, 0, 0, 0, time.UTC)
	delta := circleai.ConversationStateDelta{
		SessionID:      "sess-1",
		UserText:       "hey B",
		AssistantText:  "Hi!",
		IsTurnComplete: false,
		StartedAtUTC:   started,
		UpdatedAtUTC:   started.Add(2 * time.Second),
	}
	if err := bridge.Publish(ctx, delta); err != nil {
		t.Fatalf("Publish: %v", err)
	}

	entry, _ := store.Get(ctx, circleai.ConversationStateEntityType, "sess-1")
	if entry == nil {
		t.Fatal("delta not pushed")
	}
	decoded, ok := circleai.TryDecodeConversationDelta(*entry)
	if !ok {
		t.Fatal("decode failed")
	}
	if decoded.UserText != "hey B" || decoded.AssistantText != "Hi!" || decoded.IsTurnComplete {
		t.Errorf("decoded mismatch: %+v", decoded)
	}
	if !decoded.StartedAtUTC.Equal(started) {
		t.Errorf("started round-trip: got %v want %v", decoded.StartedAtUTC, started)
	}

	// Terminate → tombstone; decode must reject it.
	if err := bridge.Terminate(ctx, "sess-1"); err != nil {
		t.Fatalf("Terminate: %v", err)
	}
	tomb, _ := store.Get(ctx, circleai.ConversationStateEntityType, "sess-1")
	if tomb == nil || !tomb.IsTombstone {
		t.Fatalf("terminate should tombstone: %+v", tomb)
	}
	if _, ok := circleai.TryDecodeConversationDelta(*tomb); ok {
		t.Error("tombstone should not decode to a delta")
	}
}

func TestSyncBridges_ConstructorValidation(t *testing.T) {
	if _, err := circleai.NewPersonaStateSyncBridge(nil, nil); err == nil {
		t.Error("nil deps should error")
	}
	if _, err := circleai.NewLoraAdapterSyncBridge(nil); err == nil {
		t.Error("nil engine should error")
	}
	if _, err := circleai.NewCompanionConversationSyncBridge(nil); err == nil {
		t.Error("nil engine should error")
	}
}
