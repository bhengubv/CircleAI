// security_directive_publisher_test.go
//
// Verifies DirectivePublisher (ported from DirectivePublisher.cs):
//   - Publish fans out to all current subscribers.
//   - Unsubscribe removes a consumer and is idempotent.
//   - SubscriberCount tracks the live set.
//   - Callbacks fire OUTSIDE the lock: a consumer that (un)subscribes from within
//     OnDirective does not deadlock the publisher.

package circleai_test

import (
	"sync"
	"testing"

	circleai "github.com/bhengubv/CircleAI/go"
)

type recordingConsumer struct {
	mu        sync.Mutex
	received  []circleai.PeerDirective
	onReceive func(circleai.PeerDirective)
}

func (c *recordingConsumer) OnDirective(d circleai.PeerDirective) {
	c.mu.Lock()
	c.received = append(c.received, d)
	cb := c.onReceive
	c.mu.Unlock()
	if cb != nil {
		cb(d)
	}
}

func (c *recordingConsumer) count() int {
	c.mu.Lock()
	defer c.mu.Unlock()
	return len(c.received)
}

func directive() circleai.PeerDirective {
	return circleai.PeerDirective{
		Kind:         circleai.PeerDirectiveKindElevateMonitoring,
		TargetNodeID: "n1",
		TrustScore:   0.6,
		ThreatLevel:  circleai.PeerThreatLevelMedium,
		Reason:       "test",
	}
}

func TestPublisher_FansOutToAll(t *testing.T) {
	p := circleai.NewDirectivePublisher()
	a := &recordingConsumer{}
	b := &recordingConsumer{}
	p.Subscribe(a)
	p.Subscribe(b)
	if p.SubscriberCount() != 2 {
		t.Fatalf("subscriber count: got %d", p.SubscriberCount())
	}
	p.Publish(directive())
	if a.count() != 1 || b.count() != 1 {
		t.Errorf("both consumers should receive: a=%d b=%d", a.count(), b.count())
	}
}

func TestPublisher_UnsubscribeStopsDelivery(t *testing.T) {
	p := circleai.NewDirectivePublisher()
	a := &recordingConsumer{}
	unsub := p.Subscribe(a)
	p.Publish(directive())
	unsub()
	if p.SubscriberCount() != 0 {
		t.Fatalf("count after unsubscribe: got %d", p.SubscriberCount())
	}
	p.Publish(directive())
	if a.count() != 1 {
		t.Errorf("unsubscribed consumer should not receive second: got %d", a.count())
	}
}

func TestPublisher_UnsubscribeIdempotent(t *testing.T) {
	p := circleai.NewDirectivePublisher()
	a := &recordingConsumer{}
	b := &recordingConsumer{}
	unsub := p.Subscribe(a)
	p.Subscribe(b)
	unsub()
	unsub() // second call must be a no-op, not remove b
	if p.SubscriberCount() != 1 {
		t.Errorf("idempotent unsubscribe: got count %d, want 1", p.SubscriberCount())
	}
}

func TestPublisher_CallbackMaySubscribeWithoutDeadlock(t *testing.T) {
	p := circleai.NewDirectivePublisher()
	late := &recordingConsumer{}
	first := &recordingConsumer{
		onReceive: func(circleai.PeerDirective) {
			// Subscribing from inside a callback would deadlock if Publish held
			// the lock across the callback. It must not.
			p.Subscribe(late)
		},
	}
	p.Subscribe(first)
	p.Publish(directive()) // must return without hanging
	if p.SubscriberCount() != 2 {
		t.Errorf("late subscriber not added: count %d", p.SubscriberCount())
	}
}

func TestPublisher_ZeroValueUsable(t *testing.T) {
	var p circleai.DirectivePublisher
	a := &recordingConsumer{}
	p.Subscribe(a)
	p.Publish(directive())
	if a.count() != 1 {
		t.Errorf("zero-value publisher should work: got %d", a.count())
	}
}
