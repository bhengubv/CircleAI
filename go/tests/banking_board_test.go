// banking_board_test.go
//
// Verifies the CircleAI.Banking port (banking_board.go): account read, ledger
// append/read ordering, double-entry payment processing with balance/currency/
// funds checks, and the null fail-closed backends.

package circleai_test

import (
	"context"
	"testing"
	"time"

	circleai "github.com/bhengubv/CircleAI/go"
)

func mkBank(t *testing.T) *circleai.InMemoryBank {
	t.Helper()
	b := circleai.NewInMemoryBank()
	b.SeedAccount(circleai.BankAccount{AccountId: "acc-a", OwnerId: "owner-1", Currency: "ZAR", Balance: circleai.DecimalFromInt(1000)})
	b.SeedAccount(circleai.BankAccount{AccountId: "acc-b", OwnerId: "owner-1", Currency: "ZAR", Balance: circleai.DecimalFromInt(50)})
	b.SeedAccount(circleai.BankAccount{AccountId: "acc-usd", OwnerId: "owner-2", Currency: "USD", Balance: circleai.DecimalFromInt(500)})
	return b
}

func TestBanking_ReaderBackendAndGet(t *testing.T) {
	bank := mkBank(t)
	r := circleai.NewInMemoryAccountReader(bank)
	if r.BackendId() != "in-memory" {
		t.Fatalf("backend = %q", r.BackendId())
	}
	a, ok, err := r.GetAccount(context.Background(), "acc-a")
	if err != nil || !ok || a.OwnerId != "owner-1" {
		t.Fatalf("get acc-a = %+v ok=%v err=%v", a, ok, err)
	}
	if _, ok, _ := r.GetAccount(context.Background(), "nope"); ok {
		t.Fatalf("missing account should not be found")
	}
	owned, _ := r.ListForOwner(context.Background(), "owner-1")
	if len(owned) != 2 {
		t.Fatalf("owner-1 should have 2 accounts, got %d", len(owned))
	}
}

func TestBanking_LedgerAppendUpdatesBalanceAndReadsNewestFirst(t *testing.T) {
	bank := mkBank(t)
	w := circleai.NewInMemoryLedgerWriter(bank)
	base := time.Date(2026, 7, 1, 0, 0, 0, 0, time.UTC)
	_, _ = w.Append(context.Background(), circleai.LedgerEntry{TxId: "t1", AccountId: "acc-a", Amount: circleai.DecimalFromInt(100), Memo: "in", AtUtc: base})
	_, _ = w.Append(context.Background(), circleai.LedgerEntry{TxId: "t2", AccountId: "acc-a", Amount: circleai.DecimalFromInt(-30), Memo: "out", AtUtc: base.Add(time.Hour)})

	// Balance 1000 + 100 - 30 = 1070.
	a, _ := bank.Get("acc-a")
	if !a.Balance.Equal(circleai.DecimalFromInt(1070)) {
		t.Fatalf("balance = %s, want 1070", a.Balance)
	}
	entries, _ := w.Read(context.Background(), "acc-a", 100)
	if len(entries) != 2 || entries[0].TxId != "t2" || entries[1].TxId != "t1" {
		t.Fatalf("ledger newest-first failed: %+v", entries)
	}
	// Limit cap.
	one, _ := w.Read(context.Background(), "acc-a", 1)
	if len(one) != 1 || one[0].TxId != "t2" {
		t.Fatalf("limit cap failed: %+v", one)
	}
	// Unknown account append errors.
	if _, err := w.Append(context.Background(), circleai.LedgerEntry{TxId: "x", AccountId: "ghost", Amount: circleai.DecimalFromInt(1)}); err == nil {
		t.Fatalf("append to unknown account must error")
	}
	// Empty ledger read.
	if e, _ := w.Read(context.Background(), "acc-usd", 100); len(e) != 0 {
		t.Fatalf("empty ledger should read empty, got %d", len(e))
	}
	// LINQ Take semantics: limit 0 and negative both yield empty.
	if e, _ := w.Read(context.Background(), "acc-a", 0); len(e) != 0 {
		t.Fatalf("limit 0 should read empty, got %d", len(e))
	}
	if e, _ := w.Read(context.Background(), "acc-a", -5); len(e) != 0 {
		t.Fatalf("negative limit should read empty (Take semantics), got %d", len(e))
	}
}

