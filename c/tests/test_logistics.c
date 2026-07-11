/*
 * test_logistics.c — CircleAI.Logistics (C11 port) verification against
 * LogisticsPrimitives.cs.
 */

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <assert.h>
#include "circle_ai/circle_ai.h"

static ca_log_shipment_t mk_ship(const char *id) {
    ca_log_shipment_t s; memset(&s, 0, sizeof(s));
    s.shipment_id = (char *)id; s.origin = (char *)"CPT"; s.destination = (char *)"JNB";
    s.weight_kg = 100.0; s.volume_m3 = 2.0; s.incoterm = (char *)"DAP";
    s.pickup_at_utc_ms = 1000;
    return s;
}
static ca_log_vehicle_t mk_veh(const char *id, double cost) {
    ca_log_vehicle_t v; memset(&v, 0, sizeof(v));
    v.vehicle_id = (char *)id; v.capacity_kg = 1000.0; v.capacity_m3 = 20.0;
    v.cost_per_km = cost;
    return v;
}

static void test_shipments_vehicles(void) {
    ca_log_board_t *b = ca_log_board_create();
    assert(b);
    assert(ca_log_board_register_shipment(b, NULL) == -1);

    ca_log_shipment_t bad = mk_ship("  ");
    assert(ca_log_board_register_shipment(b, &bad) == 2);

    ca_log_shipment_t s = mk_ship("s1");
    assert(ca_log_board_register_shipment(b, &s) == 0);
    ca_log_shipment_t got;
    assert(ca_log_board_get_shipment(b, "s1", &got) && strcmp(got.origin, "CPT") == 0);
    ca_log_shipment_free(&got);
    assert(!ca_log_board_get_shipment(b, "nope", &got));

    ca_log_vehicle_t vbad = mk_veh(" ", 1.0);
    assert(ca_log_board_register_vehicle(b, &vbad) == 2);
    ca_log_vehicle_t v1 = mk_veh("truck", 2.5);
    ca_log_vehicle_t v2 = mk_veh("bike", 0.5);
    assert(ca_log_board_register_vehicle(b, &v1) == 0);
    assert(ca_log_board_register_vehicle(b, &v2) == 0);

    /* Vehicles ordered by VehicleId ordinal: bike, truck. */
    size_t n = 0;
    ca_log_vehicle_t *arr = ca_log_board_vehicles(b, &n);
    assert(n == 2);
    assert(strcmp(arr[0].vehicle_id, "bike") == 0);
    assert(strcmp(arr[1].vehicle_id, "truck") == 0);
    ca_log_vehicle_free_array(arr, n);

    ca_log_board_destroy(b);
    printf("  shipments_vehicles: ok\n");
}

static void test_plan_route(void) {
    ca_log_board_t *b = ca_log_board_create();
    ca_log_vehicle_t v = mk_veh("truck", 2.0);
    assert(ca_log_board_register_vehicle(b, &v) == 0);

    ca_log_route_leg_t legs[2];
    memset(legs, 0, sizeof(legs));
    legs[0].from_code = (char *)"CPT"; legs[0].to_code = (char *)"BFN"; legs[0].distance_km = 1000.0;
    legs[1].from_code = (char *)"BFN"; legs[1].to_code = (char *)"JNB"; legs[1].distance_km = 400.0;

    ca_log_route_plan_t plan;
    /* whitespace vehicle => 2. */
    assert(ca_log_board_plan_route(b, "  ", legs, 2, &plan) == 2);
    /* unknown vehicle => 1. */
    assert(ca_log_board_plan_route(b, "van", legs, 2, &plan) == 1);

    /* valid: total 1400km, cost 1400*2.0 = 2800.00. */
    assert(ca_log_board_plan_route(b, "truck", legs, 2, &plan) == 0);
    assert(plan.leg_count == 2);
    assert(strcmp(plan.plan_id, "plan-1") == 0);
    assert(strcmp(plan.vehicle_id, "truck") == 0);
    assert(plan.total_distance_km == 1400.0);
    assert(plan.estimated_cost == 2800 * CA_LOG_DECIMAL_SCALE);
    assert(strcmp(plan.legs[0].from_code, "CPT") == 0);
    assert(strcmp(plan.legs[1].to_code, "JNB") == 0);
    ca_log_route_plan_free(&plan);

    /* second plan => plan-2. */
    assert(ca_log_board_plan_route(b, "truck", legs, 0, &plan) == 0);
    assert(strcmp(plan.plan_id, "plan-2") == 0);
    assert(plan.leg_count == 0 && plan.total_distance_km == 0.0);
    assert(plan.estimated_cost == 0);
    ca_log_route_plan_free(&plan);

    ca_log_board_destroy(b);
    printf("  plan_route: ok\n");
}

int main(void) {
    test_shipments_vehicles();
    test_plan_route();
    printf("test_logistics: all assertions passed\n");
    return 0;
}
