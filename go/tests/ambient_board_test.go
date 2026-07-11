// ambient_board_test.go
//
// Verifies the CircleAI.Ambient port (ambient_board.go): reading record, latest
// + newest-first history with limit, preference set/get, and comfort evaluation
// against the temp/humidity/noise thresholds.

package circleai_test

import (
	"testing"
	"time"

	circleai "github.com/bhengubv/CircleAI/go"
)

func TestAmbient_ReadingsAndHistory(t *testing.T) {
	b := circleai.NewInMemoryAmbientBoard()
	base := time.Date(2026, 7, 1, 0, 0, 0, 0, time.UTC)
	b.Record(circleai.AmbientReading{DeviceId: "d1", TemperatureC: 21, Humidity: 45, LuxLight: 300, DbNoise: 35, AtUtc: base})
	b.Record(circleai.AmbientReading{DeviceId: "d1", TemperatureC: 22, Humidity: 46, LuxLight: 320, DbNoise: 36, AtUtc: base.Add(time.Hour)})
	b.Record(circleai.AmbientReading{DeviceId: "d2", TemperatureC: 30, Humidity: 70, LuxLight: 100, DbNoise: 60, AtUtc: base})

	last, ok := b.Latest("d1")
	if !ok || last.TemperatureC != 22 {
		t.Fatalf("latest = %+v ok=%v", last, ok)
	}
	hist := b.History("d1", 50)
	if len(hist) != 2 || hist[0].TemperatureC != 22 || hist[1].TemperatureC != 21 {
		t.Fatalf("history newest-first failed: %+v", hist)
	}
	if lim := b.History("d1", 1); len(lim) != 1 || lim[0].TemperatureC != 22 {
		t.Fatalf("history limit failed: %+v", lim)
	}
}

func TestAmbient_Comfort(t *testing.T) {
	b := circleai.NewInMemoryAmbientBoard()
	now := time.Now().UTC()
	b.SetPreference(circleai.AmbientPreference{Location: "office", TargetTempC: 22, TargetHumidity: 45, MaxNoiseDb: 40})
	if got, ok := b.GetPreference("office"); !ok || got.TargetTempC != 22 {
		t.Fatalf("get preference = %+v ok=%v", got, ok)
	}

	// Within all thresholds -> comfortable.
	b.Record(circleai.AmbientReading{DeviceId: "d1", TemperatureC: 23, Humidity: 50, DbNoise: 38, AtUtc: now})
	if !b.IsComfortable("d1", "office") {
		t.Fatalf("expected comfortable")
	}
	// Too loud -> not comfortable.
	b.Record(circleai.AmbientReading{DeviceId: "d1", TemperatureC: 22, Humidity: 45, DbNoise: 55, AtUtc: now.Add(time.Minute)})
	if b.IsComfortable("d1", "office") {
		t.Fatalf("expected not comfortable (noise)")
	}
	// Missing preference or reading -> false.
	if b.IsComfortable("d1", "nowhere") {
		t.Fatalf("missing preference must be not comfortable")
	}
	if b.IsComfortable("ghost", "office") {
		t.Fatalf("missing reading must be not comfortable")
	}
}
