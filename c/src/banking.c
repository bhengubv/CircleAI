/*
 * banking.c — CircleAI.Banking (C11 port of Contracts.cs + InMemoryBanking.cs +
 * NullImplementations.cs).
 *
 * InMemoryBank holds accounts (keyed by AccountId) + per-account ledger lists.
 * The three backends wrap a shared, borrowed InMemoryBank; Null variants are
 * fail-closed. TxIds are 32 lowercase-hex chars from a process-monotonic counter
 * (the C# uses random GUIDs; only accepted/failure semantics are load-bearing).
 *
 * Pure C11 + libc. No pthreads.
 */

#include "circle_ai/banking.h"
#include "board_common.h"

#include <stdio.h>

/* ── TxId generation — 32 lowercase-hex chars (Guid("n") shape) ─────────── */

static uint64_t g_tx_counter = 0;

/* Writes a fresh 32-hex-char id into a newly-allocated string. NULL on OOM. */
static char *bank_new_txid(void) {
    uint64_t n = ++g_tx_counter;
    char *s = (char *)malloc(33);
    if (!s) return NULL;
    /* Deterministic 128-bit-shaped value: high 64 bits a fixed tag, low 64 the
     * counter, rendered big-endian as 32 hex digits. */
    uint64_t hi = 0x0000000000000000ULL;
    uint64_t lo = n;
    static const char L[] = "0123456789abcdef";
    for (int i = 0; i < 16; ++i) s[i]      = L[(hi >> (60 - 4*i)) & 0xF];
    for (int i = 0; i < 16; ++i) s[16 + i] = L[(lo >> (60 - 4*i)) & 0xF];
    s[32] = '\0';
    return s;
}

/* ── records ────────────────────────────────────────────────────────────── */

void ca_bank_account_free(ca_bank_account_t *a) {
    if (!a) return;
    free(a->account_id);
    free(a->owner_id);
    free(a->currency);
    a->account_id = a->owner_id = a->currency = NULL;
}
void ca_bank_account_free_array(ca_bank_account_t *arr, size_t count) {
    if (!arr) return;
    for (size_t i = 0; i < count; ++i) ca_bank_account_free(&arr[i]);
    free(arr);
}

static bool account_copy(ca_bank_account_t *dst, const ca_bank_account_t *src) {
    memset(dst, 0, sizeof(*dst));
    dst->account_id = cab_strdup_empty(src->account_id);
    dst->owner_id   = cab_strdup_empty(src->owner_id);
    dst->currency   = cab_strdup_empty(src->currency);
    dst->balance    = src->balance;
    if (!dst->account_id || !dst->owner_id || !dst->currency) {
        ca_bank_account_free(dst);
        return false;
    }
    return true;
}

void ca_bank_ledger_entry_free(ca_bank_ledger_entry_t *e) {
    if (!e) return;
    free(e->tx_id);
    free(e->account_id);
    free(e->memo);
    e->tx_id = e->account_id = e->memo = NULL;
}
void ca_bank_ledger_entry_free_array(ca_bank_ledger_entry_t *arr, size_t count) {
    if (!arr) return;
    for (size_t i = 0; i < count; ++i) ca_bank_ledger_entry_free(&arr[i]);
    free(arr);
}

static bool ledger_copy(ca_bank_ledger_entry_t *dst,
                        const ca_bank_ledger_entry_t *src) {
    memset(dst, 0, sizeof(*dst));
    dst->tx_id      = cab_strdup_empty(src->tx_id);
    dst->account_id = cab_strdup_empty(src->account_id);
    dst->memo       = cab_strdup_empty(src->memo);
    dst->amount     = src->amount;
    dst->at_utc_ms  = src->at_utc_ms;
    if (!dst->tx_id || !dst->account_id || !dst->memo) {
        ca_bank_ledger_entry_free(dst);
        return false;
    }
    return true;
}

void ca_bank_payment_result_free(ca_bank_payment_result_t *r) {
    if (!r) return;
    free(r->tx_id);
    free(r->failure_reason);
    r->tx_id = r->failure_reason = NULL;
}

