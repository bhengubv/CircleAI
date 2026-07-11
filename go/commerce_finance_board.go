// commerce_finance_board.go
//
// Ports the CircleAI.Commerce.Finance primitive vertical (FinancePrimitives.cs):
//   InvoiceLine / Invoice / FinancePayment (records) -> value structs
//   IInvoiceBoard        -> InvoiceBoard interface (I-prefix dropped)
//   InMemoryInvoiceBoard -> InMemoryInvoiceBoard
//
// The CommerceFinanceDomainContext (static prompt strings) and
// CommerceFinanceCompanionAdapter (LLM-prompt wrapper) are out of scope for the
// deterministic in-memory board.
//
// MONEY: line billing is `Amount * (decimal)(1 + TaxPct/100.0)` in C#; ported via
// Decimal.MulFloat so a 15% tax on 100.00 yields exactly 115.00. Remaining =
// billed - payments, all in exact base-10 Decimal.
//
// STATUS MATCHING: "Paid"/"Overdue" comparisons are case-insensitive, matching
// the C# StringComparison.OrdinalIgnoreCase.

package circleai

import (
	"sort"
	"strings"
	"sync"
	"time"
)

// InvoiceLine is one billable line. Ports the InvoiceLine record. TaxPct is a
// whole percentage (e.g. 15.0 for 15%).
type InvoiceLine struct {
	Description string
	Amount      Decimal
	TaxPct      float64
}

// Invoice is a customer invoice. Ports the Invoice record. Lines is copied
// defensively on issue. Status is a free-form string ("Issued"/"Paid"/"Overdue").
type Invoice struct {
	InvoiceId  string
	CustomerId string
	IssueDate  time.Time
	DueDate    time.Time
	Lines      []InvoiceLine
	Currency   string
	Status     string
}

// FinancePayment is a payment applied to an invoice. Ports the FinancePayment
// record.
type FinancePayment struct {
	PaymentId string
	InvoiceId string
	Amount    Decimal
	AtUtc     time.Time
}

// InvoiceBoard is the accounts-receivable board. Ports IInvoiceBoard.
type InvoiceBoard interface {
	Issue(i Invoice)
	Get(invoiceId string) (Invoice, bool)
	RecordPayment(p FinancePayment)
	// MarkOverdue flips every unpaid invoice past its due date to "Overdue".
	MarkOverdue(asOf time.Time)
	// RemainingOn is billed-with-tax minus payments for an invoice (0 if unknown).
	RemainingOn(invoiceId string) Decimal
	// TotalOutstanding is the sum of RemainingOn across every invoice.
	TotalOutstanding() Decimal
	// Overdue lists invoices currently in "Overdue" status.
	Overdue() []Invoice
}

// InMemoryInvoiceBoard is a concurrency-safe in-memory InvoiceBoard. Ports
// InMemoryInvoiceBoard (invoices in a map, payments in an ordered list guarded by
// a mutex).
type InMemoryInvoiceBoard struct {
	mu       sync.RWMutex
	invoices map[string]Invoice
	payments []FinancePayment
}

// NewInMemoryInvoiceBoard constructs an empty board.
func NewInMemoryInvoiceBoard() *InMemoryInvoiceBoard {
	return &InMemoryInvoiceBoard{
		invoices: make(map[string]Invoice),
		payments: make([]FinancePayment, 0),
	}
}

// Issue stores (or replaces by InvoiceId) an invoice, copying its lines. Ports
// Issue.
func (b *InMemoryInvoiceBoard) Issue(i Invoice) {
	i.Lines = append([]InvoiceLine(nil), i.Lines...)
	b.mu.Lock()
	b.invoices[i.InvoiceId] = i
	b.mu.Unlock()
}

// Get returns the invoice for id and true, or (zero, false) if absent.
func (b *InMemoryInvoiceBoard) Get(invoiceId string) (Invoice, bool) {
	b.mu.RLock()
	i, ok := b.invoices[invoiceId]
	b.mu.RUnlock()
	return i, ok
}

// RecordPayment appends a payment. Ports RecordPayment.
func (b *InMemoryInvoiceBoard) RecordPayment(p FinancePayment) {
	b.mu.Lock()
	b.payments = append(b.payments, p)
	b.mu.Unlock()
}

// MarkOverdue sets Status "Overdue" on every invoice whose DueDate is before asOf
// and is not already "Paid" (case-insensitive). Ports MarkOverdue.
func (b *InMemoryInvoiceBoard) MarkOverdue(asOf time.Time) {
	b.mu.Lock()
	for id, i := range b.invoices {
		if i.DueDate.Before(asOf) && !strings.EqualFold(i.Status, "Paid") {
			i.Status = "Overdue"
			b.invoices[id] = i
		}
	}
	b.mu.Unlock()
}

// RemainingOn returns billed-with-tax minus recorded payments for an invoice, or
// zero if the invoice is unknown. Ports RemainingOn.
func (b *InMemoryInvoiceBoard) RemainingOn(invoiceId string) Decimal {
	b.mu.RLock()
	defer b.mu.RUnlock()
	return b.remainingOnLocked(invoiceId)
}

// remainingOnLocked is RemainingOn's body; the caller must hold at least the read
// lock. Kept separate so TotalOutstanding can compute under one lock acquisition.
func (b *InMemoryInvoiceBoard) remainingOnLocked(invoiceId string) Decimal {
	inv, ok := b.invoices[invoiceId]
	if !ok {
		return Decimal{}
	}
	var billed Decimal
	for _, l := range inv.Lines {
		billed = billed.Add(l.Amount.MulFloat(1 + l.TaxPct/100.0))
	}
	var paid Decimal
	for _, p := range b.payments {
		if p.InvoiceId == invoiceId {
			paid = paid.Add(p.Amount)
		}
	}
	return billed.Sub(paid)
}

// TotalOutstanding sums RemainingOn across every invoice. Ports TotalOutstanding.
func (b *InMemoryInvoiceBoard) TotalOutstanding() Decimal {
	b.mu.RLock()
	defer b.mu.RUnlock()
	var total Decimal
	for id := range b.invoices {
		total = total.Add(b.remainingOnLocked(id))
	}
	return total
}

// Overdue lists invoices whose Status is "Overdue" (case-insensitive). Ports
// Overdue. Result order is unspecified in C# (ConcurrentDictionary values); this
// port sorts by InvoiceId so identical inputs yield identical output.
func (b *InMemoryInvoiceBoard) Overdue() []Invoice {
	b.mu.RLock()
	out := make([]Invoice, 0)
	for _, i := range b.invoices {
		if strings.EqualFold(i.Status, "Overdue") {
			out = append(out, i)
		}
	}
	b.mu.RUnlock()
	sort.SliceStable(out, func(i, j int) bool { return out[i].InvoiceId < out[j].InvoiceId })
	return out
}

// Interface guard.
var _ InvoiceBoard = (*InMemoryInvoiceBoard)(nil)
