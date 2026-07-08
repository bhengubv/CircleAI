// hosting_cron_test.go
//
// Verifies the CircleAI.Hosting cron ports:
//   GetNextCronOccurrence (CronScheduleParser) — fixture-driven success + error
//   CronJob defaults (CronJobModels)
//   DeliveryTarget / CronJobState ordinals

package circleai_test

import (
	"encoding/json"
	"os"
	"path/filepath"
	"testing"
	"time"

	circleai "github.com/bhengubv/CircleAI/go"
)

type hostingCronFixture struct {
	Cases []struct {
		ID       string `json:"id"`
		Cron     string `json:"cron"`
		After    string `json:"after"`
		Expected string `json:"expected"`
	} `json:"cases"`
	ErrorCases []struct {
		ID   string `json:"id"`
		Cron string `json:"cron"`
	} `json:"errorCases"`
}

func loadHostingCronFixture(t *testing.T) hostingCronFixture {
	t.Helper()
	b, err := os.ReadFile(filepath.Join("fixtures", "hosting_cron.json"))
	if err != nil {
		t.Fatalf("read fixture: %v", err)
	}
	var f hostingCronFixture
	if err := json.Unmarshal(b, &f); err != nil {
		t.Fatalf("parse fixture: %v", err)
	}
	return f
}

func TestGetNextCronOccurrence_Fixtures(t *testing.T) {
	f := loadHostingCronFixture(t)
	for _, c := range f.Cases {
		after, err := time.Parse(time.RFC3339, c.After)
		if err != nil {
			t.Fatalf("%s: bad after: %v", c.ID, err)
		}
		want, err := time.Parse(time.RFC3339, c.Expected)
		if err != nil {
			t.Fatalf("%s: bad expected: %v", c.ID, err)
		}
		got, err := circleai.GetNextCronOccurrence(c.Cron, after)
		if err != nil {
			t.Errorf("%s (%q): unexpected error: %v", c.ID, c.Cron, err)
			continue
		}
		if !got.Equal(want) {
			t.Errorf("%s (%q): got %s, want %s", c.ID, c.Cron, got.Format(time.RFC3339), want.Format(time.RFC3339))
		}
		// Result must be strictly after the reference.
		if !got.After(after) {
			t.Errorf("%s: result %s not strictly after %s", c.ID, got, after)
		}
	}
}

func TestGetNextCronOccurrence_Errors(t *testing.T) {
	f := loadHostingCronFixture(t)
	ref := time.Date(2026, 7, 8, 0, 0, 0, 0, time.UTC)
	for _, c := range f.ErrorCases {
		if _, err := circleai.GetNextCronOccurrence(c.Cron, ref); err == nil {
			t.Errorf("%s (%q): expected error, got none", c.ID, c.Cron)
		}
	}
}

func TestNewCronJob_Defaults(t *testing.T) {
	j := circleai.NewCronJob("id1", "Morning brief", "summarise my day", "0 9 * * *", circleai.DeliveryPush)
	if j.State != circleai.CronJobPending {
		t.Errorf("default state: got %d, want Pending", j.State)
	}
	if !j.IsEnabled {
		t.Error("default IsEnabled should be true")
	}
	if j.LastRunUTC != nil || j.NextRunUTC != nil {
		t.Error("new job should have nil last/next run")
	}
	if j.Delivery != circleai.DeliveryPush {
		t.Errorf("delivery: got %d, want Push", j.Delivery)
	}
}

func TestDeliveryTargetOrdinals(t *testing.T) {
	pairs := []struct {
		got  circleai.DeliveryTarget
		want int
	}{
		{circleai.DeliveryLocal, 0},
		{circleai.DeliveryPush, 1},
		{circleai.DeliveryTelegram, 2},
		{circleai.DeliveryEmail, 3},
		{circleai.DeliveryCustom, 4},
	}
	for _, p := range pairs {
		if int(p.got) != p.want {
			t.Errorf("DeliveryTarget ordinal: got %d, want %d", int(p.got), p.want)
		}
	}
}

func TestCronJobStateOrdinals(t *testing.T) {
	pairs := []struct {
		got  circleai.CronJobState
		want int
	}{
		{circleai.CronJobPending, 0},
		{circleai.CronJobRunning, 1},
		{circleai.CronJobSucceeded, 2},
		{circleai.CronJobFailed, 3},
		{circleai.CronJobPaused, 4},
	}
	for _, p := range pairs {
		if int(p.got) != p.want {
			t.Errorf("CronJobState ordinal: got %d, want %d", int(p.got), p.want)
		}
	}
}
