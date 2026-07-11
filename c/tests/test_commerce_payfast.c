/*
 * test_commerce_payfast.c — CircleAI.Commerce.Integration.PayFast (C11 port)
 * verification against PayFastPrimitives.cs. The signature vectors were produced
 * by the reference C# (System.Net.WebUtility.UrlEncode + MD5) so this asserts
 * byte-exact parity.
 */

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <assert.h>
#include "circle_ai/circle_ai.h"

#define D(x) ((ca_payfast_decimal_t)((x) * CA_PAYFAST_DECIMAL_SCALE))

static ca_payfast_config_t mk_cfg(const char *pass) {
    ca_payfast_config_t c; memset(&c, 0, sizeof(c));
    c.merchant_id = (char *)"10000100"; c.merchant_key = (char *)"46f0cd694581a";
    c.passphrase = (char *)pass; c.sandbox = true;
    return c;
}

static ca_payfast_itn_t mk_itn(const char *mid, const char *pid, const char *status) {
    ca_payfast_itn_t p; memset(&p, 0, sizeof(p));
    p.merchant_id = (char *)mid; p.payment_id = (char *)pid;
    p.payment_status = (char *)status; p.amount = D(100);
    p.m_payment_id = (char *)"mp-1"; p.signature = (char *)"sig";
    return p;
}

static void test_signature(void) {
    /* ctor rejects a NULL config. */
    assert(ca_payfast_board_create(NULL) == NULL);

    ca_payfast_config_t cfg = mk_cfg("MyPassPhrase123");
    ca_payfast_board_t *b = ca_payfast_board_create(&cfg);
    assert(b);

    /* Config round-trips. */
    ca_payfast_config_t cgot;
    assert(ca_payfast_board_config(b, &cgot));
    assert(strcmp(cgot.merchant_id, "10000100") == 0 &&
           strcmp(cgot.merchant_key, "46f0cd694581a") == 0 &&
           strcmp(cgot.passphrase, "MyPassPhrase123") == 0 && cgot.sandbox);
    ca_payfast_config_free(&cgot);

    ca_payfast_field_t f[] = {
        {"merchant_id","10000100"},
        {"merchant_key","46f0cd694581a"},
        {"return_url","https://example.com/return?x=1 2"},
        {"amount","100.00"},
        {"item_name","Test Item & Co"},
    };
    char *sig = ca_payfast_board_signature_for(b, f, 5);
    assert(sig && strcmp(sig, "a642e97126b4f08a3934315f76121dd5") == 0);
    free(sig);
    ca_payfast_board_destroy(b);

    /* No passphrase -> different (trailing & stripped) signature. */
    ca_payfast_config_t cfg2 = mk_cfg("");
    ca_payfast_board_t *b2 = ca_payfast_board_create(&cfg2);
    char *sig2 = ca_payfast_board_signature_for(b2, f, 5);
    assert(sig2 && strcmp(sig2, "e3d996734b710e95fb98342d61b37f33") == 0);
    free(sig2);
    ca_payfast_board_destroy(b2);

    printf("  signature: ok\n");
}

static void test_verify_and_webhooks(void) {
    ca_payfast_config_t cfg = mk_cfg("pp");
    ca_payfast_board_t *b = ca_payfast_board_create(&cfg);

    /* VerifyItn: MerchantId must match config. */
    ca_payfast_itn_t good = mk_itn("10000100", "pf-1", "COMPLETE");
    ca_payfast_itn_t bad  = mk_itn("99999999", "pf-2", "COMPLETE");
    assert(ca_payfast_board_verify_itn(b, &good));
    assert(!ca_payfast_board_verify_itn(b, &bad));

    /* Record three webhooks; RecentWebhooks is most-recent-first. */
    ca_payfast_itn_t w1 = mk_itn("10000100", "w1", "PENDING");
    ca_payfast_itn_t w2 = mk_itn("10000100", "w2", "COMPLETE");
    ca_payfast_itn_t w3 = mk_itn("10000100", "w3", "COMPLETE");
    assert(ca_payfast_board_record_webhook(b, &w1) == 0);
    assert(ca_payfast_board_record_webhook(b, &w2) == 0);
    assert(ca_payfast_board_record_webhook(b, &w3) == 0);

    size_t n = 0;
    ca_payfast_itn_t *arr = ca_payfast_board_recent_webhooks(b, 20, &n);
    assert(n == 3);
    assert(strcmp(arr[0].payment_id, "w3") == 0);   /* newest first */
    assert(strcmp(arr[1].payment_id, "w2") == 0);
    assert(strcmp(arr[2].payment_id, "w1") == 0);
    ca_payfast_itn_free_array(arr, n);

    /* limit truncates after reversal. */
    arr = ca_payfast_board_recent_webhooks(b, 2, &n);
    assert(n == 2 && strcmp(arr[0].payment_id, "w3") == 0 && strcmp(arr[1].payment_id, "w2") == 0);
    ca_payfast_itn_free_array(arr, n);

    /* limit 0 -> empty; negative -> SIZE_MAX. */
    arr = ca_payfast_board_recent_webhooks(b, 0, &n);
    assert(n == 0 && arr == NULL);
    arr = ca_payfast_board_recent_webhooks(b, -1, &n);
    assert(n == (size_t)-1);

    ca_payfast_board_destroy(b);
    printf("  verify_and_webhooks: ok\n");
}

int main(void) {
    test_signature();
    test_verify_and_webhooks();
    printf("test_commerce_payfast: all assertions passed\n");
    return 0;
}
