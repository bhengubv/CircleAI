// autonomous_biz.go
//
// Ports CircleAI.AutonomousBiz (Contracts.cs + InMemoryAutonomousBiz.cs +
// NullImplementations.cs): the autonomous-business treasury / revenue-loop /
// decision-log primitives.
//
//	TreasurySnapshot / RevenueEvent / AutonomousDecision (records) -> structs
//	ITreasury / IRevenueLoop / IDecisionLog  -> interfaces (I-prefix dropped)
//	InMemoryRevenueLoop / Treasury / DecisionLog -> in-memory impls
//	NullTreasury / NullRevenueLoop / NullDecisionLog -> null impls
//
// Monetary amounts use the shared exact Decimal (C# decimal). The C#
// IRevenueLoop.Subscribe returns IDisposable; the Go idiom returns an
// unsubscribe func. InMemoryTreasury derives its balance by summing revenue
// events in the loop whose currency matches (case-insensitive).
//
// CONCURRENCY: InMemoryRevenueLoop.Publish appends to history and snapshots the
// subscriber slice UNDER the lock, then fires callbacks OUTSIDE it, so a
// subscriber that (un)subscribes from its handler cannot deadlock the publisher.
// Subscriber panics are swallowed (matching the C# try/catch + Debug.WriteLine).

package circleai

import (
	"context"
	"errors"
	"sort"
	"strings"
	"sync"
	"time"
)

// errLimitPositive mirrors the C# ArgumentOutOfRangeException when a read limit
// is not positive.
var errLimitPositive = errors.New("limit must be positive")

// TreasurySnapshot is a point-in-time treasury balance. Ports the
// TreasurySnapshot record.
type TreasurySnapshot struct {
	Balance  Decimal
	Currency string
	AtUTC    time.Time
}

// RevenueEvent is a single revenue event. Ports the RevenueEvent record.
type RevenueEvent struct {
	EventID  string
	Amount   Decimal
	Currency string
	Source   string
	AtUTC    time.Time
}

// AutonomousDecision is a logged autonomous decision. Ports the
// AutonomousDecision record.
type AutonomousDecision struct {
	DecisionID   string
	Rationale    string
	ChosenAction string
	AtUTC        time.Time
}

// Treasury reports the treasury balance. Ports ITreasury.
type Treasury interface {
	BackendID() string
	GetSnapshot(ctx context.Context) (TreasurySnapshot, error)
}

// RevenueLoop is a fan-out pub/sub of revenue events with kept history. Ports
// IRevenueLoop. Subscribe returns an unsubscribe func in place of the C#
// IDisposable handle.
type RevenueLoop interface {
	BackendID() string
	Subscribe(handler func(RevenueEvent)) (unsubscribe func())
	// Read returns events at or after since.
	Read(ctx context.Context, since time.Time) ([]RevenueEvent, error)
}

// DecisionLog is an append-only decision log. Ports IDecisionLog.
type DecisionLog interface {
	BackendID() string
	Append(ctx context.Context, d AutonomousDecision) error
	// Read returns up to limit decisions, most-recent first.
	Read(ctx context.Context, limit int) ([]AutonomousDecision, error)
}

// InMemoryRevenueLoop is a real fan-out revenue loop with kept history. Ports
// InMemoryRevenueLoop. The zero value is ready to use.
type InMemoryRevenueLoop struct {
	mu      sync.Mutex
	history []RevenueEvent
	subs    []*revenueSub
}

type revenueSub struct {
	handler func(RevenueEvent)
}

// BackendID returns "in-memory".
func (l *InMemoryRevenueLoop) BackendID() string { return "in-memory" }

// Publish records an event in history and fans it out to subscribers. Ports
// Publish.
func (l *InMemoryRevenueLoop) Publish(e RevenueEvent) {
	l.mu.Lock()
	l.history = append(l.history, e)
	snap := make([]*revenueSub, len(l.subs))
	copy(snap, l.subs)
	l.mu.Unlock()
	for _, s := range snap {
		func() {
			defer func() { _ = recover() }()
			s.handler(e)
		}()
	}
}

// Subscribe registers handler and returns an idempotent unsubscribe func. Ports
// Subscribe.
func (l *InMemoryRevenueLoop) Subscribe(handler func(RevenueEvent)) (unsubscribe func()) {
	if handler == nil {
		panic("handler must not be nil")
	}
	sub := &revenueSub{handler: handler}
	l.mu.Lock()
	l.subs = append(l.subs, sub)
	l.mu.Unlock()
	var once sync.Once
	return func() { once.Do(func() { l.unsubscribe(sub) }) }
}

func (l *InMemoryRevenueLoop) unsubscribe(sub *revenueSub) {
	l.mu.Lock()
	defer l.mu.Unlock()
	for i, s := range l.subs {
		if s == sub {
			l.subs = append(l.subs[:i], l.subs[i+1:]...)
			return
		}
	}
}

// Read returns events at or after since. Ports ReadAsync.
func (l *InMemoryRevenueLoop) Read(ctx context.Context, since time.Time) ([]RevenueEvent, error) {
	l.mu.Lock()
	out := make([]RevenueEvent, 0)
	for _, e := range l.history {
		if !e.AtUTC.Before(since) {
			out = append(out, e)
		}
	}
	l.mu.Unlock()
	return out, nil
}

