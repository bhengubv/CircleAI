// energy_board_test.go
//
// Verifies the CircleAI.Energy port (energy_board.go): reading storage +
// since-window ordering, total-kwh (last minus first), tariff set/get, cost
// estimate at peak rate, and active-outage listing.

package circleai_test

import (
	"testing"
	"time"

	circleai "github.com/bhengubv/CircleAI/go"
)

func TestEnergy_ReadingsAndCost(t *testing.T) {
	b := circleai.NewInMemoryEnergyBoard()
	base := time.Date(2026, 7, 1, 0, 0, 0, 0, time.UTC)
	b.Record(circleai.MeterReading{MeterId: "m1", Kwh: 100, AtUtc: base})
	b.Record(circleai.MeterReading{MeterId: "m1", Kwh: 130, AtUtc: base.Add(24 * time.Hour)})
	b.Record(circleai.MeterReading{MeterId: "m1", Kwh: 175, AtUtc: base.Add(48 * time.Hour)})

	rows := b.ReadingsFor("m1", base)
	if len(rows) != 3 || rows[0].Kwh != 100 || rows[2].Kwh != 175 {
		t.Fatalf("readings-for ordered failed: %+v", rows)
	}
	if kwh := b.TotalKwhSince("m1", base); kwh != 75 {
		t.Fatalf("total kwh = %v, want 75", kwh)
	}

	b.SetTariff(circleai.EnergyTariff{TariffId: "t1", Name: "Std", PeakKwhRate: 2.5, OffPeakKwhRate: 1.0, Currency: "ZAR"})
	if got, ok := b.GetTariff("t1"); !ok || got.Name != "Std" {
		t.Fatalf("get tariff = %+v ok=%v", got, ok)
	}
	// 75 kwh * 2.5 = 187.5.
	cost, err := b.EstimateCost("m1", "t1", base)
	if err != nil {
		t.Fatalf("estimate: %v", err)
	}
	if !cost.Equal(circleai.DecimalFromFloat(187.5)) {
		t.Fatalf("estimate cost = %s, want 187.50", cost.String())
	}
	if _, err := b.EstimateCost("m1", "ghost", base); err == nil {
		t.Fatalf("estimate with unknown tariff must error")
	}
}

func TestEnergy_Outages(t *testing.T) {
	b := circleai.NewInMemoryEnergyBoard()
	now := time.Now().UTC()
	end := now
	b.LogOutage(circleai.Outage{OutageId: "o2", Area: "North", StartUtc: now.Add(-time.Hour)})                   // active
	b.LogOutage(circleai.Outage{OutageId: "o1", Area: "South", StartUtc: now.Add(-2 * time.Hour), EndUtc: &end}) // resolved
	b.LogOutage(circleai.Outage{OutageId: "o3", Area: "East", StartUtc: now.Add(-30 * time.Minute)})             // active

	active := b.ActiveOutages()
	if len(active) != 2 || active[0].OutageId != "o2" || active[1].OutageId != "o3" {
		t.Fatalf("active outages (sorted) failed: %+v", active)
	}
}
