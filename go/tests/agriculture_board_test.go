// agriculture_board_test.go
//
// Verifies the CircleAI.Agriculture port (agriculture_board.go): field add/get,
// crops-for-field ordered by planting date, and average yield by variety
// (case-insensitive join, 0 when none).

package circleai_test

import (
	"math"
	"testing"
	"time"

	circleai "github.com/bhengubv/CircleAI/go"
)

func TestAgriculture_FieldsAndCrops(t *testing.T) {
	b := circleai.NewInMemoryFarmBoard()
	b.AddField(circleai.Field{FieldId: "f1", AreaHa: 12.5, SoilType: "loam", IrrigationKind: "drip"})
	if got, ok := b.GetField("f1"); !ok || got.AreaHa != 12.5 {
		t.Fatalf("get field = %+v ok=%v", got, ok)
	}
	if _, ok := b.GetField("none"); ok {
		t.Fatalf("missing field found")
	}
	jan := time.Date(2026, 1, 1, 0, 0, 0, 0, time.UTC)
	b.Plant(circleai.Crop{CropId: "c2", FieldId: "f1", Variety: "Maize", PlantedOn: jan.AddDate(0, 2, 0)})
	b.Plant(circleai.Crop{CropId: "c1", FieldId: "f1", Variety: "Maize", PlantedOn: jan})
	b.Plant(circleai.Crop{CropId: "c3", FieldId: "f2", Variety: "Wheat", PlantedOn: jan})

	crops := b.CropsForField("f1")
	if len(crops) != 2 || crops[0].CropId != "c1" || crops[1].CropId != "c2" {
		t.Fatalf("crops-for-field ordered by PlantedOn failed: %+v", crops)
	}
}

func TestAgriculture_AvgYield(t *testing.T) {
	b := circleai.NewInMemoryFarmBoard()
	jan := time.Date(2026, 1, 1, 0, 0, 0, 0, time.UTC)
	b.Plant(circleai.Crop{CropId: "c1", FieldId: "f1", Variety: "Maize", PlantedOn: jan})
	b.Plant(circleai.Crop{CropId: "c2", FieldId: "f1", Variety: "maize", PlantedOn: jan})
	b.RecordYield(circleai.YieldRecord{CropId: "c1", TonsPerHa: 6, HarvestedOn: jan})
	b.RecordYield(circleai.YieldRecord{CropId: "c2", TonsPerHa: 8, HarvestedOn: jan})

	if avg := b.AvgYieldOfVariety("MAIZE"); math.Abs(avg-7.0) > 1e-9 {
		t.Fatalf("avg yield = %v, want 7", avg)
	}
	if avg := b.AvgYieldOfVariety("Sorghum"); avg != 0.0 {
		t.Fatalf("avg yield (none) = %v, want 0", avg)
	}
}
