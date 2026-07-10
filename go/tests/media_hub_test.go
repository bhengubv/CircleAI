// media_hub_test.go
//
// Verifies the CircleAI.MediaHub port (media_hub.go): MediaItem/PlaybackPosition
// records, InMemoryHubMediaLibrary (BackendId, Get, title-ascending Search,
// validation), and InMemorySyncedPlayback (join, broadcast fan-out, subscribe/
// unsubscribe, unknown-session no-op, subscriber-error isolation, and the
// pre-subscribe-before-broadcast concurrency guarantees).

package circleai_test

import (
	"context"
	"errors"
	"sync"
	"testing"
	"time"

	circleai "github.com/bhengubv/CircleAI/go"
)

func hubItem(id, title string) circleai.MediaItem {
	return circleai.MediaItem{ItemId: id, Title: title, Kind: "audio", Duration: time.Minute, MimeType: "audio/mpeg"}
}

func TestInMemoryHubMediaLibrary_GetAndBackend(t *testing.T) {
	lib := circleai.NewInMemoryHubMediaLibrary()
	if lib.BackendId() != "in-memory" {
		t.Fatalf("backend = %q", lib.BackendId())
	}
	lib.Add(hubItem("i1", "Hello World"))

	item, ok, err := lib.Get(context.Background(), "i1")
	if err != nil || !ok || item.Title != "Hello World" {
		t.Fatalf("get = %+v ok=%v err=%v", item, ok, err)
	}
	if _, ok, _ := lib.Get(context.Background(), "nope"); ok {
		t.Fatalf("missing should be absent")
	}
	if _, _, err := lib.Get(context.Background(), "  "); err == nil {
		t.Fatalf("blank id must error")
	}
}

func TestInMemoryHubMediaLibrary_SearchTitleAscending(t *testing.T) {
	lib := circleai.NewInMemoryHubMediaLibrary()
	lib.Add(hubItem("i1", "Banana"))
	lib.Add(hubItem("i2", "apple"))
	lib.Add(hubItem("i3", "Cherry"))
	lib.Add(hubItem("i4", "date"))

	// "a" is a substring of Banana, apple, date -> ordered by Title asc (ci).
	hits, err := lib.Search(context.Background(), "a", 20)
	if err != nil {
		t.Fatalf("search: %v", err)
	}
	want := []string{"apple", "Banana", "date"}
	if len(hits) != len(want) {
		t.Fatalf("hits = %+v", hits)
	}
	for i := range want {
		if hits[i].Title != want[i] {
			t.Fatalf("order[%d] = %q, want %q", i, hits[i].Title, want[i])
		}
	}

	// topK cap keeps the first (smallest-title) entries.
	capped, _ := lib.Search(context.Background(), "a", 2)
	if len(capped) != 2 || capped[0].Title != "apple" || capped[1].Title != "Banana" {
		t.Fatalf("capped = %+v", capped)
	}

	if _, err := lib.Search(context.Background(), "a", 0); err == nil {
		t.Fatalf("topK=0 must error")
	}
}

func TestInMemorySyncedPlayback_JoinValidation(t *testing.T) {
	pb := circleai.NewInMemorySyncedPlayback()
	if pb.BackendId() != "in-memory" {
		t.Fatalf("backend = %q", pb.BackendId())
	}
	if err := pb.JoinSession(context.Background(), "s1", "u1"); err != nil {
		t.Fatalf("join: %v", err)
	}
	if err := pb.JoinSession(context.Background(), "", "u1"); err == nil {
		t.Fatalf("blank session must error")
	}
	if err := pb.JoinSession(context.Background(), "s1", ""); err == nil {
		t.Fatalf("blank user must error")
	}
}

