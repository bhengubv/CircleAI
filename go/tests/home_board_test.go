// home_board_test.go
//
// Verifies the CircleAI.Home port (home_board.go): room add/get + name-ordering,
// device add/toggle (unknown-id error), DevicesIn (room filter, nil-room never
// matches), ActiveDevices, and maintenance task schedule/complete + UpcomingTasks
// (incomplete + due-by filter, due-ascending).

package circleai_test

import (
	"testing"
	"time"

	circleai "github.com/bhengubv/CircleAI/go"
)

func TestHome_RoomsAndDevices(t *testing.T) {
	b := circleai.NewInMemoryHomeBoard()
	b.AddRoom(circleai.Room{RoomId: "r1", Name: "Lounge", AreaM2: 25})
	b.AddRoom(circleai.Room{RoomId: "r2", Name: "Bedroom", AreaM2: 15})

	if r, ok := b.GetRoom("r1"); !ok || r.Name != "Lounge" {
		t.Fatalf("get room = %+v ok=%v", r, ok)
	}
	rooms := b.Rooms()
	if len(rooms) != 2 || rooms[0].Name != "Bedroom" || rooms[1].Name != "Lounge" {
		t.Fatalf("rooms name order wrong: %+v", rooms)
	}

	r1 := "r1"
	b.AddDevice(circleai.HomeDevice{DeviceId: "d1", Name: "Lamp", Kind: "light", RoomId: &r1, IsOn: false})
	b.AddDevice(circleai.HomeDevice{DeviceId: "d2", Name: "TV", Kind: "media", RoomId: &r1, IsOn: true})
	b.AddDevice(circleai.HomeDevice{DeviceId: "d3", Name: "Sensor", Kind: "sensor", RoomId: nil, IsOn: true}) // no room

	inR1 := b.DevicesIn("r1")
	if len(inR1) != 2 || inR1[0].DeviceId != "d1" || inR1[1].DeviceId != "d2" {
		t.Fatalf("devices in r1 wrong: %+v", inR1)
	}

	if err := b.Toggle("d1", true); err != nil {
		t.Fatalf("toggle: %v", err)
	}
	if err := b.Toggle("ghost", true); err == nil {
		t.Fatalf("unknown device toggle must error")
	}
	active := b.ActiveDevices()
	// d1 (now on), d2 (on), d3 (on) -> 3, sorted by DeviceId.
	if len(active) != 3 || active[0].DeviceId != "d1" {
		t.Fatalf("active devices wrong: %+v", active)
	}
}

func TestHome_MaintenanceTasks(t *testing.T) {
	b := circleai.NewInMemoryHomeBoard()
	base := time.Date(2026, 7, 1, 0, 0, 0, 0, time.UTC)
	b.ScheduleTask(circleai.MaintenanceTask{TaskId: "t1", Description: "Gutters", DueOn: base.AddDate(0, 0, 10)})
	b.ScheduleTask(circleai.MaintenanceTask{TaskId: "t2", Description: "Filter", DueOn: base.AddDate(0, 0, 3)})
	b.ScheduleTask(circleai.MaintenanceTask{TaskId: "t3", Description: "Roof", DueOn: base.AddDate(0, 0, 40)}) // beyond by

	by := base.AddDate(0, 0, 30)
	up := b.UpcomingTasks(by)
	// t2 (day 3) then t1 (day 10); t3 (day 40) excluded.
	if len(up) != 2 || up[0].TaskId != "t2" || up[1].TaskId != "t1" {
		t.Fatalf("upcoming tasks wrong: %+v", up)
	}

	if err := b.CompleteTask("t2"); err != nil {
		t.Fatalf("complete: %v", err)
	}
	if err := b.CompleteTask("ghost"); err == nil {
		t.Fatalf("unknown task complete must error")
	}
	up2 := b.UpcomingTasks(by)
	if len(up2) != 1 || up2[0].TaskId != "t1" {
		t.Fatalf("after complete upcoming wrong: %+v", up2)
	}
}
