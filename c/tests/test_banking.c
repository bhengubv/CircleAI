/*
 * test_banking.c — CircleAI.Banking (C11 port) verification against Contracts.cs,
 * InMemoryBanking.cs and NullImplementations.cs.
 */

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <assert.h>
#include "circle_ai/circle_ai.h"

#define D(x) ((ca_bank_decimal_t)((x) * CA_BANK_DECIMAL_SCALE))

static ca_bank_account_t mk_acct(const char *id, const char *owner,
                                 const char *ccy, ca_bank_decimal_t bal) {
    ca_bank_account_t a; memset(&a, 0, sizeof(a));
    a.account_id = (char *)id; a.owner_id = (char *)owner;
    a.currency = (char *)ccy; a.balance = bal;
    return a;
}
static ca_bank_ledger_entry_t mk_entry(const char *tx, const char *acc,
                                       ca_bank_decimal_t amt, int64_t at) {
    ca_bank_ledger_entry_t e; memset(&e, 0, sizeof(e));
    e.tx_id = (char *)tx; e.account_id = (char *)acc; e.amount = amt;
    e.memo = (char *)"m"; e.at_utc_ms = at;
    return e;
}
static ca_bank_payment_request_t mk_req(const char *from, const char *to,
                                        ca_bank_decimal_t amt, const char *ccy) {
    ca_bank_payment_request_t r; memset(&r, 0, sizeof(r));
    r.from_account = (char *)from; r.to_account = (char *)to;
    r.amount = amt; r.currency = (char *)ccy; r.memo = (char *)"rent";
    return r;
}

static void test_bank_core(void) {
    ca_bank_t *bank = ca_bank_create();
    assert(bank);

    ca_bank_account_t a1 = mk_acct("acc1", "alice", "ZAR", D(500));
    ca_bank_account_t a2 = mk_acct("acc2", "alice", "ZAR", D(0));
    assert(ca_bank_seed_account(bank, &a1) == 0);
    assert(ca_bank_seed_account(bank, &a2) == 0);

    ca_bank_account_t got;
    assert(ca_bank_get(bank, "acc1", &got) && got.balance == D(500));
    ca_bank_account_free(&got);
    assert(!ca_bank_get(bank, "none", &got));

    /* ListForOwner(alice) -> both. */
    size_t n = 0;
    ca_bank_account_t *arr = ca_bank_list_for_owner(bank, "alice", &n);
    assert(n == 2);
    ca_bank_account_free_array(arr, n);

    /* Append to unknown account -> 1. */
    ca_bank_ledger_entry_t bad = mk_entry("t0", "ghost", D(10), 1);
    assert(ca_bank_append(bank, &bad, NULL) == 1);

    /* Append moves the balance and returns the stored entry. */
    ca_bank_ledger_entry_t e1 = mk_entry("t1", "acc1", D(-100), 100);
    ca_bank_ledger_entry_t out;
    assert(ca_bank_append(bank, &e1, &out) == 0);
    assert(strcmp(out.tx_id, "t1") == 0 && out.amount == D(-100));
    ca_bank_ledger_entry_free(&out);
    assert(ca_bank_get(bank, "acc1", &got) && got.balance == D(400));
    ca_bank_account_free(&got);

    /* Read ordered by AtUtc descending, limited. */
    ca_bank_ledger_entry_t e2 = mk_entry("t2", "acc1", D(-50), 300);
    ca_bank_ledger_entry_t e3 = mk_entry("t3", "acc1", D(-25), 200);
    assert(ca_bank_append(bank, &e2, NULL) == 0);
    assert(ca_bank_append(bank, &e3, NULL) == 0);
    ca_bank_ledger_entry_t *led = ca_bank_read(bank, "acc1", 100, &n);
    assert(n == 3);
    assert(strcmp(led[0].tx_id, "t2") == 0);   /* 300 newest */
    assert(strcmp(led[1].tx_id, "t3") == 0);   /* 200 */
    assert(strcmp(led[2].tx_id, "t1") == 0);   /* 100 */
    ca_bank_ledger_entry_free_array(led, n);

    /* limit truncates. */
    led = ca_bank_read(bank, "acc1", 1, &n);
    assert(n == 1 && strcmp(led[0].tx_id, "t2") == 0);
    ca_bank_ledger_entry_free_array(led, n);

    /* unknown account -> empty. */
    led = ca_bank_read(bank, "ghost", 10, &n);
    assert(n == 0 && led == NULL);

    ca_bank_destroy(bank);
    printf("  bank_core: ok\n");
}

