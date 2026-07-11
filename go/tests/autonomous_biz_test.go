// autonomous_biz_test.go
//
// Verifies the CircleAI.AutonomousBiz port (autonomous_biz.go): revenue-loop
// publish/subscribe/read, treasury balance derivation (currency-filtered),
// decision-log append + desc read, and null impls.

package circleai_test

import (
	"context"
	"testing"
	"time"

	circleai "github.com/bhengubv/CircleAI/go"
)

func TestAutonomousBiz_RevenueLoopFanoutAndRead(t *testing.T) {
	var loop circleai.InMemoryRevenueLoop
	var seen []string
	unsub := loop.Subscribe(func(e circleai.RevenueEvent) { seen = append(seen, e.EventID) })
	base := time.Date(2026, 7, 1, 0, 0, 0, 0, time.UTC)
	loop.Publish(circleai.RevenueEvent{EventID: "e1", Amount: circleai.DecimalFromInt(100), Currency: "ZAR", AtUTC: base})
	unsub()
	loop.Publish(circleai.RevenueEvent{EventID: "e2", Amount: circleai.DecimalFromInt(50), Currency: "ZAR", AtUTC: base.Add(time.Hour)})
	if len(seen) != 1 || seen[0] != "e1" {
		t.Fatalf("subscriber saw %v, want [e1] (unsubscribed before e2)", seen)
	}
	got, _ := loop.Read(context.Background(), base.Add(30*time.Minute))
	if len(got) != 1 || got[0].EventID != "e2" {
		t.Fatalf("read since = %+v", got)
	}
}

func TestAutonomousBiz_TreasuryBalanceCurrencyFiltered(t *testing.T) {
	var loop circleai.InMemoryRevenueLoop
	loop.Publish(circleai.RevenueEvent{EventID: "z1", Amount: circleai.DecimalFromInt(100), Currency: "ZAR"})
	loop.Publish(circleai.RevenueEvent{EventID: "u1", Amount: circleai.DecimalFromInt(999), Currency: "USD"})
	loop.Publish(circleai.RevenueEvent{EventID: "z2", Amount: circleai.DecimalFromInt(50), Currency: "zar"}) // case-insensitive
	tr := circleai.NewInMemoryTreasury(&loop, "ZAR")
	snap, err := tr.GetSnapshot(context.Background())
	if err != nil || !snap.Balance.Equal(circleai.DecimalFromInt(150)) || snap.Currency != "ZAR" {
		t.Fatalf("snapshot = %+v err=%v (want 150 ZAR)", snap, err)
	}
}

func TestAutonomousBiz_DecisionLogDescAndLimit(t *testing.T) {
	var log circleai.InMemoryDecisionLog
	base := time.Date(2026, 7, 1, 0, 0, 0, 0, time.UTC)
	_ = log.Append(context.Background(), circleai.AutonomousDecision{DecisionID: "d1", AtUTC: base})
	_ = log.Append(context.Background(), circleai.AutonomousDecision{DecisionID: "d3", AtUTC: base.Add(2 * time.Hour)})
	_ = log.Append(context.Background(), circleai.AutonomousDecision{DecisionID: "d2", AtUTC: base.Add(time.Hour)})
	got, err := log.Read(context.Background(), 2)
	if err != nil || len(got) != 2 || got[0].DecisionID != "d3" || got[1].DecisionID != "d2" {
		t.Fatalf("decision read desc+limit = %+v err=%v", got, err)
	}
	if _, err := log.Read(context.Background(), 0); err == nil {
		t.Fatalf("limit<=0 must error")
	}
}

func TestAutonomousBiz_NullImpls(t *testing.T) {
	snap, _ := circleai.NullTreasuryInstance.GetSnapshot(context.Background())
	if !snap.Balance.IsZero() || snap.Currency != "ZAR" {
		t.Fatalf("null treasury = %+v", snap)
	}
	circleai.NullRevenueLoopInstance.Subscribe(func(circleai.RevenueEvent) {})() // no-op unsubscribe
	if items, _ := circleai.NullDecisionLogInstance.Read(context.Background(), 5); len(items) != 0 {
		t.Fatalf("null decision log must be empty")
	}
}
