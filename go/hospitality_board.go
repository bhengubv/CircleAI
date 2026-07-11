// hospitality_board.go
//
// Ports the CircleAI.Hospitality primitive vertical (HospitalityPrimitives.cs):
//   HotelRoom / GuestReservation / FrontDeskNote (records) -> value structs
//   IHospitalityBoard        -> HospitalityBoard interface (I-prefix dropped)
//   InMemoryHospitalityBoard -> InMemoryHospitalityBoard
//
// The HospitalityDomainContext / HospitalityCompanionAdapter (LLM glue) are out
// of scope.
//
// DETERMINISM: AvailableOn mirrors a ConcurrentDictionary in C# (no defined
// order); this port sorts by RoomId. NotesFor orders by AtUtc descending.
// HotelRoom.NightlyRate uses the shared exact Decimal (C# decimal). CheckIn /
// CheckOut are date-valued time.Time (C# DateTime).

package circleai

import (
	"errors"
	"sort"
	"sync"
	"time"
)

// HotelRoom is a hotel room. Ports the HotelRoom record. NightlyRate uses the
// shared exact Decimal.
type HotelRoom struct {
	RoomId      string
	Type        string
	NightlyRate Decimal
	Currency    string
	IsClean     bool
}

// GuestReservation is a room reservation. Ports the GuestReservation record.
type GuestReservation struct {
	ReservationId string
	GuestName     string
	RoomId        string
	CheckIn       time.Time
	CheckOut      time.Time
}

// FrontDeskNote is a front-desk note on a reservation. Ports the FrontDeskNote
// record.
type FrontDeskNote struct {
	NoteId        string
	ReservationId string
	Body          string
	AtUtc         time.Time
}

// HospitalityBoard is the rooms/reservations/notes board. Ports
// IHospitalityBoard.
type HospitalityBoard interface {
	AddRoom(r HotelRoom)
	GetRoom(id string) (HotelRoom, bool)
	// AvailableOn lists clean rooms not booked over the given date, sorted by RoomId.
	AvailableOn(date time.Time) []HotelRoom
	Reserve(r GuestReservation)
	// CheckOut marks a reservation checked out; flips the room to not-clean when
	// roomNeedsCleaning. Errors on unknown reservation.
	CheckOut(reservationId string, roomNeedsCleaning bool) error
	GetReservation(id string) (GuestReservation, bool)
	AddNote(n FrontDeskNote)
	// NotesFor lists a reservation's notes newest-first.
	NotesFor(reservationId string) []FrontDeskNote
}

// InMemoryHospitalityBoard is a concurrency-safe in-memory HospitalityBoard.
// Ports InMemoryHospitalityBoard.
type InMemoryHospitalityBoard struct {
	mu    sync.Mutex
	rooms map[string]HotelRoom
	res   map[string]GuestReservation
	notes []FrontDeskNote
}

// NewInMemoryHospitalityBoard constructs an empty board.
func NewInMemoryHospitalityBoard() *InMemoryHospitalityBoard {
	return &InMemoryHospitalityBoard{
		rooms: make(map[string]HotelRoom),
		res:   make(map[string]GuestReservation),
	}
}

// AddRoom stores (or replaces by RoomId) a room. Ports AddRoom.
func (b *InMemoryHospitalityBoard) AddRoom(r HotelRoom) {
	b.mu.Lock()
	b.rooms[r.RoomId] = r
	b.mu.Unlock()
}

// GetRoom returns the room for id, or (zero,false). Ports GetRoom.
func (b *InMemoryHospitalityBoard) GetRoom(id string) (HotelRoom, bool) {
	b.mu.Lock()
	r, ok := b.rooms[id]
	b.mu.Unlock()
	return r, ok
}

// AvailableOn lists clean, unbooked rooms for a date, sorted by RoomId. Ports
// AvailableOn (booked = CheckIn <= date < CheckOut).
func (b *InMemoryHospitalityBoard) AvailableOn(date time.Time) []HotelRoom {
	b.mu.Lock()
	booked := make(map[string]struct{})
	for _, r := range b.res {
		if !r.CheckIn.After(date) && r.CheckOut.After(date) {
			booked[r.RoomId] = struct{}{}
		}
	}
	out := make([]HotelRoom, 0)
	for _, r := range b.rooms {
		if _, isBooked := booked[r.RoomId]; !isBooked && r.IsClean {
			out = append(out, r)
		}
	}
	b.mu.Unlock()
	sort.SliceStable(out, func(i, j int) bool { return out[i].RoomId < out[j].RoomId })
	return out
}

// Reserve stores (or replaces by ReservationId) a reservation. Ports Reserve.
func (b *InMemoryHospitalityBoard) Reserve(r GuestReservation) {
	b.mu.Lock()
	b.res[r.ReservationId] = r
	b.mu.Unlock()
}

// CheckOut checks a reservation out, optionally marking the room unclean. Ports
// CheckOut (throws on unknown reservation -> error).
func (b *InMemoryHospitalityBoard) CheckOut(reservationId string, roomNeedsCleaning bool) error {
	b.mu.Lock()
	defer b.mu.Unlock()
	r, ok := b.res[reservationId]
	if !ok {
		return errors.New("Unknown reservation " + reservationId)
	}
	if roomNeedsCleaning {
		if room, ok := b.rooms[r.RoomId]; ok {
			room.IsClean = false
			b.rooms[r.RoomId] = room
		}
	}
	return nil
}

// GetReservation returns the reservation for id, or (zero,false). Ports
// GetReservation.
func (b *InMemoryHospitalityBoard) GetReservation(id string) (GuestReservation, bool) {
	b.mu.Lock()
	r, ok := b.res[id]
	b.mu.Unlock()
	return r, ok
}

// AddNote appends a front-desk note. Ports AddNote.
func (b *InMemoryHospitalityBoard) AddNote(n FrontDeskNote) {
	b.mu.Lock()
	b.notes = append(b.notes, n)
	b.mu.Unlock()
}

// NotesFor lists a reservation's notes newest-first. Ports NotesFor.
func (b *InMemoryHospitalityBoard) NotesFor(reservationId string) []FrontDeskNote {
	b.mu.Lock()
	out := make([]FrontDeskNote, 0)
	for _, n := range b.notes {
		if n.ReservationId == reservationId {
			out = append(out, n)
		}
	}
	b.mu.Unlock()
	sort.SliceStable(out, func(i, j int) bool { return out[i].AtUtc.After(out[j].AtUtc) })
	return out
}

// Interface guard.
var _ HospitalityBoard = (*InMemoryHospitalityBoard)(nil)
