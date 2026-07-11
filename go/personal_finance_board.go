// personal_finance_board.go
//
// Ports the CircleAI.Personal.Finance primitive vertical
// (PersonalFinancePrimitives.cs):
//   Account -> PersonalAccount, FinanceTransaction, BudgetLine, MonthSummary
//   IPersonalFinanceBoard        -> PersonalFinanceBoard interface
//   InMemoryPersonalFinanceBoard -> InMemoryPersonalFinanceBoard
//
// The PersonalFinanceDomainContext (static prompt strings) and
// PersonalFinanceCompanionAdapter (LLM-prompt wrapper) are out of scope for the
// deterministic in-memory board.
//
// FLAT-PACKAGE DISAMBIGUATION: this module's `Account` record shares a name with
// CircleAI.Banking's `Account`; in the single Go package it is named
// PersonalAccount (Banking's is BankAccount — see banking_board.go).
//
// MONEY: balances and transaction amounts use the shared exact base-10 Decimal.
// Budget keys are compared case-insensitively (C# StringComparer.OrdinalIgnoreCase).
// Summarise reproduces the LINQ GroupBy(Category).Sum and the signed in/out split.

package circleai

import (
	"errors"
	"sort"
	"strings"
	"sync"
	"time"
)

// PersonalAccount is a personal account. Ports the CircleAI.Personal.Finance
// `Account` record (renamed for the flat package). Balance uses exact Decimal.
type PersonalAccount struct {
	AccountId string
	Name      string
	Balance   Decimal
	Currency  string
}

// FinanceTransaction is a personal-finance transaction (signed: positive = money
// in, negative = money out). Ports the FinanceTransaction record. Note is a
// pointer to mirror the nullable C# string?.
type FinanceTransaction struct {
	TxId      string
	AccountId string
	Amount    Decimal
	Category  string
	Note      *string
	AtUtc     time.Time
}

// BudgetLine is a monthly spend limit for a category. Ports the BudgetLine record.
type BudgetLine struct {
	Category     string
	MonthlyLimit Decimal
}

// MonthSummary aggregates one account-month. Ports the MonthSummary record.
// ByCategory maps category -> net signed total for that category.
type MonthSummary struct {
	Year       int
	Month      int
	TotalIn    Decimal
	TotalOut   Decimal
	ByCategory map[string]Decimal
}

// PersonalFinanceBoard is the personal accounts/transactions/budgets board. Ports
// IPersonalFinanceBoard. Budgets is exposed as a method.
type PersonalFinanceBoard interface {
	Upsert(a PersonalAccount)
	GetAccount(id string) (PersonalAccount, bool)
	// Record posts a transaction and adjusts the account balance; errors if the
	// account is unknown.
	Record(t FinanceTransaction) error
	// ListForMonth lists an account's transactions in a given year+month.
	ListForMonth(accountId string, year, month int) []FinanceTransaction
	SetBudget(b BudgetLine)
	// Budgets lists budget lines ordered by Category ascending.
	Budgets() []BudgetLine
	// Summarise aggregates an account's month into totals + a per-category map.
	Summarise(accountId string, year, month int) MonthSummary
}

// InMemoryPersonalFinanceBoard is a concurrency-safe in-memory
// PersonalFinanceBoard. Ports InMemoryPersonalFinanceBoard (accounts + budgets in
// maps, transactions in an ordered list; the same mutex guards the transaction
// list and the balance updates so a Record is atomic).
type InMemoryPersonalFinanceBoard struct {
	mu       sync.RWMutex
	accounts map[string]PersonalAccount
	budgets  map[string]BudgetLine // key: lower-cased category (OrdinalIgnoreCase)
	txns     []FinanceTransaction
}

// NewInMemoryPersonalFinanceBoard constructs an empty board.
func NewInMemoryPersonalFinanceBoard() *InMemoryPersonalFinanceBoard {
	return &InMemoryPersonalFinanceBoard{
		accounts: make(map[string]PersonalAccount),
		budgets:  make(map[string]BudgetLine),
		txns:     make([]FinanceTransaction, 0),
	}
}

