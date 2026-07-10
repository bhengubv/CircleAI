// security_unbounded_channel.go
//
// Ports the semantics of System.Threading.Channels.Channel.CreateUnbounded<T>
// as used by DefaultSecurityWatchdog (ISecurityWatchdog.cs) and
// NodeTrustRegistry (NodeTrustRegistry.cs).
//
// Why a bespoke primitive rather than a plain Go channel:
//   - The C# reference uses an UNBOUNDED channel: writes NEVER block and are
//     retained until read. A message written before any reader attaches is
//     buffered, not dropped. A fixed-size Go `chan` would either block the
//     writer when full or drop with select/default — neither matches.
//   - Multiple ReadAllAsync consumers on one Channel are COMPETING consumers:
//     each written item is delivered to exactly ONE reader, never fanned out.
//     NodeTrustRegistry declares SingleReader=false and PeerIntelligenceService
//     reads that same reader, so this is the contract to preserve.
//   - Readers complete when the channel is Completed (closed) and drained, or
//     when their context is cancelled — matching ReadAllAsync(ct).
//
// Implementation: an unbounded FIFO guarded by a mutex + sync.Cond. Write
// appends and signals; a per-reader goroutine (started by ReadAll) drains the
// shared queue into an out-channel, so competing-consumer semantics hold.

package circleai

import (
	"context"
	"sync"
)

// unboundedChannel is a multi-writer / multi-reader unbounded FIFO with
// completion semantics mirroring a .NET unbounded Channel<T>. Items are
// delivered to competing readers (one item → one reader). The zero value is
// not usable — construct with newUnboundedChannel.
type unboundedChannel[T any] struct {
	mu        sync.Mutex
	cond      *sync.Cond
	queue     []T
	completed bool
}

// newUnboundedChannel constructs an empty, open unbounded channel.
func newUnboundedChannel[T any]() *unboundedChannel[T] {
	c := &unboundedChannel[T]{}
	c.cond = sync.NewCond(&c.mu)
	return c
}

// Write enqueues item and wakes one waiting reader. It never blocks and never
// drops. Writes after Complete are ignored (mirrors TryWrite returning false on
// a completed channel — the item is simply not accepted).
func (c *unboundedChannel[T]) Write(item T) bool {
	c.mu.Lock()
	if c.completed {
		c.mu.Unlock()
		return false
	}
	c.queue = append(c.queue, item)
	c.mu.Unlock()
	// Wake a single reader; competing consumers means one item → one reader.
	c.cond.Signal()
	return true
}

// Complete marks the channel completed. Readers drain any remaining buffered
// items and then observe completion. Idempotent.
func (c *unboundedChannel[T]) Complete() {
	c.mu.Lock()
	if c.completed {
		c.mu.Unlock()
		return
	}
	c.completed = true
	c.mu.Unlock()
	// Wake every reader so they can drain-then-exit.
	c.cond.Broadcast()
}

// ReadAll returns a receive-only channel that yields buffered and future items
// in FIFO order until the channel is completed-and-drained or ctx is cancelled,
// then closes. Each call starts one competing consumer; an item read by one
// ReadAll stream is not seen by any other.
//
// Mirrors Reader.ReadAllAsync(ct): completes on cancellation or channel
// completion. The returned channel is unbuffered so back-pressure from a slow
// consumer does not force-buffer inside the bridge goroutine.
func (c *unboundedChannel[T]) ReadAll(ctx context.Context) <-chan T {
	out := make(chan T)

	// Watch ctx so a blocked cond.Wait is woken on cancellation. We cannot
	// select on a sync.Cond, so a cancellation goroutine broadcasts to unblock
	// waiting readers; each reader then re-checks ctx.Err().
	stop := make(chan struct{})
	if ctx.Done() != nil {
		go func() {
			select {
			case <-ctx.Done():
				c.cond.Broadcast()
			case <-stop:
			}
		}()
	}

	go func() {
		defer close(out)
		defer close(stop)
		for {
			if ctx.Err() != nil {
				return
			}

			c.mu.Lock()
			for len(c.queue) == 0 && !c.completed && ctx.Err() == nil {
				c.cond.Wait()
			}
			// Drain condition re-checked after wake.
			if ctx.Err() != nil {
				c.mu.Unlock()
				return
			}
			if len(c.queue) == 0 {
				// No items and (completed or cancelled) → finished.
				c.mu.Unlock()
				return
			}
			item := c.queue[0]
			// Advance the head without leaking the backing array indefinitely.
			c.queue = c.queue[1:]
			if len(c.queue) == 0 {
				c.queue = nil
			}
			c.mu.Unlock()

			// Deliver outside the lock so a slow consumer never blocks writers
			// or other readers. Honour cancellation while delivering.
			select {
			case out <- item:
			case <-ctx.Done():
				return
			}
		}
	}()

	return out
}
