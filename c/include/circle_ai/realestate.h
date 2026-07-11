#ifndef CIRCLE_AI_REALESTATE_H
#define CIRCLE_AI_REALESTATE_H

/*
 * realestate.h — CircleAI.RealEstate (C11 port of RealEstatePrimitives.cs).
 *
 *   Enum    : PropertyKind { Apartment=0, House=1, Townhouse=2, Commercial=3,
 *                            Land=4 }.
 *   Records : Property(PropertyId, Suburb, PropertyKind Kind, int Beds,
 *                      int Baths, double FloorAreaM2);
 *             Listing(ListingId, PropertyId, decimal AskingPrice, Currency,
 *                     DateTimeOffset ListedUtc, bool IsActive);
 *             Valuation(PropertyId, decimal EstimatedValue, Source,
 *                       DateTimeOffset AtUtc);
 *             Viewing(ViewingId, ListingId, AttendeeName, DateTimeOffset AtUtc).
 *   Board   : IRealEstateBoard -> InMemoryRealEstateBoard
 *               RegisterProperty (PropertyId keyed), List (ListingId keyed),
 *               Close(listingId) — sets IsActive=false; throws on unknown (rc 1),
 *               Value (appends Valuation), ScheduleViewing (appends Viewing),
 *               ActiveInSuburb(suburb) — active listings whose property's Suburb
 *               matches (OrdinalIgnoreCase), ordered by ListedUtc descending,
 *               SuburbAverage(suburb) — mean AskingPrice over ActiveInSuburb, or
 *               null when none.
 *
 * Conventions: ca_ prefix, _t types, opaque handles, strdup-owning fields with
 * matching *_free, deep-copy getters, errors via NULL / count SIZE_MAX. decimal
 * fields as ca_re_decimal_t (int64 scaled 1e6). *Utc as int64 Unix ms UTC. Linear
 * arrays, no pthreads. Pure C11 + libc.
 */

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

typedef int64_t ca_re_decimal_t;
#define CA_RE_DECIMAL_SCALE 1000000LL

typedef enum {
    CA_RE_KIND_APARTMENT = 0,
    CA_RE_KIND_HOUSE     = 1,
    CA_RE_KIND_TOWNHOUSE = 2,
    CA_RE_KIND_COMMERCIAL = 3,
    CA_RE_KIND_LAND      = 4
} ca_re_property_kind_t;

/* Property(PropertyId, Suburb, PropertyKind Kind, int Beds, int Baths,
 * double FloorAreaM2). */
typedef struct {
    char                 *property_id; /* owned, non-null */
    char                 *suburb;      /* owned, non-null */
    ca_re_property_kind_t kind;
    int                   beds;
    int                   baths;
    double                floor_area_m2;
} ca_re_property_t;

void ca_re_property_free(ca_re_property_t *p);

/* Listing(ListingId, PropertyId, decimal AskingPrice, Currency,
 * DateTimeOffset ListedUtc, bool IsActive). */
typedef struct {
    char           *listing_id;   /* owned, non-null */
    char           *property_id;  /* owned, non-null */
    ca_re_decimal_t asking_price;
    char           *currency;     /* owned, non-null */
    int64_t         listed_utc_ms;
    bool            is_active;
} ca_re_listing_t;

void ca_re_listing_free(ca_re_listing_t *l);
void ca_re_listing_free_array(ca_re_listing_t *arr, size_t count);

/* Valuation(PropertyId, decimal EstimatedValue, Source, DateTimeOffset AtUtc). */
typedef struct {
    char           *property_id;    /* owned, non-null */
    ca_re_decimal_t estimated_value;
    char           *source;         /* owned, non-null */
    int64_t         at_utc_ms;
} ca_re_valuation_t;

void ca_re_valuation_free(ca_re_valuation_t *v);

/* Viewing(ViewingId, ListingId, AttendeeName, DateTimeOffset AtUtc). */
typedef struct {
    char   *viewing_id;    /* owned, non-null */
    char   *listing_id;    /* owned, non-null */
    char   *attendee_name; /* owned, non-null */
    int64_t at_utc_ms;
} ca_re_viewing_t;

void ca_re_viewing_free(ca_re_viewing_t *v);

typedef struct ca_re_board ca_re_board_t;

ca_re_board_t *ca_re_board_create(void); /* NULL on OOM */
void ca_re_board_destroy(ca_re_board_t *b);

/* RegisterProperty(p) — PropertyId keyed set. 0 / -1 on bad args/OOM. */
int ca_re_board_register_property(ca_re_board_t *b, const ca_re_property_t *p);

/* List(l) — ListingId keyed set. 0 / -1. */
int ca_re_board_list(ca_re_board_t *b, const ca_re_listing_t *l);

/* Close(listingId) — sets IsActive=false. 0 on success, -1 on bad args, 1 when
 * unknown (InvalidOperationException). */
int ca_re_board_close(ca_re_board_t *b, const char *listing_id);

/* Value(v) — appends the Valuation. 0 / -1. */
int ca_re_board_value(ca_re_board_t *b, const ca_re_valuation_t *v);

/* ScheduleViewing(v) — appends the Viewing. 0 / -1. */
int ca_re_board_schedule_viewing(ca_re_board_t *b, const ca_re_viewing_t *v);

/* ActiveInSuburb(suburb) -> fresh owned array (*out_count): active listings whose
 * property's Suburb matches (OrdinalIgnoreCase), ordered by ListedUtc desc. NULL
 * + 0 when empty; NULL + SIZE_MAX on error (suburb NULL/whitespace). */
ca_re_listing_t *ca_re_board_active_in_suburb(const ca_re_board_t *b,
                                              const char *suburb,
                                              size_t *out_count);

/* SuburbAverage(suburb) — mean AskingPrice (micro-units, rounded) over
 * ActiveInSuburb into *out. Returns true when at least one active listing exists,
 * false (C# null) when none or on bad args. */
bool ca_re_board_suburb_average(const ca_re_board_t *b, const char *suburb,
                                ca_re_decimal_t *out);

#ifdef __cplusplus
}
#endif

#endif /* CIRCLE_AI_REALESTATE_H */
