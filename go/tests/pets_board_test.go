// pets_board_test.go
//
// Verifies the CircleAI.Pets port (pets_board.go): pet add/get + name ordering,
// vaccination record + VaccinationsFor (newest-first), weight record +
// WeightHistory (earliest-first), and appointment schedule + UpcomingAppointments
// (future-only relative to the wall clock, soonest-first).

package circleai_test

import (
	"testing"
	"time"

	circleai "github.com/bhengubv/CircleAI/go"
)

func TestPets_PetsVaccinationsWeights(t *testing.T) {
	b := circleai.NewInMemoryPetsBoard()
	dob := time.Date(2022, 5, 1, 0, 0, 0, 0, time.UTC)
	b.Add(circleai.Pet{PetId: "p1", Name: "Rex", Species: "Dog", DateOfBirth: dob})
	b.Add(circleai.Pet{PetId: "p2", Name: "Milo", Species: "Cat", DateOfBirth: dob})

	if p, ok := b.GetPet("p1"); !ok || p.Name != "Rex" {
		t.Fatalf("get pet = %+v ok=%v", p, ok)
	}
	pets := b.Pets()
	if len(pets) != 2 || pets[0].Name != "Milo" || pets[1].Name != "Rex" {
		t.Fatalf("pets name order wrong: %+v", pets)
	}

	t0 := time.Date(2026, 1, 1, 0, 0, 0, 0, time.UTC)
	booster := t0.AddDate(1, 0, 0)
	b.RecordVaccination(circleai.Vaccination{PetId: "p1", Vaccine: "Rabies", AdministeredUtc: t0, BoosterDueUtc: &booster})
	b.RecordVaccination(circleai.Vaccination{PetId: "p1", Vaccine: "Distemper", AdministeredUtc: t0.Add(48 * time.Hour)})
	vax := b.VaccinationsFor("p1")
	// Newest first: Distemper then Rabies.
	if len(vax) != 2 || vax[0].Vaccine != "Distemper" || vax[1].Vaccine != "Rabies" {
		t.Fatalf("vaccinations order wrong: %+v", vax)
	}

	b.RecordWeight(circleai.WeightSample{PetId: "p1", WeightKg: 12.0, AtUtc: t0.Add(48 * time.Hour)})
	b.RecordWeight(circleai.WeightSample{PetId: "p1", WeightKg: 10.0, AtUtc: t0})
	wh := b.WeightHistory("p1")
	// Earliest first: 10.0 then 12.0.
	if len(wh) != 2 || wh[0].WeightKg != 10.0 || wh[1].WeightKg != 12.0 {
		t.Fatalf("weight history order wrong: %+v", wh)
	}
}

func TestPets_UpcomingAppointments(t *testing.T) {
	b := circleai.NewInMemoryPetsBoard()
	now := time.Now().UTC()
	future1 := now.AddDate(0, 0, 7)
	future2 := now.AddDate(0, 0, 2)
	past := now.AddDate(0, 0, -1)
	b.Schedule(circleai.VetAppointment{ApptId: "a1", PetId: "p1", Reason: "Checkup", AtUtc: future1, Vet: "Dr A"})
	b.Schedule(circleai.VetAppointment{ApptId: "a2", PetId: "p1", Reason: "Vaccine", AtUtc: future2, Vet: "Dr B"})
	b.Schedule(circleai.VetAppointment{ApptId: "a3", PetId: "p1", Reason: "Old", AtUtc: past, Vet: "Dr C"})

	up := b.UpcomingAppointments()
	// Future only, soonest first: a2 (day +2) then a1 (day +7); a3 (past) excluded.
	if len(up) != 2 || up[0].ApptId != "a2" || up[1].ApptId != "a1" {
		t.Fatalf("upcoming appointments wrong: %+v", up)
	}
}
