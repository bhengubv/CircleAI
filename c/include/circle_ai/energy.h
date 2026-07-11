#ifndef CIRCLE_AI_ENERGY_H
#define CIRCLE_AI_ENERGY_H

/*
 * energy.h — CircleAI.Energy (C11 port of EnergyPrimitives.cs).
 *
 *   Records : MeterReading(MeterId, double Kwh, DateTimeOffset AtUtc);
 *             EnergyTariff(TariffId, Name, double PeakKwhRate, double
 *                     OffPeakKwhRate, Currency);
 *             Outage(OutageId, Area, DateTimeOffset StartUtc, DateTimeOffset?
 *                     EndUtc, string? Reason).
 *   Board   : IEnergyBoard -> InMemoryEnergyBoard
 *               Record (appends), ReadingsFor(meterId, since) ascending by AtUtc,
 *               TotalKwhSince(meterId, since) — last.Kwh - first.Kwh over the
 *               filtered readings (0 when < 2), SetTariff (TariffId keyed),
 *               GetTariff(id), EstimateCost(meterId, tariffId, since) =
 *               TotalKwhSince * PeakKwhRate (unknown tariff throws), LogOutage
 *               (OutageId keyed), ActiveOutages() — EndUtc == null (insertion
 *               order).
 *
 * decimal EstimateCost result via ca_decimal_t (the C# casts a double to decimal;
 * the port scales by 1e6 and truncates toward zero, matching (decimal)double's
 * exact-value construction for the test magnitudes). DateTimeOffset as Unix ms UTC.
 * EndUtc / Reason optional via has_*.
 *
 * Conventions: ca_ prefix, _t types, opaque handles, strdup-owning fields, deep
 * copy getters, errors via NULL / count SIZE_MAX. Linear arrays, no pthreads.
 */

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

typedef int64_t ca_energy_decimal_t; /* micro-units (1e-6) */
#define CA_ENERGY_DECIMAL_SCALE 1000000LL

/* MeterReading(MeterId, double Kwh, DateTimeOffset AtUtc). */
typedef struct {
    char   *meter_id;  /* owned, non-null */
    double  kwh;
    int64_t at_utc_ms;
} ca_energy_reading_t;

void ca_energy_reading_free(ca_energy_reading_t *r);
void ca_energy_reading_free_array(ca_energy_reading_t *arr, size_t count);

/* EnergyTariff(TariffId, Name, double PeakKwhRate, double OffPeakKwhRate,
 * Currency). */
typedef struct {
    char   *tariff_id;      /* owned, non-null */
    char   *name;           /* owned, non-null */
    double  peak_kwh_rate;
    double  off_peak_kwh_rate;
    char   *currency;       /* owned, non-null */
} ca_energy_tariff_t;

void ca_energy_tariff_free(ca_energy_tariff_t *t);

/* Outage(OutageId, Area, DateTimeOffset StartUtc, DateTimeOffset? EndUtc,
 * string? Reason). */
typedef struct {
    char   *outage_id;   /* owned, non-null */
    char   *area;        /* owned, non-null */
    int64_t start_utc_ms;
    bool    has_end_utc; /* false == C# null EndUtc (an active outage) */
    int64_t end_utc_ms;  /* valid only when has_end_utc */
    bool    has_reason;  /* false == C# null Reason */
    char   *reason;      /* owned, valid only when has_reason */
} ca_energy_outage_t;

void ca_energy_outage_free(ca_energy_outage_t *o);
void ca_energy_outage_free_array(ca_energy_outage_t *arr, size_t count);

typedef struct ca_energy_board ca_energy_board_t;

ca_energy_board_t *ca_energy_board_create(void); /* NULL on OOM */
void ca_energy_board_destroy(ca_energy_board_t *b);

/* Record(r) — appends. 0 / -1. */
int ca_energy_board_record(ca_energy_board_t *b, const ca_energy_reading_t *r);

/* ReadingsFor(meterId, since_ms) -> fresh owned array ascending by AtUtc.
 * NULL + 0 empty; NULL + SIZE_MAX on error. */
ca_energy_reading_t *ca_energy_board_readings_for(const ca_energy_board_t *b,
                                                  const char *meter_id,
                                                  int64_t since_ms,
                                                  size_t *out_count);

/* TotalKwhSince(meterId, since_ms) — last.Kwh - first.Kwh over the filtered
 * ascending readings; 0.0 when fewer than 2. */
double ca_energy_board_total_kwh_since(const ca_energy_board_t *b,
                                       const char *meter_id, int64_t since_ms);

/* SetTariff(t) — TariffId keyed set. 0 / -1. */
int ca_energy_board_set_tariff(ca_energy_board_t *b, const ca_energy_tariff_t *t);

/* GetTariff(id) -> fresh owned copy into *out, true; false on miss/bad args. */
bool ca_energy_board_get_tariff(const ca_energy_board_t *b, const char *id,
                                ca_energy_tariff_t *out);

/* EstimateCost(meterId, tariffId, since_ms) -> TotalKwhSince * PeakKwhRate (as
 * micro-units) into *out; 0 on success, -1 on bad args, -2 when the tariff is
 * unknown (C# InvalidOperationException). */
int ca_energy_board_estimate_cost(const ca_energy_board_t *b,
                                  const char *meter_id, const char *tariff_id,
                                  int64_t since_ms, ca_energy_decimal_t *out);

/* LogOutage(o) — OutageId keyed set. 0 / -1. */
int ca_energy_board_log_outage(ca_energy_board_t *b,
                               const ca_energy_outage_t *o);

/* ActiveOutages() -> fresh owned array (insertion order) with EndUtc == null.
 * NULL + 0 empty; NULL + SIZE_MAX on error. */
ca_energy_outage_t *ca_energy_board_active_outages(const ca_energy_board_t *b,
                                                   size_t *out_count);

#ifdef __cplusplus
}
#endif

#endif /* CIRCLE_AI_ENERGY_H */
