// hospitality_board_test.go
//
// Verifies the CircleAI.Hospitality port (hospitality_board.go): room add/get,
// availability on a date (booked + clean filtering), reserve/check-out with room
// cleaning flag, and notes newest-first.

package circleai_test

import (
	"testing"
	"time"

	circleai "github.com/bhengubv/CircleAI/go"
)

func TestHospitality_Availability(t *testing.T) {
	b := circleai.NewInMemoryHospitalityBoard()
	b.AddRoom(circleai.HotelRoom{RoomId: "101", Type: "Std", NightlyRate: circleai.DecimalFromInt(900), Currency: "ZAR", IsClean: true})
	b.AddRoom(circleai.HotelRoom{RoomId: "102", Type: "Std", NightlyRate: circleai.DecimalFromInt(900), Currency: "ZAR", IsClean: true})
	b.AddRoom(circleai.HotelRoom{RoomId: "103", Type: "Dlx", NightlyRate: circleai.DecimalFromInt(1500), Currency: "ZAR", IsClean: false})
	if got, ok := b.GetRoom("101"); !ok || got.Type != "Std" {
		t.Fatalf("get room = %+v ok=%v", got, ok)
	}

	date := time.Date(2026, 7, 10, 0, 0, 0, 0, time.UTC)
	// 101 booked over the date; 103 not clean; only 102 should be available.
	b.Reserve(circleai.GuestReservation{ReservationId: "r1", GuestName: "Sam", RoomId: "101",
		CheckIn: date.AddDate(0, 0, -1), CheckOut: date.AddDate(0, 0, 2)})
	avail := b.AvailableOn(date)
	if len(avail) != 1 || avail[0].RoomId != "102" {
		t.Fatalf("available-on failed: %+v", avail)
	}
}

func TestHospitality_CheckOutAndNotes(t *testing.T) {
	b := circleai.NewInMemoryHospitalityBoard()
	b.AddRoom(circleai.HotelRoom{RoomId: "201", IsClean: true})
	b.Reserve(circleai.GuestReservation{ReservationId: "r1", GuestName: "Sam", RoomId: "201"})
	if got, ok := b.GetReservation("r1"); !ok || got.GuestName != "Sam" {
		t.Fatalf("get reservation = %+v ok=%v", got, ok)
	}
	if err := b.CheckOut("r1", true); err != nil {
		t.Fatalf("checkout: %v", err)
	}
	if room, _ := b.GetRoom("201"); room.IsClean {
		t.Fatalf("room should be flagged not-clean after checkout")
	}
	if err := b.CheckOut("ghost", false); err == nil {
		t.Fatalf("checkout unknown reservation must error")
	}

	now := time.Now().UTC()
	b.AddNote(circleai.FrontDeskNote{NoteId: "n1", ReservationId: "r1", Body: "early", AtUtc: now.Add(-time.Hour)})
	b.AddNote(circleai.FrontDeskNote{NoteId: "n2", ReservationId: "r1", Body: "late", AtUtc: now})
	notes := b.NotesFor("r1")
	if len(notes) != 2 || notes[0].NoteId != "n2" {
		t.Fatalf("notes newest-first failed: %+v", notes)
	}
}
