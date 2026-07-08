// hosting_cron.go
//
// Ports CircleAI.Hosting cron domain + parser:
//   DeliveryTarget, CronJobState, CronJob (CronJobModels.cs)
//   CronScheduleParser.GetNextOccurrence (CronScheduleParser.cs)
//
// The parser is a real, deterministic 5-field cron implementation. Field
// order: minute(0-59) hour(0-23) dom(1-31) month(1-12) dow(0-6, 0=Sunday).
// Supports *, N, N,M, */N, N-M, N-M/S. No external dependencies.

package circleai

import (
	"errors"
	"fmt"
	"strconv"
	"strings"
	"time"
)

// DeliveryTarget is the delivery channel for a scheduled job's output.
// Ports CircleAI.Hosting.DeliveryTarget (stable ordinals).
type DeliveryTarget int

const (
	// DeliveryLocal — deliver via in-process observer callback.
	DeliveryLocal DeliveryTarget = iota
	// DeliveryPush — deliver via push notification (IPushNotificationSender).
	DeliveryPush
	// DeliveryTelegram — deliver as a Telegram message.
	DeliveryTelegram
	// DeliveryEmail — deliver via email (SMTP).
	DeliveryEmail
	// DeliveryCustom — caller handles delivery via custom callback.
	DeliveryCustom
)

// CronJobState is the state of a scheduled job's last execution.
// Ports CircleAI.Hosting.CronJobState (stable ordinals).
type CronJobState int

const (
	// CronJobPending — job has never run.
	CronJobPending CronJobState = iota
	// CronJobRunning — job is currently executing.
	CronJobRunning
	// CronJobSucceeded — last run completed without error.
	CronJobSucceeded
	// CronJobFailed — last run threw or the model returned an error.
	CronJobFailed
	// CronJobPaused — job is manually paused and will not fire until re-enabled.
	CronJobPaused
)

// CronJob is a named, recurring B! task with a cron schedule.
// Ports CircleAI.Hosting.CronJob (record). LastRunUTC/NextRunUTC are pointers
// so nil models the C# nullable DateTimeOffset? ("never run" / "not scheduled").
type CronJob struct {
	ID             string
	Name           string
	Prompt         string
	CronExpression string
	Delivery       DeliveryTarget
	LastRunUTC     *time.Time
	NextRunUTC     *time.Time
	State          CronJobState
	IsEnabled      bool
}

// NewCronJob builds a CronJob with C#-record default values
// (State=Pending, IsEnabled=true, no last/next run).
func NewCronJob(id, name, prompt, cronExpression string, delivery DeliveryTarget) CronJob {
	return CronJob{
		ID:             id,
		Name:           name,
		Prompt:         prompt,
		CronExpression: cronExpression,
		Delivery:       delivery,
		State:          CronJobPending,
		IsEnabled:      true,
	}
}

// ---------------------------------------------------------------------------
// CronScheduleParser
// ---------------------------------------------------------------------------

// GetNextCronOccurrence returns the earliest UTC timestamp strictly after
// `after` that satisfies cronExpression. Ports
// CircleAI.Hosting.CronScheduleParser.GetNextOccurrence.
//
// Returns an error when the expression cannot be parsed, or when no occurrence
// is found within 5 years (impossible expressions like "0 9 31 2 *").
func GetNextCronOccurrence(cronExpression string, after time.Time) (time.Time, error) {
	if strings.TrimSpace(cronExpression) == "" {
		return time.Time{}, errors.New("cron expression must not be null or whitespace")
	}

	parts := strings.Fields(strings.TrimSpace(cronExpression))
	if len(parts) != 5 {
		return time.Time{}, fmt.Errorf(
			"cron expression must have exactly 5 fields, got %d: '%s'",
			len(parts), cronExpression)
	}

	minuteSet, err := parseHostingCronField(parts[0], 0, 59)
	if err != nil {
		return time.Time{}, err
	}
	hourSet, err := parseHostingCronField(parts[1], 0, 23)
	if err != nil {
		return time.Time{}, err
	}
	domSet, err := parseHostingCronField(parts[2], 1, 31)
	if err != nil {
		return time.Time{}, err
	}
	monthSet, err := parseHostingCronField(parts[3], 1, 12)
	if err != nil {
		return time.Time{}, err
	}
	dowSet, err := parseHostingCronField(parts[4], 0, 6)
	if err != nil {
		return time.Time{}, err
	}

	u := after.UTC()
	// Start searching from the next whole minute after `after`.
	candidate := time.Date(u.Year(), u.Month(), u.Day(), u.Hour(), u.Minute(), 0, 0, time.UTC).
		Add(time.Minute)

	// Cap iteration to prevent infinite loops on impossible expressions.
	limit := candidate.AddDate(5, 0, 0)

	for !candidate.After(limit) {
		// Month check.
		if !monthSet[int(candidate.Month())] {
			candidate, err = advanceCronToNextMonth(candidate, monthSet)
			if err != nil {
				return time.Time{}, err
			}
			continue
		}
		// Day-of-month check.
		if !domSet[candidate.Day()] {
			candidate = cronMidnight(candidate.AddDate(0, 0, 1))
			continue
		}
		// Day-of-week check (Go: Sunday=0, matches cron's 0=Sunday).
		if !dowSet[int(candidate.Weekday())] {
			candidate = cronMidnight(candidate.AddDate(0, 0, 1))
			continue
		}
		// Hour check.
		if !hourSet[candidate.Hour()] {
			candidate = advanceCronToNextHour(candidate, hourSet)
			continue
		}
		// Minute check.
		if !minuteSet[candidate.Minute()] {
			candidate = candidate.Add(time.Minute)
			continue
		}
		// All fields match.
		return candidate, nil
	}

	return time.Time{}, fmt.Errorf(
		"no occurrence found within 5 years for cron expression '%s'", cronExpression)
}

