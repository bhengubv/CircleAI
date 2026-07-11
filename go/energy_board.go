// energy_board.go
//
// Ports the CircleAI.Energy primitive vertical (EnergyPrimitives.cs):
//   MeterReading / EnergyTariff / Outage (records) -> value structs
//   IEnergyBoard             -> EnergyBoard interface (I-prefix dropped)
//   InMemoryEnergyBoard      -> InMemoryEnergyBoard
//
// The EnergyDomainContext / EnergyCompanionAdapter (LLM glue) are out of scope.
//
// DETERMINISM: ReadingsFor orders by AtUtc ascending. TotalKwhSince is the
// last-minus-first Kwh over that window (0 when fewer than two readings).
// EstimateCost casts kwh * PeakKwhRate (both float64) to the shared exact
// Decimal, matching the C# (decimal)(...) cast. ActiveOutages mirrors an
// unordered ConcurrentDictionary in C#; this port sorts by OutageId. Outage.End
// Utc / Reason are pointers for the C# nullable fields.

package circleai

import (
	"errors"
	"sort"
	"sync"
	"time"
)

// MeterReading is a cumulative meter reading. Ports the MeterReading record.
type MeterReading struct {
	MeterId string
	Kwh     float64
	AtUtc   time.Time
}

// EnergyTariff is an energy tariff. Ports the EnergyTariff record.
type EnergyTariff struct {
	TariffId       string
	Name           string
	PeakKwhRate    float64
	OffPeakKwhRate float64
	Currency       string
}

// Outage is a logged supply outage. Ports the Outage record. EndUtc and Reason
// are pointers to model the C# nullable fields (EndUtc==nil means still active).
type Outage struct {
	OutageId string
	Area     string
	StartUtc time.Time
	EndUtc   *time.Time
	Reason   *string
}

// EnergyBoard is the readings/tariffs/outages board. Ports IEnergyBoard.
type EnergyBoard interface {
	Record(r MeterReading)
	// ReadingsFor lists a meter's readings at/after since, oldest-first.
	ReadingsFor(meterId string, since time.Time) []MeterReading
	// TotalKwhSince is last-minus-first Kwh over the window (0 if < 2 readings).
	TotalKwhSince(meterId string, since time.Time) float64
	SetTariff(t EnergyTariff)
	GetTariff(id string) (EnergyTariff, bool)
	// EstimateCost prices TotalKwhSince at the tariff's peak rate. Errors on
	// unknown tariff.
	EstimateCost(meterId, tariffId string, since time.Time) (Decimal, error)
	LogOutage(o Outage)
	// ActiveOutages lists outages with no end time, sorted by OutageId.
	ActiveOutages() []Outage
}

// InMemoryEnergyBoard is a concurrency-safe in-memory EnergyBoard. Ports
// InMemoryEnergyBoard.
type InMemoryEnergyBoard struct {
	mu       sync.Mutex
	readings []MeterReading
	tariffs  map[string]EnergyTariff
	outages  map[string]Outage
}

// NewInMemoryEnergyBoard constructs an empty board.
func NewInMemoryEnergyBoard() *InMemoryEnergyBoard {
	return &InMemoryEnergyBoard{
		tariffs: make(map[string]EnergyTariff),
		outages: make(map[string]Outage),
	}
}

// Record appends a meter reading. Ports Record.
func (b *InMemoryEnergyBoard) Record(r MeterReading) {
	b.mu.Lock()
	b.readings = append(b.readings, r)
	b.mu.Unlock()
}

// ReadingsFor lists a meter's readings at/after since, oldest-first. Ports
// ReadingsFor.
func (b *InMemoryEnergyBoard) ReadingsFor(meterId string, since time.Time) []MeterReading {
	b.mu.Lock()
	out := make([]MeterReading, 0)
	for _, r := range b.readings {
		if r.MeterId == meterId && !r.AtUtc.Before(since) {
			out = append(out, r)
		}
	}
	b.mu.Unlock()
	sort.SliceStable(out, func(i, j int) bool { return out[i].AtUtc.Before(out[j].AtUtc) })
	return out
}

// TotalKwhSince is last-minus-first Kwh over the window. Ports TotalKwhSince.
func (b *InMemoryEnergyBoard) TotalKwhSince(meterId string, since time.Time) float64 {
	rows := b.ReadingsFor(meterId, since)
	if len(rows) < 2 {
		return 0.0
	}
	return rows[len(rows)-1].Kwh - rows[0].Kwh
}

// SetTariff stores (or replaces by TariffId) a tariff. Ports SetTariff.
func (b *InMemoryEnergyBoard) SetTariff(t EnergyTariff) {
	b.mu.Lock()
	b.tariffs[t.TariffId] = t
	b.mu.Unlock()
}

// GetTariff returns the tariff for id, or (zero,false). Ports GetTariff.
func (b *InMemoryEnergyBoard) GetTariff(id string) (EnergyTariff, bool) {
	b.mu.Lock()
	t, ok := b.tariffs[id]
	b.mu.Unlock()
	return t, ok
}

// EstimateCost prices TotalKwhSince at the tariff's peak rate. Ports
// EstimateCost (throws on unknown tariff -> error).
func (b *InMemoryEnergyBoard) EstimateCost(meterId, tariffId string, since time.Time) (Decimal, error) {
	b.mu.Lock()
	t, ok := b.tariffs[tariffId]
	b.mu.Unlock()
	if !ok {
		return Decimal{}, errors.New("Unknown tariff " + tariffId)
	}
	kwh := b.TotalKwhSince(meterId, since)
	return DecimalFromFloat(kwh * t.PeakKwhRate), nil
}

// LogOutage stores (or replaces by OutageId) an outage. Ports LogOutage.
func (b *InMemoryEnergyBoard) LogOutage(o Outage) {
	b.mu.Lock()
	b.outages[o.OutageId] = o
	b.mu.Unlock()
}

// ActiveOutages lists outages with no end time, sorted by OutageId. Ports
// ActiveOutages.
func (b *InMemoryEnergyBoard) ActiveOutages() []Outage {
	b.mu.Lock()
	out := make([]Outage, 0)
	for _, o := range b.outages {
		if o.EndUtc == nil {
			out = append(out, o)
		}
	}
	b.mu.Unlock()
	sort.SliceStable(out, func(i, j int) bool { return out[i].OutageId < out[j].OutageId })
	return out
}

// Interface guard.
var _ EnergyBoard = (*InMemoryEnergyBoard)(nil)
