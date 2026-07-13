// agriculture_board.go
//
// Ports the CircleAI.Agriculture primitive vertical (AgriculturePrimitives.cs):
//   Field / Crop / YieldRecord (records) -> value structs
//   IFarmBoard               -> FarmBoard interface (I-prefix dropped)
//   InMemoryFarmBoard        -> InMemoryFarmBoard
//
// The AgricultureDomainContext / AgricultureCompanionAdapter (LLM glue) are out
// of scope.
//
// DETERMINISM: CropsForField orders by PlantedOn ascending (ties by CropId for
// stable output where the C# LINQ OrderBy is a stable sort). AvgYieldOfVariety
// joins yields to their crop by CropId, filters by variety (case-insensitive),
// and averages TonsPerHa (0.0 when no rows).

package circleai

import (
	"sort"
	"strings"
	"sync"
	"time"
)

// Field is a farm field. Ports the Field record. AreaHa is area in hectares.
type Field struct {
	FieldId        string
	AreaHa         float64
	SoilType       string
	IrrigationKind string
}

// Crop is a planting on a field. Ports the Crop record. ExpectedHarvest is a
// *time.Time to model the C# nullable DateTime.
type Crop struct {
	CropId          string
	FieldId         string
	Variety         string
	PlantedOn       time.Time
	ExpectedHarvest *time.Time
}

// YieldRecord is a harvest yield for a crop. Ports the YieldRecord record.
type YieldRecord struct {
	CropId       string
	TonsPerHa    float64
	HarvestedOn  time.Time
}

// FarmBoard is the fields/crops/yields board. Ports IFarmBoard.
type FarmBoard interface {
	AddField(f Field)
	Plant(c Crop)
	RecordYield(y YieldRecord)
	GetField(id string) (Field, bool)
	// CropsForField lists a field's crops ordered by PlantedOn ascending.
	CropsForField(fieldId string) []Crop
	// AvgYieldOfVariety is the mean TonsPerHa across a variety's yields (0 if none).
	AvgYieldOfVariety(variety string) float64
	// FieldCount returns the number of fields.
	FieldCount() int
	// RemoveField drops a field by id, returning true if it was present.
	RemoveField(fieldId string) bool
	// TotalAreaHa is the summed AreaHa across all fields.
	TotalAreaHa() float64
	// FieldsBySoil lists fields of a soil type (case-insensitive), largest-area first.
	FieldsBySoil(soilType string) []Field
	// DueForHarvest lists crops with an ExpectedHarvest at or before asOf, earliest first.
	DueForHarvest(asOf time.Time) []Crop
	// BestYieldingVariety returns the variety with the highest mean yield, or ("",false).
	BestYieldingVariety() (string, bool)
}

// InMemoryFarmBoard is a concurrency-safe in-memory FarmBoard. Ports
// InMemoryFarmBoard.
type InMemoryFarmBoard struct {
	mu     sync.Mutex
	fields map[string]Field
	crops  map[string]Crop
	yields []YieldRecord
}

// NewInMemoryFarmBoard constructs an empty board.
func NewInMemoryFarmBoard() *InMemoryFarmBoard {
	return &InMemoryFarmBoard{
		fields: make(map[string]Field),
		crops:  make(map[string]Crop),
	}
}

// AddField stores (or replaces by FieldId) a field. Ports AddField.
func (b *InMemoryFarmBoard) AddField(f Field) {
	b.mu.Lock()
	b.fields[f.FieldId] = f
	b.mu.Unlock()
}

// Plant stores (or replaces by CropId) a crop. Ports Plant.
func (b *InMemoryFarmBoard) Plant(c Crop) {
	b.mu.Lock()
	b.crops[c.CropId] = c
	b.mu.Unlock()
}

// RecordYield appends a yield record. Ports RecordYield.
func (b *InMemoryFarmBoard) RecordYield(y YieldRecord) {
	b.mu.Lock()
	b.yields = append(b.yields, y)
	b.mu.Unlock()
}

// GetField returns the field for id, or (zero,false). Ports GetField.
func (b *InMemoryFarmBoard) GetField(id string) (Field, bool) {
	b.mu.Lock()
	f, ok := b.fields[id]
	b.mu.Unlock()
	return f, ok
}

// CropsForField lists a field's crops ordered by PlantedOn ascending. Ports
// CropsForField.
func (b *InMemoryFarmBoard) CropsForField(fieldId string) []Crop {
	b.mu.Lock()
	out := make([]Crop, 0)
	for _, c := range b.crops {
		if c.FieldId == fieldId {
			out = append(out, c)
		}
	}
	b.mu.Unlock()
	sort.SliceStable(out, func(i, j int) bool {
		if !out[i].PlantedOn.Equal(out[j].PlantedOn) {
			return out[i].PlantedOn.Before(out[j].PlantedOn)
		}
		return out[i].CropId < out[j].CropId
	})
	return out
}