/* Build a PaymentResult with a fresh TxId + failure reason. Returns 0 / -1. */
static int result_make(ca_bank_payment_result_t *out, const char *tx_id,
                       bool accepted, const char *failure_reason) {
    memset(out, 0, sizeof(*out));
    out->tx_id = cab_strdup_empty(tx_id);
    if (!out->tx_id) return -1;
    out->accepted = accepted;
    if (failure_reason) {
        out->has_failure = true;
        out->failure_reason = cab_strdup(failure_reason);
        if (!out->failure_reason) { ca_bank_payment_result_free(out); return -1; }
    }
    return 0;
}

/* ── InMemoryBank ───────────────────────────────────────────────────────── */

/* Per-account ledger list. */
typedef struct {
    char                   *account_id; /* owned */
    ca_bank_ledger_entry_t *entries;    /* owned */
    size_t                  count, cap;
} ledger_list_t;

struct ca_bank {
    ca_bank_account_t *accounts;
    size_t             acct_count, acct_cap;
    ledger_list_t     *ledgers;
    size_t             ledger_count, ledger_cap;
};

ca_bank_t *ca_bank_create(void) {
    return (ca_bank_t *)calloc(1, sizeof(ca_bank_t));
}
void ca_bank_destroy(ca_bank_t *bank) {
    if (!bank) return;
    for (size_t i = 0; i < bank->acct_count; ++i)
        ca_bank_account_free(&bank->accounts[i]);
    free(bank->accounts);
    for (size_t i = 0; i < bank->ledger_count; ++i) {
        free(bank->ledgers[i].account_id);
        for (size_t k = 0; k < bank->ledgers[i].count; ++k)
            ca_bank_ledger_entry_free(&bank->ledgers[i].entries[k]);
        free(bank->ledgers[i].entries);
    }
    free(bank->ledgers);
    free(bank);
}

static size_t bank_acct_index(const ca_bank_t *bank, const char *id) {
    for (size_t i = 0; i < bank->acct_count; ++i)
        if (cab_ord_eq(bank->accounts[i].account_id, id)) return i;
    return (size_t)-1;
}

int ca_bank_seed_account(ca_bank_t *bank, const ca_bank_account_t *account) {
    if (!bank || !account) return -1;
    size_t idx = bank_acct_index(bank, account->account_id);
    ca_bank_account_t copy;
    if (!account_copy(&copy, account)) return -1;
    if (idx != (size_t)-1) {
        ca_bank_account_free(&bank->accounts[idx]);
        bank->accounts[idx] = copy;
        return 0;
    }
    if (bank->acct_count == bank->acct_cap) {
        size_t nc = bank->acct_cap ? bank->acct_cap * 2 : 4;
        void *n = realloc(bank->accounts, nc * sizeof(*bank->accounts));
        if (!n) { ca_bank_account_free(&copy); return -1; }
        bank->accounts = (ca_bank_account_t *)n;
        bank->acct_cap = nc;
    }
    bank->accounts[bank->acct_count++] = copy;
    return 0;
}

bool ca_bank_get(const ca_bank_t *bank, const char *id, ca_bank_account_t *out) {
    if (out) memset(out, 0, sizeof(*out));
    if (!bank || !id || !out) return false;
    size_t idx = bank_acct_index(bank, id);
    if (idx == (size_t)-1) return false;
    return account_copy(out, &bank->accounts[idx]);
}

ca_bank_account_t *ca_bank_list_for_owner(const ca_bank_t *bank,
                                          const char *owner_id,
                                          size_t *out_count) {
    if (!out_count) return NULL;
    if (!bank || !owner_id) { *out_count = (size_t)-1; return NULL; }
    if (bank->acct_count == 0) { *out_count = 0; return NULL; }

    size_t *idx = (size_t *)malloc(bank->acct_count * sizeof(size_t));
    if (!idx) { *out_count = (size_t)-1; return NULL; }
    size_t n = 0;
    for (size_t i = 0; i < bank->acct_count; ++i)
        if (cab_ord_eq(bank->accounts[i].owner_id, owner_id)) idx[n++] = i;

    if (n == 0) { free(idx); *out_count = 0; return NULL; }
    ca_bank_account_t *out = (ca_bank_account_t *)calloc(n, sizeof(*out));
    if (!out) { free(idx); *out_count = (size_t)-1; return NULL; }
    for (size_t i = 0; i < n; ++i) {
        if (!account_copy(&out[i], &bank->accounts[idx[i]])) {
            ca_bank_account_free_array(out, i);
            free(idx);
            *out_count = (size_t)-1;
            return NULL;
        }
    }
    free(idx);
    *out_count = n;
    return out;
}

