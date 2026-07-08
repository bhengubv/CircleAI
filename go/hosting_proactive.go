// hosting_proactive.go
//
// Ports CircleAI.Hosting proactive reasoning:
//   ITriggerCondition + ProactiveContext (ITriggerCondition.cs)
//   ScheduleTrigger (ScheduleTrigger.cs)
//   IdleTrigger (IdleTrigger.cs)
//   IProactiveReasoningService + ProactiveMessageEventArgs (IProactiveReasoningService.cs)
//   ProactiveReasoningService (ProactiveReasoningService.cs)
//
// The reasoning service evaluates an ordered list of trigger conditions and,
// when the first fires, asks the butler to generate a warm, goal-aware
// check-in message. Only one trigger fires per Check call (list order = priority).

package circleai

import (
	"context"
	"fmt"
	"strings"
	"sync"
	"time"
)

// ProactiveContext is the snapshot passed to trigger conditions.
// Ports CircleAI.Hosting.ProactiveContext (record). AffectState is nil when no
// affect store is configured.
type ProactiveContext struct {
	UserID                   string
	NowUTC                   time.Time
	TimeSinceLastInteraction time.Duration
	AffectState              *AffectState
	ActiveGoals              []Goal
}

// ITriggerCondition is a condition that, when met, signals B! to check in.
// Ports CircleAI.Hosting.ITriggerCondition.
type ITriggerCondition interface {
	// Name is a stable name used for logging and deduplication.
	Name() string
	// IsMet returns true when the condition is currently met.
	IsMet(ctx context.Context, pctx ProactiveContext) (bool, error)
}

// ---------------------------------------------------------------------------
// ScheduleTrigger
// ---------------------------------------------------------------------------

// ScheduleTrigger fires at a specific time of day. The trigger is active for a
// 5-minute window starting at triggerTime and fires at most once per calendar
// day. Ports CircleAI.Hosting.ScheduleTrigger.
//
// triggerTime is a time-of-day expressed as a duration since local midnight
// (e.g. 9h30m for 09:30). Comparison is done in the machine's local time zone,
// matching the C# use of NowUtc.LocalDateTime.
type ScheduleTrigger struct {
	triggerTime time.Duration // since local midnight, [0, 24h)
	name        string

	mu           sync.Mutex
	lastFireDate string // "2006-01-02" of the last local date it fired, "" = never
}

// NewScheduleTrigger constructs a ScheduleTrigger. name defaults to "schedule"
// when empty.
func NewScheduleTrigger(triggerTime time.Duration, name string) *ScheduleTrigger {
	if name == "" {
		name = "schedule"
	}
	// Normalise into [0, 24h).
	day := 24 * time.Hour
	triggerTime %= day
	if triggerTime < 0 {
		triggerTime += day
	}
	return &ScheduleTrigger{triggerTime: triggerTime, name: name}
}

// TriggerTime returns the time-of-day (since local midnight) this trigger fires.
func (t *ScheduleTrigger) TriggerTime() time.Duration { return t.triggerTime }

// Name implements ITriggerCondition.
func (t *ScheduleTrigger) Name() string { return t.name }

// IsMet implements ITriggerCondition.
func (t *ScheduleTrigger) IsMet(_ context.Context, pctx ProactiveContext) (bool, error) {
	localNow := pctx.NowUTC.Local()
	localDate := localNow.Format("2006-01-02")
	localTime := time.Duration(localNow.Hour())*time.Hour +
		time.Duration(localNow.Minute())*time.Minute +
		time.Duration(localNow.Second())*time.Second +
		time.Duration(localNow.Nanosecond())

	t.mu.Lock()
	defer t.mu.Unlock()

	// Already fired today — don't fire again.
	if t.lastFireDate != "" && t.lastFireDate == localDate {
		return false, nil
	}

	day := 24 * time.Hour
	windowStart := t.triggerTime
	windowEnd := t.triggerTime + 5*time.Minute

	var inWindow bool
	if windowEnd < day {
		// Normal case — window doesn't wrap midnight.
		inWindow = localTime >= windowStart && localTime < windowEnd
	} else {
		// Window wraps midnight (e.g. 23:58 + 5 min = 00:03).
		inWindow = localTime >= windowStart || localTime < (windowEnd-day)
	}

	if !inWindow {
		return false, nil
	}

	t.lastFireDate = localDate
	return true, nil
}

