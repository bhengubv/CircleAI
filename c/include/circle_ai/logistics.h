#ifndef CIRCLE_AI_LOGISTICS_H
#define CIRCLE_AI_LOGISTICS_H

/*
 * logistics.h — CircleAI.Logistics (C11 port of LogisticsPrimitives.cs).
 *
 *   Records : Shipment(ShipmentId, Origin, Destination, double WeightKg,
 *                      double VolumeM3, Incoterm, DateTimeOffset PickupAtUtc);
 *             Vehicle(VehicleId, double CapacityKg, double CapacityM3,
 *                     double CostPerKm);
 *             RouteLeg(FromCode, ToCode, double DistanceKm);
 *             RoutePlan(PlanId, VehicleId, IReadOnlyList<RouteLeg> Legs,
 *                       double TotalDistanceKm, decimal EstimatedCost).
 *   Board   : ILogisticsBoard -> InMemoryLogisticsBoard
 *               RegisterShipment (ShipmentId keyed; throws on whitespace id),
 *               RegisterVehicle (VehicleId keyed; throws on whitespace id),
 *               GetShipment(id) -> shipment?,
 *               Vehicles ordered by VehicleId (ordinal) asc,
 *               PlanRoute(vehicleId, legs) — throws on unknown vehicle (=> rc 1),
 *               builds RoutePlan("plan-{n}", vehicleId, legs copy,
 *               sum(DistanceKm), (decimal)(totalKm * CostPerKm)).
 *
 * Conventions: ca_ prefix, _t types, opaque handles, strdup-owning fields with
 * matching *_free, deep-copy getters, errors via NULL / count SIZE_MAX. decimal
 * EstimatedCost as ca_log_decimal_t (int64 scaled 1e6). PickupAtUtc as int64 Unix
 * ms UTC. Linear arrays, no pthreads. Pure C11 + libc.
 */

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

typedef int64_t ca_log_decimal_t;
#define CA_LOG_DECIMAL_SCALE 1000000LL

/* Shipment(ShipmentId, Origin, Destination, double WeightKg, double VolumeM3,
 * Incoterm, DateTimeOffset PickupAtUtc). */
typedef struct {
    char   *shipment_id;    /* owned, non-null */
    char   *origin;         /* owned, non-null */
    char   *destination;    /* owned, non-null */
    double  weight_kg;
    double  volume_m3;
    char   *incoterm;       /* owned, non-null */
    int64_t pickup_at_utc_ms;
} ca_log_shipment_t;

void ca_log_shipment_free(ca_log_shipment_t *s);

/* Vehicle(VehicleId, double CapacityKg, double CapacityM3, double CostPerKm). */
typedef struct {
    char  *vehicle_id;   /* owned, non-null */
    double capacity_kg;
    double capacity_m3;
    double cost_per_km;
} ca_log_vehicle_t;

void ca_log_vehicle_free(ca_log_vehicle_t *v);
void ca_log_vehicle_free_array(ca_log_vehicle_t *arr, size_t count);

/* RouteLeg(FromCode, ToCode, double DistanceKm). */
typedef struct {
    char  *from_code;   /* owned, non-null */
    char  *to_code;     /* owned, non-null */
    double distance_km;
} ca_log_route_leg_t;

void ca_log_route_leg_free(ca_log_route_leg_t *l);
void ca_log_route_leg_free_array(ca_log_route_leg_t *arr, size_t count);

/* RoutePlan(PlanId, VehicleId, IReadOnlyList<RouteLeg> Legs,
 * double TotalDistanceKm, decimal EstimatedCost). */
typedef struct {
    char               *plan_id;    /* owned, non-null */
    char               *vehicle_id; /* owned, non-null */
    ca_log_route_leg_t *legs;       /* owned (may be NULL when count 0) */
    size_t              leg_count;
    double              total_distance_km;
    ca_log_decimal_t    estimated_cost;
} ca_log_route_plan_t;

void ca_log_route_plan_free(ca_log_route_plan_t *p);

typedef struct ca_log_board ca_log_board_t;

ca_log_board_t *ca_log_board_create(void); /* NULL on OOM */
void ca_log_board_destroy(ca_log_board_t *b);

/* RegisterShipment(s) — ShipmentId keyed set. 0 on success, -1 on bad args/OOM,
 * 2 when ShipmentId is whitespace (ArgumentException). */
int ca_log_board_register_shipment(ca_log_board_t *b,
                                   const ca_log_shipment_t *s);

/* RegisterVehicle(v) — VehicleId keyed set. 0 / -1 / 2 (whitespace). */
int ca_log_board_register_vehicle(ca_log_board_t *b, const ca_log_vehicle_t *v);

/* GetShipment(id) -> fresh owned copy into *out, true; false on miss/bad args. */
bool ca_log_board_get_shipment(const ca_log_board_t *b, const char *id,
                               ca_log_shipment_t *out);

/* Vehicles -> fresh owned array (*out_count) ordered by VehicleId (ordinal) asc.
 * NULL + 0 when empty; NULL + SIZE_MAX on error. */
ca_log_vehicle_t *ca_log_board_vehicles(const ca_log_board_t *b,
                                        size_t *out_count);

/* PlanRoute(vehicleId, legs, leg_count) -> RoutePlan into *out (freshly owned;
 * caller frees with ca_log_route_plan_free). 0 on success, -1 on bad args/OOM,
 * 2 when vehicleId is whitespace (ArgumentException), 1 when the vehicle is
 * unknown (InvalidOperationException). legs may be NULL only when leg_count 0. */
int ca_log_board_plan_route(ca_log_board_t *b, const char *vehicle_id,
                            const ca_log_route_leg_t *legs, size_t leg_count,
                            ca_log_route_plan_t *out);

#ifdef __cplusplus
}
#endif

#endif /* CIRCLE_AI_LOGISTICS_H */
