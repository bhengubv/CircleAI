// commerce_finance_board_test.go
//
// Verifies the CircleAI.Commerce.Finance port (commerce_finance_board.go):
// invoice issue/get, payment recording, tax-inclusive remaining balance,
// total outstanding, and mark-overdue (case-insensitive "Paid" skip).

package circleai_test

import (
	"testing"
	"time"

	circleai "github.com/bhengubv/CircleAI/go"
)

func finTime(y int, m time.Month, d int) time.Time {
	return time.Date(y, m, d, 0, 0, 0, 0, time.UTC)
}

func TestFinance_IssueGetAndRemainingWithTax(t *testing.T) {
	b := circleai.NewInMemoryInvoiceBoard()
	inv := circleai.Invoice{
		InvoiceId:  "i1",
		CustomerId: "c1",
		IssueDate:  finTime(2026, 7, 1),
		DueDate:    finTime(2026, 7, 31),
		Currency:   "ZAR",
		Status:     "Issued",
		Lines: []circleai.InvoiceLine{
			{Description: "Item A", Amount: circleai.DecimalFromInt(100), TaxPct: 15}, // 115
			{Description: "Item B", Amount: circleai.DecimalFromInt(200), TaxPct: 0},  // 200
		},
	}
	b.Issue(inv)
	if got, ok := b.Get("i1"); !ok || got.CustomerId != "c1" {
		t.Fatalf("get invoice = %+v ok=%v", got, ok)
	}
	// Billed = 115 + 200 = 315, no payments yet.
	if rem := b.RemainingOn("i1"); !rem.Equal(circleai.DecimalFromInt(315)) {
		t.Fatalf("remaining = %s, want 315", rem)
	}
	// Unknown invoice remaining = 0.
	if rem := b.RemainingOn("ghost"); !rem.IsZero() {
		t.Fatalf("unknown remaining = %s, want 0", rem)
	}
	// Record a partial payment of 115.
	b.RecordPayment(circleai.FinancePayment{PaymentId: "p1", InvoiceId: "i1", Amount: circleai.DecimalFromInt(115), AtUtc: finTime(2026, 7, 10)})
	if rem := b.RemainingOn("i1"); !rem.Equal(circleai.DecimalFromInt(200)) {
		t.Fatalf("remaining after payment = %s, want 200", rem)
	}
}

func TestFinance_TaxInclusiveRemainingMatchesDotNet(t *testing.T) {
	// Reference billed amounts from C# `Amount * (decimal)(1 + TaxPct/100.0)`:
	//   79.99 @ 12.5% => 89.98875 ; 33.33 @ 15% => 38.3295 ; 0.01 @ 15% => 0.0115.
	b := circleai.NewInMemoryInvoiceBoard()
	b.Issue(circleai.Invoice{InvoiceId: "i1", Status: "Issued", Lines: []circleai.InvoiceLine{
		{Description: "A", Amount: circleai.NewDecimal(79, 990_000), TaxPct: 12.5},
		{Description: "B", Amount: circleai.NewDecimal(33, 330_000), TaxPct: 15},
		{Description: "C", Amount: circleai.NewDecimal(0, 10_000), TaxPct: 15},
	}})
	// Sum: 89.98875 + 38.3295 + 0.0115 = 128.32975.
	want := circleai.NewDecimal(128, 329_750)
	if rem := b.RemainingOn("i1"); !rem.Equal(want) {
		t.Fatalf("tax-inclusive remaining = %s, want 128.32975", rem)
	}
}

func TestFinance_IssueCopiesLines(t *testing.T) {
	b := circleai.NewInMemoryInvoiceBoard()
	lines := []circleai.InvoiceLine{{Description: "X", Amount: circleai.DecimalFromInt(10), TaxPct: 0}}
	b.Issue(circleai.Invoice{InvoiceId: "i1", Lines: lines, Status: "Issued"})
	lines[0].Amount = circleai.DecimalFromInt(9999)
	if rem := b.RemainingOn("i1"); !rem.Equal(circleai.DecimalFromInt(10)) {
		t.Fatalf("invoice lines not defensively copied: remaining %s", rem)
	}
}

func TestFinance_TotalOutstandingAndOverdue(t *testing.T) {
	b := circleai.NewInMemoryInvoiceBoard()
	b.Issue(circleai.Invoice{InvoiceId: "i1", DueDate: finTime(2026, 6, 1), Status: "Issued", Lines: []circleai.InvoiceLine{{Amount: circleai.DecimalFromInt(100), TaxPct: 0}}})
	b.Issue(circleai.Invoice{InvoiceId: "i2", DueDate: finTime(2026, 6, 15), Status: "paid", Lines: []circleai.InvoiceLine{{Amount: circleai.DecimalFromInt(50), TaxPct: 0}}})
	b.Issue(circleai.Invoice{InvoiceId: "i3", DueDate: finTime(2026, 12, 1), Status: "Issued", Lines: []circleai.InvoiceLine{{Amount: circleai.DecimalFromInt(70), TaxPct: 0}}})

	// Total outstanding = 100 + 50 + 70 = 220 (no payments; remaining counts all).
	if tot := b.TotalOutstanding(); !tot.Equal(circleai.DecimalFromInt(220)) {
		t.Fatalf("total outstanding = %s, want 220", tot)
	}

	// Mark overdue as of Jul 1: i1 (Issued, past due) -> Overdue; i2 is "paid"
	// (case-insensitive) so skipped; i3 not yet due.
	b.MarkOverdue(finTime(2026, 7, 1))
	overdue := b.Overdue()
	if len(overdue) != 1 || overdue[0].InvoiceId != "i1" {
		t.Fatalf("overdue = %+v, want [i1]", overdue)
	}
	if got, _ := b.Get("i2"); got.Status != "paid" {
		t.Fatalf("paid invoice should not be marked overdue, got %q", got.Status)
	}
}