static void test_payments(void) {
    ca_bank_t *bank = ca_bank_create();
    ca_bank_account_t a1 = mk_acct("acc1", "alice", "ZAR", D(500));
    ca_bank_account_t a2 = mk_acct("acc2", "bob",   "ZAR", D(0));
    ca_bank_account_t a3 = mk_acct("acc3", "carol", "USD", D(1000));
    assert(ca_bank_seed_account(bank, &a1) == 0);
    assert(ca_bank_seed_account(bank, &a2) == 0);
    assert(ca_bank_seed_account(bank, &a3) == 0);

    ca_bank_payment_result_t res;

    /* Non-positive amount rejected. */
    ca_bank_payment_request_t rq = mk_req("acc1", "acc2", D(0), "ZAR");
    assert(ca_bank_process_payment(bank, &rq, &res) == 0);
    assert(!res.accepted && res.has_failure &&
           strcmp(res.failure_reason, "Amount must be positive") == 0);
    assert(res.tx_id && strlen(res.tx_id) == 32);   /* Guid("n") shape */
    ca_bank_payment_result_free(&res);

    /* Unknown source. */
    rq = mk_req("ghost", "acc2", D(10), "ZAR");
    assert(ca_bank_process_payment(bank, &rq, &res) == 0);
    assert(!res.accepted && strcmp(res.failure_reason, "Unknown source account") == 0);
    ca_bank_payment_result_free(&res);

    /* Unknown destination. */
    rq = mk_req("acc1", "ghost", D(10), "ZAR");
    assert(ca_bank_process_payment(bank, &rq, &res) == 0);
    assert(!res.accepted && strcmp(res.failure_reason, "Unknown destination account") == 0);
    ca_bank_payment_result_free(&res);

    /* Currency mismatch (acc3 is USD). */
    rq = mk_req("acc1", "acc3", D(10), "ZAR");
    assert(ca_bank_process_payment(bank, &rq, &res) == 0);
    assert(!res.accepted && strcmp(res.failure_reason, "Currency mismatch") == 0);
    ca_bank_payment_result_free(&res);

    /* Insufficient funds. */
    rq = mk_req("acc1", "acc2", D(9999), "ZAR");
    assert(ca_bank_process_payment(bank, &rq, &res) == 0);
    assert(!res.accepted && strcmp(res.failure_reason, "Insufficient funds") == 0);
    ca_bank_payment_result_free(&res);

    /* Happy path: 200 acc1 -> acc2, double-entry. */
    rq = mk_req("acc1", "acc2", D(200), "ZAR");
    assert(ca_bank_process_payment(bank, &rq, &res) == 0);
    assert(res.accepted && !res.has_failure && res.failure_reason == NULL);
    assert(strlen(res.tx_id) == 32);
    ca_bank_payment_result_free(&res);

    ca_bank_account_t got;
    assert(ca_bank_get(bank, "acc1", &got) && got.balance == D(300));
    ca_bank_account_free(&got);
    assert(ca_bank_get(bank, "acc2", &got) && got.balance == D(200));
    ca_bank_account_free(&got);

    /* Each side booked one ledger entry. */
    size_t n = 0;
    ca_bank_ledger_entry_t *led = ca_bank_read(bank, "acc1", 10, &n);
    assert(n == 1 && led[0].amount == D(-200));
    ca_bank_ledger_entry_free_array(led, n);
    led = ca_bank_read(bank, "acc2", 10, &n);
    assert(n == 1 && led[0].amount == D(200));
    ca_bank_ledger_entry_free_array(led, n);

    ca_bank_destroy(bank);
    printf("  payments: ok\n");
}

