// commerce_accounting_board_test.go
//
// Verifies the CircleAI.Commerce.Accounting port (commerce_accounting_board.go):
// entry posting with non-negative validation, tax define/get, account balance
// and period sum (Debit-minus-Credit), for-account ordering, and net profit.

package circleai_test

import (
	"testing"
	"time"

	circleai "github.com/bhengubv/CircleAI/go"
)

func acctTime(y int, m time.Month, d int) time.Time {
	return time.Date(y, m, d, 12, 0, 0, 0, time.UTC)
}

func TestAccounting_PostValidationAndBalance(t *testing.T) {
	b := circleai.NewInMemoryAccountingBoard()
	if err := b.Post(circleai.AccountingEntry{EntryId: "e1", AtUtc: acctTime(2026, 7, 1), AccountCode: "4000", DebitAmount: circleai.DecimalFromInt(100), Memo: "sale"}); err != nil {
		t.Fatalf("post e1: %v", err)
	}
	if err := b.Post(circleai.AccountingEntry{EntryId: "e2", AtUtc: acctTime(2026, 7, 2), AccountCode: "4000", CreditAmount: circleai.DecimalFromInt(30), Memo: "refund"}); err != nil {
		t.Fatalf("post e2: %v", err)
	}
	// Negative amount rejected.
	if err := b.Post(circleai.AccountingEntry{EntryId: "bad", AccountCode: "4000", DebitAmount: circleai.DecimalFromInt(-1)}); err == nil {
		t.Fatalf("negative amount must error")
	}
	// Balance = 100 - 30 = 70.
	if bal := b.AccountBalance("4000"); !bal.Equal(circleai.DecimalFromInt(70)) {
		t.Fatalf("balance = %s, want 70", bal)
	}
	if bal := b.AccountBalance("9999"); !bal.IsZero() {
		t.Fatalf("unknown account balance = %s, want 0", bal)
	}
}

func TestAccounting_TaxDefineGet(t *testing.T) {
	b := circleai.NewInMemoryAccountingBoard()
	b.DefineTax(circleai.TaxRate{Code: "VAT", Percentage: 15.0})
	if r, ok := b.GetTax("VAT"); !ok || r.Percentage != 15.0 {
		t.Fatalf("get tax = %+v ok=%v", r, ok)
	}
	if _, ok := b.GetTax("NONE"); ok {
		t.Fatalf("missing tax found")
	}
}

func TestAccounting_PeriodSumForAccountAndNetProfit(t *testing.T) {
	b := circleai.NewInMemoryAccountingBoard()
	jul := circleai.Period{Year: 2026, Month: 7}
	aug := circleai.Period{Year: 2026, Month: 8}
	// Revenue account 4000.
	_ = b.Post(circleai.AccountingEntry{EntryId: "r1", AtUtc: acctTime(2026, 7, 5), AccountCode: "4000", DebitAmount: circleai.DecimalFromInt(1000)})
	_ = b.Post(circleai.AccountingEntry{EntryId: "r2", AtUtc: acctTime(2026, 7, 20), AccountCode: "4000", DebitAmount: circleai.DecimalFromInt(500)})
	_ = b.Post(circleai.AccountingEntry{EntryId: "r3", AtUtc: acctTime(2026, 8, 1), AccountCode: "4000", DebitAmount: circleai.DecimalFromInt(9999)}) // Aug, excluded
	// Expense account 5000.
	_ = b.Post(circleai.AccountingEntry{EntryId: "x1", AtUtc: acctTime(2026, 7, 10), AccountCode: "5000", DebitAmount: circleai.DecimalFromInt(400)})

	if s := b.Sum("4000", jul); !s.Equal(circleai.DecimalFromInt(1500)) {
		t.Fatalf("jul revenue sum = %s, want 1500", s)
	}
	if s := b.Sum("4000", aug); !s.Equal(circleai.DecimalFromInt(9999)) {
		t.Fatalf("aug revenue sum = %s, want 9999", s)
	}
	forAcc := b.ForAccount("4000", jul)
	if len(forAcc) != 2 || forAcc[0].EntryId != "r1" || forAcc[1].EntryId != "r2" {
		t.Fatalf("for-account order failed: %+v", forAcc)
	}
	// Net profit Jul = 1500 - 400 = 1100.
	if np := b.NetProfit(jul, "4000", "5000"); !np.Equal(circleai.DecimalFromInt(1100)) {
		t.Fatalf("net profit = %s, want 1100", np)
	}
}
