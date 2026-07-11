// business_board.go
//
// Ports the CircleAI.Business primitive vertical (BusinessPrimitives.cs):
//   BusinessUnit / KpiSample / QuarterTarget (records) -> value structs
//   IBusinessBoard        -> BusinessBoard interface (I-prefix dropped)
//   InMemoryBusinessBoard -> InMemoryBusinessBoard
//
// The BusinessDomainContext (static prompt strings) and BusinessCompanionAdapter
// (LLM-prompt wrapper) are out of scope for the deterministic in-memory board.
//
// DETERMINISM: ChildrenOf keeps no defined C# order (ConcurrentDictionary
// values); this port sorts by UnitId for stable output. LatestKpi picks the most
// recent sample by AtUtc (exact ties resolve to the first-inserted, matching the
// C# stable OrderByDescending.FirstOrDefault) and returns double.NaN when there is
// no matching sample. TargetAchievement returns
// NaN when the target is missing or its Target is exactly 0. KpiTags is copied
// defensively on Add so a later caller mutation cannot alter the stored unit.

package circleai

import (
	"math"
	"sort"
	"sync"
	"time"
)

// BusinessUnit is an org unit. Ports the BusinessUnit record. KpiTags mirrors the
// C# IReadOnlyList<string>.
type BusinessUnit struct {
	UnitId       string
	Name         string
	ParentUnitId string
	KpiTags      []string
}

// KpiSample is a timestamped KPI reading. Ports the KpiSample record.
type KpiSample struct {
	UnitId string
	Metric string
	Value  float64
	AtUtc  time.Time
}

// QuarterTarget is a per-unit per-metric quarterly target. Ports the
// QuarterTarget record.
type QuarterTarget struct {
	UnitId  string
	Metric  string
	Year    int
	Quarter int
	Target  float64
}

// BusinessBoard is the units/KPIs/targets board. Ports IBusinessBoard.
type BusinessBoard interface {
	Add(u BusinessUnit)
	GetUnit(id string) (BusinessUnit, bool)
	// ChildrenOf lists units whose ParentUnitId == parentUnitId.
	ChildrenOf(parentUnitId string) []BusinessUnit
	Record(s KpiSample)
	// LatestKpi is the most recent Value for (unitId, metric), or NaN if none.
	LatestKpi(unitId, metric string) float64
	SetTarget(t QuarterTarget)
	// TargetAchievement is LatestKpi / Target for the quarter, or NaN if the
	// target is missing or zero.
	TargetAchievement(unitId, metric string, year, quarter int) float64
}

// InMemoryBusinessBoard is a concurrency-safe in-memory BusinessBoard. Ports
// InMemoryBusinessBoard (units + targets in maps; KPI samples in an ordered list
// guarded by the mutex). Target keys use the C# "{Unit}/{Metric}/{Year}Q{Quarter}".
type InMemoryBusinessBoard struct {
	mu      sync.RWMutex
	units   map[string]BusinessUnit
	kpis    []KpiSample
	targets map[string]QuarterTarget
}

// NewInMemoryBusinessBoard constructs an empty board.
func NewInMemoryBusinessBoard() *InMemoryBusinessBoard {
	return &InMemoryBusinessBoard{
		units:   make(map[string]BusinessUnit),
		kpis:    make([]KpiSample, 0),
		targets: make(map[string]QuarterTarget),
	}
}

// Add stores (or replaces by UnitId) a unit, copying KpiTags defensively. Ports
// Add.
func (b *InMemoryBusinessBoard) Add(u BusinessUnit) {
	u.KpiTags = append([]string(nil), u.KpiTags...)
	b.mu.Lock()
	b.units[u.UnitId] = u
	b.mu.Unlock()
}

// GetUnit returns the unit for id and true, or (zero, false) if absent. Ports
// GetUnit.
func (b *InMemoryBusinessBoard) GetUnit(id string) (BusinessUnit, bool) {
	b.mu.RLock()
	u, ok := b.units[id]
	b.mu.RUnlock()
	return u, ok
}

// ChildrenOf lists units whose ParentUnitId == parentUnitId, sorted by UnitId for
// determinism. Ports ChildrenOf.
func (b *InMemoryBusinessBoard) ChildrenOf(parentUnitId string) []BusinessUnit {
	b.mu.RLock()
	out := make([]BusinessUnit, 0)
	for _, u := range b.units {
		if u.ParentUnitId == parentUnitId {
			out = append(out, u)
		}
	}
	b.mu.RUnlock()
	sort.SliceStable(out, func(i, j int) bool { return out[i].UnitId < out[j].UnitId })
	return out
}

// Record appends a KPI sample. Ports Record.
func (b *InMemoryBusinessBoard) Record(s KpiSample) {
	b.mu.Lock()
	b.kpis = append(b.kpis, s)
	b.mu.Unlock()
}

// LatestKpi returns the Value of the most recent sample for (unitId, metric) by
// AtUtc, or NaN when there is no matching sample. Ports LatestKpi
// (OrderByDescending(AtUtc).FirstOrDefault()?.Value ?? double.NaN). Ties on AtUtc
// resolve to the last-inserted matching sample (stable descending sort).
func (b *InMemoryBusinessBoard) LatestKpi(unitId, metric string) float64 {
	b.mu.RLock()
	defer b.mu.RUnlock()
	found := false
	var bestAt time.Time
	var bestVal float64
	for _, k := range b.kpis {
		if k.UnitId != unitId || k.Metric != metric {
			continue
		}
		// Keep the latest by AtUtc. On an exact tie keep the FIRST-inserted: C#
		// OrderByDescending is a STABLE sort (equal keys retain source order), so
		// the tied-max group leads the descending result in insertion order and
		// FirstOrDefault returns the first-inserted of that group. A strict After
		// (not >=) reproduces that — a later equal-key sample does not displace it.
		if !found || k.AtUtc.After(bestAt) {
			found = true
			bestAt = k.AtUtc
			bestVal = k.Value
		}
	}
	if !found {
		return math.NaN()
	}
	return bestVal
}

// SetTarget stores (or replaces) a quarterly target under the C# composite key.
// Ports SetTarget.
func (b *InMemoryBusinessBoard) SetTarget(t QuarterTarget) {
	key := targetKey(t.UnitId, t.Metric, t.Year, t.Quarter)
	b.mu.Lock()
	b.targets[key] = t
	b.mu.Unlock()
}

// TargetAchievement returns LatestKpi(unitId, metric) / target.Target for the
// quarter, or NaN when the target is absent or its Target is exactly 0. Ports
// TargetAchievement.
func (b *InMemoryBusinessBoard) TargetAchievement(unitId, metric string, year, quarter int) float64 {
	key := targetKey(unitId, metric, year, quarter)
	b.mu.RLock()
	target, ok := b.targets[key]
	b.mu.RUnlock()
	if !ok || target.Target == 0 {
		return math.NaN()
	}
	return b.LatestKpi(unitId, metric) / target.Target
}

// targetKey builds the C# "{unit}/{metric}/{year}Q{quarter}" composite key.
func targetKey(unitId, metric string, year, quarter int) string {
	return unitId + "/" + metric + "/" + itoa(year) + "Q" + itoa(quarter)
}

// Interface guard.
var _ BusinessBoard = (*InMemoryBusinessBoard)(nil)