func TestInMemorySyncedPlayback_BroadcastFanOut(t *testing.T) {
	pb := circleai.NewInMemorySyncedPlayback()
	ctx := context.Background()

	var mu sync.Mutex
	var got1, got2 []circleai.PlaybackPosition
	unsub1, err := pb.Subscribe("s1", func(p circleai.PlaybackPosition) error {
		mu.Lock()
		got1 = append(got1, p)
		mu.Unlock()
		return nil
	})
	if err != nil {
		t.Fatalf("subscribe1: %v", err)
	}
	_, _ = pb.Subscribe("s1", func(p circleai.PlaybackPosition) error {
		mu.Lock()
		got2 = append(got2, p)
		mu.Unlock()
		return nil
	})

	pos := circleai.PlaybackPosition{ItemId: "i1", Position: 5 * time.Second, AtUtc: time.Now().UTC()}
	if err := pb.BroadcastPosition(ctx, "s1", pos); err != nil {
		t.Fatalf("broadcast: %v", err)
	}
	mu.Lock()
	if len(got1) != 1 || len(got2) != 1 || got1[0].ItemId != "i1" {
		mu.Unlock()
		t.Fatalf("fan-out got1=%v got2=%v", got1, got2)
	}
	mu.Unlock()

	// Unsubscribe the first; only the second should now receive.
	unsub1()
	unsub1() // idempotent
	if err := pb.BroadcastPosition(ctx, "s1", pos); err != nil {
		t.Fatalf("broadcast2: %v", err)
	}
	mu.Lock()
	defer mu.Unlock()
	if len(got1) != 1 {
		t.Fatalf("unsubscribed handler still fired: %d", len(got1))
	}
	if len(got2) != 2 {
		t.Fatalf("remaining handler count = %d, want 2", len(got2))
	}
}

func TestInMemorySyncedPlayback_UnknownSessionNoOp(t *testing.T) {
	pb := circleai.NewInMemorySyncedPlayback()
	pos := circleai.PlaybackPosition{ItemId: "x", AtUtc: time.Now().UTC()}
	if err := pb.BroadcastPosition(context.Background(), "never-joined", pos); err != nil {
		t.Fatalf("unknown session broadcast should be a silent no-op, got %v", err)
	}
	if err := pb.BroadcastPosition(context.Background(), "", pos); err == nil {
		t.Fatalf("blank session must error")
	}
}

func TestInMemorySyncedPlayback_SubscriberErrorIsolated(t *testing.T) {
	pb := circleai.NewInMemorySyncedPlayback()
	ctx := context.Background()

	var mu sync.Mutex
	goodCalls := 0
	_, _ = pb.Subscribe("s1", func(circleai.PlaybackPosition) error {
		return errors.New("boom") // must not stop the next subscriber
	})
	_, _ = pb.Subscribe("s1", func(circleai.PlaybackPosition) error {
		mu.Lock()
		goodCalls++
		mu.Unlock()
		return nil
	})
	// A panicking subscriber must also be isolated.
	_, _ = pb.Subscribe("s1", func(circleai.PlaybackPosition) error {
		panic("panic-subscriber")
	})

	pos := circleai.PlaybackPosition{ItemId: "i1", AtUtc: time.Now().UTC()}
	if err := pb.BroadcastPosition(ctx, "s1", pos); err != nil {
		t.Fatalf("broadcast: %v", err)
	}
	mu.Lock()
	defer mu.Unlock()
	if goodCalls != 1 {
		t.Fatalf("good subscriber calls = %d, want 1 (error/panic peers must be isolated)", goodCalls)
	}
}

func TestInMemorySyncedPlayback_SubscribeValidation(t *testing.T) {
	pb := circleai.NewInMemorySyncedPlayback()
	if _, err := pb.Subscribe("", func(circleai.PlaybackPosition) error { return nil }); err == nil {
		t.Fatalf("blank session must error")
	}
	if _, err := pb.Subscribe("s1", nil); err == nil {
		t.Fatalf("nil handler must error")
	}
}

// TestInMemorySyncedPlayback_SubscribeBeforeBroadcast asserts a subscriber
// attached before the very first broadcast receives it (no lost-message race).
func TestInMemorySyncedPlayback_SubscribeBeforeBroadcast(t *testing.T) {
	pb := circleai.NewInMemorySyncedPlayback()
	ctx := context.Background()

	done := make(chan circleai.PlaybackPosition, 1)
	if _, err := pb.Subscribe("live", func(p circleai.PlaybackPosition) error {
		done <- p
		return nil
	}); err != nil {
		t.Fatalf("subscribe: %v", err)
	}
	pos := circleai.PlaybackPosition{ItemId: "i9", Position: time.Second, AtUtc: time.Now().UTC()}
	if err := pb.BroadcastPosition(ctx, "live", pos); err != nil {
		t.Fatalf("broadcast: %v", err)
	}
	select {
	case got := <-done:
		if got.ItemId != "i9" {
			t.Fatalf("got %+v", got)
		}
	case <-time.After(time.Second):
		t.Fatalf("subscriber attached before broadcast did not receive")
	}
}

func TestMediaHub_InterfacesSatisfied(t *testing.T) {
	var _ circleai.HubMediaLibrary = circleai.NewInMemoryHubMediaLibrary()
	var _ circleai.SyncedPlayback = circleai.NewInMemorySyncedPlayback()
}