// Upsert stores (or replaces by AccountId) an account. Ports Upsert.
func (b *InMemoryPersonalFinanceBoard) Upsert(a PersonalAccount) {
	b.mu.Lock()
	b.accounts[a.AccountId] = a
	b.mu.Unlock()
}

// GetAccount returns the account for id and true, or (zero, false) if absent.
func (b *InMemoryPersonalFinanceBoard) GetAccount(id string) (PersonalAccount, bool) {
	b.mu.RLock()
	a, ok := b.accounts[id]
	b.mu.RUnlock()
	return a, ok
}

// Record appends a transaction and adds its (signed) amount to the account
// balance. Ports Record (throws InvalidOperationException on unknown account ->
// error).
func (b *InMemoryPersonalFinanceBoard) Record(t FinanceTransaction) error {
	b.mu.Lock()
	defer b.mu.Unlock()
	a, ok := b.accounts[t.AccountId]
	if !ok {
		return errors.New("Unknown account " + t.AccountId)
	}
	b.txns = append(b.txns, t)
	a.Balance = a.Balance.Add(t.Amount)
	b.accounts[t.AccountId] = a
	return nil
}

// ListForMonth returns an account's transactions in year+month, in insertion
// order. Ports ListForMonth.
func (b *InMemoryPersonalFinanceBoard) ListForMonth(accountId string, year, month int) []FinanceTransaction {
	b.mu.RLock()
	out := make([]FinanceTransaction, 0)
	for _, t := range b.txns {
		if t.AccountId == accountId && t.AtUtc.Year() == year && int(t.AtUtc.Month()) == month {
			out = append(out, t)
		}
	}
	b.mu.RUnlock()
	return out
}

// SetBudget stores (or replaces, case-insensitively by Category) a budget line.
// Ports SetBudget (OrdinalIgnoreCase key). The stored value keeps the original
// Category casing.
func (b *InMemoryPersonalFinanceBoard) SetBudget(line BudgetLine) {
	b.mu.Lock()
	b.budgets[strings.ToLower(line.Category)] = line
	b.mu.Unlock()
}

// Budgets lists budget lines ordered by Category ascending. Ports the Budgets
// property (OrderBy(Category)). C# OrderBy(string) is culture-sensitive;
// cultureLess reproduces that for the ASCII category names these budgets hold
// (see domain_sort.go — full Unicode collation is out of scope dependency-free).
func (b *InMemoryPersonalFinanceBoard) Budgets() []BudgetLine {
	b.mu.RLock()
	out := make([]BudgetLine, 0, len(b.budgets))
	for _, v := range b.budgets {
		out = append(out, v)
	}
	b.mu.RUnlock()
	sort.SliceStable(out, func(i, j int) bool { return cultureLess(out[i].Category, out[j].Category) })
	return out
}

// Summarise aggregates an account-month into TotalIn (sum of positive amounts),
// TotalOut (negated sum of negative amounts, i.e. a positive figure), and a
// per-category net signed map. Ports Summarise.
func (b *InMemoryPersonalFinanceBoard) Summarise(accountId string, year, month int) MonthSummary {
	rows := b.ListForMonth(accountId, year, month)
	byCat := make(map[string]Decimal)
	var inSum, negSum Decimal
	for _, t := range rows {
		byCat[t.Category] = byCat[t.Category].Add(t.Amount)
		switch {
		case t.Amount.Sign() > 0:
			inSum = inSum.Add(t.Amount)
		case t.Amount.Sign() < 0:
			negSum = negSum.Add(t.Amount)
		}
	}
	return MonthSummary{
		Year:       year,
		Month:      month,
		TotalIn:    inSum,
		TotalOut:   negSum.Neg(), // -(sum of negatives) => positive outflow
		ByCategory: byCat,
	}
}

// Interface guard.
var _ PersonalFinanceBoard = (*InMemoryPersonalFinanceBoard)(nil)
