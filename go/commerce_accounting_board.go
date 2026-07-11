// commerce_accounting_board.go
//
// Ports the CircleAI.Commerce.Accounting primitive vertical
// (AccountingPrimitives.cs):
//   AccountingEntry / TaxRate / Period (records) -> value structs
//   IAccountingBoard        -> AccountingBoard interface (I-prefix dropped)
//   InMemoryAccountingBoard -> InMemoryAccountingBoard
//
// The CommerceAccountingDomainContext (static prompt strings) and
// CommerceAccountingCompanionAdapter (LLM-prompt wrapper) are out of scope for
// the deterministic in-memory board.
//
// MONEY: debit/credit amounts and all balance/sum/net-profit maths use the
// shared exact base-10 Decimal (a ledger must not accumulate binary-float dust).
// ForAccount orders entries by AtUtc ascending (id tiebreak added; C# leaves ties
// undefined over the backing list + OrderBy which is stable on insertion order).

package circleai

import (
	"errors"
	"sort"
	"sync"
	"time"
)

// Period is a year+month accounting period. Ports the Period record. As a
// comparable value struct it can be used as a map key or compared directly.
type Period struct {
	Year  int
	Month int
}

// AccountingEntry is one double-entry posting. Ports the AccountingEntry record.
// Exactly one of DebitAmount / CreditAmount is normally non-zero; both must be
// non-negative (enforced by Post).
type AccountingEntry struct {
	EntryId      string
	AtUtc        time.Time
	AccountCode  string
	DebitAmount  Decimal
	CreditAmount Decimal
	Memo         string
}

// TaxRate is a named tax rate. Ports the TaxRate record. Percentage is a whole
// percentage figure (e.g. 15.0 for 15%).
type TaxRate struct {
	Code       string
	Percentage float64
}

// AccountingBoard is the general-ledger board. Ports IAccountingBoard.
type AccountingBoard interface {
	// Post appends an entry; errors if either amount is negative.
	Post(e AccountingEntry) error
	DefineTax(r TaxRate)
	GetTax(code string) (TaxRate, bool)
	// AccountBalance is the running Debit-minus-Credit total for an account.
	AccountBalance(accountCode string) Decimal
	// Sum is Debit-minus-Credit for an account confined to one period.
	Sum(accountCode string, p Period) Decimal
	// ForAccount lists an account's entries in a period, earliest first.
	ForAccount(accountCode string, p Period) []AccountingEntry
	// NetProfit is Sum(revenue) minus Sum(expense) for a period.
	NetProfit(p Period, revenueAccount, expenseAccount string) Decimal
}

// InMemoryAccountingBoard is a concurrency-safe in-memory AccountingBoard. Ports
// InMemoryAccountingBoard (ordered entry list guarded by a mutex; tax rates in a
// map).
type InMemoryAccountingBoard struct {
	mu      sync.RWMutex
	entries []AccountingEntry
	tax     map[string]TaxRate
}

// NewInMemoryAccountingBoard constructs an empty board.
func NewInMemoryAccountingBoard() *InMemoryAccountingBoard {
	return &InMemoryAccountingBoard{
		entries: make([]AccountingEntry, 0),
		tax:     make(map[string]TaxRate),
	}
}

// Post appends an entry after validating non-negative amounts. Ports Post
// (ArgumentException on negative amounts -> error).
func (b *InMemoryAccountingBoard) Post(e AccountingEntry) error {
	if e.DebitAmount.Sign() < 0 || e.CreditAmount.Sign() < 0 {
		return errors.New("amounts must be non-negative")
	}
	b.mu.Lock()
	b.entries = append(b.entries, e)
	b.mu.Unlock()
	return nil
}

// DefineTax stores (or replaces by Code) a tax rate. Ports DefineTax.
func (b *InMemoryAccountingBoard) DefineTax(r TaxRate) {
	b.mu.Lock()
	b.tax[r.Code] = r
	b.mu.Unlock()
}

// GetTax returns the tax rate for code and true, or (zero, false) if absent.
func (b *InMemoryAccountingBoard) GetTax(code string) (TaxRate, bool) {
	b.mu.RLock()
	r, ok := b.tax[code]
	b.mu.RUnlock()
	return r, ok
}

// AccountBalance sums Debit-minus-Credit over all of an account's entries. Ports
// AccountBalance.
func (b *InMemoryAccountingBoard) AccountBalance(accountCode string) Decimal {
	b.mu.RLock()
	defer b.mu.RUnlock()
	var total Decimal
	for _, e := range b.entries {
		if e.AccountCode == accountCode {
			total = total.Add(e.DebitAmount.Sub(e.CreditAmount))
		}
	}
	return total
}

// Sum sums Debit-minus-Credit for an account within a period. Ports Sum.
func (b *InMemoryAccountingBoard) Sum(accountCode string, p Period) Decimal {
	b.mu.RLock()
	defer b.mu.RUnlock()
	var total Decimal
	for _, e := range b.entries {
		if e.AccountCode == accountCode && e.AtUtc.Year() == p.Year && int(e.AtUtc.Month()) == p.Month {
			total = total.Add(e.DebitAmount.Sub(e.CreditAmount))
		}
	}
	return total
}

// ForAccount lists an account's entries in a period ordered by AtUtc ascending.
// Ports ForAccount.
func (b *InMemoryAccountingBoard) ForAccount(accountCode string, p Period) []AccountingEntry {
	b.mu.RLock()
	out := make([]AccountingEntry, 0)
	for _, e := range b.entries {
		if e.AccountCode == accountCode && e.AtUtc.Year() == p.Year && int(e.AtUtc.Month()) == p.Month {
			out = append(out, e)
		}
	}
	b.mu.RUnlock()
	sort.SliceStable(out, func(i, j int) bool {
		if !out[i].AtUtc.Equal(out[j].AtUtc) {
			return out[i].AtUtc.Before(out[j].AtUtc)
		}
		return out[i].EntryId < out[j].EntryId
	})
	return out
}

// NetProfit returns Sum(revenue) - Sum(expense) for a period. Ports NetProfit.
func (b *InMemoryAccountingBoard) NetProfit(p Period, revenueAccount, expenseAccount string) Decimal {
	return b.Sum(revenueAccount, p).Sub(b.Sum(expenseAccount, p))
}

// Interface guard.
var _ AccountingBoard = (*InMemoryAccountingBoard)(nil)