// AvgYieldOfVariety averages TonsPerHa across a variety's yields. Ports
// AvgYieldOfVariety.
func (b *InMemoryFarmBoard) AvgYieldOfVariety(variety string) float64 {
	b.mu.Lock()
	defer b.mu.Unlock()
	var sum float64
	var n int
	for _, y := range b.yields {
		c, ok := b.crops[y.CropId]
		if ok && strings.EqualFold(c.Variety, variety) {
			sum += y.TonsPerHa
			n++
		}
	}
	if n == 0 {
		return 0.0
	}
	return sum / float64(n)
}

// FieldCount returns the number of fields. Ports InMemoryFarmBoard.FieldCount.
func (b *InMemoryFarmBoard) FieldCount() int {
	b.mu.Lock()
	defer b.mu.Unlock()
	return len(b.fields)
}

// RemoveField drops a field by id, returning true if it was present. Ports
// InMemoryFarmBoard.RemoveField (TryRemove).
func (b *InMemoryFarmBoard) RemoveField(fieldId string) bool {
	b.mu.Lock()
	defer b.mu.Unlock()
	_, ok := b.fields[fieldId]
	delete(b.fields, fieldId)
	return ok
}

// TotalAreaHa is the summed AreaHa across all fields. Ports
// InMemoryFarmBoard.TotalAreaHa (Sum(f => f.AreaHa)).
func (b *InMemoryFarmBoard) TotalAreaHa() float64 {
	b.mu.Lock()
	defer b.mu.Unlock()
	var total float64
	for _, f := range b.fields {
		total += f.AreaHa
	}
	return total
}

// FieldsBySoil lists fields whose SoilType matches (case-insensitive), ordered by
// AreaHa descending. Ports InMemoryFarmBoard.FieldsBySoil.
func (b *InMemoryFarmBoard) FieldsBySoil(soilType string) []Field {
	b.mu.Lock()
	out := make([]Field, 0)
	for _, f := range b.fields {
		if strings.EqualFold(f.SoilType, soilType) {
			out = append(out, f)
		}
	}
	b.mu.Unlock()
	sort.SliceStable(out, func(i, j int) bool { return out[i].AreaHa > out[j].AreaHa })
	return out
}

// DueForHarvest lists crops whose ExpectedHarvest is set and at or before asOf,
// ordered by ExpectedHarvest ascending. Ports InMemoryFarmBoard.DueForHarvest.
func (b *InMemoryFarmBoard) DueForHarvest(asOf time.Time) []Crop {
	b.mu.Lock()
	out := make([]Crop, 0)
	for _, c := range b.crops {
		if c.ExpectedHarvest != nil && !c.ExpectedHarvest.After(asOf) {
			out = append(out, c)
		}
	}
	b.mu.Unlock()
	sort.SliceStable(out, func(i, j int) bool { return out[i].ExpectedHarvest.Before(*out[j].ExpectedHarvest) })
	return out
}

// BestYieldingVariety returns the variety with the highest mean TonsPerHa across
// yields whose crop is known, or ("", false) when there are no such yields.
// Grouping is case-insensitive on Variety, keeping the first-encountered spelling
// (in yield insertion order); ties resolve to the earliest-encountered variety
// (stable sort over insertion order). Ports InMemoryFarmBoard.BestYieldingVariety.
func (b *InMemoryFarmBoard) BestYieldingVariety() (string, bool) {
	b.mu.Lock()
	defer b.mu.Unlock()
	type group struct {
		variety string
		sum     float64
		count   int
		order   int
	}
	groups := make(map[string]*group)
	order := 0
	for _, y := range b.yields {
		c, ok := b.crops[y.CropId]
		if !ok {
			continue
		}
		key := strings.ToUpper(c.Variety)
		g, exists := groups[key]
		if !exists {
			g = &group{variety: c.Variety, order: order}
			groups[key] = g
			order++
		}
		g.sum += y.TonsPerHa
		g.count++
	}
	if len(groups) == 0 {
		return "", false
	}
	ordered := make([]*group, 0, len(groups))
	for _, g := range groups {
		ordered = append(ordered, g)
	}
	// Preserve first-encounter order, then stably order by average descending —
	// mirroring C# GroupBy (encounter order) + OrderByDescending(Avg) + First.
	sort.SliceStable(ordered, func(i, j int) bool { return ordered[i].order < ordered[j].order })
	sort.SliceStable(ordered, func(i, j int) bool {
		return ordered[i].sum/float64(ordered[i].count) > ordered[j].sum/float64(ordered[j].count)
	})
	return ordered[0].variety, true
}

// Interface guard.
var _ FarmBoard = (*InMemoryFarmBoard)(nil)
