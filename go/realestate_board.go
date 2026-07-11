// realestate_board.go
//
// Ports the CircleAI.RealEstate primitive vertical (RealEstatePrimitives.cs):
//   PropertyKind (enum)                               -> int consts (stable ordinals)
//   Property / Listing / Valuation / Viewing (records) -> value structs
//   IRealEstateBoard        -> RealEstateBoard interface (I-prefix dropped)
//   InMemoryRealEstateBoard -> InMemoryRealEstateBoard
//
// The RealEstateDomainContext (static prompt strings) and
// RealEstateCompanionAdapter (LLM-prompt wrapper) are out of scope for the
// deterministic in-memory board.
//
// MONEY: AskingPrice / EstimatedValue / averages use the shared exact Decimal.
// ActiveInSuburb returns active listings whose property is in the given suburb
// (case-insensitive), ordered by ListedUtc descending. SuburbAverage is the exact
// decimal mean of those listings' AskingPrice (nil when there are none).

package circleai

import (
	"errors"
	"sort"
	"strings"
	"sync"
	"time"
)

// PropertyKind is the type of a property. Ordinals match the C# enum
// (Apartment=0, House=1, Townhouse=2, Commercial=3, Land=4). Ports PropertyKind.
type PropertyKind int

const (
	// PropertyKindApartment is an apartment.
	PropertyKindApartment PropertyKind = iota
	// PropertyKindHouse is a house.
	PropertyKindHouse
	// PropertyKindTownhouse is a townhouse.
	PropertyKindTownhouse
	// PropertyKindCommercial is a commercial property.
	PropertyKindCommercial
	// PropertyKindLand is land.
	PropertyKindLand
)

// String renders the C# enum member name for a PropertyKind.
func (k PropertyKind) String() string {
	switch k {
	case PropertyKindApartment:
		return "Apartment"
	case PropertyKindHouse:
		return "House"
	case PropertyKindTownhouse:
		return "Townhouse"
	case PropertyKindCommercial:
		return "Commercial"
	case PropertyKindLand:
		return "Land"
	default:
		return "Unknown"
	}
}

// Property is a property record. Ports the Property record.
type Property struct {
	PropertyId  string
	Suburb      string
	Kind        PropertyKind
	Beds        int
	Baths       int
	FloorAreaM2 float64
}

// Listing is a for-sale listing. Ports the Listing record. AskingPrice uses exact
// Decimal.
type Listing struct {
	ListingId   string
	PropertyId  string
	AskingPrice Decimal
	Currency    string
	ListedUtc   time.Time
	IsActive    bool
}

// Valuation is a property valuation. Ports the Valuation record. EstimatedValue
// uses exact Decimal.
type Valuation struct {
	PropertyId     string
	EstimatedValue Decimal
	Source         string
	AtUtc          time.Time
}

// Viewing is a scheduled viewing. Ports the Viewing record.
type Viewing struct {
	ViewingId    string
	ListingId    string
	AttendeeName string
	AtUtc        time.Time
}

// RealEstateBoard is the properties/listings/valuations/viewings board. Ports
// IRealEstateBoard.
type RealEstateBoard interface {
	RegisterProperty(p Property)
	List(l Listing)
	// Close deactivates a listing; errors if the id is unknown.
	Close(listingId string) error
	Value(v Valuation)
	ScheduleViewing(v Viewing)
	// ActiveInSuburb lists active listings in suburb (case-insensitive), newest first.
	ActiveInSuburb(suburb string) ([]Listing, error)
	// SuburbAverage is the mean AskingPrice of active listings in suburb, or
	// (zero, false) when there are none.
	SuburbAverage(suburb string) (Decimal, bool, error)
}

