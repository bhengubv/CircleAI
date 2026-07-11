// games_runtime_test.go
//
// Verifies the CircleAI.Games port (games_runtime.go): scene-graph add/remove/
// snapshot, input-map fan-out + unsubscribe, timer game-loop tick delivery, and
// the null implementations.

package circleai_test

import (
	"context"
	"testing"
	"time"

	circleai "github.com/bhengubv/CircleAI/go"
)

func TestGames_SceneGraph(t *testing.T) {
	g := circleai.NewInMemorySceneGraph()
	ctx := context.Background()
	if g.BackendId() != "in-memory" {
		t.Fatalf("backend id = %q", g.BackendId())
	}
	if err := g.Add(ctx, circleai.SceneNode{NodeId: "n1", Kind: "sprite", X: 1, Y: 2, Z: 3}); err != nil {
		t.Fatalf("add: %v", err)
	}
	if err := g.Add(ctx, circleai.SceneNode{NodeId: "n2", Kind: "light"}); err != nil {
		t.Fatalf("add: %v", err)
	}
	if err := g.Add(ctx, circleai.SceneNode{NodeId: "  "}); err == nil {
		t.Fatalf("blank NodeId must error")
	}
	snap, _ := g.Snapshot(ctx)
	if len(snap) != 2 {
		t.Fatalf("snapshot size = %d, want 2", len(snap))
	}
	if err := g.Remove(ctx, "n1"); err != nil {
		t.Fatalf("remove: %v", err)
	}
	snap, _ = g.Snapshot(ctx)
	if len(snap) != 1 || snap[0].NodeId != "n2" {
		t.Fatalf("post-remove snapshot failed: %+v", snap)
	}
}

func TestGames_InputMapFanOut(t *testing.T) {
	m := circleai.NewInMemoryInputMap()
	got := make(chan circleai.InputEvent, 8)
	// Subscribe synchronously before raising.
	unsub := m.Subscribe(func(ev circleai.InputEvent) { got <- ev })
	m.Raise(circleai.InputEvent{Action: "jump"})
	select {
	case ev := <-got:
		if ev.Action != "jump" {
			t.Fatalf("event action = %q", ev.Action)
		}
	case <-time.After(2 * time.Second):
		t.Fatal("timed out waiting for input event")
	}
	// After unsubscribe, no more events are delivered.
	unsub()
	m.Raise(circleai.InputEvent{Action: "fire"})
	select {
	case ev := <-got:
		t.Fatalf("unexpected event after unsubscribe: %+v", ev)
	case <-time.After(150 * time.Millisecond):
	}
}

func TestGames_TimerLoop(t *testing.T) {
	l := circleai.NewTimerGameLoop()
	ticks := make(chan circleai.GameTick, 16)
	// Subscribe synchronously before starting the loop.
	l.Subscribe(func(tk circleai.GameTick) { ticks <- tk })
	ctx := context.Background()
	if err := l.StartAsync(ctx, 100); err != nil {
		t.Fatalf("start: %v", err)
	}
	if err := l.StartAsync(ctx, 100); err == nil {
		t.Fatalf("double start must error")
	}
	select {
	case tk := <-ticks:
		if tk.Frame < 1 {
			t.Fatalf("first frame = %d, want >= 1", tk.Frame)
		}
	case <-time.After(2 * time.Second):
		t.Fatal("timed out waiting for a tick")
	}
	if err := l.StopAsync(ctx); err != nil {
		t.Fatalf("stop: %v", err)
	}
	_ = l.Close()
}

func TestGames_NullImpls(t *testing.T) {
	ctx := context.Background()
	var loop circleai.GameLoop = circleai.NullGameLoop{}
	if loop.BackendId() != "null" {
		t.Fatalf("null loop backend id = %q", loop.BackendId())
	}
	if err := loop.StartAsync(ctx, 60); err != nil {
		t.Fatalf("null start: %v", err)
	}
	unsub := loop.Subscribe(func(circleai.GameTick) {})
	unsub()

	var im circleai.InputMap = circleai.NullInputMap{}
	im.Subscribe(func(circleai.InputEvent) {})()

	var sg circleai.SceneGraph = circleai.NullSceneGraph{}
	if err := sg.Add(ctx, circleai.SceneNode{NodeId: "x"}); err != nil {
		t.Fatalf("null scene add: %v", err)
	}
	if snap, _ := sg.Snapshot(ctx); len(snap) != 0 {
		t.Fatalf("null scene snapshot must be empty: %+v", snap)
	}
}
