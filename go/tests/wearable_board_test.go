// wearable_board_test.go
//
// Verifies the CircleAI.Wearable port (wearable_board.go): device add/get +
// vendor-sorted listing, sample recording with unknown-device rejection,
// since-window reads, latest value, and average (NaN when empty).

package circleai_test

import (
	"math"
	"testing"
	"time"

	circleai "github.com/bhengubv/CircleAI/go"
)

func TestWearable_DevicesAndSamples(t *testing.T) {
	b := circleai.NewInMemoryWearableBoard()
	b.Add(circleai.WearableDevice{DeviceId: "d1", Kind: circleai.WearableKindSmartwatch, Vendor: "Zephyr", FirmwareVersion: "1.0", BatteryPct: 80})
	b.Add(circleai.WearableDevice{DeviceId: "d2", Kind: circleai.WearableKindChestStrap, Vendor: "Acme", FirmwareVersion: "2.0", BatteryPct: 50})
	if got, ok := b.GetDevice("d1"); !ok || got.Vendor != "Zephyr" {
		t.Fatalf("get device = %+v ok=%v", got, ok)
	}
	devs := b.Devices()
	if len(devs) != 2 || devs[0].Vendor != "Acme" || devs[1].Vendor != "Zephyr" {
		t.Fatalf("devices sorted by Vendor failed: %+v", devs)
	}

	if err := b.Record(circleai.WearableSample{DeviceId: "ghost", Kind: circleai.WearableTelemetryKindHeartRate, Value: 70, AtUtc: time.Now().UTC()}); err == nil {
		t.Fatalf("recording for unknown device must error")
	}
}

func TestWearable_ReadAndAverage(t *testing.T) {
	b := circleai.NewInMemoryWearableBoard()
	b.Add(circleai.WearableDevice{DeviceId: "d1", Vendor: "Z"})
	base := time.Date(2026, 7, 1, 0, 0, 0, 0, time.UTC)
	must := func(err error) {
		if err != nil {
			t.Fatalf("record: %v", err)
		}
	}
	must(b.Record(circleai.WearableSample{DeviceId: "d1", Kind: circleai.WearableTelemetryKindHeartRate, Value: 60, AtUtc: base}))
	must(b.Record(circleai.WearableSample{DeviceId: "d1", Kind: circleai.WearableTelemetryKindHeartRate, Value: 80, AtUtc: base.Add(time.Hour)}))
	must(b.Record(circleai.WearableSample{DeviceId: "d1", Kind: circleai.WearableTelemetryKindHeartRate, Value: 100, AtUtc: base.Add(2 * time.Hour)}))

	rows := b.ReadSince("d1", circleai.WearableTelemetryKindHeartRate, base.Add(30*time.Minute))
	if len(rows) != 2 || rows[0].Value != 80 || rows[1].Value != 100 {
		t.Fatalf("read-since window failed: %+v", rows)
	}
	if v, ok := b.LatestValue("d1", circleai.WearableTelemetryKindHeartRate); !ok || v != 100 {
		t.Fatalf("latest value = %v ok=%v, want 100", v, ok)
	}
	if _, ok := b.LatestValue("d1", circleai.WearableTelemetryKindSteps); ok {
		t.Fatalf("latest for absent kind must be false")
	}
	if avg := b.AverageValue("d1", circleai.WearableTelemetryKindHeartRate, base); math.Abs(avg-80.0) > 1e-9 {
		t.Fatalf("average = %v, want 80", avg)
	}
	if avg := b.AverageValue("d1", circleai.WearableTelemetryKindSteps, base); !math.IsNaN(avg) {
		t.Fatalf("average with no samples must be NaN, got %v", avg)
	}
}
