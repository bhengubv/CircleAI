// banking_board.go
//
// Ports the CircleAI.Banking vertical:
//   Contracts.cs         -> BankAccount, LedgerEntry, PaymentRequest,
//                           PaymentResult records; AccountReader / LedgerWriter /
//                           PaymentProcessor interfaces (I-prefix dropped per Go
//                           convention).
//   InMemoryBanking.cs   -> InMemoryBank + InMemoryAccountReader /
//                           InMemoryLedgerWriter / InMemoryPaymentProcessor.
//   NullImplementations.cs -> NullAccountReader / NullLedgerWriter /
//                           NullPaymentProcessor (fail-closed defaults).
//
// FLAT-PACKAGE DISAMBIGUATION: CircleAI.Personal.Finance also declares a record
// named `Account` (with a different shape). Both port into the one Go package, so
// the Banking one is named BankAccount and the Personal.Finance one is named
// PersonalAccount (see personal_finance_board.go). All other Banking type names
// are unique and kept verbatim.
//
// ASYNC: the C# contracts are ValueTask-returning with a CancellationToken. Per
// the established telephony convention they port to methods taking a
// context.Context and returning (value, error). The in-memory implementations
// never fail I/O, so they return a nil error (except payment, whose business
// outcome is carried in PaymentResult, mirroring the C#).
//
// MONEY: decimal balances/amounts use the shared base-10 Decimal type
// (telephony_decimal.go + domain_money.go) so cents arithmetic is exact.
//
// CONCURRENCY: InMemoryBank guards account balances + the ledger with a single
// mutex, reproducing the C# `_txLock` so a payment's paired debit/credit and the
// balance updates are atomic. IDs for payment results come from google/uuid
// (32-char hex, no dashes) matching Guid.NewGuid().ToString("n").

package circleai

import (
	"context"
	"errors"
	"sort"
	"strings"
	"sync"
	"time"

	"github.com/google/uuid"
)

// BankAccount is a bank account. Ports the CircleAI.Banking `Account` record
// (renamed to avoid colliding with Personal.Finance's Account in the flat
// package). Treat as an immutable value; balance changes produce a new value.
type BankAccount struct {
	AccountId string
	OwnerId   string
	Currency  string
	Balance   Decimal
}

// LedgerEntry is one posting against an account. Ports the LedgerEntry record.
// Amount is signed (negative = debit, positive = credit).
type LedgerEntry struct {
	TxId      string
	AccountId string
	Amount    Decimal
	Memo      string
	AtUtc     time.Time
}

// PaymentRequest asks to move Amount from FromAccount to ToAccount. Ports the
// PaymentRequest record.
type PaymentRequest struct {
	FromAccount string
	ToAccount   string
	Amount      Decimal
	Currency    string
	Memo        string
}

// PaymentResult is the outcome of a payment. Ports the PaymentResult record.
// FailureReason is empty when Accepted is true (the C# uses a nullable string;
// empty string is the Go analogue of null here).
type PaymentResult struct {
	TxId          string
	Accepted      bool
	FailureReason string
}

// AccountReader reads accounts. Ports IAccountReader.
type AccountReader interface {
	// BackendId identifies the backing store (e.g. "in-memory", "null").
	BackendId() string
	// GetAccount returns the account and true, or (zero, false) when absent.
	GetAccount(ctx context.Context, accountId string) (BankAccount, bool, error)
	// ListForOwner returns every account owned by ownerId.
	ListForOwner(ctx context.Context, ownerId string) ([]BankAccount, error)
}

// LedgerWriter appends and reads ledger entries. Ports ILedgerWriter.
type LedgerWriter interface {
	BackendId() string
	// Append records an entry and returns it.
	Append(ctx context.Context, entry LedgerEntry) (LedgerEntry, error)
	// Read returns up to limit entries for accountId, newest first.
	Read(ctx context.Context, accountId string, limit int) ([]LedgerEntry, error)
}

// PaymentProcessor processes payments. Ports IPaymentProcessor.
type PaymentProcessor interface {
	BackendId() string
	// Process attempts req and returns its result (business failures are carried
	// in PaymentResult.Accepted/FailureReason, not the error).
	Process(ctx context.Context, req PaymentRequest) (PaymentResult, error)
}

// DefaultLedgerReadLimit is the C# default `limit = 100` for ledger reads.
const DefaultLedgerReadLimit = 100

// InMemoryBank is the concurrent in-memory bank shared by the reader, ledger, and
// payment processor. Ports InMemoryBank. All balance + ledger mutations are
// serialized by a single mutex (the C# `_txLock`).
type InMemoryBank struct {
	mu       sync.Mutex
	accounts map[string]BankAccount
	ledger   map[string][]LedgerEntry
}

// NewInMemoryBank constructs an empty bank.
func NewInMemoryBank() *InMemoryBank {
	return &InMemoryBank{
		accounts: make(map[string]BankAccount),
		ledger:   make(map[string][]LedgerEntry),
	}
}

