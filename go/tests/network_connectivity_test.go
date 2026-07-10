// network_connectivity_test.go
//
// Verifies network_connectivity.go InMemoryConnectivityMonitor:
//   - CurrentState / GetSnapshot reflect the latest Publish
//   - Watch is FAN-OUT: every watcher sees every change
//   - subscribe-before-publish: a Publish immediately after Watch is not lost
//   - Watch on a closed monitor yields a closed stream
//   - ctx cancel deregisters the watcher; Close completes all streams

package circleai_test

import (
	"context"
	"testing"
	"time"

	circleai "github.com/bhengubv/CircleAI/go"
)

func recvCtx(t *testing.T, ch <-chan circleai.NetworkContext) circleai.NetworkContext {
	t.Helper()
	select {
	case c, ok := <-ch:
		if !ok {
			t.Fatal("connectivity stream closed unexpectedly")
		}
		return c
	case <-time.After(2 * time.Second):
		t.Fatal("timed out waiting for a NetworkContext")
		return circleai.NetworkContext{}
	}
}

func onlineCtx(state circleai.ConnectivityState) circleai.NetworkContext {
	c := circleai.NewNetworkContextOffline()
	c.State = state
	return c
}

func TestConnectivity_CurrentStateAndSnapshot(t *testing.T) {
	m := circleai.NewInMemoryConnectivityMonitor(circleai.NewNetworkContextOffline())
	if m.CurrentState() != circleai.ConnectivityStateOffline {
		t.Errorf("initial state got %v want Offline", m.CurrentState())
	}
	m.Publish(onlineCtx(circleai.ConnectivityStateOnline))
	if m.CurrentState() != circleai.ConnectivityStateOnline {
		t.Errorf("after publish state got %v want Online", m.CurrentState())
	}
	if m.GetSnapshot().State != circleai.ConnectivityStateOnline {
		t.Errorf("snapshot state got %v want Online", m.GetSnapshot().State)
	}
}

func TestConnectivity_SubscribeBeforePublishNotLost(t *testing.T) {
	m := circleai.NewInMemoryConnectivityMonitor(circleai.NewNetworkContextOffline())
	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()

	// Watch registers synchronously; the Publish that follows must be seen.
	stream := m.Watch(ctx)
	m.Publish(onlineCtx(circleai.ConnectivityStateMeshOnly))

	got := recvCtx(t, stream)
	if got.State != circleai.ConnectivityStateMeshOnly {
		t.Errorf("watcher got %v want MeshOnly", got.State)
	}
}

func TestConnectivity_FanOutAllWatchersSeeEveryChange(t *testing.T) {
	m := circleai.NewInMemoryConnectivityMonitor(circleai.NewNetworkContextOffline())
	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()

	w1 := m.Watch(ctx)
	w2 := m.Watch(ctx)
	w3 := m.Watch(ctx)
	if m.SubscriberCount() != 3 {
		t.Fatalf("SubscriberCount got %d want 3", m.SubscriberCount())
	}

	states := []circleai.ConnectivityState{
		circleai.ConnectivityStateOnline,
		circleai.ConnectivityStateLocalOnly,
		circleai.ConnectivityStateMeshOnly,
	}
	for _, s := range states {
		m.Publish(onlineCtx(s))
	}

	for _, w := range []<-chan circleai.NetworkContext{w1, w2, w3} {
		for i, s := range states {
			got := recvCtx(t, w)
			if got.State != s {
				t.Fatalf("watcher change %d got %v want %v", i, got.State, s)
			}
		}
	}
}

func TestConnectivity_WatchOnClosedMonitor(t *testing.T) {
	m := circleai.NewInMemoryConnectivityMonitor(circleai.NewNetworkContextOffline())
	m.Close()
	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()
	stream := m.Watch(ctx)
	select {
	case _, ok := <-stream:
		if ok {
			t.Error("closed monitor should yield a closed Watch stream")
		}
	case <-time.After(2 * time.Second):
		t.Error("Watch stream on closed monitor did not close")
	}
	// Publish after close is a no-op (state unchanged).
	m.Publish(onlineCtx(circleai.ConnectivityStateOnline))
	if m.GetSnapshot().State != circleai.ConnectivityStateOffline {
		t.Error("Publish after Close should be a no-op")
	}
}

func TestConnectivity_CloseCompletesWatchers(t *testing.T) {
	m := circleai.NewInMemoryConnectivityMonitor(circleai.NewNetworkContextOffline())
	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()
	stream := m.Watch(ctx)
	m.Close()
	select {
	case _, ok := <-stream:
		if ok {
			t.Error("Close should complete active watchers")
		}
	case <-time.After(2 * time.Second):
		t.Error("watcher stream did not complete after Close")
	}
}

func TestConnectivity_CtxCancelDeregisters(t *testing.T) {
	m := circleai.NewInMemoryConnectivityMonitor(circleai.NewNetworkContextOffline())
	ctx, cancel := context.WithCancel(context.Background())
	_ = m.Watch(ctx)
	if m.SubscriberCount() != 1 {
		t.Fatalf("expected 1 subscriber, got %d", m.SubscriberCount())
	}
	cancel()
	// Give the deregistration goroutine a moment.
	deadline := time.Now().Add(2 * time.Second)
	for time.Now().Before(deadline) {
		if m.SubscriberCount() == 0 {
			return
		}
		time.Sleep(10 * time.Millisecond)
	}
	t.Errorf("ctx cancel should deregister the watcher, count=%d", m.SubscriberCount())
}
