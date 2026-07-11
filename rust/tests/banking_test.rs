//! banking_test.rs
//!
//! Ports the behaviour of `CircleAI.Banking`: the shared `InMemoryBank`
//! (seed / list-for-owner / append with balance application + newest-first read
//! / balance-checked double-entry payment) behind the reader / ledger / payment
//! adapters, plus the fail-closed null backends.

use chrono::{Duration, Utc};
use circle_ai::banking::{
    Account, IAccountReader, ILedgerWriter, IPaymentProcessor, InMemoryAccountReader, InMemoryBank,
    InMemoryLedgerWriter, InMemoryPaymentProcessor, LedgerEntry, NullAccountReader, NullLedgerWriter,
    NullPaymentProcessor, PaymentRequest, EMPTY_GUID,
};

fn seeded_bank() -> InMemoryBank {
    let bank = InMemoryBank::new();
    bank.seed_account(Account::new("a1", "owner", "ZAR", 100.0));
    bank.seed_account(Account::new("a2", "owner", "ZAR", 0.0));
    bank
}

#[test]
fn reader_gets_and_lists_by_owner() {
    let bank = InMemoryBank::new();
    bank.seed_account(Account::new("a1", "alice", "ZAR", 50.0));
    bank.seed_account(Account::new("a2", "alice", "ZAR", 10.0));
    bank.seed_account(Account::new("b1", "bob", "ZAR", 5.0));
    let reader = InMemoryAccountReader::new(bank.clone());

    assert_eq!(reader.backend_id(), "in-memory");
    assert_eq!(reader.get_account("a1").unwrap().balance, 50.0);
    assert!(reader.get_account("missing").is_none());

    let mut ids: Vec<String> = reader
        .list_for_owner("alice")
        .into_iter()
        .map(|a| a.account_id)
        .collect();
    ids.sort();
    assert_eq!(ids, vec!["a1", "a2"]);
}

#[test]
fn append_applies_balance_and_reads_newest_first() {
    let bank = seeded_bank();
    let writer = InMemoryLedgerWriter::new(bank.clone());

    let older = LedgerEntry::new("t-old", "a1", 5.0, "old", Utc::now() - Duration::hours(1));
    let newer = LedgerEntry::new("t-new", "a1", -3.0, "new", Utc::now());
    writer.append(older);
    writer.append(newer);

    // 100 + 5 - 3 = 102.
    assert_eq!(bank.get("a1").unwrap().balance, 102.0);

    let entries = writer.read("a1", 100);
    let ids: Vec<&str> = entries.iter().map(|e| e.tx_id.as_str()).collect();
    assert_eq!(ids, vec!["t-new", "t-old"]);
}

#[test]
fn read_respects_limit() {
    let bank = seeded_bank();
    for i in 0i64..5 {
        bank.append(LedgerEntry::new(
            format!("t{i}"),
            "a1",
            1.0,
            "m",
            Utc::now() + Duration::seconds(i),
        ));
    }
    assert_eq!(bank.read("a1", 2).len(), 2);
    assert!(bank.read("unknown", 100).is_empty());
}

#[test]
#[should_panic(expected = "Unknown account")]
fn append_unknown_account_panics() {
    let bank = InMemoryBank::new();
    bank.append(LedgerEntry::new("t", "nope", 1.0, "m", Utc::now()));
}

#[test]
fn payment_double_entry_moves_funds() {
    let bank = seeded_bank();
    let proc = InMemoryPaymentProcessor::new(bank.clone());
    assert_eq!(proc.backend_id(), "in-memory");

    let res = proc.process(PaymentRequest::new("a1", "a2", 40.0, "ZAR", "rent"));
    assert!(res.accepted);
    assert!(res.failure_reason.is_none());
    assert_eq!(bank.get("a1").unwrap().balance, 60.0);
    assert_eq!(bank.get("a2").unwrap().balance, 40.0);

    // Two ledger entries share the tx id; debit on source, credit on dest.
    let src_entry = &bank.read("a1", 100)[0];
    let dst_entry = &bank.read("a2", 100)[0];
    assert_eq!(src_entry.tx_id, res.tx_id);
    assert_eq!(dst_entry.tx_id, res.tx_id);
    assert_eq!(src_entry.amount, -40.0);
    assert_eq!(dst_entry.amount, 40.0);
    assert_eq!(src_entry.memo, "To a2: rent");
    assert_eq!(dst_entry.memo, "From a1: rent");
}

#[test]
fn payment_rejections() {
    let bank = seeded_bank();
    bank.seed_account(Account::new("usd", "owner", "USD", 100.0));

    let non_positive = bank.process_payment(PaymentRequest::new("a1", "a2", 0.0, "ZAR", "m"));
    assert!(!non_positive.accepted);
    assert_eq!(non_positive.failure_reason.as_deref(), Some("Amount must be positive"));

    let no_src = bank.process_payment(PaymentRequest::new("nope", "a2", 1.0, "ZAR", "m"));
    assert_eq!(no_src.failure_reason.as_deref(), Some("Unknown source account"));

    let no_dst = bank.process_payment(PaymentRequest::new("a1", "nope", 1.0, "ZAR", "m"));
    assert_eq!(no_dst.failure_reason.as_deref(), Some("Unknown destination account"));

    let mismatch = bank.process_payment(PaymentRequest::new("a1", "usd", 1.0, "ZAR", "m"));
    assert_eq!(mismatch.failure_reason.as_deref(), Some("Currency mismatch"));

    let broke = bank.process_payment(PaymentRequest::new("a2", "a1", 1.0, "ZAR", "m"));
    assert_eq!(broke.failure_reason.as_deref(), Some("Insufficient funds"));

    // No rejection mutated a balance.
    assert_eq!(bank.get("a1").unwrap().balance, 100.0);
    assert_eq!(bank.get("a2").unwrap().balance, 0.0);
}

#[test]
fn currency_check_is_case_insensitive() {
    let bank = InMemoryBank::new();
    bank.seed_account(Account::new("a1", "o", "ZAR", 100.0));
    bank.seed_account(Account::new("a2", "o", "zar", 0.0));
    let res = bank.process_payment(PaymentRequest::new("a1", "a2", 10.0, "zAr", "m"));
    assert!(res.accepted);
}

#[test]
fn null_backends_fail_closed() {
    let reader = NullAccountReader::INSTANCE;
    assert_eq!(reader.backend_id(), "null");
    assert!(reader.get_account("a1").is_none());
    assert!(reader.list_for_owner("o").is_empty());

    let writer = NullLedgerWriter::INSTANCE;
    assert_eq!(writer.backend_id(), "null");
    let echoed = writer.append(LedgerEntry::new("t", "a", 1.0, "m", Utc::now()));
    assert_eq!(echoed.tx_id, "t");
    assert!(writer.read("a", 100).is_empty());

    let proc = NullPaymentProcessor::INSTANCE;
    assert_eq!(proc.backend_id(), "null");
    let res = proc.process(PaymentRequest::new("a1", "a2", 10.0, "ZAR", "m"));
    assert!(!res.accepted);
    assert_eq!(res.tx_id, EMPTY_GUID);
    assert_eq!(res.failure_reason.as_deref(), Some("NullPaymentProcessor."));
}
