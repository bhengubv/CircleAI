// domain_sort_test.go
//
// Verifies the cultureLess stand-in for .NET OrderBy(string) via the public
// board methods that use it. cultureLess itself is unexported; these tests drive
// it through Personal.Health.ActiveMedications (OrderBy(Name)) to confirm the
// case-insensitive primary ordering and lower-before-upper tie-break match the
// .NET reference for ASCII names.

package circleai_test

import (
	"testing"
	"time"

	circleai "github.com/bhengubv/CircleAI/go"
)

func TestCultureOrder_ActiveMedications_CaseInsensitivePrimary(t *testing.T) {
	// Reference (.NET en-ZA OrderBy): apple-style case folding means
	// "aspirin" < "Betaloc" < "metformin" regardless of case.
	b := circleai.NewInMemoryPersonalHealthBoard()
	start := time.Date(2026, 6, 1, 0, 0, 0, 0, time.UTC)
	b.AddMedication(circleai.Medication{MedId: "m1", Name: "metformin", StartedAtUtc: start})
	b.AddMedication(circleai.Medication{MedId: "m2", Name: "Betaloc", StartedAtUtc: start})
	b.AddMedication(circleai.Medication{MedId: "m3", Name: "aspirin", StartedAtUtc: start})
	got := b.ActiveMedications()
	if got[0].Name != "aspirin" || got[1].Name != "Betaloc" || got[2].Name != "metformin" {
		t.Fatalf("case-insensitive primary order = %v", []string{got[0].Name, got[1].Name, got[2].Name})
	}
}

func TestCultureOrder_ActiveMedications_LowerBeforeUpperTie(t *testing.T) {
	// "aspirin" and "Aspirin" fold equal; lower-case sorts first (.NET reference).
	b := circleai.NewInMemoryPersonalHealthBoard()
	start := time.Date(2026, 6, 1, 0, 0, 0, 0, time.UTC)
	b.AddMedication(circleai.Medication{MedId: "m1", Name: "Aspirin", StartedAtUtc: start})
	b.AddMedication(circleai.Medication{MedId: "m2", Name: "aspirin", StartedAtUtc: start})
	got := b.ActiveMedications()
	if got[0].Name != "aspirin" || got[1].Name != "Aspirin" {
		t.Fatalf("lower-before-upper tie failed: %v", []string{got[0].Name, got[1].Name})
	}
}
