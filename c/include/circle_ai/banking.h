#ifndef CIRCLE_AI_BANKING_H
#define CIRCLE_AI_BANKING_H

/*
 * banking.h — CircleAI.Banking (C11 port of Contracts.cs + InMemoryBanking.cs +
 * NullImplementations.cs).
 *
 *   Records : Account(AccountId, OwnerId, Currency, Balance);
 *             LedgerEntry(TxId, AccountId, Amount, Memo, AtUtc);
 *             PaymentRequest(FromAccount, ToAccount, Amount, Currency, Memo);
 *             PaymentResult(TxId, Accepted, FailureReason?).
 *   Contracts: IAccountReader / ILedgerWriter / IPaymentProcessor — each with a
 *             BackendId. The async ValueTask methods collapse to synchronous
 *             calls here.
 *   Bank    : InMemoryBank shared by the three backends. SeedAccount, Get,
 *             ListForOwner, Append (Balance += Amount; unknown account -> error),
 *             Read(limit) (that account's ledger ordered by AtUtc descending,
 *             first `limit`), ProcessPayment (positive-amount + known-accounts +
 *             currency-match + sufficient-funds checks, then double-entry: debit
 *             source, credit destination with one shared TxId).
 *   Backends: InMemoryAccountReader / InMemoryLedgerWriter /
 *             InMemoryPaymentProcessor over a shared InMemoryBank (BackendId
 *             "in-memory"); Null* variants (BackendId "null", fail-closed).
 *
 * The C# uses Guid.NewGuid().ToString("n") (32 lowercase hex) for TxIds. Here a
 * process-monotonic counter is formatted to the same 32-hex-char shape so results
 * are deterministic and testable; only the accepted/failure semantics are
 * load-bearing.
 *
 * Conventions: ca_ prefix, _t types, opaque handles, strdup-owning fields with
 * matching *_free, deep-copy getters, errors via NULL / count SIZE_MAX. Money as
 * ca_bank_decimal_t (int64 scaled 1e6). AtUtc as int64 Unix ms UTC. Linear
 * arrays, no pthreads.
 *
 * Pure C11 + libc.
 */

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

/* Money surrogate: int64 count of 1e-6 units. */
typedef int64_t ca_bank_decimal_t;
#define CA_BANK_DECIMAL_SCALE 1000000LL

/* Account(AccountId, OwnerId, Currency, decimal Balance). */
typedef struct {
    char             *account_id; /* owned, non-null */
    char             *owner_id;   /* owned, non-null */
    char             *currency;   /* owned, non-null */
    ca_bank_decimal_t balance;
} ca_bank_account_t;

void ca_bank_account_free(ca_bank_account_t *a);
void ca_bank_account_free_array(ca_bank_account_t *arr, size_t count);

/* LedgerEntry(TxId, AccountId, decimal Amount, Memo, DateTimeOffset AtUtc). */
typedef struct {
    char             *tx_id;      /* owned, non-null */
    char             *account_id; /* owned, non-null */
    ca_bank_decimal_t amount;
    char             *memo;       /* owned, non-null */
    int64_t           at_utc_ms;  /* DateTimeOffset as Unix ms UTC */
} ca_bank_ledger_entry_t;

void ca_bank_ledger_entry_free(ca_bank_ledger_entry_t *e);
void ca_bank_ledger_entry_free_array(ca_bank_ledger_entry_t *arr, size_t count);

/* PaymentRequest(FromAccount, ToAccount, decimal Amount, Currency, Memo). */
typedef struct {
    char             *from_account; /* owned, non-null */
    char             *to_account;   /* owned, non-null */
    ca_bank_decimal_t amount;
    char             *currency;     /* owned, non-null */
    char             *memo;         /* owned, non-null */
} ca_bank_payment_request_t;

/* PaymentResult(TxId, Accepted, string? FailureReason). */
typedef struct {
    char *tx_id;           /* owned, non-null */
    bool  accepted;
    bool  has_failure;     /* false == C# null FailureReason */
    char *failure_reason;  /* owned, valid only when has_failure */
} ca_bank_payment_result_t;

void ca_bank_payment_result_free(ca_bank_payment_result_t *r);

/* ── InMemoryBank ───────────────────────────────────────────────────────── */

typedef struct ca_bank ca_bank_t;

/* InMemoryBank(). NULL on OOM. */
ca_bank_t *ca_bank_create(void);
void ca_bank_destroy(ca_bank_t *bank);

/* SeedAccount(account) — deep-copies; AccountId keyed set. 0 / -1 on bad
 * args/OOM. */
int ca_bank_seed_account(ca_bank_t *bank, const ca_bank_account_t *account);
/* Get(id) -> fresh owned copy into *out, true; false (C# null) on miss. */
bool ca_bank_get(const ca_bank_t *bank, const char *id, ca_bank_account_t *out);
/* ListForOwner(ownerId) -> fresh owned array (*out_count) in insertion order.
 * NULL + 0 when empty; NULL + SIZE_MAX on error. */
