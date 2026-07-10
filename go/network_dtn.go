// network_dtn.go
//
// Ports CircleAI.Networking.Dtn:
//   DtnBundle.cs           -> DtnBundle
//   DtnTransportCommons.cs -> DtnPriority, DtnCustodyRecord,
//                             InMemoryDtnBundleStore
//   DtnSyncChannel.cs      -> DtnSyncChannel (ISyncChannel)
//
// Delay-tolerant networking: a bundle is a self-contained delivery unit with a
// 72h TTL and optional custody transfer. DtnSyncChannel wraps a pushed SyncDelta
// into a bundle, tries the first available injected INetworkTransport, and (per
// the porting rules — NO stubs) buffers the delta for delivery to
// ReceiveDeltas consumers and tracks per-(owner,domain) sequence. When no
// transport is available the bundle is retained locally (unbounded buffer) and
// surfaced once a consumer attaches, faithfully realising store-and-forward.
//
// Concurrency (Wave-1 lessons): the delivered-delta stream uses the unbounded
// channel so a delta enqueued before any ReceiveDeltas consumer attaches is
// BUFFERED, never lost; the delta is enqueued off any bundle-store lock.

package circleai

import (
	"context"
	"sort"
	"strings"
	"sync"
	"time"

	"github.com/google/uuid"
)

// ---------------------------------------------------------------------------
// DtnPriority — DtnTransportCommons.cs enum DtnPriority
// ---------------------------------------------------------------------------

// DtnPriority ranks a bundle's forwarding urgency. Ordinals match the C#
// declaration order exactly.
type DtnPriority int

const (
	// DtnPriorityBulk — lowest; forward when convenient.
	DtnPriorityBulk DtnPriority = iota
	// DtnPriorityNormal — default forwarding priority.
	DtnPriorityNormal
	// DtnPriorityExpedited — highest; forward ahead of others.
	DtnPriorityExpedited
)

// String renders the C# enum member name for a DtnPriority.
func (p DtnPriority) String() string {
	switch p {
	case DtnPriorityBulk:
		return "Bulk"
	case DtnPriorityNormal:
		return "Normal"
	case DtnPriorityExpedited:
		return "Expedited"
	default:
		return "Unknown"
	}
}

// ---------------------------------------------------------------------------
// DtnBundle — DtnBundle.cs
// ---------------------------------------------------------------------------

// DtnBundle is a self-contained DTN delivery unit with TTL and custody
// semantics. Ports the C# `sealed record DtnBundle(BundleId, SourceNodeId,
// DestinationNodeId, Payload, ExpiresAt, CustodyRequired, HopCount, CreatedAt)`.
type DtnBundle struct {
	// BundleId is the unique bundle identifier (Guid "N").
	BundleId string
	// SourceNodeId is the origin node.
	SourceNodeId string
	// DestinationNodeId is the intended recipient node.
	DestinationNodeId string
	// Payload is the bundle body.
	Payload []byte
	// ExpiresAt is when the bundle dies (default: CreatedAt + 72h).
	ExpiresAt time.Time
	// CustodyRequired requests custody transfer at each hop.
	CustodyRequired bool
	// HopCount is how many hops the bundle has traversed.
	HopCount int
	// CreatedAt is the UTC creation time.
	CreatedAt time.Time
}

// ---------------------------------------------------------------------------
// DtnCustodyRecord — DtnTransportCommons.cs
// ---------------------------------------------------------------------------

// DtnCustodyRecord records a custodian accepting responsibility for a bundle.
// Ports the C# `sealed record DtnCustodyRecord(BundleId, CustodianNode, AcceptedAtUtc)`.
type DtnCustodyRecord struct {
	BundleId      string
	CustodianNode string
	AcceptedAtUtc time.Time
}

// ---------------------------------------------------------------------------
// InMemoryDtnBundleStore — DtnTransportCommons.cs
// ---------------------------------------------------------------------------

// InMemoryDtnBundleStore is a deterministic in-memory bundle + custody store.
// Ports the C# `InMemoryDtnBundleStore`. Safe for concurrent use.
type InMemoryDtnBundleStore struct {
	mu      sync.Mutex
	bundles map[string]DtnBundle
	custody map[string]DtnCustodyRecord
}

// NewInMemoryDtnBundleStore constructs an empty store.
func NewInMemoryDtnBundleStore() *InMemoryDtnBundleStore {
	return &InMemoryDtnBundleStore{
		bundles: make(map[string]DtnBundle),
		custody: make(map[string]DtnCustodyRecord),
	}
}

// Store inserts or updates a bundle keyed by BundleId. Panics on empty BundleId
// (mirrors the C# ArgumentNullException guard).
func (s *InMemoryDtnBundleStore) Store(b DtnBundle) {
	if b.BundleId == "" {
		panic("dtn bundle requires BundleId")
	}
	s.mu.Lock()
	s.bundles[b.BundleId] = b
	s.mu.Unlock()
}

