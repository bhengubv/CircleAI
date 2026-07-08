// sync_hlc.go
//
// Ports CircleAI.Memory.Sync.HybridLogicalClock (HybridLogicalClock.cs).
//
// Hybrid Logical Clock (HLC) — monotonic version stamps that survive small
// clock skew between peers WITHOUT needing NTP. Composes a physical
// millisecond timestamp with a logical counter and the node's short ID so
// every emitted version is globally unique and monotonically increasing.
//
// Layout of the version:
//   high 48 bits — physical time in milliseconds (Unix epoch)
//   mid  10 bits — logical counter (resets when physical advances)
//   low   6 bits — node short ID (0..63)
// Total: 64 bits.

package circleai

import (
	"fmt"
	"sync"
	"time"
)

// HybridLogicalClock produces monotonic, globally-unique version stamps for
// syncable entries. It is safe for concurrent use.
type HybridLogicalClock struct {
	physicalNowMs func() int64
	nodeShortID   int64

	mu           sync.Mutex
	lastPhysical int64
	logical      int64
}

// NewHybridLogicalClock creates a clock for the given node short id (0..63).
// physicalNowMs is the source of physical time in milliseconds; pass nil to use
// the system wall clock. Returns an error when nodeShortID is out of range.
//
// nodeShortID packs into the low 6 bits of every version. Each device a user
// has should pick a stable distinct value (any deterministic hash works).
func NewHybridLogicalClock(nodeShortID int64, physicalNowMs func() int64) (*HybridLogicalClock, error) {
	if nodeShortID < 0 || nodeShortID > 63 {
		return nil, fmt.Errorf("nodeShortId must be in 0..63, got %d", nodeShortID)
	}
	now := physicalNowMs
	if now == nil {
		now = hlcDefaultNow
	}
	return &HybridLogicalClock{
		physicalNowMs: now,
		nodeShortID:   nodeShortID,
		lastPhysical:  now(),
		logical:       0,
	}, nil
}

// MustNewHybridLogicalClock is like NewHybridLogicalClock but panics on an
// out-of-range nodeShortID. Convenience for wiring where the id is a constant.
func MustNewHybridLogicalClock(nodeShortID int64) *HybridLogicalClock {
	c, err := NewHybridLogicalClock(nodeShortID, nil)
	if err != nil {
		panic(err)
	}
	return c
}

// Tick produces the next outgoing version (for a write we originated).
func (c *HybridLogicalClock) Tick() int64 {
	c.mu.Lock()
	defer c.mu.Unlock()
	now := c.physicalNowMs()
	if now > c.lastPhysical {
		c.lastPhysical = now
		c.logical = 0
	} else {
		c.logical++
		if c.logical >= 1024 {
			// Logical counter overflowed within the same ms — bump physical.
			c.lastPhysical++
			c.logical = 0
		}
	}
	return HLCCompose(c.lastPhysical, c.logical, c.nodeShortID)
}

// Observe updates the clock from a received version (must be called on every
// inbound apply so subsequent local ticks remain monotonic w.r.t. peers).
func (c *HybridLogicalClock) Observe(incoming int64) int64 {
	c.mu.Lock()
	defer c.mu.Unlock()
	incomingPhysical, incomingLogical, _ := HLCDecompose(incoming)
	now := c.physicalNowMs()
	maxPhysical := maxInt64(maxInt64(c.lastPhysical, incomingPhysical), now)

	switch {
	case maxPhysical == c.lastPhysical && maxPhysical == incomingPhysical:
		c.logical++
	case maxPhysical == c.lastPhysical:
		c.logical++
	case maxPhysical == incomingPhysical:
		c.logical = incomingLogical + 1
	default:
		c.logical = 0
	}

	c.lastPhysical = maxPhysical
	return HLCCompose(c.lastPhysical, c.logical, c.nodeShortID)
}

// HLCCompose composes the three components into a 64-bit version.
func HLCCompose(physicalMs, logical, nodeShortID int64) int64 {
	return (physicalMs << 16) | ((logical & 0x3FF) << 6) | (nodeShortID & 0x3F)
}

// HLCDecompose decomposes a version into its (physicalMs, logical, nodeShortId).
func HLCDecompose(version int64) (physicalMs, logical, nodeShortID int64) {
	return version >> 16, (version >> 6) & 0x3FF, version & 0x3F
}

func hlcDefaultNow() int64 { return time.Now().UTC().UnixMilli() }

func maxInt64(a, b int64) int64 {
	if a > b {
		return a
	}
	return b
}