static void test_backends(void) {
    ca_bank_t *bank = ca_bank_create();
    ca_bank_account_t a1 = mk_acct("acc1", "alice", "ZAR", D(500));
    ca_bank_account_t a2 = mk_acct("acc2", "bob",   "ZAR", D(0));
    assert(ca_bank_seed_account(bank, &a1) == 0);
    assert(ca_bank_seed_account(bank, &a2) == 0);

    /* In-memory backends share the bank. */
    ca_bank_account_reader_t *rd = ca_bank_account_reader_create(bank);
    ca_bank_ledger_writer_t  *wr = ca_bank_ledger_writer_create(bank);
    ca_bank_payment_processor_t *pp = ca_bank_payment_processor_create(bank);
    assert(strcmp(ca_bank_account_reader_backend_id(rd), "in-memory") == 0);
    assert(strcmp(ca_bank_ledger_writer_backend_id(wr), "in-memory") == 0);
    assert(strcmp(ca_bank_payment_processor_backend_id(pp), "in-memory") == 0);

    ca_bank_account_t got;
    assert(ca_bank_account_reader_get(rd, "acc1", &got) && got.balance == D(500));
    ca_bank_account_free(&got);

    /* A payment via the processor moves money seen by the reader. */
    ca_bank_payment_request_t rq = mk_req("acc1", "acc2", D(100), "ZAR");
    ca_bank_payment_result_t res;
    assert(ca_bank_payment_processor_process(pp, &rq, &res) == 0 && res.accepted);
    ca_bank_payment_result_free(&res);
    assert(ca_bank_account_reader_get(rd, "acc2", &got) && got.balance == D(100));
    ca_bank_account_free(&got);

    /* The ledger writer reads that account's entries. */
    size_t n = 0;
    ca_bank_ledger_entry_t *led = ca_bank_ledger_writer_read(wr, "acc2", 10, &n);
    assert(n == 1 && led[0].amount == D(100));
    ca_bank_ledger_entry_free_array(led, n);

    ca_bank_account_reader_destroy(rd);
    ca_bank_ledger_writer_destroy(wr);
    ca_bank_payment_processor_destroy(pp);
    ca_bank_destroy(bank);
    printf("  backends: ok\n");
}

static void test_null_backends(void) {
    ca_bank_account_reader_t *rd = ca_bank_null_account_reader_create();
    ca_bank_ledger_writer_t  *wr = ca_bank_null_ledger_writer_create();
    ca_bank_payment_processor_t *pp = ca_bank_null_payment_processor_create();
    assert(strcmp(ca_bank_account_reader_backend_id(rd), "null") == 0);
    assert(strcmp(ca_bank_ledger_writer_backend_id(wr), "null") == 0);
    assert(strcmp(ca_bank_payment_processor_backend_id(pp), "null") == 0);

    /* NullAccountReader -> null / empty. */
    ca_bank_account_t got;
    assert(!ca_bank_account_reader_get(rd, "acc1", &got));
    size_t n = 0;
    ca_bank_account_t *arr = ca_bank_account_reader_list_for_owner(rd, "alice", &n);
    assert(n == 0 && arr == NULL);

    /* NullLedgerWriter.Append echoes the entry back; Read -> empty. */
    ca_bank_ledger_entry_t e = mk_entry("t1", "acc1", D(-5), 1);
    ca_bank_ledger_entry_t out;
    assert(ca_bank_ledger_writer_append(wr, &e, &out) == 0);
    assert(strcmp(out.tx_id, "t1") == 0 && out.amount == D(-5));
    ca_bank_ledger_entry_free(&out);
    ca_bank_ledger_entry_t *led = ca_bank_ledger_writer_read(wr, "acc1", 10, &n);
    assert(n == 0 && led == NULL);

    /* NullPaymentProcessor -> {Guid.Empty, false, "NullPaymentProcessor."}. */
    ca_bank_payment_request_t rq = mk_req("acc1", "acc2", D(10), "ZAR");
    ca_bank_payment_result_t res;
    assert(ca_bank_payment_processor_process(pp, &rq, &res) == 0);
    assert(!res.accepted && res.has_failure &&
           strcmp(res.failure_reason, "NullPaymentProcessor.") == 0);
    assert(strcmp(res.tx_id, "00000000-0000-0000-0000-000000000000") == 0);
    ca_bank_payment_result_free(&res);

    ca_bank_account_reader_destroy(rd);
    ca_bank_ledger_writer_destroy(wr);
    ca_bank_payment_processor_destroy(pp);
    printf("  null_backends: ok\n");
}

int main(void) {
    test_bank_core();
    test_payments();
    test_backends();
    test_null_backends();
    printf("test_banking: all assertions passed\n");
    return 0;
}