// Get returns the bundle for bundleId and true, or a zero value and false when
// absent.
func (s *InMemoryDtnBundleStore) Get(bundleId string) (DtnBundle, bool) {
	s.mu.Lock()
	defer s.mu.Unlock()
	b, ok := s.bundles[bundleId]
	return b, ok
}

// All returns every stored bundle ordered by BundleId for deterministic output.
// (The C# `_bundles.Values.ToArray()` is unordered over a ConcurrentDictionary;
// the Go port sorts so callers/tests get a stable sequence.)
func (s *InMemoryDtnBundleStore) All() []DtnBundle {
	s.mu.Lock()
	out := make([]DtnBundle, 0, len(s.bundles))
	for _, b := range s.bundles {
		out = append(out, b)
	}
	s.mu.Unlock()
	sort.Slice(out, func(i, j int) bool { return out[i].BundleId < out[j].BundleId })
	return out
}

// AcceptCustody records a custody transfer keyed by BundleId. Panics on empty
// BundleId (mirrors the C# guard).
func (s *InMemoryDtnBundleStore) AcceptCustody(r DtnCustodyRecord) {
	if r.BundleId == "" {
		panic("dtn custody record requires BundleId")
	}
	s.mu.Lock()
	s.custody[r.BundleId] = r
	s.mu.Unlock()
}

// GetCustody returns the custody record for bundleId and true, or a zero value
// and false when absent.
func (s *InMemoryDtnBundleStore) GetCustody(bundleId string) (DtnCustodyRecord, bool) {
	s.mu.Lock()
	defer s.mu.Unlock()
	r, ok := s.custody[bundleId]
	return r, ok
}

// IsExpired reports whether the bundle is unknown or has passed its ExpiresAt at
// now. Mirrors the C# semantics (an absent bundle counts as expired).
func (s *InMemoryDtnBundleStore) IsExpired(bundleId string, now time.Time) bool {
	s.mu.Lock()
	defer s.mu.Unlock()
	b, ok := s.bundles[bundleId]
	if !ok {
		return true
	}
	return now.After(b.ExpiresAt)
}

// Purge removes every bundle (and its custody record) whose ExpiresAt is before
// now, returning the count removed. Mirrors the C# Purge.
func (s *InMemoryDtnBundleStore) Purge(now time.Time) int {
	s.mu.Lock()
	defer s.mu.Unlock()
	dead := make([]string, 0)
	for id, b := range s.bundles {
		if now.After(b.ExpiresAt) {
			dead = append(dead, id)
		}
	}
	for _, id := range dead {
		delete(s.bundles, id)
		delete(s.custody, id)
	}
	return len(dead)
}

// InFlightTo returns every stored bundle addressed to destinationNodeId, ordered
// by BundleId for determinism. Mirrors the C# InFlightTo filter.
func (s *InMemoryDtnBundleStore) InFlightTo(destinationNodeId string) []DtnBundle {
	s.mu.Lock()
	out := make([]DtnBundle, 0)
	for _, b := range s.bundles {
		if b.DestinationNodeId == destinationNodeId {
			out = append(out, b)
		}
	}
	s.mu.Unlock()
	sort.Slice(out, func(i, j int) bool { return out[i].BundleId < out[j].BundleId })
	return out
}

// ---------------------------------------------------------------------------
// DtnSyncChannel — DtnSyncChannel.cs
// ---------------------------------------------------------------------------

// dtnDefaultTTL is the DtnSyncChannel default bundle lifetime (72h).
const dtnDefaultTTL = 72 * time.Hour

// DtnSyncChannel is an ISyncChannel backed by DTN store-and-forward over any set
// of injected INetworkTransports. Ports the C# DtnSyncChannel. On PushDelta it
// wraps the delta into a 72h DtnBundle (custody required when the delivery mode
// is Guaranteed), tries the FIRST available transport with an
// "application/dtn-bundle" payload, and — realising the C# "queued locally"
// path as a working behaviour — buffers the delta into the delivered stream and
// stores the bundle so ReceiveDeltas consumers observe it and Purge/InFlightTo
// can inspect it. Per-(owner,domain) sequence is tracked. Safe for concurrent
// use.
type DtnSyncChannel struct {
	transports []INetworkTransport
	store      *InMemoryDtnBundleStore

	mu        sync.Mutex
	sequences map[[2]string]int64
	delivered *unboundedChannel[SyncDelta]
}

// NewDtnSyncChannel builds a channel over transports (the ordered candidate
// list; may be empty). A fresh bundle store backs it.
func NewDtnSyncChannel(transports []INetworkTransport) *DtnSyncChannel {
	cp := make([]INetworkTransport, len(transports))
	copy(cp, transports)
	return &DtnSyncChannel{
		transports: cp,
		store:      NewInMemoryDtnBundleStore(),
		sequences:  make(map[[2]string]int64),
		delivered:  newUnboundedChannel[SyncDelta](),
	}
}

