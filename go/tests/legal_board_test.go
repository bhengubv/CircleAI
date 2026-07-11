// legal_board_test.go
//
// Verifies the CircleAI.Legal port (legal_board.go): matter open/close/active
// ordering, contract expiry filtering + ordering, deadline upcoming filtering,
// and clause tag lookup (case-insensitive) with blank-tag error.

package circleai_test

import (
	"testing"
	"time"

	circleai "github.com/bhengubv/CircleAI/go"
)

func legalTime(y int, m time.Month, d int) time.Time {
	return time.Date(y, m, d, 0, 0, 0, 0, time.UTC)
}

func TestLegal_MattersOpenCloseActive(t *testing.T) {
	b := circleai.NewInMemoryLegalBoard()
	b.Open(circleai.Matter{MatterId: "m1", Title: "One", OpenedAtUtc: legalTime(2026, 1, 1), Open: true})
	b.Open(circleai.Matter{MatterId: "m2", Title: "Two", OpenedAtUtc: legalTime(2026, 3, 1), Open: true})
	b.Open(circleai.Matter{MatterId: "m3", Title: "Three", OpenedAtUtc: legalTime(2026, 2, 1), Open: true})

	active := b.ActiveMatters()
	if len(active) != 3 || active[0].MatterId != "m2" || active[1].MatterId != "m3" || active[2].MatterId != "m1" {
		t.Fatalf("active matters desc-by-open failed: %+v", active)
	}
	if err := b.Close("m2"); err != nil {
		t.Fatalf("close: %v", err)
	}
	if active = b.ActiveMatters(); len(active) != 2 {
		t.Fatalf("after close want 2 active, got %d", len(active))
	}
	if m, ok := b.GetMatter("m2"); !ok || m.Open {
		t.Fatalf("m2 should be closed: %+v ok=%v", m, ok)
	}
	if err := b.Close("ghost"); err == nil {
		t.Fatalf("closing unknown matter must error")
	}
}

func TestLegal_ContractsExpiringBefore(t *testing.T) {
	b := circleai.NewInMemoryLegalBoard()
	exp1 := legalTime(2026, 6, 1)
	exp2 := legalTime(2026, 8, 1)
	b.AddContract(circleai.Contract{ContractId: "c1", MatterId: "m1", Title: "Early", EffectiveDate: legalTime(2026, 1, 1), ExpiryDate: &exp1, Counterparties: []string{"X"}})
	b.AddContract(circleai.Contract{ContractId: "c2", MatterId: "m1", Title: "Late", EffectiveDate: legalTime(2026, 1, 1), ExpiryDate: &exp2})
	b.AddContract(circleai.Contract{ContractId: "c3", MatterId: "m1", Title: "Perpetual", EffectiveDate: legalTime(2026, 1, 1), ExpiryDate: nil})

	before := b.ContractsExpiringBefore(legalTime(2026, 7, 1))
	if len(before) != 1 || before[0].ContractId != "c1" {
		t.Fatalf("expiring-before failed: %+v", before)
	}
	// On-or-before is inclusive of the exact date; both c1 and c2 by Sep.
	all := b.ContractsExpiringBefore(legalTime(2026, 9, 1))
	if len(all) != 2 || all[0].ContractId != "c1" || all[1].ContractId != "c2" {
		t.Fatalf("inclusive expiring order failed: %+v", all)
	}
}

func TestLegal_ContractCounterpartiesCopied(t *testing.T) {
	b := circleai.NewInMemoryLegalBoard()
	exp := legalTime(2026, 6, 1)
	parties := []string{"Acme"}
	b.AddContract(circleai.Contract{ContractId: "c1", MatterId: "m1", ExpiryDate: &exp, Counterparties: parties})
	parties[0] = "MUTATED"
	got := b.ContractsExpiringBefore(legalTime(2027, 1, 1))
	if got[0].Counterparties[0] != "Acme" {
		t.Fatalf("counterparties not defensively copied: %v", got[0].Counterparties)
	}
}

func TestLegal_UpcomingDeadlines(t *testing.T) {
	b := circleai.NewInMemoryLegalBoard()
	b.Add(circleai.LegalDeadline{DeadlineId: "d1", MatterId: "m1", Description: "past", DueOn: legalTime(2026, 1, 1)})
	b.Add(circleai.LegalDeadline{DeadlineId: "d2", MatterId: "m1", Description: "soon", DueOn: legalTime(2026, 7, 15)})
	b.Add(circleai.LegalDeadline{DeadlineId: "d3", MatterId: "m1", Description: "later", DueOn: legalTime(2026, 9, 1)})
	up := b.UpcomingDeadlines(legalTime(2026, 7, 1))
	if len(up) != 2 || up[0].DeadlineId != "d2" || up[1].DeadlineId != "d3" {
		t.Fatalf("upcoming deadlines failed: %+v", up)
	}
}

func TestLegal_ClausesByTag(t *testing.T) {
	b := circleai.NewInMemoryLegalBoard()
	b.AddClause(circleai.Clause{ClauseId: "cl1", Title: "Indemnity", Body: "...", Tags: []string{"Risk", "Liability"}})
	b.AddClause(circleai.Clause{ClauseId: "cl2", Title: "Term", Body: "...", Tags: []string{"General"}})
	b.AddClause(circleai.Clause{ClauseId: "cl3", Title: "Cap", Body: "...", Tags: []string{"liability"}})

	hits, err := b.ClausesByTag("LIABILITY")
	if err != nil {
		t.Fatalf("by tag: %v", err)
	}
	if len(hits) != 2 || hits[0].ClauseId != "cl1" || hits[1].ClauseId != "cl3" {
		t.Fatalf("case-insensitive tag match failed: %+v", hits)
	}
	if _, err := b.ClausesByTag("  "); err == nil {
		t.Fatalf("blank tag must error")
	}
}
