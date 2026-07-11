/*
 * test_legal.c — CircleAI.Legal (C11 port) verification against LegalPrimitives.cs.
 */

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <assert.h>
#include "circle_ai/circle_ai.h"

static ca_legal_matter_t mk_matter(const char *id, const char *title,
                                   int64_t opened, bool open) {
    ca_legal_matter_t m; memset(&m, 0, sizeof(m));
    m.matter_id = (char *)id; m.title = (char *)title;
    m.jurisdiction = (char *)"ZA"; m.client = (char *)"Acme";
    m.opened_at_utc_ms = opened; m.open = open;
    return m;
}

static void test_matters(void) {
    ca_legal_board_t *b = ca_legal_board_create();
    assert(b);

    assert(ca_legal_board_close(b, "nope") == 1);   /* unknown -> 1 */

    ca_legal_matter_t m1 = mk_matter("m1", "Case One", 100, true);
    ca_legal_matter_t m2 = mk_matter("m2", "Case Two", 300, true);
    ca_legal_matter_t m3 = mk_matter("m3", "Closed",   200, false);
    assert(ca_legal_board_open(b, &m1) == 0);
    assert(ca_legal_board_open(b, &m2) == 0);
    assert(ca_legal_board_open(b, &m3) == 0);

    ca_legal_matter_t got;
    assert(ca_legal_board_get_matter(b, "m1", &got));
    assert(strcmp(got.title, "Case One") == 0 && got.open);
    ca_legal_matter_free(&got);

    /* ActiveMatters: only Open, ordered by OpenedAtUtc descending: m2(300), m1(100). */
    size_t n = 0;
    ca_legal_matter_t *arr = ca_legal_board_active_matters(b, &n);
    assert(n == 2);
    assert(strcmp(arr[0].matter_id, "m2") == 0);
    assert(strcmp(arr[1].matter_id, "m1") == 0);
    ca_legal_matter_free_array(arr, n);

    /* Close m2 -> only m1 active. */
    assert(ca_legal_board_close(b, "m2") == 0);
    arr = ca_legal_board_active_matters(b, &n);
    assert(n == 1 && strcmp(arr[0].matter_id, "m1") == 0);
    ca_legal_matter_free_array(arr, n);

    ca_legal_board_destroy(b);
    printf("  matters: ok\n");
}

static void test_contracts(void) {
    ca_legal_board_t *b = ca_legal_board_create();

    const char *cps1[] = { "PartyA", "PartyB" };
    ca_legal_contract_t c1; memset(&c1, 0, sizeof(c1));
    c1.contract_id = (char *)"k1"; c1.matter_id = (char *)"m1";
    c1.title = (char *)"NDA"; c1.effective_date_ms = 0;
    c1.has_expiry = true; c1.expiry_date_ms = 300;
    c1.counterparties = (char **)cps1; c1.counterparty_count = 2;

    ca_legal_contract_t c2; memset(&c2, 0, sizeof(c2));
    c2.contract_id = (char *)"k2"; c2.matter_id = (char *)"m1";
    c2.title = (char *)"MSA"; c2.effective_date_ms = 0;
    c2.has_expiry = true; c2.expiry_date_ms = 100;
    c2.counterparties = NULL; c2.counterparty_count = 0;   /* empty list */

    ca_legal_contract_t c3; memset(&c3, 0, sizeof(c3));
    c3.contract_id = (char *)"k3"; c3.matter_id = (char *)"m2";
    c3.title = (char *)"Perpetual"; c3.effective_date_ms = 0;
    c3.has_expiry = false;   /* null ExpiryDate -> never in ExpiringBefore */

    assert(ca_legal_board_add_contract(b, &c1) == 0);
    assert(ca_legal_board_add_contract(b, &c2) == 0);
    assert(ca_legal_board_add_contract(b, &c3) == 0);

    /* ContractsExpiringBefore(250): k2(100) only (k1 expires 300 > 250; k3 null).
     * ordered by ExpiryDate ascending. */
    size_t n = 0;
    ca_legal_contract_t *arr = ca_legal_board_contracts_expiring_before(b, 250, &n);
    assert(n == 1 && strcmp(arr[0].contract_id, "k2") == 0);
    assert(arr[0].counterparty_count == 0 && arr[0].counterparties == NULL);
    ca_legal_contract_free_array(arr, n);

    /* Before 350: k2(100), k1(300) ascending. */
    arr = ca_legal_board_contracts_expiring_before(b, 350, &n);
    assert(n == 2);
    assert(strcmp(arr[0].contract_id, "k2") == 0);
    assert(strcmp(arr[1].contract_id, "k1") == 0);
    /* counterparties deep-copied. */
    assert(arr[1].counterparty_count == 2 &&
           strcmp(arr[1].counterparties[0], "PartyA") == 0 &&
           strcmp(arr[1].counterparties[1], "PartyB") == 0);
    ca_legal_contract_free_array(arr, n);

    ca_legal_board_destroy(b);
    printf("  contracts: ok\n");
}

