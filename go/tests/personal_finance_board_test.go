// personal_finance_board_test.go
//
// Verifies the CircleAI.Personal.Finance port (personal_finance_board.go):
// account upsert/get, transaction record + balance adjust (unknown-account
// error), list-for-month filtering, case-insensitive budget upsert + ordering,
// and month summary (in/out split + per-category totals).

package circleai_test

import (
	"testing"
	"time"

	circleai "github.com/bhengubv/CircleAI/go"
)

func pfTime(y int, m time.Month, d int) time.Time {
	return time.Date(y, m, d, 12, 0, 0, 0, time.UTC)
}

func TestPersonalFinance_RecordAdjustsBalance(t *testing.T) {
	b := circleai.NewInMemoryPersonalFinanceBoard()
	b.Upsert(circleai.PersonalAccount{AccountId: "a1", Name: "Cheque", Balance: circleai.DecimalFromInt(1000), Currency: "ZAR"})
	if got, ok := b.GetAccount("a1"); !ok || got.Name != "Cheque" {
		t.Fatalf("get account = %+v ok=%v", got, ok)
	}
	if err := b.Record(circleai.FinanceTransaction{TxId: "t1", AccountId: "a1", Amount: circleai.DecimalFromInt(-200), Category: "Rent", AtUtc: pfTime(2026, 7, 1)}); err != nil {
		t.Fatalf("record: %v", err)
	}
	a, _ := b.GetAccount("a1")
	if !a.Balance.Equal(circleai.DecimalFromInt(800)) {
		t.Fatalf("balance = %s, want 800", a.Balance)
	}
	// Unknown account errors.
	if err := b.Record(circleai.FinanceTransaction{TxId: "t2", AccountId: "ghost", Amount: circleai.DecimalFromInt(1)}); err == nil {
		t.Fatalf("record on unknown account must error")
	}
}

func TestPersonalFinance_ListForMonth(t *testing.T) {
	b := circleai.NewInMemoryPersonalFinanceBoard()
	b.Upsert(circleai.PersonalAccount{AccountId: "a1", Balance: circleai.ZeroDecimal, Currency: "ZAR"})
	_ = b.Record(circleai.FinanceTransaction{TxId: "t1", AccountId: "a1", Amount: circleai.DecimalFromInt(100), Category: "Salary", AtUtc: pfTime(2026, 7, 5)})
	_ = b.Record(circleai.FinanceTransaction{TxId: "t2", AccountId: "a1", Amount: circleai.DecimalFromInt(-40), Category: "Food", AtUtc: pfTime(2026, 7, 20)})
	_ = b.Record(circleai.FinanceTransaction{TxId: "t3", AccountId: "a1", Amount: circleai.DecimalFromInt(-10), Category: "Food", AtUtc: pfTime(2026, 8, 1)})

	jul := b.ListForMonth("a1", 2026, 7)
	if len(jul) != 2 {
		t.Fatalf("july txns = %d, want 2", len(jul))
	}
	if len(b.ListForMonth("a1", 2026, 8)) != 1 {
		t.Fatalf("aug txns wrong")
	}
}

func TestPersonalFinance_BudgetsCaseInsensitiveAndOrdered(t *testing.T) {
	b := circleai.NewInMemoryPersonalFinanceBoard()
	b.SetBudget(circleai.BudgetLine{Category: "Food", MonthlyLimit: circleai.DecimalFromInt(2000)})
	b.SetBudget(circleai.BudgetLine{Category: "Transport", MonthlyLimit: circleai.DecimalFromInt(1500)})
	b.SetBudget(circleai.BudgetLine{Category: "food", MonthlyLimit: circleai.DecimalFromInt(2500)}) // replaces Food (case-insensitive)

	budgets := b.Budgets()
	if len(budgets) != 2 {
		t.Fatalf("want 2 budgets after case-insensitive replace, got %d: %+v", len(budgets), budgets)
	}
	// Ordered by Category with .NET culture-sensitive OrderBy semantics
	// (case-insensitive primary): "food" (f) sorts before "Transport" (t).
	if budgets[0].Category != "food" || !budgets[0].MonthlyLimit.Equal(circleai.DecimalFromInt(2500)) {
		t.Fatalf("budget replace/order failed: %+v", budgets)
	}
}

func TestPersonalFinance_BudgetOrderingMatchesDotNetCulture(t *testing.T) {
	// Mirrors the C# reference: {"Food","Transport","food","Entertainment",
	// "utilities","Utilities"}.OrderBy(c=>c) on en-ZA =>
	// Entertainment,food,Food,Transport,utilities,Utilities.
	b := circleai.NewInMemoryPersonalFinanceBoard()
	// SetBudget is case-insensitive-keyed, so use distinct case-folded categories
	// plus one case pair (utilities/Utilities collide -> keep the pair separate by
	// giving them genuinely different spellings is impossible; instead assert the
	// primary ordering over distinct categories).
	for _, c := range []string{"Transport", "food", "Entertainment"} {
		b.SetBudget(circleai.BudgetLine{Category: c, MonthlyLimit: circleai.DecimalFromInt(1)})
	}
	got := b.Budgets()
	want := []string{"Entertainment", "food", "Transport"}
	if len(got) != 3 {
		t.Fatalf("want 3 budgets, got %d", len(got))
	}
	for i, w := range want {
		if got[i].Category != w {
			t.Fatalf("culture order = %v, want %v", []string{got[0].Category, got[1].Category, got[2].Category}, want)
		}
	}
}

func TestPersonalFinance_Summarise(t *testing.T) {
	b := circleai.NewInMemoryPersonalFinanceBoard()
	b.Upsert(circleai.PersonalAccount{AccountId: "a1", Balance: circleai.ZeroDecimal, Currency: "ZAR"})
	_ = b.Record(circleai.FinanceTransaction{TxId: "t1", AccountId: "a1", Amount: circleai.DecimalFromInt(5000), Category: "Salary", AtUtc: pfTime(2026, 7, 1)})
	_ = b.Record(circleai.FinanceTransaction{TxId: "t2", AccountId: "a1", Amount: circleai.DecimalFromInt(-1200), Category: "Rent", AtUtc: pfTime(2026, 7, 2)})
	_ = b.Record(circleai.FinanceTransaction{TxId: "t3", AccountId: "a1", Amount: circleai.DecimalFromInt(-300), Category: "Food", AtUtc: pfTime(2026, 7, 3)})
	_ = b.Record(circleai.FinanceTransaction{TxId: "t4", AccountId: "a1", Amount: circleai.DecimalFromInt(-200), Category: "Food", AtUtc: pfTime(2026, 7, 4)})

	s := b.Summarise("a1", 2026, 7)
	if s.Year != 2026 || s.Month != 7 {
		t.Fatalf("summary period wrong: %+v", s)
	}
	if !s.TotalIn.Equal(circleai.DecimalFromInt(5000)) {
		t.Fatalf("total in = %s, want 5000", s.TotalIn)
	}
	// Out = 1200 + 300 + 200 = 1700 (positive).
	if !s.TotalOut.Equal(circleai.DecimalFromInt(1700)) {
		t.Fatalf("total out = %s, want 1700", s.TotalOut)
	}
	if !s.ByCategory["Food"].Equal(circleai.DecimalFromInt(-500)) {
		t.Fatalf("food category = %s, want -500", s.ByCategory["Food"])
	}
	if !s.ByCategory["Salary"].Equal(circleai.DecimalFromInt(5000)) {
		t.Fatalf("salary category = %s, want 5000", s.ByCategory["Salary"])
	}
}