static ledger_list_t *bank_ledger_get_or_add(ca_bank_t *bank, const char *id) {
    for (size_t i = 0; i < bank->ledger_count; ++i)
        if (cab_ord_eq(bank->ledgers[i].account_id, id)) return &bank->ledgers[i];
    if (bank->ledger_count == bank->ledger_cap) {
        size_t nc = bank->ledger_cap ? bank->ledger_cap * 2 : 4;
        void *n = realloc(bank->ledgers, nc * sizeof(*bank->ledgers));
        if (!n) return NULL;
        bank->ledgers = (ledger_list_t *)n;
        bank->ledger_cap = nc;
    }
    ledger_list_t *l = &bank->ledgers[bank->ledger_count];
    memset(l, 0, sizeof(*l));
    l->account_id = cab_strdup(id);
    if (!l->account_id) return NULL;
    bank->ledger_count++;
    return l;
}

static const ledger_list_t *bank_ledger_find(const ca_bank_t *bank,
                                             const char *id) {
    for (size_t i = 0; i < bank->ledger_count; ++i)
        if (cab_ord_eq(bank->ledgers[i].account_id, id)) return &bank->ledgers[i];
    return NULL;
}

/* Internal append: adds a copy of `entry` to its account's ledger and applies
 * Balance += Amount. Returns 0, -1 (bad/OOM), or 1 (unknown account). */
static int bank_append_internal(ca_bank_t *bank,
                                const ca_bank_ledger_entry_t *entry) {
    size_t ai = bank_acct_index(bank, entry->account_id);
    if (ai == (size_t)-1) return 1;   /* InvalidOperationException */

    ledger_list_t *l = bank_ledger_get_or_add(bank, entry->account_id);
    if (!l) return -1;
    ca_bank_ledger_entry_t copy;
    if (!ledger_copy(&copy, entry)) return -1;
    if (l->count == l->cap) {
        size_t nc = l->cap ? l->cap * 2 : 4;
        void *n = realloc(l->entries, nc * sizeof(*l->entries));
        if (!n) { ca_bank_ledger_entry_free(&copy); return -1; }
        l->entries = (ca_bank_ledger_entry_t *)n;
        l->cap = nc;
    }
    l->entries[l->count++] = copy;
    bank->accounts[ai].balance += entry->amount;
    return 0;
}

int ca_bank_append(ca_bank_t *bank, const ca_bank_ledger_entry_t *entry,
                   ca_bank_ledger_entry_t *out) {
    if (out) memset(out, 0, sizeof(*out));
    if (!bank || !entry) return -1;
    int rc = bank_append_internal(bank, entry);
    if (rc != 0) return rc;
    if (out && !ledger_copy(out, entry)) return -1;
    return 0;
}

/* Stable descending sort of collected ledger indices by at_utc_ms. */
static void ledger_sort_desc(const ledger_list_t *l, size_t *idx, size_t n) {
    for (size_t i = 1; i < n; ++i) {
        size_t key = idx[i];
        int64_t kt = l->entries[key].at_utc_ms;
        size_t j = i;
        while (j > 0 && l->entries[idx[j - 1]].at_utc_ms < kt) {
            idx[j] = idx[j - 1];
            j--;
        }
        idx[j] = key;
    }
}

ca_bank_ledger_entry_t *ca_bank_read(const ca_bank_t *bank, const char *account_id,
                                     int limit, size_t *out_count) {
    if (!out_count) return NULL;
    if (!bank || !account_id) { *out_count = (size_t)-1; return NULL; }
    const ledger_list_t *l = bank_ledger_find(bank, account_id);
    if (!l || l->count == 0 || limit <= 0) { *out_count = 0; return NULL; }

    size_t *idx = (size_t *)malloc(l->count * sizeof(size_t));
    if (!idx) { *out_count = (size_t)-1; return NULL; }
    for (size_t i = 0; i < l->count; ++i) idx[i] = i;
    ledger_sort_desc(l, idx, l->count);

    size_t n = l->count;
    if (n > (size_t)limit) n = (size_t)limit;
    ca_bank_ledger_entry_t *out =
        (ca_bank_ledger_entry_t *)calloc(n, sizeof(*out));
    if (!out) { free(idx); *out_count = (size_t)-1; return NULL; }
    for (size_t i = 0; i < n; ++i) {
        if (!ledger_copy(&out[i], &l->entries[idx[i]])) {
            ca_bank_ledger_entry_free_array(out, i);
            free(idx);
            *out_count = (size_t)-1;
            return NULL;
        }
    }
    free(idx);
    *out_count = n;
    return out;
}

