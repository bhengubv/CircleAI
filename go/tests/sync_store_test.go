// sync_store_test.go
//
// Verifies InMemorySyncableEntryStore (ported from InMemorySyncableEntryStore.cs):
//   - Apply follows the convergence rules for every fixture case.
//   - Apply reports true only when local state actually changed.
//   - Get returns the current entry (including tombstones) or nil.
//   - GetSince returns strictly-newer entries ordered ascending by Version.
//   - GetStateVector returns the per-type high-watermark, ordered by type.

package circleai_test

import (
	"context"
	"testing"
	"time"

	circleai "github.com/bhengubv/CircleAI/go"
)

func mkSyncEntry(ty, id string, version int64, tombstone bool, hash string) circleai.SyncableEntry {
	return circleai.SyncableEntry{
		EntityType:   ty,
		EntityID:     id,
		Version:      version,
		IsTombstone:  tombstone,
		ContentHash:  hash,
		Payload:      "p",
		SourceNodeID: "node",
		AuthoredAt:   time.Unix(0, 0).UTC(),
	}
}

type applyRulesFixture struct {
	Cases []struct {
		ID       string `json:"id"`
		Existing struct {
			Version     int64  `json:"version"`
			IsTombstone bool   `json:"isTombstone"`
			ContentHash string `json:"contentHash"`
		} `json:"existing"`
		Incoming struct {
			Version     int64  `json:"version"`
			IsTombstone bool   `json:"isTombstone"`
			ContentHash string `json:"contentHash"`
		} `json:"incoming"`
		Applied bool `json:"applied"`
	} `json:"cases"`
}

func TestSyncStore_ApplyRules_Fixture(t *testing.T) {
	var fix applyRulesFixture
	readLocalFixture(t, "sync_apply_rules.json", &fix)
	if len(fix.Cases) == 0 {
		t.Fatal("no apply-rule cases")
	}
	ctx := context.Background()
	for _, c := range fix.Cases {
		c := c
		t.Run(c.ID, func(t *testing.T) {
			store := circleai.NewInMemorySyncableEntryStore()
			existing := mkSyncEntry("T", "1", c.Existing.Version, c.Existing.IsTombstone, c.Existing.ContentHash)
			if applied, err := store.Apply(ctx, existing); err != nil || !applied {
				t.Fatalf("seed apply: applied=%v err=%v", applied, err)
			}
			incoming := mkSyncEntry("T", "1", c.Incoming.Version, c.Incoming.IsTombstone, c.Incoming.ContentHash)
			applied, err := store.Apply(ctx, incoming)
			if err != nil {
				t.Fatalf("apply: %v", err)
			}
			if applied != c.Applied {
				t.Errorf("applied: got %v want %v", applied, c.Applied)
			}
			// When applied, the stored entry must be the incoming one.
			got, _ := store.Get(ctx, "T", "1")
			if got == nil {
				t.Fatal("entry vanished")
			}
			wantWinnerHash := c.Existing.ContentHash
			if c.Applied {
				wantWinnerHash = c.Incoming.ContentHash
			}
			if got.ContentHash != wantWinnerHash {
				t.Errorf("winner hash: got %q want %q", got.ContentHash, wantWinnerHash)
			}
		})
	}
}

func TestSyncStore_FirstApplyAlwaysWins(t *testing.T) {
	ctx := context.Background()
	store := circleai.NewInMemorySyncableEntryStore()
	applied, err := store.Apply(ctx, mkSyncEntry("T", "x", 1, false, "aa"))
	if err != nil || !applied {
		t.Fatalf("first apply should win: applied=%v err=%v", applied, err)
	}
}

func TestSyncStore_Get_ReturnsTombstoneAndNil(t *testing.T) {
	ctx := context.Background()
	store := circleai.NewInMemorySyncableEntryStore()
	if got, _ := store.Get(ctx, "T", "missing"); got != nil {
		t.Error("missing entry should be nil")
	}
	_, _ = store.Apply(ctx, mkSyncEntry("T", "d", 5, true, "aa"))
	got, _ := store.Get(ctx, "T", "d")
	if got == nil || !got.IsTombstone {
		t.Errorf("tombstone should be returned: %+v", got)
	}
}

func TestSyncStore_GetSince_OrdersAscending(t *testing.T) {
	ctx := context.Background()
	store := circleai.NewInMemorySyncableEntryStore()
	_, _ = store.Apply(ctx, mkSyncEntry("T", "a", 30, false, "a"))
	_, _ = store.Apply(ctx, mkSyncEntry("T", "b", 10, false, "b"))
	_, _ = store.Apply(ctx, mkSyncEntry("T", "c", 20, false, "c"))
	_, _ = store.Apply(ctx, mkSyncEntry("U", "d", 99, false, "d")) // different type

	got, _ := store.GetSince(ctx, "T", 10) // strictly greater than 10 → 20, 30
	if len(got) != 2 {
		t.Fatalf("expected 2 entries, got %d", len(got))
	}
	if got[0].Version != 20 || got[1].Version != 30 {
		t.Errorf("order: got %d,%d want 20,30", got[0].Version, got[1].Version)
	}
}

func TestSyncStore_GetStateVector_HighWatermarkPerType(t *testing.T) {
	ctx := context.Background()
	store := circleai.NewInMemorySyncableEntryStore()
	_, _ = store.Apply(ctx, mkSyncEntry("Beta", "1", 5, false, "a"))
	_, _ = store.Apply(ctx, mkSyncEntry("Beta", "2", 9, false, "b"))
	_, _ = store.Apply(ctx, mkSyncEntry("Alpha", "1", 3, false, "c"))

	vec, _ := store.GetStateVector(ctx)
	if len(vec) != 2 {
		t.Fatalf("expected 2 types, got %d", len(vec))
	}
	// Ordered by EntityType (ordinal): Alpha before Beta.
	if vec[0].EntityType != "Alpha" || vec[0].MaxKnownVersion != 3 {
		t.Errorf("vec[0]: got %+v", vec[0])
	}
	if vec[1].EntityType != "Beta" || vec[1].MaxKnownVersion != 9 {
		t.Errorf("vec[1] high-watermark: got %+v want Beta=9", vec[1])
	}
}
