#ifndef CIRCLE_AI_CRM_H
#define CIRCLE_AI_CRM_H

/*
 * crm.h — CircleAI.CRM (C11 port of Contracts.cs + InMemoryCrm.cs).
 *
 * Ports the CRM contract surface + real in-memory backends:
 *
 *   Records : Contact(ContactId, FullName, string? Email, string? Phone,
 *                      string? CompanyId);
 *             Company(CompanyId, Name, string? Industry);
 *             Deal(DealId, CompanyId, Name, decimal Value, Currency, Stage);
 *             Activity(ActivityId, ContactId, Kind, Body, DateTimeOffset AtUtc).
 *   Stores  : IContactStore  -> InMemoryContactStore
 *               Upsert (ContactId keyed, replace), Get(id) -> contact?,
 *               Search(query, topK=20) — FullName OR Email OrdinalIgnoreCase
 *               substring, ordered by FullName (OrdinalIgnoreCase) asc, Take(topK).
 *             IDealPipeline  -> InMemoryDealPipeline
 *               Upsert (DealId keyed), Get(id) -> deal?,
 *               ListByStage(stage) — Stage OrdinalIgnoreCase equal, ordered by
 *               Value descending.
 *             IActivityLog   -> InMemoryActivityLog
 *               Append (per-ContactId list), ReadForContact(contactId, limit=100)
 *               ordered by AtUtc descending, Take(limit).
 *             BackendId == "in-memory" for all three.
 *
 * Conventions: ca_ prefix, _t types, opaque handles, strdup-owning fields with
 * matching *_free, deep-copy getters, errors via NULL / count SIZE_MAX. Nullable
 * C# string fields carried as has_* flag + owned buffer. decimal Value as
 * ca_crm_decimal_t (int64 scaled 1e6). AtUtc as int64 Unix ms UTC. Linear arrays,
 * no hashtable, no pthreads. Pure C11 + libc.
 */

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

/* Money surrogate: int64 count of 1e-6 units. */
typedef int64_t ca_crm_decimal_t;
#define CA_CRM_DECIMAL_SCALE 1000000LL

/* Contact(ContactId, FullName, string? Email, string? Phone, string? CompanyId). */
typedef struct {
    char *contact_id;   /* owned, non-null */
    char *full_name;    /* owned, non-null */
    bool  has_email;    /* false == C# null Email */
    char *email;        /* owned, valid only when has_email */
    bool  has_phone;    /* false == C# null Phone */
    char *phone;        /* owned, valid only when has_phone */
    bool  has_company;  /* false == C# null CompanyId */
    char *company_id;   /* owned, valid only when has_company */
} ca_crm_contact_t;

void ca_crm_contact_free(ca_crm_contact_t *c);
void ca_crm_contact_free_array(ca_crm_contact_t *arr, size_t count);

/* Company(CompanyId, Name, string? Industry). */
typedef struct {
    char *company_id;   /* owned, non-null */
    char *name;         /* owned, non-null */
    bool  has_industry; /* false == C# null Industry */
    char *industry;     /* owned, valid only when has_industry */
} ca_crm_company_t;

void ca_crm_company_free(ca_crm_company_t *c);

/* Deal(DealId, CompanyId, Name, decimal Value, Currency, Stage). */
typedef struct {
    char            *deal_id;    /* owned, non-null */
    char            *company_id; /* owned, non-null */
    char            *name;       /* owned, non-null */
    ca_crm_decimal_t value;
    char            *currency;   /* owned, non-null */
    char            *stage;      /* owned, non-null */
} ca_crm_deal_t;

void ca_crm_deal_free(ca_crm_deal_t *d);
void ca_crm_deal_free_array(ca_crm_deal_t *arr, size_t count);

/* Activity(ActivityId, ContactId, Kind, Body, DateTimeOffset AtUtc). */
typedef struct {
    char   *activity_id; /* owned, non-null */
    char   *contact_id;  /* owned, non-null */
    char   *kind;        /* owned, non-null */
    char   *body;        /* owned, non-null */
    int64_t at_utc_ms;   /* DateTimeOffset as Unix ms UTC */
} ca_crm_activity_t;

