// business_board_test.go
//
// Verifies the CircleAI.Business port (business_board.go): unit add/get,
// ChildrenOf filter, KPI record + LatestKpi (most recent; NaN if none), target
// set + TargetAchievement (ratio; NaN when target missing or zero), and defensive
// KpiTags copy.

package circleai_test

import (
	"math"
	"testing"
	"time"

	circleai "github.com/bhengubv/CircleAI/go"
)

func TestBusiness_UnitsAndChildren(t *testing.T) {
	b := circleai.NewInMemoryBusinessBoard()
	b.Add(circleai.BusinessUnit{UnitId: "root", Name: "Group", ParentUnitId: "", KpiTags: []string{"rev"}})
	b.Add(circleai.BusinessUnit{UnitId: "u1", Name: "Sales", ParentUnitId: "root"})
	b.Add(circleai.BusinessUnit{UnitId: "u2", Name: "Ops", ParentUnitId: "root"})

	if u, ok := b.GetUnit("u1"); !ok || u.Name != "Sales" {
		t.Fatalf("get u1 = %+v ok=%v", u, ok)
	}
	kids := b.ChildrenOf("root")
	if len(kids) != 2 || kids[0].UnitId != "u1" || kids[1].UnitId != "u2" {
		t.Fatalf("children order/count wrong: %+v", kids)
	}

	// Defensive KpiTags copy: mutating the caller's slice must not change stored.
	tags := []string{"rev"}
	b.Add(circleai.BusinessUnit{UnitId: "u3", Name: "Fin", ParentUnitId: "root", KpiTags: tags})
	tags[0] = "MUTATED"
	u3, _ := b.GetUnit("u3")
	if len(u3.KpiTags) != 1 || u3.KpiTags[0] != "rev" {
		t.Fatalf("KpiTags not defensively copied: %+v", u3.KpiTags)
	}
}

func TestBusiness_LatestKpiAndTarget(t *testing.T) {
	b := circleai.NewInMemoryBusinessBoard()
	t0 := time.Date(2026, 7, 1, 0, 0, 0, 0, time.UTC)
	b.Record(circleai.KpiSample{UnitId: "u1", Metric: "revenue", Value: 100, AtUtc: t0})
	b.Record(circleai.KpiSample{UnitId: "u1", Metric: "revenue", Value: 150, AtUtc: t0.Add(24 * time.Hour)})
	b.Record(circleai.KpiSample{UnitId: "u1", Metric: "cost", Value: 40, AtUtc: t0})

	if got := b.LatestKpi("u1", "revenue"); got != 150 {
		t.Fatalf("latest revenue = %v, want 150", got)
	}
	// No sample -> NaN.
	if got := b.LatestKpi("u1", "missing"); !math.IsNaN(got) {
		t.Fatalf("missing metric should be NaN, got %v", got)
	}

	b.SetTarget(circleai.QuarterTarget{UnitId: "u1", Metric: "revenue", Year: 2026, Quarter: 3, Target: 300})
	if got := b.TargetAchievement("u1", "revenue", 2026, 3); got != 0.5 { // 150/300
		t.Fatalf("achievement = %v, want 0.5", got)
	}
	// Missing target -> NaN.
	if got := b.TargetAchievement("u1", "revenue", 2026, 4); !math.IsNaN(got) {
		t.Fatalf("missing target should be NaN, got %v", got)
	}
	// Zero target -> NaN.
	b.SetTarget(circleai.QuarterTarget{UnitId: "u1", Metric: "cost", Year: 2026, Quarter: 3, Target: 0})
	if got := b.TargetAchievement("u1", "cost", 2026, 3); !math.IsNaN(got) {
		t.Fatalf("zero target should be NaN, got %v", got)
	}
}
