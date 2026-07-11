// tourism_board_test.go
//
// Verifies the CircleAI.Tourism port (tourism_board.go): attractions in a city
// and by tag (both ordered by Name, case-insensitive), itinerary plan/get, and
// booking insertion order.

package circleai_test

import (
	"testing"
	"time"

	circleai "github.com/bhengubv/CircleAI/go"
)

func TestTourism_AttractionsAndTags(t *testing.T) {
	b := circleai.NewInMemoryTourismBoard()
	b.Add(circleai.Attraction{AttractionId: "a1", Name: "Table Mountain", City: "Cape Town", Country: "ZA", Tags: []string{"nature", "hike"}})
	b.Add(circleai.Attraction{AttractionId: "a2", Name: "Aquarium", City: "cape town", Country: "ZA", Tags: []string{"family"}})
	b.Add(circleai.Attraction{AttractionId: "a3", Name: "Union Buildings", City: "Pretoria", Country: "ZA", Tags: []string{"nature"}})

	city := b.AttractionsInCity("Cape Town")
	if len(city) != 2 || city[0].Name != "Aquarium" || city[1].Name != "Table Mountain" {
		t.Fatalf("attractions-in-city ordered by Name failed: %+v", city)
	}
	nature := b.ByTag("NATURE")
	if len(nature) != 2 || nature[0].Name != "Table Mountain" || nature[1].Name != "Union Buildings" {
		t.Fatalf("by-tag ordered by Name failed: %+v", nature)
	}
}

func TestTourism_ItineraryAndBookings(t *testing.T) {
	b := circleai.NewInMemoryTourismBoard()
	b.Plan(circleai.Itinerary{ItineraryId: "i1", Title: "Weekend", Items: []circleai.ItineraryItem{
		{DayIndex: 1, StartLocal: 9 * time.Hour, EndLocal: 11 * time.Hour, AttractionId: "a1"},
	}})
	if got, ok := b.GetItinerary("i1"); !ok || got.Title != "Weekend" || len(got.Items) != 1 {
		t.Fatalf("get itinerary = %+v ok=%v", got, ok)
	}
	if _, ok := b.GetItinerary("none"); ok {
		t.Fatalf("missing itinerary found")
	}

	b.Book(circleai.TourismBooking{BookingId: "b1", ItineraryId: "i1", Travelers: 2, TotalPrice: circleai.DecimalFromInt(2000), Currency: "ZAR"})
	b.Book(circleai.TourismBooking{BookingId: "b2", ItineraryId: "i1", Travelers: 1, TotalPrice: circleai.DecimalFromInt(1000), Currency: "ZAR"})
	got := b.Bookings()
	if len(got) != 2 || got[0].BookingId != "b1" || got[1].BookingId != "b2" {
		t.Fatalf("bookings insertion order failed: %+v", got)
	}
}