static void test_deadlines(void) {
    ca_legal_board_t *b = ca_legal_board_create();

    ca_legal_deadline_t d1; memset(&d1, 0, sizeof(d1));
    d1.deadline_id = (char *)"d1"; d1.matter_id = (char *)"m1";
    d1.description = (char *)"File brief"; d1.due_on_ms = 300;
    ca_legal_deadline_t d2; memset(&d2, 0, sizeof(d2));
    d2.deadline_id = (char *)"d2"; d2.matter_id = (char *)"m1";
    d2.description = (char *)"Past"; d2.due_on_ms = 50;
    ca_legal_deadline_t d3; memset(&d3, 0, sizeof(d3));
    d3.deadline_id = (char *)"d3"; d3.matter_id = (char *)"m1";
    d3.description = (char *)"Soon"; d3.due_on_ms = 150;

    assert(ca_legal_board_add_deadline(b, &d1) == 0);
    assert(ca_legal_board_add_deadline(b, &d2) == 0);
    assert(ca_legal_board_add_deadline(b, &d3) == 0);

    /* UpcomingDeadlines(now=100): DueOn >= 100 -> d3(150), d1(300) ascending. */
    size_t n = 0;
    ca_legal_deadline_t *arr = ca_legal_board_upcoming_deadlines(b, 100, &n);
    assert(n == 2);
    assert(strcmp(arr[0].deadline_id, "d3") == 0);
    assert(strcmp(arr[1].deadline_id, "d1") == 0);
    ca_legal_deadline_free_array(arr, n);

    ca_legal_board_destroy(b);
    printf("  deadlines: ok\n");
}

static void test_clauses(void) {
    ca_legal_board_t *b = ca_legal_board_create();

    const char *t1[] = { "indemnity", "liability" };
    const char *t2[] = { "Termination" };
    ca_legal_clause_t c1; memset(&c1, 0, sizeof(c1));
    c1.clause_id = (char *)"cl1"; c1.title = (char *)"Indemnity";
    c1.body = (char *)"..."; c1.tags = (char **)t1; c1.tag_count = 2;
    ca_legal_clause_t c2; memset(&c2, 0, sizeof(c2));
    c2.clause_id = (char *)"cl2"; c2.title = (char *)"Termination";
    c2.body = (char *)"..."; c2.tags = (char **)t2; c2.tag_count = 1;
    assert(ca_legal_board_add_clause(b, &c1) == 0);
    assert(ca_legal_board_add_clause(b, &c2) == 0);

    /* ClausesByTag("LIABILITY") case-insensitive -> cl1. */
    size_t n = 0;
    ca_legal_clause_t *arr = ca_legal_board_clauses_by_tag(b, "LIABILITY", &n);
    assert(n == 1 && strcmp(arr[0].clause_id, "cl1") == 0);
    assert(arr[0].tag_count == 2);
    ca_legal_clause_free_array(arr, n);

    /* miss. */
    arr = ca_legal_board_clauses_by_tag(b, "nonexistent", &n);
    assert(n == 0 && arr == NULL);

    /* whitespace tag -> SIZE_MAX (ArgumentException). */
    arr = ca_legal_board_clauses_by_tag(b, "   ", &n);
    assert(n == (size_t)-1 && arr == NULL);
    arr = ca_legal_board_clauses_by_tag(b, NULL, &n);
    assert(n == (size_t)-1 && arr == NULL);

    ca_legal_board_destroy(b);
    printf("  clauses: ok\n");
}

int main(void) {
    test_matters();
    test_contracts();
    test_deadlines();
    test_clauses();
    printf("test_legal: all assertions passed\n");
    return 0;
}
