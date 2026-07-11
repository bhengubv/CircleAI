// healthcare_board_test.go
//
// Verifies the CircleAI.Healthcare port (healthcare_board.go): patient register/
// get, appointment schedule/status-update/ordering (asc), and prescription
// ordering (desc), plus unknown-id errors and deterministic timestamp ties.

package circleai_test

import (
	"testing"
	"time"

	circleai "github.com/bhengubv/CircleAI/go"
)

func TestHealthcare_PatientRegisterGet(t *testing.T) {
	b := circleai.NewInMemoryHealthcareBoard()
	b.Register(circleai.Patient{PatientId: "p1", Name: "Ada", DateOfBirth: time.Date(1990, 1, 1, 0, 0, 0, 0, time.UTC)})
	if got, ok := b.GetPatient("p1"); !ok || got.Name != "Ada" {
		t.Fatalf("get p1 = %+v ok=%v", got, ok)
	}
	if _, ok := b.GetPatient("none"); ok {
		t.Fatalf("missing patient found")
	}
}

func TestHealthcare_AppointmentsOrderedAndStatus(t *testing.T) {
	b := circleai.NewInMemoryHealthcareBoard()
	base := time.Date(2026, 7, 1, 9, 0, 0, 0, time.UTC)
	b.Schedule(circleai.HealthAppointment{ApptId: "a2", PatientId: "p1", Provider: "Dr X", AtUtc: base.Add(2 * time.Hour), Status: "Booked"})
	b.Schedule(circleai.HealthAppointment{ApptId: "a1", PatientId: "p1", Provider: "Dr X", AtUtc: base, Status: "Booked"})
	b.Schedule(circleai.HealthAppointment{ApptId: "a3", PatientId: "p2", Provider: "Dr Y", AtUtc: base.Add(time.Hour), Status: "Booked"})

	appts := b.AppointmentsFor("p1")
	if len(appts) != 2 || appts[0].ApptId != "a1" || appts[1].ApptId != "a2" {
		t.Fatalf("appointments asc failed: %+v", appts)
	}
	if err := b.UpdateStatus("a1", "Completed"); err != nil {
		t.Fatalf("update status: %v", err)
	}
	if appts = b.AppointmentsFor("p1"); appts[0].Status != "Completed" {
		t.Fatalf("status not updated: %+v", appts[0])
	}
	if err := b.UpdateStatus("ghost", "X"); err == nil {
		t.Fatalf("unknown appointment status update must error")
	}
}

func TestHealthcare_PrescriptionsNewestFirst(t *testing.T) {
	b := circleai.NewInMemoryHealthcareBoard()
	base := time.Date(2026, 6, 1, 0, 0, 0, 0, time.UTC)
	b.Prescribe(circleai.Prescription{RxId: "r1", PatientId: "p1", MedicationName: "A", PrescribedUtc: base})
	b.Prescribe(circleai.Prescription{RxId: "r2", PatientId: "p1", MedicationName: "B", PrescribedUtc: base.Add(48 * time.Hour)})
	b.Prescribe(circleai.Prescription{RxId: "r3", PatientId: "p1", MedicationName: "C", PrescribedUtc: base.Add(24 * time.Hour)})
	rx := b.PrescriptionsFor("p1")
	if len(rx) != 3 || rx[0].RxId != "r2" || rx[1].RxId != "r3" || rx[2].RxId != "r1" {
		t.Fatalf("prescriptions desc failed: %+v", rx)
	}
	if len(b.PrescriptionsFor("other")) != 0 {
		t.Fatalf("other patient should have no prescriptions")
	}
}

func TestHealthcare_DeterministicTies(t *testing.T) {
	same := time.Date(2026, 5, 5, 5, 0, 0, 0, time.UTC)
	for i := 0; i < 5; i++ {
		b := circleai.NewInMemoryHealthcareBoard()
		b.Schedule(circleai.HealthAppointment{ApptId: "c", PatientId: "p", AtUtc: same})
		b.Schedule(circleai.HealthAppointment{ApptId: "a", PatientId: "p", AtUtc: same})
		b.Schedule(circleai.HealthAppointment{ApptId: "b", PatientId: "p", AtUtc: same})
		got := b.AppointmentsFor("p")
		if got[0].ApptId != "a" || got[1].ApptId != "b" || got[2].ApptId != "c" {
			t.Fatalf("iter %d tie order = %v", i, []string{got[0].ApptId, got[1].ApptId, got[2].ApptId})
		}
	}
}