// InMemoryRealEstateBoard is a concurrency-safe in-memory RealEstateBoard. Ports
// InMemoryRealEstateBoard (properties + listings in maps; valuations + viewings
// in ordered lists guarded by the mutex).
type InMemoryRealEstateBoard struct {
	mu       sync.RWMutex
	props    map[string]Property
	listings map[string]Listing
	vals     []Valuation
	viewings []Viewing
}

// NewInMemoryRealEstateBoard constructs an empty board.
func NewInMemoryRealEstateBoard() *InMemoryRealEstateBoard {
	return &InMemoryRealEstateBoard{
		props:    make(map[string]Property),
		listings: make(map[string]Listing),
		vals:     make([]Valuation, 0),
		viewings: make([]Viewing, 0),
	}
}

// RegisterProperty stores (or replaces by PropertyId) a property. Ports
// RegisterProperty.
func (b *InMemoryRealEstateBoard) RegisterProperty(p Property) {
	b.mu.Lock()
	b.props[p.PropertyId] = p
	b.mu.Unlock()
}

// List stores (or replaces by ListingId) a listing. Ports List.
func (b *InMemoryRealEstateBoard) List(l Listing) {
	b.mu.Lock()
	b.listings[l.ListingId] = l
	b.mu.Unlock()
}

// Close deactivates a listing (IsActive=false). Ports Close (throws on unknown id
// -> error).
func (b *InMemoryRealEstateBoard) Close(listingId string) error {
	b.mu.Lock()
	defer b.mu.Unlock()
	l, ok := b.listings[listingId]
	if !ok {
		return errors.New("Unknown listing " + listingId)
	}
	l.IsActive = false
	b.listings[listingId] = l
	return nil
}

// Value appends a valuation. Ports Value.
func (b *InMemoryRealEstateBoard) Value(v Valuation) {
	b.mu.Lock()
	b.vals = append(b.vals, v)
	b.mu.Unlock()
}

// ScheduleViewing appends a viewing. Ports ScheduleViewing.
func (b *InMemoryRealEstateBoard) ScheduleViewing(v Viewing) {
	b.mu.Lock()
	b.viewings = append(b.viewings, v)
	b.mu.Unlock()
}

// ActiveInSuburb lists active listings whose property is in suburb
// (case-insensitive), ordered by ListedUtc descending. Ports ActiveInSuburb
// (ArgumentException on blank suburb -> error). Equal timestamps break by
// ListingId for determinism.
func (b *InMemoryRealEstateBoard) ActiveInSuburb(suburb string) ([]Listing, error) {
	if strings.TrimSpace(suburb) == "" {
		return nil, errors.New("suburb required")
	}
	b.mu.RLock()
	out := make([]Listing, 0)
	for _, l := range b.listings {
		if !l.IsActive {
			continue
		}
		p, ok := b.props[l.PropertyId]
		if ok && strings.EqualFold(p.Suburb, suburb) {
			out = append(out, l)
		}
	}
	b.mu.RUnlock()
	sort.SliceStable(out, func(i, j int) bool {
		if !out[i].ListedUtc.Equal(out[j].ListedUtc) {
			return out[i].ListedUtc.After(out[j].ListedUtc)
		}
		return out[i].ListingId < out[j].ListingId
	})
	return out, nil
}

// SuburbAverage returns the mean AskingPrice of active listings in suburb, or
// (zero, false) when there are none. Ports SuburbAverage (returns decimal? null
// on an empty suburb -> (zero, false)).
func (b *InMemoryRealEstateBoard) SuburbAverage(suburb string) (Decimal, bool, error) {
	rows, err := b.ActiveInSuburb(suburb)
	if err != nil {
		return ZeroDecimal, false, err
	}
	if len(rows) == 0 {
		return ZeroDecimal, false, nil
	}
	var sum Decimal
	for _, l := range rows {
		sum = sum.Add(l.AskingPrice)
	}
	return sum.DivInt(len(rows)), true, nil
}

// Interface guard.
var _ RealEstateBoard = (*InMemoryRealEstateBoard)(nil)
