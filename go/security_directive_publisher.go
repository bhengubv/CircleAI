// security_directive_publisher.go
//
// Ports CircleAI.Security.DirectivePublisher (DirectivePublisher.cs).
//
// Fan-out publisher for PeerDirectives. Keeps a list of IPeerDirectiveConsumer
// subscriptions and fans every published directive out to all current
// subscribers. Concurrent subscribe, unsubscribe, and publish are all
// thread-safe.
//
// Concurrency: Publish snapshots the subscriber list UNDER the lock and fires
// callbacks OUTSIDE it — a consumer callback that (un)subscribes cannot
// self-deadlock the publisher, and a slow consumer cannot block subscription
// churn.

package circleai

import "sync"

// DirectivePublisher manages IPeerDirectiveConsumer subscriptions and fans
// published PeerDirective instances out to all subscribers. Ports
// DirectivePublisher. The zero value is ready to use.
type DirectivePublisher struct {
	mu        sync.Mutex
	consumers []*directiveSubscription
}

// NewDirectivePublisher returns an empty publisher. (The zero value is also
// usable; this constructor exists for symmetry with the rest of the package.)
func NewDirectivePublisher() *DirectivePublisher {
	return &DirectivePublisher{}
}

// directiveSubscription wraps one consumer so identical consumer values can be
// unsubscribed by pointer identity (mirrors the C# SubscriptionHandle capturing
// the exact IPeerDirectiveConsumer instance).
type directiveSubscription struct {
	consumer IPeerDirectiveConsumer
}

// Subscribe registers consumer to receive directives and returns an unsubscribe
// func. Calling the func more than once is a no-op (idempotent disposal), and
// the func is safe to call concurrently. Ports DirectivePublisher.Subscribe.
// Panics if consumer is nil, mirroring the C# ArgumentNullException.
func (p *DirectivePublisher) Subscribe(consumer IPeerDirectiveConsumer) (unsubscribe func()) {
	if consumer == nil {
		panic("consumer must not be nil")
	}
	sub := &directiveSubscription{consumer: consumer}

	p.mu.Lock()
	p.consumers = append(p.consumers, sub)
	p.mu.Unlock()

	var once sync.Once
	return func() {
		once.Do(func() { p.unsubscribe(sub) })
	}
}

// Publish sends directive to all current subscribers. A snapshot is taken under
// the lock; callbacks fire outside it. Ports DirectivePublisher.Publish.
func (p *DirectivePublisher) Publish(directive PeerDirective) {
	p.mu.Lock()
	snapshot := make([]*directiveSubscription, len(p.consumers))
	copy(snapshot, p.consumers)
	p.mu.Unlock()

	for _, sub := range snapshot {
		sub.consumer.OnDirective(directive)
	}
}

// SubscriberCount returns the number of currently active subscribers. Useful in
// tests. Ports DirectivePublisher.SubscriberCount.
func (p *DirectivePublisher) SubscriberCount() int {
	p.mu.Lock()
	defer p.mu.Unlock()
	return len(p.consumers)
}

func (p *DirectivePublisher) unsubscribe(sub *directiveSubscription) {
	p.mu.Lock()
	defer p.mu.Unlock()
	for i, s := range p.consumers {
		if s == sub {
			p.consumers = append(p.consumers[:i], p.consumers[i+1:]...)
			return
		}
	}
}
