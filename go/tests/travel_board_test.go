// travel_board_test.go
//
// Verifies the CircleAI.Travel port (travel_board.go): flight/stay/trip storage,
// trip cost totalling (flights + nightly*nights), and upcoming-trips ordering.

package circleai_test

import (
	"testing"
	"time"

	circleai "github.com/bhengubv/CircleAI/go"
)

func TestTravel_TripCost(t *testing.T) {
	b := circleai.NewInMemoryTravelBoard()
	b.AddFlight(circleai.Flight{FlightId: "f1", From: "JNB", To: "CPT", Price: circleai.DecimalFromInt(1200), Currency: "ZAR"})
	// 3 nights at 900 = 2700.
	ci := time.Date(2026, 8, 1, 0, 0, 0, 0, time.UTC)
	b.AddStay(circleai.HotelStay{StayId: "s1", Hotel: "Grand", City: "CPT", CheckIn: ci, CheckOut: ci.AddDate(0, 0, 3), NightlyRate: circleai.DecimalFromInt(900), Currency: "ZAR"})
	b.Plan(circleai.TravelTrip{TripId: "t1", Name: "Coast", StartDate: ci, EndDate: ci.AddDate(0, 0, 3), FlightIds: []string{"f1"}, StayIds: []string{"s1"}})

	if got, ok := b.GetTrip("t1"); !ok || got.Name != "Coast" {
		t.Fatalf("get trip = %+v ok=%v", got, ok)
	}
	if got, ok := b.GetFlight("f1"); !ok || got.To != "CPT" {
		t.Fatalf("get flight = %+v ok=%v", got, ok)
	}
	if got, ok := b.GetStay("s1"); !ok || got.Hotel != "Grand" {
		t.Fatalf("get stay = %+v ok=%v", got, ok)
	}

	cost, err := b.TripCost("t1")
	if err != nil {
		t.Fatalf("trip cost: %v", err)
	}
	if !cost.Equal(circleai.DecimalFromInt(3900)) {
		t.Fatalf("trip cost = %s, want 3900", cost.String())
	}
	if _, err := b.TripCost("ghost"); err == nil {
		t.Fatalf("unknown trip cost must error")
	}
}

func TestTravel_UpcomingTrips(t *testing.T) {
	b := circleai.NewInMemoryTravelBoard()
	now := time.Date(2026, 7, 1, 0, 0, 0, 0, time.UTC)
	b.Plan(circleai.TravelTrip{TripId: "t1", Name: "B", StartDate: now.AddDate(0, 0, 20)})
	b.Plan(circleai.TravelTrip{TripId: "t2", Name: "A", StartDate: now.AddDate(0, 0, 10)})
	b.Plan(circleai.TravelTrip{TripId: "t3", Name: "Past", StartDate: now.AddDate(0, 0, -10)})

	up := b.UpcomingTrips(now)
	if len(up) != 2 || up[0].TripId != "t2" || up[1].TripId != "t1" {
		t.Fatalf("upcoming trips ordered failed: %+v", up)
	}
}
