// iot_board_test.go
//
// Verifies the CircleAI.IoT port (iot_board.go): device register/get + name
// ordering, telemetry record + LatestValue (most recent; NaN if none) + History
// (newest-first, limit cap, limit<=0 error), and command send + CommandsFor
// (newest-first).

package circleai_test

import (
	"math"
	"testing"
	"time"

	circleai "github.com/bhengubv/CircleAI/go"
)

func TestIoT_DevicesAndTelemetry(t *testing.T) {
	b := circleai.NewInMemoryIoTBoard()
	b.Register(circleai.IoTDevice{DeviceId: "d1", Name: "Thermostat", Kind: "hvac", FirmwareVersion: "1.0"})
	b.Register(circleai.IoTDevice{DeviceId: "d2", Name: "Camera", Kind: "cam", FirmwareVersion: "2.1"})

	if d, ok := b.GetDevice("d1"); !ok || d.Name != "Thermostat" {
		t.Fatalf("get device = %+v ok=%v", d, ok)
	}
	devs := b.Devices()
	if len(devs) != 2 || devs[0].Name != "Camera" || devs[1].Name != "Thermostat" {
		t.Fatalf("devices name order wrong: %+v", devs)
	}

	t0 := time.Date(2026, 7, 1, 0, 0, 0, 0, time.UTC)
	b.RecordTelemetry(circleai.IoTTelemetry{DeviceId: "d1", Metric: "temp", Value: 21.0, AtUtc: t0})
	b.RecordTelemetry(circleai.IoTTelemetry{DeviceId: "d1", Metric: "temp", Value: 22.5, AtUtc: t0.Add(time.Hour)})
	b.RecordTelemetry(circleai.IoTTelemetry{DeviceId: "d1", Metric: "humidity", Value: 40, AtUtc: t0})

	if got := b.LatestValue("d1", "temp"); got != 22.5 {
		t.Fatalf("latest temp = %v, want 22.5", got)
	}
	if got := b.LatestValue("d1", "missing"); !math.IsNaN(got) {
		t.Fatalf("missing metric should be NaN, got %v", got)
	}

	hist, err := b.History("d1", "temp", 100)
	if err != nil {
		t.Fatalf("history: %v", err)
	}
	// Newest first: 22.5 then 21.0.
	if len(hist) != 2 || hist[0].Value != 22.5 || hist[1].Value != 21.0 {
		t.Fatalf("history order wrong: %+v", hist)
	}
	if one, _ := b.History("d1", "temp", 1); len(one) != 1 || one[0].Value != 22.5 {
		t.Fatalf("history limit cap wrong: %+v", one)
	}
	if _, err := b.History("d1", "temp", 0); err == nil {
		t.Fatalf("limit<=0 must error")
	}
}

func TestIoT_Commands(t *testing.T) {
	b := circleai.NewInMemoryIoTBoard()
	t0 := time.Date(2026, 7, 1, 0, 0, 0, 0, time.UTC)
	b.SendCommand(circleai.IoTCommand{CommandId: "c1", DeviceId: "d1", Action: "reboot", ArgumentsJson: "{}", SentUtc: t0})
	b.SendCommand(circleai.IoTCommand{CommandId: "c2", DeviceId: "d1", Action: "setpoint", ArgumentsJson: "{\"t\":20}", SentUtc: t0.Add(time.Hour)})
	b.SendCommand(circleai.IoTCommand{CommandId: "c3", DeviceId: "d2", Action: "ping", ArgumentsJson: "{}", SentUtc: t0})

	cmds := b.CommandsFor("d1")
	// Newest first: c2 then c1.
	if len(cmds) != 2 || cmds[0].CommandId != "c2" || cmds[1].CommandId != "c1" {
		t.Fatalf("commands order wrong: %+v", cmds)
	}
}