int ca_bank_process_payment(ca_bank_t *bank,
                            const ca_bank_payment_request_t *req,
                            ca_bank_payment_result_t *out) {
    if (out) memset(out, 0, sizeof(*out));
    if (!bank || !req || !out) return -1;

    /* The validation ladder mirrors InMemoryBank.ProcessPayment; each failure
     * carries a fresh TxId (Guid.NewGuid) and the exact FailureReason text. */
    char *txid = bank_new_txid();
    if (!txid) return -1;

    if (req->amount <= 0) {
        int rc = result_make(out, txid, false, "Amount must be positive");
        free(txid); return rc;
    }
    size_t si = bank_acct_index(bank, req->from_account);
    if (si == (size_t)-1) {
        int rc = result_make(out, txid, false, "Unknown source account");
        free(txid); return rc;
    }
    size_t di = bank_acct_index(bank, req->to_account);
    if (di == (size_t)-1) {
        int rc = result_make(out, txid, false, "Unknown destination account");
        free(txid); return rc;
    }
    if (!cab_ci_eq(bank->accounts[si].currency, req->currency) ||
        !cab_ci_eq(bank->accounts[di].currency, req->currency)) {
        int rc = result_make(out, txid, false, "Currency mismatch");
        free(txid); return rc;
    }
    if (bank->accounts[si].balance < req->amount) {
        int rc = result_make(out, txid, false, "Insufficient funds");
        free(txid); return rc;
    }

    /* Double-entry: debit source (-amount), credit destination (+amount), one
     * shared TxId. Memos mirror the C# interpolation. */
    char memo_from[512], memo_to[512];
    const char *rmemo = req->memo ? req->memo : "";
    snprintf(memo_from, sizeof(memo_from), "To %s: %s",
             req->to_account ? req->to_account : "", rmemo);
    snprintf(memo_to, sizeof(memo_to), "From %s: %s",
             req->from_account ? req->from_account : "", rmemo);

    /* C# uses DateTimeOffset.UtcNow; both legs share one timestamp. 0 keeps the
     * port deterministic (ordering within a payment is not observable via Read
     * since both share the TxId + timestamp). */
    ca_bank_ledger_entry_t leg1, leg2;
    memset(&leg1, 0, sizeof(leg1));
    memset(&leg2, 0, sizeof(leg2));
    leg1.tx_id = txid;               /* borrowed for the append copy */
    leg1.account_id = req->from_account;
    leg1.amount = -req->amount;
    leg1.memo = memo_from;
    leg1.at_utc_ms = 0;
    leg2.tx_id = txid;
    leg2.account_id = req->to_account;
    leg2.amount = req->amount;
    leg2.memo = memo_to;
    leg2.at_utc_ms = 0;

    if (bank_append_internal(bank, &leg1) != 0) { free(txid); return -1; }
    if (bank_append_internal(bank, &leg2) != 0) { free(txid); return -1; }

    int rc = result_make(out, txid, true, NULL);
    free(txid);
    return rc;
}

/* ── Backends ───────────────────────────────────────────────────────────── */

struct ca_bank_account_reader { bool is_null; ca_bank_t *bank; };
struct ca_bank_ledger_writer { bool is_null; ca_bank_t *bank; };
struct ca_bank_payment_processor { bool is_null; ca_bank_t *bank; };