// parseHostingCronField parses one comma-separated cron field into the set of
// matching integer values. Ports CronScheduleParser.ParseField/ParsePart.
// (Named distinctly from proactive_scheduler.go's parseCronField, which ports a
// different C# type with a 1-year search cap and struct{}-set representation.)
func parseHostingCronField(field string, min, max int) (map[int]bool, error) {
	result := make(map[int]bool)
	for _, part := range strings.Split(field, ",") {
		if err := parseHostingCronPart(strings.TrimSpace(part), min, max, result); err != nil {
			return nil, err
		}
	}
	return result, nil
}

func parseHostingCronPart(part string, min, max int, result map[int]bool) error {
	// */N or N-M/S or N-M
	step := 0
	hasStep := false
	core := part

	if slashIdx := strings.IndexByte(part, '/'); slashIdx >= 0 {
		s, err := strconv.Atoi(part[slashIdx+1:])
		if err != nil || s < 1 {
			return fmt.Errorf("invalid step in cron field part '%s'", part)
		}
		step = s
		hasStep = true
		core = part[:slashIdx]
	}

	var rangeMin, rangeMax int

	if core == "*" {
		rangeMin = min
		rangeMax = max
	} else if dashIdx := strings.IndexByte(core, '-'); dashIdx >= 0 {
		a, errA := strconv.Atoi(core[:dashIdx])
		b, errB := strconv.Atoi(core[dashIdx+1:])
		if errA != nil || errB != nil {
			return fmt.Errorf("invalid range in cron field part '%s'", part)
		}
		rangeMin, rangeMax = a, b
	} else {
		v, err := strconv.Atoi(core)
		if err != nil {
			return fmt.Errorf("invalid value in cron field part '%s'", part)
		}
		rangeMin, rangeMax = v, v
	}

	if rangeMin < min || rangeMax > max || rangeMin > rangeMax {
		return fmt.Errorf("cron field value %d-%d out of range [%d,%d]",
			rangeMin, rangeMax, min, max)
	}

	effectiveStep := 1
	if hasStep {
		effectiveStep = step
	}
	for v := rangeMin; v <= rangeMax; v += effectiveStep {
		result[v] = true
	}
	return nil
}

// advanceCronToNextMonth advances to the first day of the next month whose
// month index is in monthSet. Ports CronScheduleParser.AdvanceToNextMonth.
func advanceCronToNextMonth(dt time.Time, monthSet map[int]bool) (time.Time, error) {
	year := dt.Year()
	month := int(dt.Month()) + 1
	if month > 12 {
		month = 1
		year++
	}
	for year < dt.Year()+6 {
		if monthSet[month] {
			return time.Date(year, time.Month(month), 1, 0, 0, 0, 0, time.UTC), nil
		}
		month++
		if month > 12 {
			month = 1
			year++
		}
	}
	return time.Time{}, errors.New("no valid month found in cron expression")
}

// advanceCronToNextHour advances to the next valid hour (minutes reset to 0),
// rolling to the next day's first valid hour when needed. Ports
// CronScheduleParser.AdvanceToNextHour.
func advanceCronToNextHour(dt time.Time, hourSet map[int]bool) time.Time {
	for h := dt.Hour() + 1; h <= 23; h++ {
		if hourSet[h] {
			return time.Date(dt.Year(), dt.Month(), dt.Day(), h, 0, 0, 0, time.UTC)
		}
	}
	nextDay := cronMidnight(dt.AddDate(0, 0, 1))
	minHour := 24
	for h := range hourSet {
		if h < minHour {
			minHour = h
		}
	}
	return time.Date(nextDay.Year(), nextDay.Month(), nextDay.Day(), minHour, 0, 0, 0, time.UTC)
}

// cronMidnight returns midnight UTC for the given time's date. Ports the
// file-scoped DateTimeOffsetExtensions.Date helper.
func cronMidnight(dt time.Time) time.Time {
	return time.Date(dt.Year(), dt.Month(), dt.Day(), 0, 0, 0, 0, time.UTC)
}