// ---------------------------------------------------------------------------
// IdleTrigger
// ---------------------------------------------------------------------------

// IdleTrigger fires when ProactiveContext.TimeSinceLastInteraction exceeds the
// idle threshold. Ports CircleAI.Hosting.IdleTrigger.
type IdleTrigger struct {
	idleThreshold time.Duration
}

// NewIdleTrigger constructs an IdleTrigger. A zero threshold defaults to 4 hours.
func NewIdleTrigger(idleThreshold time.Duration) *IdleTrigger {
	if idleThreshold <= 0 {
		idleThreshold = 4 * time.Hour
	}
	return &IdleTrigger{idleThreshold: idleThreshold}
}

// IdleThreshold returns the idle threshold used by this trigger.
func (t *IdleTrigger) IdleThreshold() time.Duration { return t.idleThreshold }

// Name implements ITriggerCondition.
func (t *IdleTrigger) Name() string { return "idle" }

// IsMet implements ITriggerCondition.
func (t *IdleTrigger) IsMet(_ context.Context, pctx ProactiveContext) (bool, error) {
	return pctx.TimeSinceLastInteraction > t.idleThreshold, nil
}

// ---------------------------------------------------------------------------
// ProactiveReasoningService
// ---------------------------------------------------------------------------

// ProactiveMessageEventArgs is emitted when B! generates a proactive message.
// Ports CircleAI.Hosting.ProactiveMessageEventArgs.
type ProactiveMessageEventArgs struct {
	UserID       string
	Message      string
	TriggerName  string
	GeneratedUTC time.Time
}

// IProactiveReasoningService evaluates trigger conditions and, when any fires,
// generates a proactive check-in unprompted. Ports
// CircleAI.Hosting.IProactiveReasoningService.
type IProactiveReasoningService interface {
	// Check evaluates all triggers and fires OnProactiveMessage when the first
	// one is met.
	Check(ctx context.Context, userID string) error
	// SetOnProactiveMessage registers the callback raised when B! has something
	// to say unprompted.
	SetOnProactiveMessage(handler func(ProactiveMessageEventArgs))
}

// ProactiveReasoningService is the default IProactiveReasoningService.
// Ports CircleAI.Hosting.ProactiveReasoningService. goalStore/affectStore are
// optional (nil disables that enrichment). triggers are evaluated in order; the
// first that fires causes exactly one check-in.
type ProactiveReasoningService struct {
	butler      IAIService
	goalStore   IGoalStore
	affectStore IAffectStore
	triggers    []ITriggerCondition

	mu      sync.Mutex
	handler func(ProactiveMessageEventArgs)
}

// NewProactiveReasoningService constructs the service.
func NewProactiveReasoningService(
	butler IAIService,
	goalStore IGoalStore,
	affectStore IAffectStore,
	triggers []ITriggerCondition,
) *ProactiveReasoningService {
	return &ProactiveReasoningService{
		butler:      butler,
		goalStore:   goalStore,
		affectStore: affectStore,
		triggers:    triggers,
	}
}

// SetOnProactiveMessage registers the check-in callback.
func (s *ProactiveReasoningService) SetOnProactiveMessage(handler func(ProactiveMessageEventArgs)) {
	s.mu.Lock()
	s.handler = handler
	s.mu.Unlock()
}

