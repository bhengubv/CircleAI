// sync_hlc_test.go
//
// Verifies HybridLogicalClock (ported from HybridLogicalClock.cs):
//   - Compose/Decompose match the fixture vectors (bit layout parity).
//   - Tick is strictly monotonic within a fixed ms and across ms advances.
//   - Tick bumps physical when the logical counter overflows 1024 in one ms.
//   - Observe keeps the clock monotonic w.r.t. a higher incoming version.
//   - Construction rejects out-of-range node short ids.

package circleai_test

import (
	"encoding/json"
	"os"
	"path/filepath"
	"runtime"
	"testing"

	circleai "github.com/bhengubv/CircleAI/go"
)

// localFixturesDir resolves go/tests/fixtures relative to this test file.
func localFixturesDir(t *testing.T) string {
	t.Helper()
	_, file, _, ok := runtime.Caller(0)
	if !ok {
		t.Fatal("runtime.Caller failed")
	}
	return filepath.Join(filepath.Dir(file), "fixtures")
}

func readLocalFixture(t *testing.T, name string, v any) {
	t.Helper()
	data, err := os.ReadFile(filepath.Join(localFixturesDir(t), name))
	if err != nil {
		t.Fatalf("read fixture %s: %v", name, err)
	}
	if err := json.Unmarshal(data, v); err != nil {
		t.Fatalf("parse fixture %s: %v", name, err)
	}
}

type hlcComposeFixture struct {
	Vectors []struct {
		ID          string `json:"id"`
		PhysicalMs  int64  `json:"physicalMs"`
		Logical     int64  `json:"logical"`
		NodeShortID int64  `json:"nodeShortId"`
		Version     int64  `json:"version"`
	} `json:"vectors"`
}

func TestHLC_ComposeDecompose_Fixture(t *testing.T) {
	var fix hlcComposeFixture
	readLocalFixture(t, "hlc_compose.json", &fix)
	if len(fix.Vectors) == 0 {
		t.Fatal("no hlc vectors")
	}
	for _, v := range fix.Vectors {
		v := v
		t.Run(v.ID, func(t *testing.T) {
			got := circleai.HLCCompose(v.PhysicalMs, v.Logical, v.NodeShortID)
			if got != v.Version {
				t.Errorf("Compose: got %d want %d", got, v.Version)
			}
			p, l, n := circleai.HLCDecompose(v.Version)
			if p != v.PhysicalMs || l != v.Logical || n != v.NodeShortID {
				t.Errorf("Decompose: got (%d,%d,%d) want (%d,%d,%d)", p, l, n, v.PhysicalMs, v.Logical, v.NodeShortID)
			}
		})
	}
}

func TestHLC_Tick_MonotonicWithinFixedMs(t *testing.T) {
	now := int64(1_000_000)
	clk, err := circleai.NewHybridLogicalClock(5, func() int64 { return now })
	if err != nil {
		t.Fatalf("ctor: %v", err)
	}
	prev := clk.Tick()
	for i := 0; i < 50; i++ {
		cur := clk.Tick()
		if cur <= prev {
			t.Fatalf("tick not strictly increasing: prev=%d cur=%d", prev, cur)
		}
		prev = cur
	}
}

func TestHLC_Tick_ResetsLogicalWhenPhysicalAdvances(t *testing.T) {
	now := int64(1000)
	clk, _ := circleai.NewHybridLogicalClock(0, func() int64 { return now })
	clk.Tick() // logical 0
	clk.Tick() // logical 1 (same ms)
	now = 2000
	v := clk.Tick()
	_, logical, _ := circleai.HLCDecompose(v)
	if logical != 0 {
		t.Errorf("logical should reset to 0 on physical advance, got %d", logical)
	}
}

func TestHLC_Tick_OverflowBumpsPhysical(t *testing.T) {
	now := int64(500)
	clk, _ := circleai.NewHybridLogicalClock(1, func() int64 { return now })
	// Ctor sets physical=500, logical=0. With physical pinned, each Tick takes
	// the else branch and increments logical: tick #1→1, ... tick #1024 makes
	// logical hit 1024 which overflows → physical bumps to 501, logical resets
	// to 0. So the 1024th tick is exactly the overflow tick.
	var overflowTick int64
	for i := 0; i < 1024; i++ {
		overflowTick = clk.Tick()
	}
	p, logical, _ := circleai.HLCDecompose(overflowTick)
	if p != 501 {
		t.Errorf("physical should have bumped to 501 on overflow, got %d", p)
	}
	if logical != 0 {
		t.Errorf("logical should reset to 0 on the overflow tick, got %d", logical)
	}
	// The very next tick continues from the bumped physical with logical=1.
	pNext, lNext, _ := circleai.HLCDecompose(clk.Tick())
	if pNext != 501 || lNext != 1 {
		t.Errorf("post-overflow tick: got (%d,%d) want (501,1)", pNext, lNext)
	}
}

func TestHLC_Observe_StaysMonotonic(t *testing.T) {
	now := int64(1000)
	clk, _ := circleai.NewHybridLogicalClock(2, func() int64 { return now })
	local := clk.Tick()

	// An incoming version far in the future (physical 5000).
	incoming := circleai.HLCCompose(5000, 3, 9)
	observed := clk.Observe(incoming)
	if observed <= incoming {
		t.Errorf("observed (%d) should exceed incoming (%d)", observed, incoming)
	}

	// A subsequent local tick must still exceed everything seen so far.
	next := clk.Tick()
	if next <= observed || next <= local {
		t.Errorf("next tick %d should exceed observed %d and local %d", next, observed, local)
	}
}

func TestHLC_Ctor_RejectsOutOfRangeNode(t *testing.T) {
	if _, err := circleai.NewHybridLogicalClock(-1, nil); err == nil {
		t.Error("node -1 should error")
	}
	if _, err := circleai.NewHybridLogicalClock(64, nil); err == nil {
		t.Error("node 64 should error")
	}
	if _, err := circleai.NewHybridLogicalClock(63, nil); err != nil {
		t.Errorf("node 63 should be valid: %v", err)
	}
}
