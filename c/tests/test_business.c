/*
 * test_business.c — CircleAI.Business (C11 port) verification against
 * BusinessPrimitives.cs.
 */

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <math.h>
#include <assert.h>
#include "circle_ai/circle_ai.h"

static ca_biz_unit_t mk_unit(const char *id, const char *name, const char *parent) {
    ca_biz_unit_t u; memset(&u, 0, sizeof(u));
    u.unit_id = (char *)id; u.name = (char *)name; u.parent_unit_id = (char *)parent;
    return u;
}
static ca_biz_kpi_t mk_kpi(const char *uid, const char *metric, double v, int64_t at) {
    ca_biz_kpi_t k; memset(&k, 0, sizeof(k));
    k.unit_id = (char *)uid; k.metric = (char *)metric; k.value = v; k.at_utc_ms = at;
    return k;
}

static void test_units(void) {
    ca_biz_board_t *b = ca_biz_board_create();
    assert(b);
    assert(ca_biz_board_add(b, NULL) == -1);

    char *tags[] = { (char *)"rev", (char *)"growth" };
    ca_biz_unit_t root = mk_unit("root", "Root", "");
    ca_biz_unit_t sales = mk_unit("sales", "Sales", "root");
    sales.kpi_tags = tags; sales.kpi_tag_count = 2;
    ca_biz_unit_t eng = mk_unit("eng", "Eng", "root");
    assert(ca_biz_board_add(b, &root) == 0);
    assert(ca_biz_board_add(b, &sales) == 0);
    assert(ca_biz_board_add(b, &eng) == 0);

    ca_biz_unit_t got;
    assert(ca_biz_board_get_unit(b, "sales", &got));
    assert(got.kpi_tag_count == 2 && strcmp(got.kpi_tags[0], "rev") == 0);
    ca_biz_unit_free(&got);
    assert(!ca_biz_board_get_unit(b, "nope", &got));

    /* ChildrenOf("root"): sales, eng in insertion order. */
    size_t n = 0;
    ca_biz_unit_t *kids = ca_biz_board_children_of(b, "root", &n);
    assert(n == 2);
    assert(strcmp(kids[0].unit_id, "sales") == 0);
    assert(strcmp(kids[1].unit_id, "eng") == 0);
    ca_biz_unit_free_array(kids, n);

    kids = ca_biz_board_children_of(b, "orphan", &n);
    assert(n == 0 && kids == NULL);

    ca_biz_board_destroy(b);
    printf("  units: ok\n");
}

static void test_kpis_targets(void) {
    ca_biz_board_t *b = ca_biz_board_create();

    /* LatestKpi with no samples => NaN. */
    assert(isnan(ca_biz_board_latest_kpi(b, "sales", "rev")));

    ca_biz_kpi_t k1 = mk_kpi("sales", "rev", 100.0, 10);
    ca_biz_kpi_t k2 = mk_kpi("sales", "rev", 250.0, 30); /* newest */
    ca_biz_kpi_t k3 = mk_kpi("sales", "rev", 175.0, 20);
    assert(ca_biz_board_record(b, &k1) == 0);
    assert(ca_biz_board_record(b, &k2) == 0);
    assert(ca_biz_board_record(b, &k3) == 0);
    assert(ca_biz_board_latest_kpi(b, "sales", "rev") == 250.0);
    assert(isnan(ca_biz_board_latest_kpi(b, "sales", "other")));

    /* TargetAchievement: missing target => NaN. */
    assert(isnan(ca_biz_board_target_achievement(b, "sales", "rev", 2026, 3)));

    ca_biz_target_t t; memset(&t, 0, sizeof(t));
    t.unit_id = (char *)"sales"; t.metric = (char *)"rev"; t.year = 2026;
    t.quarter = 3; t.target = 500.0;
    assert(ca_biz_board_set_target(b, &t) == 0);
    /* 250 / 500 = 0.5 */
    assert(ca_biz_board_target_achievement(b, "sales", "rev", 2026, 3) == 0.5);

    /* Target == 0 => NaN. */
    ca_biz_target_t tz; memset(&tz, 0, sizeof(tz));
    tz.unit_id = (char *)"sales"; tz.metric = (char *)"rev"; tz.year = 2027;
    tz.quarter = 1; tz.target = 0.0;
    assert(ca_biz_board_set_target(b, &tz) == 0);
    assert(isnan(ca_biz_board_target_achievement(b, "sales", "rev", 2027, 1)));

    /* SetTarget replaces same composite key. */
    ca_biz_target_t t2 = t; t2.target = 250.0;
    assert(ca_biz_board_set_target(b, &t2) == 0);
    assert(ca_biz_board_target_achievement(b, "sales", "rev", 2026, 3) == 1.0);

    ca_biz_board_destroy(b);
    printf("  kpis_targets: ok\n");
}

int main(void) {
    test_units();
    test_kpis_targets();
    printf("test_business: all assertions passed\n");
    return 0;
}
