// travel_board.go
//
// Ports the CircleAI.Travel primitive vertical (TravelPrimitives.cs):
//   Flight / HotelStay / TravelTrip (records) -> value structs
//   ITravelBoard             -> TravelBoard interface (I-prefix dropped)
//   InMemoryTravelBoard      -> InMemoryTravelBoard
//
// The TravelDomainContext / TravelCompanionAdapter (LLM glue) are out of scope.
//
// The C# board has two Add overloads (Flight, HotelStay); Go has no overloading,
// so they become AddFlight / AddStay. All other members map 1:1.
//
// DETERMINISM: UpcomingTrips orders by StartDate ascending. TripCost sums flight
// prices plus per-stay NightlyRate * max(1, nights) using the shared exact
// Decimal (C# decimal); nights = whole days between CheckIn and CheckOut.

package circleai

import (
	"errors"
	"sort"
	"sync"
	"time"
)

// Flight is a flight segment. Ports the Flight record. Price uses the shared
// exact Decimal.
type Flight struct {
	FlightId   string
	From       string
	To         string
	DepartUtc  time.Time
	ArriveUtc  time.Time
	Carrier    string
	Cabin      string
	Price      Decimal
	Currency   string
}

// HotelStay is a hotel stay. Ports the HotelStay record. NightlyRate uses the
// shared exact Decimal.
type HotelStay struct {
	StayId      string
	Hotel       string
	City        string
	CheckIn     time.Time
	CheckOut    time.Time
	NightlyRate Decimal
	Currency    string
}

// TravelTrip is a planned trip referencing flights and stays. Ports the
// TravelTrip record.
type TravelTrip struct {
	TripId    string
	Name      string
	StartDate time.Time
	EndDate   time.Time
	FlightIds []string
	StayIds   []string
}

// TravelBoard is the flights/stays/trips board. Ports ITravelBoard.
type TravelBoard interface {
	// AddFlight stores (or replaces by FlightId) a flight. Ports Add(Flight).
	AddFlight(f Flight)
	// AddStay stores (or replaces by StayId) a stay. Ports Add(HotelStay).
	AddStay(s HotelStay)
	Plan(t TravelTrip)
	GetTrip(id string) (TravelTrip, bool)
	GetFlight(id string) (Flight, bool)
	GetStay(id string) (HotelStay, bool)
	// TripCost totals a trip's flights and stays. Errors on unknown trip.
	TripCost(tripId string) (Decimal, error)
	// UpcomingTrips lists trips starting at/after now, ordered by StartDate.
	UpcomingTrips(now time.Time) []TravelTrip
}

// InMemoryTravelBoard is a concurrency-safe in-memory TravelBoard. Ports
// InMemoryTravelBoard.
type InMemoryTravelBoard struct {
	mu      sync.Mutex
	flights map[string]Flight
	stays   map[string]HotelStay
	trips   map[string]TravelTrip
}

// NewInMemoryTravelBoard constructs an empty board.
func NewInMemoryTravelBoard() *InMemoryTravelBoard {
	return &InMemoryTravelBoard{
		flights: make(map[string]Flight),
		stays:   make(map[string]HotelStay),
		trips:   make(map[string]TravelTrip),
	}
}

// AddFlight stores (or replaces by FlightId) a flight. Ports Add(Flight).
func (b *InMemoryTravelBoard) AddFlight(f Flight) {
	b.mu.Lock()
	b.flights[f.FlightId] = f
	b.mu.Unlock()
}

// AddStay stores (or replaces by StayId) a stay. Ports Add(HotelStay).
func (b *InMemoryTravelBoard) AddStay(s HotelStay) {
	b.mu.Lock()
	b.stays[s.StayId] = s
	b.mu.Unlock()
}

// Plan stores (or replaces by TripId) a trip. Ports Plan.
func (b *InMemoryTravelBoard) Plan(t TravelTrip) {
	b.mu.Lock()
	b.trips[t.TripId] = t
	b.mu.Unlock()
}

// GetTrip returns the trip for id, or (zero,false). Ports GetTrip.
func (b *InMemoryTravelBoard) GetTrip(id string) (TravelTrip, bool) {
	b.mu.Lock()
	t, ok := b.trips[id]
	b.mu.Unlock()
	return t, ok
}

// GetFlight returns the flight for id, or (zero,false). Ports GetFlight.
func (b *InMemoryTravelBoard) GetFlight(id string) (Flight, bool) {
	b.mu.Lock()
	f, ok := b.flights[id]
	b.mu.Unlock()
	return f, ok
}

// GetStay returns the stay for id, or (zero,false). Ports GetStay.
func (b *InMemoryTravelBoard) GetStay(id string) (HotelStay, bool) {
	b.mu.Lock()
	s, ok := b.stays[id]
	b.mu.Unlock()
	return s, ok
}

// TripCost totals a trip's flights and stays. Ports TripCost (throws on unknown
// trip -> error).
func (b *InMemoryTravelBoard) TripCost(tripId string) (Decimal, error) {
	b.mu.Lock()
	defer b.mu.Unlock()
	t, ok := b.trips[tripId]
	if !ok {
		return Decimal{}, errors.New("Unknown trip " + tripId)
	}
	var total Decimal
	for _, fid := range t.FlightIds {
		if f, ok := b.flights[fid]; ok {
			total = total.Add(f.Price)
		}
	}
	for _, sid := range t.StayIds {
		if s, ok := b.stays[sid]; ok {
			nights := int(s.CheckOut.Sub(s.CheckIn).Hours() / 24)
			if nights < 1 {
				nights = 1
			}
			total = total.Add(s.NightlyRate.Mul(DecimalFromInt(int64(nights))))
		}
	}
	return total, nil
}

// UpcomingTrips lists trips starting at/after now, ordered by StartDate. Ports
// UpcomingTrips.
func (b *InMemoryTravelBoard) UpcomingTrips(now time.Time) []TravelTrip {
	b.mu.Lock()
	out := make([]TravelTrip, 0)
	for _, t := range b.trips {
		if !t.StartDate.Before(now) {
			out = append(out, t)
		}
	}
	b.mu.Unlock()
	sort.SliceStable(out, func(i, j int) bool { return out[i].StartDate.Before(out[j].StartDate) })
	return out
}

// Interface guard.
var _ TravelBoard = (*InMemoryTravelBoard)(nil)