// Check implements IProactiveReasoningService. Ports ProactiveReasoningService.CheckAsync.
func (s *ProactiveReasoningService) Check(ctx context.Context, userID string) error {
	if strings.TrimSpace(userID) == "" {
		return fmt.Errorf("userId must not be null or whitespace")
	}
	if len(s.triggers) == 0 {
		return nil
	}

	// 1. Load affect state.
	var affect *AffectState
	if s.affectStore != nil {
		if a, err := s.affectStore.Load(ctx, userID); err == nil {
			affect = &a
		}
	}

	// 2. Load active goals.
	var activeGoals []Goal
	if s.goalStore != nil {
		if goals, err := s.goalStore.List(ctx, userID); err == nil {
			for _, g := range goals {
				if g.Status == GoalActive {
					activeGoals = append(activeGoals, g)
				}
			}
		}
	}

	// 3. Build context snapshot.
	now := time.Now().UTC()
	var timeSinceLast time.Duration
	if affect != nil {
		timeSinceLast = now.Sub(affect.LastUpdatedUTC)
	}

	pctx := ProactiveContext{
		UserID:                   userID,
		NowUTC:                   now,
		TimeSinceLastInteraction: timeSinceLast,
		AffectState:              affect,
		ActiveGoals:              activeGoals,
	}

	// 4. Check triggers in order — fire only the first one.
	for _, trigger := range s.triggers {
		met, err := trigger.IsMet(ctx, pctx)
		if err != nil {
			continue // trigger threw; skip it
		}
		if !met {
			continue
		}

		// 5. Build a proactive prompt.
		prompt := buildProactivePrompt(userID, timeSinceLast, activeGoals)

		// 6. Generate the message.
		message, err := s.butler.Ask(ctx, prompt)
		if err != nil {
			return nil // butler failed for this trigger; stop (non-fatal)
		}

		// 7. Raise the event.
		s.mu.Lock()
		handler := s.handler
		s.mu.Unlock()
		if handler != nil {
			func() {
				defer func() { _ = recover() }() // handler errors are non-fatal
				handler(ProactiveMessageEventArgs{
					UserID:       userID,
					Message:      message,
					TriggerName:  trigger.Name(),
					GeneratedUTC: time.Now().UTC(),
				})
			}()
		}
		return nil // only one trigger per call
	}
	return nil
}

// buildProactivePrompt mirrors ProactiveReasoningService.BuildProactivePrompt
// exactly (wording + pluralisation).
func buildProactivePrompt(_ string, timeSinceLastInteraction time.Duration, activeGoals []Goal) string {
	var sb strings.Builder
	sb.WriteString("You are B!. ")

	if timeSinceLastInteraction.Minutes() > 5 {
		hours := int(timeSinceLastInteraction.Hours())
		minutes := int(timeSinceLastInteraction.Minutes()) % 60
		if hours > 0 {
			sb.WriteString(fmt.Sprintf("The user has been away for approximately %d hour%s. ",
				hours, plural(hours)))
		} else {
			sb.WriteString(fmt.Sprintf("The user has been away for approximately %d minute%s. ",
				minutes, plural(minutes)))
		}
	}

	if len(activeGoals) > 0 {
		sb.WriteString(fmt.Sprintf("They have %d active goal%s: ",
			len(activeGoals), plural(len(activeGoals))))
		for i, g := range activeGoals {
			sb.WriteByte('"')
			sb.WriteString(g.Title)
			sb.WriteByte('"')
			if i < len(activeGoals)-1 {
				sb.WriteString(", ")
			}
		}
		sb.WriteString(". ")
	}

	sb.WriteString("Generate a brief, friendly check-in message (1-2 sentences). ")
	sb.WriteString("Be warm, specific to their goals if you know them, and not intrusive.")
	return sb.String()
}

func plural(n int) string {
	if n == 1 {
		return ""
	}
	return "s"
}

var (
	_ ITriggerCondition          = (*ScheduleTrigger)(nil)
	_ ITriggerCondition          = (*IdleTrigger)(nil)
	_ IProactiveReasoningService = (*ProactiveReasoningService)(nil)
)