ca_bank_account_reader_t *ca_bank_account_reader_create(ca_bank_t *bank) {
    if (!bank) return NULL;   /* ArgumentNullException(bank) */
    ca_bank_account_reader_t *r = (ca_bank_account_reader_t *)calloc(1, sizeof(*r));
    if (r) r->bank = bank;
    return r;
}
ca_bank_account_reader_t *ca_bank_null_account_reader_create(void) {
    ca_bank_account_reader_t *r = (ca_bank_account_reader_t *)calloc(1, sizeof(*r));
    if (r) r->is_null = true;
    return r;
}
void ca_bank_account_reader_destroy(ca_bank_account_reader_t *r) { free(r); }
const char *ca_bank_account_reader_backend_id(const ca_bank_account_reader_t *r) {
    if (!r) return NULL;
    return r->is_null ? "null" : "in-memory";
}
bool ca_bank_account_reader_get(const ca_bank_account_reader_t *r, const char *id,
                                ca_bank_account_t *out) {
    if (out) memset(out, 0, sizeof(*out));
    if (!r || !out) return false;
    if (r->is_null) return false;   /* NullAccountReader -> null */
    return ca_bank_get(r->bank, id, out);
}
ca_bank_account_t *ca_bank_account_reader_list_for_owner(
    const ca_bank_account_reader_t *r, const char *owner_id, size_t *out_count) {
    if (!out_count) return NULL;
    if (!r) { *out_count = (size_t)-1; return NULL; }
    if (r->is_null) { *out_count = 0; return NULL; }   /* Array.Empty */
    return ca_bank_list_for_owner(r->bank, owner_id, out_count);
}

ca_bank_ledger_writer_t *ca_bank_ledger_writer_create(ca_bank_t *bank) {
    if (!bank) return NULL;
    ca_bank_ledger_writer_t *w = (ca_bank_ledger_writer_t *)calloc(1, sizeof(*w));
    if (w) w->bank = bank;
    return w;
}
ca_bank_ledger_writer_t *ca_bank_null_ledger_writer_create(void) {
    ca_bank_ledger_writer_t *w = (ca_bank_ledger_writer_t *)calloc(1, sizeof(*w));
    if (w) w->is_null = true;
    return w;
}
void ca_bank_ledger_writer_destroy(ca_bank_ledger_writer_t *w) { free(w); }
const char *ca_bank_ledger_writer_backend_id(const ca_bank_ledger_writer_t *w) {
    if (!w) return NULL;
    return w->is_null ? "null" : "in-memory";
}
int ca_bank_ledger_writer_append(ca_bank_ledger_writer_t *w,
                                 const ca_bank_ledger_entry_t *entry,
                                 ca_bank_ledger_entry_t *out) {
    if (out) memset(out, 0, sizeof(*out));
    if (!w || !entry) return -1;
    if (w->is_null) {               /* NullLedgerWriter echoes the entry back */
        if (out && !ledger_copy(out, entry)) return -1;
        return 0;
    }
    return ca_bank_append(w->bank, entry, out);
}
ca_bank_ledger_entry_t *ca_bank_ledger_writer_read(
    const ca_bank_ledger_writer_t *w, const char *account_id, int limit,
    size_t *out_count) {
    if (!out_count) return NULL;
    if (!w) { *out_count = (size_t)-1; return NULL; }
    if (w->is_null) { *out_count = 0; return NULL; }   /* Array.Empty */
    return ca_bank_read(w->bank, account_id, limit, out_count);
}

ca_bank_payment_processor_t *ca_bank_payment_processor_create(ca_bank_t *bank) {
    if (!bank) return NULL;
    ca_bank_payment_processor_t *p =
        (ca_bank_payment_processor_t *)calloc(1, sizeof(*p));
    if (p) p->bank = bank;
    return p;
}
ca_bank_payment_processor_t *ca_bank_null_payment_processor_create(void) {
    ca_bank_payment_processor_t *p =
        (ca_bank_payment_processor_t *)calloc(1, sizeof(*p));
    if (p) p->is_null = true;
    return p;
}
void ca_bank_payment_processor_destroy(ca_bank_payment_processor_t *p) { free(p); }
const char *ca_bank_payment_processor_backend_id(
    const ca_bank_payment_processor_t *p) {
    if (!p) return NULL;
    return p->is_null ? "null" : "in-memory";
}
int ca_bank_payment_processor_process(ca_bank_payment_processor_t *p,
                                      const ca_bank_payment_request_t *req,
                                      ca_bank_payment_result_t *out) {
    if (out) memset(out, 0, sizeof(*out));
    if (!p || !req || !out) return -1;
    if (p->is_null) {
        /* NullPaymentProcessor -> {Guid.Empty, false, "NullPaymentProcessor."} */
        return result_make(out, "00000000-0000-0000-0000-000000000000", false,
                           "NullPaymentProcessor.");
    }
    return ca_bank_process_payment(p->bank, req, out);
}