func TestBanking_PaymentHappyPathDoubleEntry(t *testing.T) {
	bank := mkBank(t)
	p := circleai.NewInMemoryPaymentProcessor(bank)
	res, _ := p.Process(context.Background(), circleai.PaymentRequest{FromAccount: "acc-a", ToAccount: "acc-b", Amount: circleai.DecimalFromInt(200), Currency: "ZAR", Memo: "rent"})
	if !res.Accepted || res.FailureReason != "" {
		t.Fatalf("payment should succeed: %+v", res)
	}
	if len(res.TxId) != 32 {
		t.Fatalf("txid should be 32 hex chars, got %q (len %d)", res.TxId, len(res.TxId))
	}
	a, _ := bank.Get("acc-a")
	b, _ := bank.Get("acc-b")
	if !a.Balance.Equal(circleai.DecimalFromInt(800)) {
		t.Fatalf("source balance = %s, want 800", a.Balance)
	}
	if !b.Balance.Equal(circleai.DecimalFromInt(250)) {
		t.Fatalf("dest balance = %s, want 250", b.Balance)
	}
	// Both legs recorded under the same TxId.
	w := circleai.NewInMemoryLedgerWriter(bank)
	al, _ := w.Read(context.Background(), "acc-a", 10)
	bl, _ := w.Read(context.Background(), "acc-b", 10)
	if len(al) != 1 || al[0].TxId != res.TxId || !al[0].Amount.Equal(circleai.DecimalFromInt(-200)) {
		t.Fatalf("source leg wrong: %+v", al)
	}
	if len(bl) != 1 || bl[0].TxId != res.TxId || !bl[0].Amount.Equal(circleai.DecimalFromInt(200)) {
		t.Fatalf("dest leg wrong: %+v", bl)
	}
}

func TestBanking_PaymentFailureModes(t *testing.T) {
	bank := mkBank(t)
	p := circleai.NewInMemoryPaymentProcessor(bank)
	ctx := context.Background()

	cases := []struct {
		name   string
		req    circleai.PaymentRequest
		reason string
	}{
		{"nonpositive", circleai.PaymentRequest{FromAccount: "acc-a", ToAccount: "acc-b", Amount: circleai.ZeroDecimal, Currency: "ZAR"}, "Amount must be positive"},
		{"unknown-source", circleai.PaymentRequest{FromAccount: "ghost", ToAccount: "acc-b", Amount: circleai.DecimalFromInt(10), Currency: "ZAR"}, "Unknown source account"},
		{"unknown-dest", circleai.PaymentRequest{FromAccount: "acc-a", ToAccount: "ghost", Amount: circleai.DecimalFromInt(10), Currency: "ZAR"}, "Unknown destination account"},
		{"currency", circleai.PaymentRequest{FromAccount: "acc-a", ToAccount: "acc-b", Amount: circleai.DecimalFromInt(10), Currency: "USD"}, "Currency mismatch"},
		{"funds", circleai.PaymentRequest{FromAccount: "acc-b", ToAccount: "acc-a", Amount: circleai.DecimalFromInt(9999), Currency: "ZAR"}, "Insufficient funds"},
	}
	for _, c := range cases {
		res, _ := p.Process(ctx, c.req)
		if res.Accepted || res.FailureReason != c.reason {
			t.Fatalf("%s: got accepted=%v reason=%q want reason=%q", c.name, res.Accepted, res.FailureReason, c.reason)
		}
		if len(res.TxId) != 32 {
			t.Fatalf("%s: failure result should still carry a 32-hex txid, got %q", c.name, res.TxId)
		}
	}
	// Balances untouched after all failures.
	a, _ := bank.Get("acc-a")
	if !a.Balance.Equal(circleai.DecimalFromInt(1000)) {
		t.Fatalf("acc-a balance changed on failures: %s", a.Balance)
	}
}

func TestBanking_CurrencyMatchIsCaseInsensitive(t *testing.T) {
	bank := mkBank(t)
	p := circleai.NewInMemoryPaymentProcessor(bank)
	res, _ := p.Process(context.Background(), circleai.PaymentRequest{FromAccount: "acc-a", ToAccount: "acc-b", Amount: circleai.DecimalFromInt(10), Currency: "zar"})
	if !res.Accepted {
		t.Fatalf("lowercase currency should still match ZAR: %+v", res)
	}
}

func TestBanking_NullBackends(t *testing.T) {
	ctx := context.Background()
	if circleai.NullAccountReaderInstance.BackendId() != "null" {
		t.Fatalf("null reader backend")
	}
	if _, ok, _ := circleai.NullAccountReaderInstance.GetAccount(ctx, "x"); ok {
		t.Fatalf("null reader should return no account")
	}
	if l, _ := circleai.NullAccountReaderInstance.ListForOwner(ctx, "x"); len(l) != 0 {
		t.Fatalf("null reader list should be empty")
	}
	e := circleai.LedgerEntry{TxId: "t", AccountId: "a", Amount: circleai.DecimalFromInt(5)}
	if got, _ := circleai.NullLedgerWriterInstance.Append(ctx, e); got.TxId != "t" {
		t.Fatalf("null writer should echo the entry")
	}
	if r, _ := circleai.NullLedgerWriterInstance.Read(ctx, "a", 10); len(r) != 0 {
		t.Fatalf("null writer read should be empty")
	}
	res, _ := circleai.NullPaymentProcessorInstance.Process(ctx, circleai.PaymentRequest{})
	if res.Accepted || res.FailureReason != "NullPaymentProcessor." || res.TxId != "00000000-0000-0000-0000-000000000000" {
		t.Fatalf("null processor result wrong: %+v", res)
	}
}
