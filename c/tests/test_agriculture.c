/*
 * test_agriculture.c — CircleAI.Agriculture (C11 port) verification against
 * AgriculturePrimitives.cs.
 */

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <math.h>
#include <assert.h>
#include "circle_ai/circle_ai.h"

static void test_farm(void) {
    ca_farm_board_t *b = ca_farm_board_create();
    assert(b);
    assert(ca_farm_board_add_field(b, NULL) == -1);

    ca_farm_field_t f1; memset(&f1, 0, sizeof(f1));
    f1.field_id = (char *)"f1"; f1.area_ha = 10.0; f1.soil_type = (char *)"loam";
    f1.irrigation_kind = (char *)"drip";
    assert(ca_farm_board_add_field(b, &f1) == 0);

    ca_farm_field_t got;
    assert(ca_farm_board_get_field(b, "f1", &got) && got.area_ha == 10.0 &&
           strcmp(got.soil_type, "loam") == 0);
    ca_farm_field_free(&got);
    assert(!ca_farm_board_get_field(b, "nope", &got));

    /* Crops on f1, planted out of order. */
    ca_farm_crop_t c1; memset(&c1, 0, sizeof(c1));
    c1.crop_id = (char *)"c1"; c1.field_id = (char *)"f1"; c1.variety = (char *)"Maize";
    c1.planted_on_ms = 300; c1.has_expected_harvest = true; c1.expected_harvest_ms = 900;
    ca_farm_crop_t c2; memset(&c2, 0, sizeof(c2));
    c2.crop_id = (char *)"c2"; c2.field_id = (char *)"f1"; c2.variety = (char *)"maize";
    c2.planted_on_ms = 100; c2.has_expected_harvest = false;
    ca_farm_crop_t c3; memset(&c3, 0, sizeof(c3));
    c3.crop_id = (char *)"c3"; c3.field_id = (char *)"f2"; c3.variety = (char *)"Wheat";
    c3.planted_on_ms = 50;
    assert(ca_farm_board_plant(b, &c1) == 0);
    assert(ca_farm_board_plant(b, &c2) == 0);
    assert(ca_farm_board_plant(b, &c3) == 0);

    /* CropsForField f1 ordered by PlantedOn asc: c2(100), c1(300). */
    size_t n = 0;
    ca_farm_crop_t *cs = ca_farm_board_crops_for_field(b, "f1", &n);
    assert(n == 2 && strcmp(cs[0].crop_id, "c2") == 0 && strcmp(cs[1].crop_id, "c1") == 0);
    assert(cs[1].has_expected_harvest && cs[1].expected_harvest_ms == 900);
    ca_farm_crop_free_array(cs, n);

    /* Yields: c1 -> 8, c2 -> 6 (both Maize CI), c3 -> 4 (Wheat). */
    ca_farm_yield_t y1; memset(&y1, 0, sizeof(y1));
    y1.crop_id = (char *)"c1"; y1.tons_per_ha = 8.0; y1.harvested_on_ms = 1000;
    ca_farm_yield_t y2; memset(&y2, 0, sizeof(y2));
    y2.crop_id = (char *)"c2"; y2.tons_per_ha = 6.0; y2.harvested_on_ms = 1100;
    ca_farm_yield_t y3; memset(&y3, 0, sizeof(y3));
    y3.crop_id = (char *)"c3"; y3.tons_per_ha = 4.0; y3.harvested_on_ms = 1200;
    assert(ca_farm_board_record_yield(b, &y1) == 0);
    assert(ca_farm_board_record_yield(b, &y2) == 0);
    assert(ca_farm_board_record_yield(b, &y3) == 0);

    /* AvgYield "maize" (CI) -> (8+6)/2 = 7. */
    assert(fabs(ca_farm_board_avg_yield_of_variety(b, "maize") - 7.0) < 1e-9);
    /* Wheat -> 4. */
    assert(fabs(ca_farm_board_avg_yield_of_variety(b, "Wheat") - 4.0) < 1e-9);
    /* Unknown -> 0. */
    assert(ca_farm_board_avg_yield_of_variety(b, "Rice") == 0.0);

    ca_farm_board_destroy(b);
    printf("  farm: ok\n");
}

int main(void) {
    test_farm();
    printf("test_agriculture: all assertions passed\n");
    return 0;
}