ca_bank_account_t *ca_bank_list_for_owner(const ca_bank_t *bank,
                                          const char *owner_id,
                                          size_t *out_count);
/* Append(entry) -> writes the stored entry into *out (fresh owned copy) and does
 * Balance += Amount on the entry's account. 0 on success, -1 on bad args/OOM, 1
 * when the account is unknown (InvalidOperationException). */
int ca_bank_append(ca_bank_t *bank, const ca_bank_ledger_entry_t *entry,
                   ca_bank_ledger_entry_t *out);
/* Read(accountId, limit) -> fresh owned array (*out_count): that account's ledger
 * ordered by AtUtc descending, first `limit`. NULL + 0 when empty / unknown
 * account; NULL + SIZE_MAX on error. */
ca_bank_ledger_entry_t *ca_bank_read(const ca_bank_t *bank, const char *account_id,
                                     int limit, size_t *out_count);
/* ProcessPayment(req) -> writes the PaymentResult into *out (fresh owned). Returns
 * 0 on success (Accepted may be true or false), -1 on bad args/OOM. On success it
 * runs the same validation ladder as the C# and, when accepted, posts the two
 * ledger entries. */
int ca_bank_process_payment(ca_bank_t *bank,
                            const ca_bank_payment_request_t *req,
                            ca_bank_payment_result_t *out);

/* ── Backends (readers / writers / processors) ──────────────────────────── */

typedef struct ca_bank_account_reader ca_bank_account_reader_t;
typedef struct ca_bank_ledger_writer ca_bank_ledger_writer_t;
typedef struct ca_bank_payment_processor ca_bank_payment_processor_t;

/* InMemoryAccountReader(bank) — borrows the bank (does not own it). BackendId
 * "in-memory". NULL on bad args/OOM. */
ca_bank_account_reader_t *ca_bank_account_reader_create(ca_bank_t *bank);
/* NullAccountReader — BackendId "null"; GetAccount -> null; ListForOwner ->
 * empty. */
ca_bank_account_reader_t *ca_bank_null_account_reader_create(void);
void ca_bank_account_reader_destroy(ca_bank_account_reader_t *r);
const char *ca_bank_account_reader_backend_id(const ca_bank_account_reader_t *r);
/* GetAccountAsync(id). Writes a copy into *out, true; false on miss / Null. */
bool ca_bank_account_reader_get(const ca_bank_account_reader_t *r, const char *id,
                                ca_bank_account_t *out);
/* ListForOwnerAsync(owner). NULL + 0 when empty; NULL + SIZE_MAX on error. */
ca_bank_account_t *ca_bank_account_reader_list_for_owner(
    const ca_bank_account_reader_t *r, const char *owner_id, size_t *out_count);

/* InMemoryLedgerWriter(bank) — borrows the bank. BackendId "in-memory". */
ca_bank_ledger_writer_t *ca_bank_ledger_writer_create(ca_bank_t *bank);
/* NullLedgerWriter — BackendId "null"; Append echoes the entry; Read -> empty. */
ca_bank_ledger_writer_t *ca_bank_null_ledger_writer_create(void);
void ca_bank_ledger_writer_destroy(ca_bank_ledger_writer_t *w);
const char *ca_bank_ledger_writer_backend_id(const ca_bank_ledger_writer_t *w);
/* AppendAsync(entry). Writes the stored/echoed entry into *out. 0 on success, -1
 * on bad args/OOM, 1 when the account is unknown (in-memory only). */
int ca_bank_ledger_writer_append(ca_bank_ledger_writer_t *w,
                                 const ca_bank_ledger_entry_t *entry,
                                 ca_bank_ledger_entry_t *out);
/* ReadAsync(accountId, limit). NULL + 0 when empty; NULL + SIZE_MAX on error. */
ca_bank_ledger_entry_t *ca_bank_ledger_writer_read(
    const ca_bank_ledger_writer_t *w, const char *account_id, int limit,
    size_t *out_count);

/* InMemoryPaymentProcessor(bank) — borrows the bank. BackendId "in-memory". */
ca_bank_payment_processor_t *ca_bank_payment_processor_create(ca_bank_t *bank);
/* NullPaymentProcessor — BackendId "null"; Process -> {Guid.Empty, false,
 * "NullPaymentProcessor."}. */
ca_bank_payment_processor_t *ca_bank_null_payment_processor_create(void);
void ca_bank_payment_processor_destroy(ca_bank_payment_processor_t *p);
const char *ca_bank_payment_processor_backend_id(
    const ca_bank_payment_processor_t *p);
/* ProcessAsync(req). Writes the PaymentResult into *out. 0 on success, -1 on bad
 * args/OOM. */
int ca_bank_payment_processor_process(ca_bank_payment_processor_t *p,
                                      const ca_bank_payment_request_t *req,
                                      ca_bank_payment_result_t *out);

#ifdef __cplusplus
}
#endif

#endif /* CIRCLE_AI_BANKING_H */
