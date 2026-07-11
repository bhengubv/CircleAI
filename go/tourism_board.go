// tourism_board.go
//
// Ports the CircleAI.Tourism primitive vertical (TourismPrimitives.cs):
//   Attraction / ItineraryItem / Itinerary / TourismBooking (records)
//                            -> value structs
//   ITourismBoard            -> TourismBoard interface (I-prefix dropped)
//   InMemoryTourismBoard     -> InMemoryTourismBoard
//
// The TourismDomainContext / TourismCompanionAdapter (LLM glue) are out of scope.
//
// DETERMINISM: AttractionsInCity and ByTag order by Name ascending (matching the
// C# OrderBy(a => a.Name); a stable sort preserves prior order on ties). Bookings
// preserves insertion order (C# backing List). TourismBooking.TotalPrice uses
// the shared exact Decimal (C# decimal).

package circleai

import (
	"sort"
	"strings"
	"sync"
	"time"
)

// Attraction is a tourist attraction. Ports the Attraction record. Tags mirrors
// the C# IReadOnlyList<string>.
type Attraction struct {
	AttractionId string
	Name         string
	City         string
	Country      string
	Lat          float64
	Lon          float64
	Tags         []string
}

// ItineraryItem is a scheduled visit within an itinerary day. Ports the
// ItineraryItem record. StartLocal/EndLocal mirror the C# TimeSpan (time-of-day
// offsets). Note is a *string to model the C# nullable string.
type ItineraryItem struct {
	DayIndex     int
	StartLocal   time.Duration
	EndLocal     time.Duration
	AttractionId string
	Note         *string
}

// Itinerary is a titled sequence of itinerary items. Ports the Itinerary record.
type Itinerary struct {
	ItineraryId string
	Title       string
	Items       []ItineraryItem
}

// TourismBooking is a booked itinerary. Ports the TourismBooking record.
// TotalPrice uses the shared exact Decimal.
type TourismBooking struct {
	BookingId   string
	ItineraryId string
	StartDate   time.Time
	Travelers   int
	TotalPrice  Decimal
	Currency    string
}

// TourismBoard is the attractions/itineraries/bookings board. Ports
// ITourismBoard.
type TourismBoard interface {
	Add(a Attraction)
	// AttractionsInCity lists a city's attractions (case-insensitive) by Name.
	// Panics on blank city, matching the C# ArgumentException.
	AttractionsInCity(city string) []Attraction
	// ByTag lists attractions carrying the tag (case-insensitive) by Name. Panics
	// on blank tag.
	ByTag(tag string) []Attraction
	Plan(i Itinerary)
	GetItinerary(id string) (Itinerary, bool)
	Book(b TourismBooking)
	// Bookings lists all bookings in insertion order.
	Bookings() []TourismBooking
}

// InMemoryTourismBoard is a concurrency-safe in-memory TourismBoard. Ports
// InMemoryTourismBoard.
type InMemoryTourismBoard struct {
	mu          sync.Mutex
	attractions map[string]Attraction
	itineraries map[string]Itinerary
	bookings    []TourismBooking
}

// NewInMemoryTourismBoard constructs an empty board.
func NewInMemoryTourismBoard() *InMemoryTourismBoard {
	return &InMemoryTourismBoard{
		attractions: make(map[string]Attraction),
		itineraries: make(map[string]Itinerary),
	}
}

// Add stores (or replaces by AttractionId) an attraction. Ports Add.
func (b *InMemoryTourismBoard) Add(a Attraction) {
	b.mu.Lock()
	b.attractions[a.AttractionId] = a
	b.mu.Unlock()
}

// AttractionsInCity lists a city's attractions by Name. Ports AttractionsInCity.
func (b *InMemoryTourismBoard) AttractionsInCity(city string) []Attraction {
	if strings.TrimSpace(city) == "" {
		panic("city required")
	}
	b.mu.Lock()
	out := make([]Attraction, 0)
	for _, a := range b.attractions {
		if strings.EqualFold(a.City, city) {
			out = append(out, a)
		}
	}
	b.mu.Unlock()
	sort.SliceStable(out, func(i, j int) bool { return out[i].Name < out[j].Name })
	return out
}

// ByTag lists attractions carrying the tag by Name. Ports ByTag.
func (b *InMemoryTourismBoard) ByTag(tag string) []Attraction {
	if strings.TrimSpace(tag) == "" {
		panic("tag required")
	}
	b.mu.Lock()
	out := make([]Attraction, 0)
	for _, a := range b.attractions {
		for _, t := range a.Tags {
			if strings.EqualFold(t, tag) {
				out = append(out, a)
				break
			}
		}
	}
	b.mu.Unlock()
	sort.SliceStable(out, func(i, j int) bool { return out[i].Name < out[j].Name })
	return out
}

// Plan stores (or replaces by ItineraryId) an itinerary. Ports Plan.
func (b *InMemoryTourismBoard) Plan(i Itinerary) {
	b.mu.Lock()
	b.itineraries[i.ItineraryId] = i
	b.mu.Unlock()
}

// GetItinerary returns the itinerary for id, or (zero,false). Ports
// GetItinerary.
func (b *InMemoryTourismBoard) GetItinerary(id string) (Itinerary, bool) {
	b.mu.Lock()
	i, ok := b.itineraries[id]
	b.mu.Unlock()
	return i, ok
}

// Book appends a booking. Ports Book.
func (b *InMemoryTourismBoard) Book(bk TourismBooking) {
	b.mu.Lock()
	b.bookings = append(b.bookings, bk)
	b.mu.Unlock()
}

// Bookings lists all bookings in insertion order. Ports the Bookings property.
func (b *InMemoryTourismBoard) Bookings() []TourismBooking {
	b.mu.Lock()
	out := make([]TourismBooking, len(b.bookings))
	copy(out, b.bookings)
	b.mu.Unlock()
	return out
}

// Interface guard.
var _ TourismBoard = (*InMemoryTourismBoard)(nil)