// SeedAccount stores (or replaces by AccountId) an account. Ports SeedAccount.
func (b *InMemoryBank) SeedAccount(a BankAccount) {
	b.mu.Lock()
	b.accounts[a.AccountId] = a
	b.mu.Unlock()
}

// Get returns the account for id and true, or (zero, false) if absent.
func (b *InMemoryBank) Get(id string) (BankAccount, bool) {
	b.mu.Lock()
	a, ok := b.accounts[id]
	b.mu.Unlock()
	return a, ok
}

// ListForOwner returns all accounts for ownerId (unordered, matching the C#
// ConcurrentDictionary enumeration which has no defined order).
func (b *InMemoryBank) ListForOwner(ownerId string) []BankAccount {
	b.mu.Lock()
	out := make([]BankAccount, 0)
	for _, a := range b.accounts {
		if a.OwnerId == ownerId {
			out = append(out, a)
		}
	}
	b.mu.Unlock()
	return out
}

// Append records entry, adjusts the target account balance by entry.Amount, and
// returns entry. Errors if the account is unknown. Ports Append.
func (b *InMemoryBank) Append(entry LedgerEntry) (LedgerEntry, error) {
	b.mu.Lock()
	defer b.mu.Unlock()
	return b.appendLocked(entry)
}

// appendLocked is Append's body; the caller must hold b.mu.
func (b *InMemoryBank) appendLocked(entry LedgerEntry) (LedgerEntry, error) {
	acct, ok := b.accounts[entry.AccountId]
	if !ok {
		return LedgerEntry{}, errors.New("Unknown account " + entry.AccountId)
	}
	acct.Balance = acct.Balance.Add(entry.Amount)
	b.accounts[entry.AccountId] = acct
	b.ledger[entry.AccountId] = append(b.ledger[entry.AccountId], entry)
	return entry, nil
}

// Read returns up to limit entries for accountId ordered AtUtc descending
// (newest first). Ports Read. Ties on AtUtc break by original append order
// reversed is not defined by C# (ConcurrentDictionary + OrderByDescending is a
// stable sort over insertion order), so this uses a stable sort to keep equal
// timestamps in reverse-append order deterministically.
func (b *InMemoryBank) Read(accountId string, limit int) []LedgerEntry {
	b.mu.Lock()
	list, ok := b.ledger[accountId]
	if !ok {
		b.mu.Unlock()
		return []LedgerEntry{}
	}
	cp := make([]LedgerEntry, len(list))
	copy(cp, list)
	b.mu.Unlock()

	sort.SliceStable(cp, func(i, j int) bool { return cp[i].AtUtc.After(cp[j].AtUtc) })
	// LINQ Take(n) yields empty for n <= 0; clamp negatives to 0 to match.
	if limit < 0 {
		limit = 0
	}
	if len(cp) > limit {
		cp = cp[:limit]
	}
	return cp
}

// ProcessPayment moves req.Amount from source to destination via a paired
// debit/credit, after validating amount, existence, currency, and funds. Ports
// ProcessPayment including double-entry bookkeeping and the exact failure
// messages/order. Every result carries a fresh 32-hex transaction id.
func (b *InMemoryBank) ProcessPayment(req PaymentRequest) PaymentResult {
	if req.Amount.Sign() <= 0 {
		return PaymentResult{TxId: newTxId(), Accepted: false, FailureReason: "Amount must be positive"}
	}
	b.mu.Lock()
	defer b.mu.Unlock()

	src, ok := b.accounts[req.FromAccount]
	if !ok {
		return PaymentResult{TxId: newTxId(), Accepted: false, FailureReason: "Unknown source account"}
	}
	dst, ok := b.accounts[req.ToAccount]
	if !ok {
		return PaymentResult{TxId: newTxId(), Accepted: false, FailureReason: "Unknown destination account"}
	}
	if !strings.EqualFold(src.Currency, req.Currency) || !strings.EqualFold(dst.Currency, req.Currency) {
		return PaymentResult{TxId: newTxId(), Accepted: false, FailureReason: "Currency mismatch"}
	}
	if src.Balance.Less(req.Amount) {
		return PaymentResult{TxId: newTxId(), Accepted: false, FailureReason: "Insufficient funds"}
	}

	txId := newTxId()
	now := time.Now().UTC()
	// Debit source, credit destination (append under the held lock).
	if _, err := b.appendLocked(LedgerEntry{TxId: txId, AccountId: req.FromAccount, Amount: req.Amount.Neg(), Memo: "To " + req.ToAccount + ": " + req.Memo, AtUtc: now}); err != nil {
		return PaymentResult{TxId: txId, Accepted: false, FailureReason: err.Error()}
	}
	if _, err := b.appendLocked(LedgerEntry{TxId: txId, AccountId: req.ToAccount, Amount: req.Amount, Memo: "From " + req.FromAccount + ": " + req.Memo, AtUtc: now}); err != nil {
		return PaymentResult{TxId: txId, Accepted: false, FailureReason: err.Error()}
	}
	return PaymentResult{TxId: txId, Accepted: true}
}

// newTxId returns a 32-char lowercase hex id, matching Guid.NewGuid().ToString("n").
func newTxId() string { return strings.ReplaceAll(uuid.NewString(), "-", "") }

