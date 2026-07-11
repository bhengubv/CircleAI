#ifndef CIRCLE_AI_COMMERCE_PAYFAST_H
#define CIRCLE_AI_COMMERCE_PAYFAST_H

/*
 * commerce_payfast.h — CircleAI.Commerce.Integration.PayFast (C11 port of
 * PayFastPrimitives.cs). PayFast signature builder + ITN recorder.
 *
 *   Records : PayFastConfig(MerchantId, MerchantKey, Passphrase, Sandbox);
 *             PayFastItnPayload(MerchantId, PaymentId, PaymentStatus, Amount,
 *                               MPaymentId, Signature).
 *   Board   : IPayFastBoard -> InMemoryPayFastBoard(config).
 *             Config (the stored config), SignatureFor(orderedFields) (the real
 *             PayFast MD5 signature over key=UrlEncode(value)&... with an
 *             appended passphrase= when the config passphrase is non-empty),
 *             VerifyItn(p) (p.MerchantId == Config.MerchantId), RecordWebhook(p)
 *             (appended list), RecentWebhooks(limit) (reverse-chronological, i.e.
 *             most-recent-first, first `limit`; default 20).
 *
 *   SignatureFor exactly reproduces System.Net.WebUtility.UrlEncode +
 *   .Replace("%20","+"): unreserved bytes A-Z a-z 0-9 ! ( ) * - . _ pass through,
 *   space -> '+', every other byte -> %XX with UPPERCASE hex; then MD5, lowercase
 *   hex. Field order is the caller-supplied order (an ordered key/value list).
 *
 * Conventions: ca_ prefix, _t types, opaque handle, strdup-owning fields with
 * matching *_free, deep-copy getters, errors via NULL / count SIZE_MAX. Amount as
 * ca_payfast_decimal_t (int64 scaled 1e6). Linear arrays, no pthreads.
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
typedef int64_t ca_payfast_decimal_t;
#define CA_PAYFAST_DECIMAL_SCALE 1000000LL

/* PayFastConfig(MerchantId, MerchantKey, Passphrase, bool Sandbox). */
typedef struct {
    char *merchant_id;  /* owned, non-null */
    char *merchant_key; /* owned, non-null */
    char *passphrase;   /* owned, non-null (may be empty) */
    bool  sandbox;
} ca_payfast_config_t;

void ca_payfast_config_free(ca_payfast_config_t *c);

/* PayFastItnPayload(MerchantId, PaymentId, PaymentStatus, decimal Amount,
 * MPaymentId, Signature). */
typedef struct {
    char                *merchant_id;    /* owned, non-null */
    char                *payment_id;     /* owned, non-null */
    char                *payment_status; /* owned, non-null */
    ca_payfast_decimal_t amount;
    char                *m_payment_id;   /* owned, non-null */
    char                *signature;      /* owned, non-null */
} ca_payfast_itn_t;

void ca_payfast_itn_free(ca_payfast_itn_t *p);
void ca_payfast_itn_free_array(ca_payfast_itn_t *arr, size_t count);

/* One ordered key/value pair for SignatureFor (mirrors the C# ordered dict). */
typedef struct {
    const char *key;    /* borrowed */
    const char *value;  /* borrowed */
} ca_payfast_field_t;

typedef struct ca_payfast_board ca_payfast_board_t;

/* InMemoryPayFastBoard(config) — deep-copies config. NULL on bad args / OOM
 * (the C# ctor throws ArgumentNullException on a null config). */
ca_payfast_board_t *ca_payfast_board_create(const ca_payfast_config_t *config);
void ca_payfast_board_destroy(ca_payfast_board_t *b);

/* Config -> fresh owned copy into *out, true; false on bad args. */
bool ca_payfast_board_config(const ca_payfast_board_t *b,
                             ca_payfast_config_t *out);

/* SignatureFor(orderedFields) -> newly-allocated lowercase-hex MD5 string (32
 * chars + NUL; caller frees) over the PayFast pre-hash. fields may be NULL only
 * when count==0. Returns NULL on bad args / OOM. */
char *ca_payfast_board_signature_for(const ca_payfast_board_t *b,
                                     const ca_payfast_field_t *fields,
                                     size_t count);

/* VerifyItn(p) -> p.MerchantId == Config.MerchantId. false on bad args. */
bool ca_payfast_board_verify_itn(const ca_payfast_board_t *b,
                                 const ca_payfast_itn_t *p);

/* RecordWebhook(p) — deep-copies; appended list. 0 / -1 on bad args/OOM. */
int ca_payfast_board_record_webhook(ca_payfast_board_t *b,
                                    const ca_payfast_itn_t *p);

/* RecentWebhooks(limit) -> fresh owned array (*out_count): the recorded webhooks
 * reversed (most-recent-first), first `limit`. limit < 0 -> SIZE_MAX; limit 0 ->
 * empty. Use 20 for the C# default. NULL + 0 when empty. */
ca_payfast_itn_t *ca_payfast_board_recent_webhooks(const ca_payfast_board_t *b,
                                                   int limit, size_t *out_count);

#ifdef __cplusplus
}
#endif

#endif /* CIRCLE_AI_COMMERCE_PAYFAST_H */
