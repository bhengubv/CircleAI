// personal_health_board_test.go
//
// Verifies the CircleAI.Personal.Health port (personal_health_board.go):
// VitalKind ordinals/names, vital record + read-since (asc) + latest (desc),
// allergy add/list, and medication add/end/active filtering (by Name).

package circleai_test

import (
	"testing"
	"time"

	circleai "github.com/bhengubv/CircleAI/go"
)

func vitTime(y int, m time.Month, d, h int) time.Time {
	return time.Date(y, m, d, h, 0, 0, 0, time.UTC)
}

func TestPersonalHealth_VitalKindOrdinals(t *testing.T) {
	if circleai.VitalBloodPressureSystolic != 0 || circleai.VitalStepsCount != 7 {
		t.Fatalf("ordinals: sys=%d steps=%d", circleai.VitalBloodPressureSystolic, circleai.VitalStepsCount)
	}
	if circleai.VitalGlucoseMgDl.String() != "GlucoseMgDl" || circleai.VitalOxygenPct.String() != "OxygenPct" {
		t.Fatalf("names wrong: %s / %s", circleai.VitalGlucoseMgDl, circleai.VitalOxygenPct)
	}
}

func TestPersonalHealth_VitalsReadSinceAndLatest(t *testing.T) {
	b := circleai.NewInMemoryPersonalHealthBoard()
	b.Record(circleai.VitalReading{Kind: circleai.VitalWeightKg, Value: 80.0, AtUtc: vitTime(2026, 7, 1, 8)})
	b.Record(circleai.VitalReading{Kind: circleai.VitalWeightKg, Value: 79.5, AtUtc: vitTime(2026, 7, 3, 8)})
	b.Record(circleai.VitalReading{Kind: circleai.VitalWeightKg, Value: 79.8, AtUtc: vitTime(2026, 7, 2, 8)})
	b.Record(circleai.VitalReading{Kind: circleai.VitalGlucoseMgDl, Value: 5.5, AtUtc: vitTime(2026, 7, 2, 8)})

	// ReadSince from Jul 2: weight readings on Jul 2 and Jul 3, ascending.
	since := b.ReadSince(circleai.VitalWeightKg, vitTime(2026, 7, 2, 0))
	if len(since) != 2 || since[0].AtUtc != vitTime(2026, 7, 2, 8) || since[1].AtUtc != vitTime(2026, 7, 3, 8) {
		t.Fatalf("read-since asc failed: %+v", since)
	}
	// Latest weight is the Jul 3 reading (79.5).
	latest, ok := b.Latest(circleai.VitalWeightKg)
	if !ok || latest.Value != 79.5 {
		t.Fatalf("latest weight = %+v ok=%v", latest, ok)
	}
	// No temperature readings.
	if _, ok := b.Latest(circleai.VitalTemperatureC); ok {
		t.Fatalf("no temperature reading should exist")
	}
}

func TestPersonalHealth_LatestEqualTimestampTakesFirstInserted(t *testing.T) {
	// C# OrderByDescending is stable + First() -> first-inserted among equal-max.
	same := vitTime(2026, 7, 5, 9)
	b := circleai.NewInMemoryPersonalHealthBoard()
	b.Record(circleai.VitalReading{Kind: circleai.VitalHeartRateBpm, Value: 70, AtUtc: same})
	b.Record(circleai.VitalReading{Kind: circleai.VitalHeartRateBpm, Value: 71, AtUtc: same})
	b.Record(circleai.VitalReading{Kind: circleai.VitalHeartRateBpm, Value: 72, AtUtc: same})
	got, ok := b.Latest(circleai.VitalHeartRateBpm)
	if !ok || got.Value != 70 {
		t.Fatalf("latest with equal timestamps = %+v, want first-inserted (70)", got)
	}
}

func TestPersonalHealth_Allergies(t *testing.T) {
	b := circleai.NewInMemoryPersonalHealthBoard()
	b.AddAllergy(circleai.Allergy{AllergyId: "al2", Substance: "Penicillin", Severity: "High"})
	b.AddAllergy(circleai.Allergy{AllergyId: "al1", Substance: "Peanuts", Severity: "Severe"})
	list := b.Allergies()
	if len(list) != 2 || list[0].AllergyId != "al1" || list[1].AllergyId != "al2" {
		t.Fatalf("allergies (sorted by id) failed: %+v", list)
	}
}

func TestPersonalHealth_Medications(t *testing.T) {
	b := circleai.NewInMemoryPersonalHealthBoard()
	start := vitTime(2026, 6, 1, 0)
	b.AddMedication(circleai.Medication{MedId: "m1", Name: "Zoloft", Dose: "50mg", Frequency: "daily", StartedAtUtc: start})
	b.AddMedication(circleai.Medication{MedId: "m2", Name: "Aspirin", Dose: "100mg", Frequency: "daily", StartedAtUtc: start})
	b.AddMedication(circleai.Medication{MedId: "m3", Name: "Metformin", Dose: "500mg", Frequency: "bid", StartedAtUtc: start})

	// End Zoloft.
	if err := b.EndMedication("m1", vitTime(2026, 7, 1, 0)); err != nil {
		t.Fatalf("end medication: %v", err)
	}
	if err := b.EndMedication("ghost", vitTime(2026, 7, 1, 0)); err == nil {
		t.Fatalf("ending unknown medication must error")
	}
	active := b.ActiveMedications()
	// Aspirin, Metformin remain; ordered by Name asc.
	if len(active) != 2 || active[0].Name != "Aspirin" || active[1].Name != "Metformin" {
		t.Fatalf("active medications failed: %+v", active)
	}
}
