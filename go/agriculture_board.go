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

// Interface guard.
var _ FarmBoard = (*InMemoryFarmBoard)(nil)