// Store exposes the underlying bundle store for inspection (in-flight bundles,
// custody, purge). Not part of the C# public surface — a Go convenience over the
// working store the C# comment describes ("full impl: persist to SQLite").
func (c *DtnSyncChannel) Store() *InMemoryDtnBundleStore { return c.store }

// PushDelta wraps delta into a 72h bundle and forwards it. It sends over the
// first available transport (payload priority Urgent when the delivery mode is
// Urgent, else Normal) and always records the bundle + advances the delta's
// (owner,domain) sequence and buffers it for delivery. Returns when accepted.
func (c *DtnSyncChannel) PushDelta(ctx context.Context, delta SyncDelta) error {
	if err := ctx.Err(); err != nil {
		return err
	}

	ttl := dtnDefaultTTL
	if delta.TTL != nil {
		ttl = *delta.TTL
	}
	now := time.Now().UTC()
	bundle := DtnBundle{
		BundleId:          strings.ReplaceAll(uuid.NewString(), "-", ""),
		SourceNodeId:      delta.SourceDeviceID,
		DestinationNodeId: delta.TargetDeviceID,
		Payload:           append([]byte(nil), delta.Payload...),
		ExpiresAt:         now.Add(ttl),
		CustodyRequired:   delta.DeliveryMode == SyncDeliveryModeGuaranteed,
		HopCount:          0,
		CreatedAt:         now,
	}
	c.store.Store(bundle)
	if bundle.CustodyRequired {
		c.store.AcceptCustody(DtnCustodyRecord{
			BundleId:      bundle.BundleId,
			CustodianNode: delta.SourceDeviceID,
			AcceptedAtUtc: now,
		})
	}

	c.bumpSequence(delta.OwnerID, delta.DomainKey, delta.Sequence)

	// Try live transports first; if none available the bundle stays queued
	// (already stored) and is surfaced to ReceiveDeltas below.
	if tr := c.firstAvailable(); tr != nil {
		priority := MessagePriorityNormal
		if delta.DeliveryMode == SyncDeliveryModeUrgent {
			priority = MessagePriorityUrgent
		}
		payload := NewNetworkPayloadWith(delta.Payload, delta.TargetDeviceID, priority, "application/dtn-bundle", nil)
		if err := tr.Send(ctx, payload); err != nil {
			return err
		}
	}

	// Surface the delta to local ReceiveDeltas consumers (store-and-forward
	// delivery). Buffered even if no consumer has attached yet.
	c.delivered.Write(delta)
	return nil
}

// firstAvailable returns the first transport reporting IsAvailable, or nil.
func (c *DtnSyncChannel) firstAvailable() INetworkTransport {
	for _, tr := range c.transports {
		if tr.IsAvailable() {
			return tr
		}
	}
	return nil
}

// bumpSequence raises the stored high-water for (owner,domain) to at least seq.
func (c *DtnSyncChannel) bumpSequence(owner, domain string, seq int64) {
	k := [2]string{owner, domain}
	c.mu.Lock()
	if cur, ok := c.sequences[k]; !ok || seq > cur {
		c.sequences[k] = seq
	}
	c.mu.Unlock()
}

// ReceiveDeltas returns a stream of delivered deltas for ownerID with Sequence >
// afterSeq. Deltas buffered before this call are replayed (unbounded buffering),
// so a delta pushed while offline is delivered once a consumer attaches. The
// errs channel is closed with out. The stream closes on ctx cancellation.
func (c *DtnSyncChannel) ReceiveDeltas(ctx context.Context, ownerID string, afterSeq int64) (<-chan SyncDelta, <-chan error) {
	out := make(chan SyncDelta)
	errs := make(chan error, 1)
	raw := c.delivered.ReadAll(ctx)

	go func() {
		defer close(out)
		defer close(errs)
		for {
			select {
			case <-ctx.Done():
				return
			case d, ok := <-raw:
				if !ok {
					return
				}
				if d.OwnerID != ownerID || d.Sequence <= afterSeq {
					continue
				}
				select {
				case out <- d:
				case <-ctx.Done():
					return
				}
			}
		}
	}()

	return out, errs
}

// GetLastSequence returns the highest sequence observed for (ownerID,domainKey),
// or 0 when none. Mirrors the C# dictionary lookup defaulting to 0.
func (c *DtnSyncChannel) GetLastSequence(ctx context.Context, ownerID, domainKey string) (int64, error) {
	if err := ctx.Err(); err != nil {
		return 0, err
	}
	c.mu.Lock()
	defer c.mu.Unlock()
	if v, ok := c.sequences[[2]string{ownerID, domainKey}]; ok {
		return v, nil
	}
	return 0, nil
}

var _ ISyncChannel = (*DtnSyncChannel)(nil)