// InMemoryTreasury derives its balance from a RevenueLoop. Ports InMemoryTreasury.
// Construct with NewInMemoryTreasury.
type InMemoryTreasury struct {
	loop     RevenueLoop
	currency string
}

// NewInMemoryTreasury constructs a treasury over loop denominated in currency
// (default "ZAR" when currency is empty). Panics if loop is nil.
func NewInMemoryTreasury(loop RevenueLoop, currency string) *InMemoryTreasury {
	if loop == nil {
		panic("loop must not be nil")
	}
	if currency == "" {
		currency = "ZAR"
	}
	return &InMemoryTreasury{loop: loop, currency: currency}
}

// BackendID returns "in-memory".
func (t *InMemoryTreasury) BackendID() string { return "in-memory" }

// GetSnapshot sums same-currency (case-insensitive) events into a balance. Ports
// GetSnapshotAsync.
func (t *InMemoryTreasury) GetSnapshot(ctx context.Context) (TreasurySnapshot, error) {
	events, err := t.loop.Read(ctx, time.Time{})
	if err != nil {
		return TreasurySnapshot{}, err
	}
	var bal Decimal
	for _, e := range events {
		if strings.EqualFold(e.Currency, t.currency) {
			bal = bal.Add(e.Amount)
		}
	}
	return TreasurySnapshot{Balance: bal, Currency: t.currency, AtUTC: time.Now().UTC()}, nil
}

// InMemoryDecisionLog is an append-only decision log. Ports InMemoryDecisionLog.
// The zero value is ready to use.
type InMemoryDecisionLog struct {
	mu    sync.Mutex
	items []AutonomousDecision
}

// BackendID returns "in-memory".
func (d *InMemoryDecisionLog) BackendID() string { return "in-memory" }

// Append records a decision. Ports AppendAsync.
func (d *InMemoryDecisionLog) Append(ctx context.Context, dec AutonomousDecision) error {
	d.mu.Lock()
	d.items = append(d.items, dec)
	d.mu.Unlock()
	return nil
}

// Read returns up to limit decisions, most-recent first. Ports ReadAsync
// (OrderByDescending(AtUtc).Take(limit)). Returns an error if limit <= 0.
func (d *InMemoryDecisionLog) Read(ctx context.Context, limit int) ([]AutonomousDecision, error) {
	if limit <= 0 {
		return nil, errLimitPositive
	}
	d.mu.Lock()
	out := make([]AutonomousDecision, len(d.items))
	copy(out, d.items)
	d.mu.Unlock()
	sort.SliceStable(out, func(i, j int) bool { return out[i].AtUTC.After(out[j].AtUTC) })
	if len(out) > limit {
		out = out[:limit]
	}
	return out, nil
}

// ── Null implementations ────────────────────────────────────────────────────

// NullTreasury is a no-op treasury. Ports NullTreasury.
type NullTreasury struct{}

// NullTreasuryInstance mirrors NullTreasury.Instance.
var NullTreasuryInstance = NullTreasury{}

// BackendID returns "null".
func (NullTreasury) BackendID() string { return "null" }

// GetSnapshot returns a zero-balance ZAR snapshot at MinValue. Ports
// GetSnapshotAsync.
func (NullTreasury) GetSnapshot(ctx context.Context) (TreasurySnapshot, error) {
	return TreasurySnapshot{Balance: Decimal{}, Currency: "ZAR", AtUTC: time.Time{}}, nil
}

// NullRevenueLoop is a no-op revenue loop. Ports NullRevenueLoop.
type NullRevenueLoop struct{}

// NullRevenueLoopInstance mirrors NullRevenueLoop.Instance.
var NullRevenueLoopInstance = NullRevenueLoop{}

// BackendID returns "null".
func (NullRevenueLoop) BackendID() string { return "null" }

// Subscribe returns a no-op unsubscribe. Ports Subscribe (EmptyDisposable).
func (NullRevenueLoop) Subscribe(handler func(RevenueEvent)) (unsubscribe func()) {
	return func() {}
}
func (NullRevenueLoop) Read(context.Context, time.Time) ([]RevenueEvent, error) {
	return []RevenueEvent{}, nil
}

// NullDecisionLog is a no-op decision log. Ports NullDecisionLog.
type NullDecisionLog struct{}

// NullDecisionLogInstance mirrors NullDecisionLog.Instance.
var NullDecisionLogInstance = NullDecisionLog{}

// BackendID returns "null".
func (NullDecisionLog) BackendID() string                                { return "null" }
func (NullDecisionLog) Append(context.Context, AutonomousDecision) error { return nil }
func (NullDecisionLog) Read(context.Context, int) ([]AutonomousDecision, error) {
	return []AutonomousDecision{}, nil
}

// Interface guards.
var (
	_ RevenueLoop = (*InMemoryRevenueLoop)(nil)
	_ Treasury    = (*InMemoryTreasury)(nil)
	_ DecisionLog = (*InMemoryDecisionLog)(nil)
	_ Treasury    = NullTreasury{}
	_ RevenueLoop = NullRevenueLoop{}
	_ DecisionLog = NullDecisionLog{}
)
