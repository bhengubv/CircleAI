// sync_channel.go
//
// Ports CircleAI.Memory.Sync.ICompanionStateChannel (ICompanionStateChannel.cs)
// and CircleAI.Memory.Sync.InProcessCompanionStateChannel +
// InProcessSyncHub (InProcessCompanionStateChannel.cs).
//
// Transport seam for the sync engine. InProcessCompanionStateChannel is a
// loopback channel that wires N nodes in the same process so two
// CompanionStateSyncEngine instances can converge in tests without any real
// transport. Every channel belongs to an InProcessSyncHub; Send broadcasts to
// every peer channel on the hub EXCEPT the sender's own.

package circleai

import (
	"context"
	"errors"
	"sync"
)

// SyncEnvelopeHandler handles an inbound envelope. Returning an error aborts
// the current delivery loop.
type SyncEnvelopeHandler func(ctx context.Context, envelope SyncEnvelope) error

// ICompanionStateChannel is a transport that moves SyncEnvelope messages
// between peers.
type ICompanionStateChannel interface {
	// LocalNodeID is the stable identifier of THIS node on this channel.
	// Stamped onto every envelope as SyncEnvelope.FromNodeID.
	LocalNodeID() string

	// Send sends an envelope to peers. For v0.1 every channel implements
	// broadcast semantics.
	Send(ctx context.Context, envelope SyncEnvelope) error

	// Subscribe registers a handler for inbound envelopes. The returned func
	// unsubscribes when called.
	Subscribe(handler SyncEnvelopeHandler) (unsubscribe func(), err error)
}

// InProcessSyncHub routes envelopes between every InProcessCompanionStateChannel
// that has joined the hub. One hub per simulated "mesh".
type InProcessSyncHub struct {
	mu       sync.Mutex
	channels map[string]*InProcessCompanionStateChannel
}

// NewInProcessSyncHub creates an empty hub.
func NewInProcessSyncHub() *InProcessSyncHub {
	return &InProcessSyncHub{channels: make(map[string]*InProcessCompanionStateChannel)}
}

func (h *InProcessSyncHub) join(c *InProcessCompanionStateChannel) {
	h.mu.Lock()
	defer h.mu.Unlock()
	h.channels[c.localNodeID] = c
}

func (h *InProcessSyncHub) leave(nodeID string) {
	h.mu.Lock()
	defer h.mu.Unlock()
	delete(h.channels, nodeID)
}

func (h *InProcessSyncHub) broadcast(ctx context.Context, envelope SyncEnvelope, senderNodeID string) error {
	h.mu.Lock()
	peers := make([]*InProcessCompanionStateChannel, 0, len(h.channels))
	for _, c := range h.channels {
		if c.localNodeID != senderNodeID {
			peers = append(peers, c)
		}
	}
	h.mu.Unlock()

	for _, peer := range peers {
		if err := ctx.Err(); err != nil {
			return err
		}
		if err := peer.deliver(ctx, envelope); err != nil {
			return err
		}
	}
	return nil
}

// ConnectedNodeIDs returns the ids of the channels currently on this hub.
func (h *InProcessSyncHub) ConnectedNodeIDs() []string {
	h.mu.Lock()
	defer h.mu.Unlock()
	ids := make([]string, 0, len(h.channels))
	for id := range h.channels {
		ids = append(ids, id)
	}
	return ids
}

// InProcessCompanionStateChannel is an in-process ICompanionStateChannel that
// broadcasts via an InProcessSyncHub.
type InProcessCompanionStateChannel struct {
	hub         *InProcessSyncHub
	localNodeID string

	mu       sync.Mutex
	handlers []*registeredHandler
	disposed bool
}

type registeredHandler struct {
	fn SyncEnvelopeHandler
}

// NewInProcessCompanionStateChannel joins hub as localNodeID. Returns an error
// when hub is nil or localNodeID is blank.
func NewInProcessCompanionStateChannel(hub *InProcessSyncHub, localNodeID string) (*InProcessCompanionStateChannel, error) {
	if hub == nil {
		return nil, errors.New("hub required")
	}
	if isBlank(localNodeID) {
		return nil, errors.New("localNodeId required")
	}
	c := &InProcessCompanionStateChannel{hub: hub, localNodeID: localNodeID}
	hub.join(c)
	return c, nil
}

// LocalNodeID returns this channel's node id.
func (c *InProcessCompanionStateChannel) LocalNodeID() string { return c.localNodeID }

// Send broadcasts envelope to every peer on the hub except this channel.
func (c *InProcessCompanionStateChannel) Send(ctx context.Context, envelope SyncEnvelope) error {
	c.mu.Lock()
	disposed := c.disposed
	c.mu.Unlock()
	if disposed {
		return errors.New("channel disposed")
	}
	return c.hub.broadcast(ctx, envelope, c.localNodeID)
}

// Subscribe registers handler and returns an unsubscribe func.
func (c *InProcessCompanionStateChannel) Subscribe(handler SyncEnvelopeHandler) (func(), error) {
	if handler == nil {
		return nil, errors.New("handler required")
	}
	c.mu.Lock()
	if c.disposed {
		c.mu.Unlock()
		return nil, errors.New("channel disposed")
	}
	rh := &registeredHandler{fn: handler}
	c.handlers = append(c.handlers, rh)
	c.mu.Unlock()

	return func() { c.removeHandler(rh) }, nil
}

func (c *InProcessCompanionStateChannel) removeHandler(rh *registeredHandler) {
	c.mu.Lock()
	defer c.mu.Unlock()
	for i, h := range c.handlers {
		if h == rh {
			c.handlers = append(c.handlers[:i], c.handlers[i+1:]...)
			return
		}
	}
}

func (c *InProcessCompanionStateChannel) deliver(ctx context.Context, envelope SyncEnvelope) error {
	c.mu.Lock()
	snapshot := make([]*registeredHandler, len(c.handlers))
	copy(snapshot, c.handlers)
	c.mu.Unlock()

	for _, h := range snapshot {
		if err := ctx.Err(); err != nil {
			return err
		}
		if err := h.fn(ctx, envelope); err != nil {
			return err
		}
	}
	return nil
}

// Close unregisters from the hub and drops all handlers. Idempotent.
func (c *InProcessCompanionStateChannel) Close() {
	c.mu.Lock()
	if c.disposed {
		c.mu.Unlock()
		return
	}
	c.disposed = true
	c.handlers = nil
	c.mu.Unlock()
	c.hub.leave(c.localNodeID)
}

func isBlank(s string) bool {
	for _, r := range s {
		if r != ' ' && r != '\t' && r != '\n' && r != '\r' {
			return false
		}
	}
	return true
}

var _ ICompanionStateChannel = (*InProcessCompanionStateChannel)(nil)
