// beauty_board_test.go
//
// Verifies the CircleAI.Beauty port (beauty_board.go): treatment add/get,
// appointment window query (ordered), profile save/get, and concern-based
// recommendation (case-insensitive, sorted).

package circleai_test

import (
	"testing"
	"time"

	circleai "github.com/bhengubv/CircleAI/go"
)

func TestBeauty_TreatmentsAndAppointments(t *testing.T) {
	b := circleai.NewInMemoryBeautyBoard()
	b.AddTreatment(circleai.Treatment{TreatmentId: "t1", Name: "Acne Facial", DurationMinutes: 60, Price: circleai.DecimalFromInt(400), Currency: "ZAR"})
	if got, ok := b.GetTreatment("t1"); !ok || got.Name != "Acne Facial" {
		t.Fatalf("get treatment = %+v ok=%v", got, ok)
	}
	base := time.Date(2026, 7, 8, 9, 0, 0, 0, time.UTC)
	b.Book(circleai.Appointment{ApptId: "a2", ClientName: "Sam", TreatmentId: "t1", AtUtc: base.Add(3 * time.Hour)})
	b.Book(circleai.Appointment{ApptId: "a1", ClientName: "Sam", TreatmentId: "t1", AtUtc: base.Add(time.Hour)})
	b.Book(circleai.Appointment{ApptId: "a3", ClientName: "Sam", TreatmentId: "t1", AtUtc: base.Add(48 * time.Hour)})

	appts := b.AppointmentsBetween(base, base.Add(6*time.Hour))
	if len(appts) != 2 || appts[0].ApptId != "a1" || appts[1].ApptId != "a2" {
		t.Fatalf("appointments-between ordered failed: %+v", appts)
	}
}

func TestBeauty_Recommend(t *testing.T) {
	b := circleai.NewInMemoryBeautyBoard()
	b.AddTreatment(circleai.Treatment{TreatmentId: "t1", Name: "Acne Facial", Price: circleai.DecimalFromInt(400)})
	b.AddTreatment(circleai.Treatment{TreatmentId: "t2", Name: "Anti-Ageing Peel", Price: circleai.DecimalFromInt(700)})
	b.AddTreatment(circleai.Treatment{TreatmentId: "t3", Name: "Massage", Price: circleai.DecimalFromInt(500)})
	b.SaveProfile(circleai.SkinProfile{ClientName: "Sam", SkinType: "combination", Concerns: []string{"acne", "ageing"}})
	if got, ok := b.GetProfile("Sam"); !ok || got.SkinType != "combination" {
		t.Fatalf("get profile = %+v ok=%v", got, ok)
	}

	recs := b.RecommendFor("Sam")
	if len(recs) != 2 || recs[0].TreatmentId != "t1" || recs[1].TreatmentId != "t2" {
		t.Fatalf("recommend failed: %+v", recs)
	}
	if empty := b.RecommendFor("Unknown"); len(empty) != 0 {
		t.Fatalf("recommend for unknown client must be empty: %+v", empty)
	}
}