void ca_crm_activity_free(ca_crm_activity_t *a);
void ca_crm_activity_free_array(ca_crm_activity_t *arr, size_t count);

/* ── IContactStore -> InMemoryContactStore ──────────────────────────────── */

typedef struct ca_crm_contact_store ca_crm_contact_store_t;

ca_crm_contact_store_t *ca_crm_contact_store_create(void); /* NULL on OOM */
void ca_crm_contact_store_destroy(ca_crm_contact_store_t *s);
const char *ca_crm_contact_store_backend_id(const ca_crm_contact_store_t *s);

/* Upsert(c) — ContactId keys the store (replace). ContactId required
 * (non-null/whitespace). 0 on success, -1 on bad args/OOM (2 when
 * ContactId is whitespace, mirroring ArgumentException). */
int ca_crm_contact_store_upsert(ca_crm_contact_store_t *s,
                                const ca_crm_contact_t *c);

/* Get(id) -> fresh owned copy into *out, true; false (C# null) on miss. id
 * required; false on bad args. */
bool ca_crm_contact_store_get(const ca_crm_contact_store_t *s, const char *id,
                              ca_crm_contact_t *out);

/* Search(query, topK) -> fresh owned array (*out_count): FullName OR Email
 * OrdinalIgnoreCase substring, ordered by FullName (OrdinalIgnoreCase) asc,
 * Take(topK). NULL + 0 when empty; NULL + SIZE_MAX on error (query NULL or
 * topK <= 0). */
ca_crm_contact_t *ca_crm_contact_store_search(const ca_crm_contact_store_t *s,
                                              const char *query, int top_k,
                                              size_t *out_count);

/* ── IDealPipeline -> InMemoryDealPipeline ──────────────────────────────── */

typedef struct ca_crm_deal_pipeline ca_crm_deal_pipeline_t;

ca_crm_deal_pipeline_t *ca_crm_deal_pipeline_create(void); /* NULL on OOM */
void ca_crm_deal_pipeline_destroy(ca_crm_deal_pipeline_t *p);
const char *ca_crm_deal_pipeline_backend_id(const ca_crm_deal_pipeline_t *p);

/* Upsert(d) — DealId keys the store. DealId required. 0 / -1 / 2 (whitespace). */
int ca_crm_deal_pipeline_upsert(ca_crm_deal_pipeline_t *p,
                                const ca_crm_deal_t *d);

/* Get(id) -> fresh owned copy into *out, true; false on miss/bad args. */
bool ca_crm_deal_pipeline_get(const ca_crm_deal_pipeline_t *p, const char *id,
                              ca_crm_deal_t *out);

/* ListByStage(stage) -> fresh owned array (*out_count): Stage OrdinalIgnoreCase
 * equal, ordered by Value descending. NULL + 0 when empty; NULL + SIZE_MAX on
 * error (stage NULL/whitespace). */
ca_crm_deal_t *ca_crm_deal_pipeline_list_by_stage(const ca_crm_deal_pipeline_t *p,
                                                  const char *stage,
                                                  size_t *out_count);

/* ── IActivityLog -> InMemoryActivityLog ────────────────────────────────── */

typedef struct ca_crm_activity_log ca_crm_activity_log_t;

ca_crm_activity_log_t *ca_crm_activity_log_create(void); /* NULL on OOM */
void ca_crm_activity_log_destroy(ca_crm_activity_log_t *l);
const char *ca_crm_activity_log_backend_id(const ca_crm_activity_log_t *l);

/* Append(a) — appended to the ContactId's list. ContactId required.
 * 0 / -1 / 2 (whitespace). */
int ca_crm_activity_log_append(ca_crm_activity_log_t *l,
                               const ca_crm_activity_t *a);

/* ReadForContact(contactId, limit) -> fresh owned array (*out_count) ordered by
 * AtUtc descending, Take(limit). NULL + 0 when empty; NULL + SIZE_MAX on error
 * (contactId NULL/whitespace). */
ca_crm_activity_t *ca_crm_activity_log_read_for_contact(
    const ca_crm_activity_log_t *l, const char *contact_id, int limit,
    size_t *out_count);

#ifdef __cplusplus
}
#endif

#endif /* CIRCLE_AI_CRM_H */