// --- In-memory backends (thin adapters over InMemoryBank) ---

// InMemoryAccountReader adapts an InMemoryBank to AccountReader. Ports
// InMemoryAccountReader. BackendId is "in-memory".
type InMemoryAccountReader struct{ bank *InMemoryBank }

// NewInMemoryAccountReader wraps bank.
func NewInMemoryAccountReader(bank *InMemoryBank) *InMemoryAccountReader {
	return &InMemoryAccountReader{bank: bank}
}
func (r *InMemoryAccountReader) BackendId() string { return "in-memory" }
func (r *InMemoryAccountReader) GetAccount(_ context.Context, id string) (BankAccount, bool, error) {
	a, ok := r.bank.Get(id)
	return a, ok, nil
}
func (r *InMemoryAccountReader) ListForOwner(_ context.Context, ownerId string) ([]BankAccount, error) {
	return r.bank.ListForOwner(ownerId), nil
}

// InMemoryLedgerWriter adapts an InMemoryBank to LedgerWriter. Ports
// InMemoryLedgerWriter. BackendId is "in-memory".
type InMemoryLedgerWriter struct{ bank *InMemoryBank }

// NewInMemoryLedgerWriter wraps bank.
func NewInMemoryLedgerWriter(bank *InMemoryBank) *InMemoryLedgerWriter {
	return &InMemoryLedgerWriter{bank: bank}
}
func (w *InMemoryLedgerWriter) BackendId() string { return "in-memory" }
func (w *InMemoryLedgerWriter) Append(_ context.Context, e LedgerEntry) (LedgerEntry, error) {
	return w.bank.Append(e)
}
func (w *InMemoryLedgerWriter) Read(_ context.Context, acc string, limit int) ([]LedgerEntry, error) {
	return w.bank.Read(acc, limit), nil
}

// InMemoryPaymentProcessor adapts an InMemoryBank to PaymentProcessor. Ports
// InMemoryPaymentProcessor. BackendId is "in-memory".
type InMemoryPaymentProcessor struct{ bank *InMemoryBank }

// NewInMemoryPaymentProcessor wraps bank.
func NewInMemoryPaymentProcessor(bank *InMemoryBank) *InMemoryPaymentProcessor {
	return &InMemoryPaymentProcessor{bank: bank}
}
func (p *InMemoryPaymentProcessor) BackendId() string { return "in-memory" }
func (p *InMemoryPaymentProcessor) Process(_ context.Context, req PaymentRequest) (PaymentResult, error) {
	return p.bank.ProcessPayment(req), nil
}

// --- Null (fail-closed) backends ---

// NullAccountReader returns no accounts. Ports NullAccountReader. BackendId "null".
type NullAccountReader struct{}

// NullAccountReaderInstance is the shared fail-closed reader (ports the static Instance).
var NullAccountReaderInstance = NullAccountReader{}

func (NullAccountReader) BackendId() string { return "null" }
func (NullAccountReader) GetAccount(context.Context, string) (BankAccount, bool, error) {
	return BankAccount{}, false, nil
}
func (NullAccountReader) ListForOwner(context.Context, string) ([]BankAccount, error) {
	return []BankAccount{}, nil
}

// NullLedgerWriter accepts appends (echoing them) but stores nothing and reads
// empty. Ports NullLedgerWriter. BackendId "null".
type NullLedgerWriter struct{}

// NullLedgerWriterInstance is the shared fail-closed writer.
var NullLedgerWriterInstance = NullLedgerWriter{}

func (NullLedgerWriter) BackendId() string { return "null" }
func (NullLedgerWriter) Append(_ context.Context, e LedgerEntry) (LedgerEntry, error) {
	return e, nil
}
func (NullLedgerWriter) Read(context.Context, string, int) ([]LedgerEntry, error) {
	return []LedgerEntry{}, nil
}

// NullPaymentProcessor always declines. Ports NullPaymentProcessor. The declined
// result carries the all-zero Guid ("00000000-0000-0000-0000-000000000000",
// matching Guid.Empty.ToString()) and reason "NullPaymentProcessor.".
type NullPaymentProcessor struct{}

// NullPaymentProcessorInstance is the shared fail-closed processor.
var NullPaymentProcessorInstance = NullPaymentProcessor{}

func (NullPaymentProcessor) BackendId() string { return "null" }
func (NullPaymentProcessor) Process(context.Context, PaymentRequest) (PaymentResult, error) {
	return PaymentResult{TxId: "00000000-0000-0000-0000-000000000000", Accepted: false, FailureReason: "NullPaymentProcessor."}, nil
}

// Interface guards.
var (
	_ AccountReader    = (*InMemoryAccountReader)(nil)
	_ LedgerWriter     = (*InMemoryLedgerWriter)(nil)
	_ PaymentProcessor = (*InMemoryPaymentProcessor)(nil)
	_ AccountReader    = NullAccountReader{}
	_ LedgerWriter     = NullLedgerWriter{}
	_ PaymentProcessor = NullPaymentProcessor{}
)
