#ifndef CIRCLE_AI_LEGAL_H
#define CIRCLE_AI_LEGAL_H

/*
 * legal.h — CircleAI.Legal (C11 port of LegalPrimitives.cs).
 *
 *   Records : Matter(MatterId, Title, Jurisdiction, Client, OpenedAtUtc, Open);
 *             Contract(ContractId, MatterId, Title, EffectiveDate, ExpiryDate?,
 *                      Counterparties[]);
 *             LegalDeadline(DeadlineId, MatterId, Description, DueOn);
 *             Clause(ClauseId, Title, Body, Tags[]).
 *   Board   : ILegalBoard -> InMemoryLegalBoard.
 *             Open(m) (MatterId keyed set), Close(matterId) (throws on unknown;
 *             flips Open=false), GetMatter(id) -> matter?, ActiveMatters (Open
 *             only, ordered by OpenedAtUtc descending), AddContract(c),
 *             ContractsExpiringBefore(date) (ExpiryDate present && <= date,
 *             ordered by ExpiryDate ascending), Add(d) deadline,
 *             UpcomingDeadlines(now) (DueOn >= now, ordered by DueOn ascending),
 *             AddClause(c), ClausesByTag(tag) (tag match OrdinalIgnoreCase;
 *             empty tag rejected).
 *
 * Conventions: ca_ prefix, _t types, opaque handles, strdup-owning fields with
 * matching *_free, deep-copy getters, errors via NULL / count SIZE_MAX. Dates
 * (DateTime / DateTimeOffset) as int64 Unix ms UTC. ExpiryDate optional
 * (has_expiry gate). Counterparties / Tags are owned string arrays. Linear
 * arrays, no pthreads.
 *
 * Pure C11 + libc.
 */

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

/* Matter(MatterId, Title, Jurisdiction, Client, DateTimeOffset OpenedAtUtc,
 * bool Open). */
typedef struct {
    char   *matter_id;     /* owned, non-null */
    char   *title;         /* owned, non-null */
    char   *jurisdiction;  /* owned, non-null */
    char   *client;        /* owned, non-null */
    int64_t opened_at_utc_ms;/* DateTimeOffset as Unix ms UTC */
    bool    open;
} ca_legal_matter_t;

void ca_legal_matter_free(ca_legal_matter_t *m);
void ca_legal_matter_free_array(ca_legal_matter_t *arr, size_t count);

/* Contract(ContractId, MatterId, Title, DateTime EffectiveDate,
 * DateTime? ExpiryDate, IReadOnlyList<string> Counterparties). */
typedef struct {
    char   *contract_id;      /* owned, non-null */
    char   *matter_id;        /* owned, non-null */
    char   *title;            /* owned, non-null */
    int64_t effective_date_ms;/* DateTime as Unix ms UTC */
    bool    has_expiry;       /* false == C# null ExpiryDate */
    int64_t expiry_date_ms;   /* valid only when has_expiry */
    char  **counterparties;   /* owned string array (may be NULL when count 0) */
    size_t  counterparty_count;
} ca_legal_contract_t;

void ca_legal_contract_free(ca_legal_contract_t *c);
void ca_legal_contract_free_array(ca_legal_contract_t *arr, size_t count);

/* LegalDeadline(DeadlineId, MatterId, Description, DateTime DueOn). */
typedef struct {
    char   *deadline_id;  /* owned, non-null */
    char   *matter_id;    /* owned, non-null */
    char   *description;  /* owned, non-null */
    int64_t due_on_ms;    /* DateTime as Unix ms UTC */
} ca_legal_deadline_t;

void ca_legal_deadline_free(ca_legal_deadline_t *d);
void ca_legal_deadline_free_array(ca_legal_deadline_t *arr, size_t count);

/* Clause(ClauseId, Title, Body, IReadOnlyList<string> Tags). */
typedef struct {
    char  *clause_id;  /* owned, non-null */
    char  *title;      /* owned, non-null */
    char  *body;       /* owned, non-null */
    char **tags;       /* owned string array (may be NULL when count 0) */
    size_t tag_count;
} ca_legal_clause_t;

void ca_legal_clause_free(ca_legal_clause_t *c);
void ca_legal_clause_free_array(ca_legal_clause_t *arr, size_t count);

typedef struct ca_legal_board ca_legal_board_t;

/* InMemoryLegalBoard(). NULL on OOM. */
ca_legal_board_t *ca_legal_board_create(void);
void ca_legal_board_destroy(ca_legal_board_t *b);

/* Open(m) — deep-copies; MatterId keyed set. 0 / -1 on bad args/OOM. */
int ca_legal_board_open(ca_legal_board_t *b, const ca_legal_matter_t *m);
/* Close(matterId). 0 on success, -1 on bad args, 1 when unknown
 * (InvalidOperationException). Flips Open=false. */
int ca_legal_board_close(ca_legal_board_t *b, const char *matter_id);
/* GetMatter(id) -> fresh owned copy into *out, true; false on miss. */
bool ca_legal_board_get_matter(const ca_legal_board_t *b, const char *id,
                               ca_legal_matter_t *out);
/* ActiveMatters -> fresh owned array (*out_count): Open matters ordered by
 * OpenedAtUtc descending. NULL + 0 when empty; NULL + SIZE_MAX on error. */
ca_legal_matter_t *ca_legal_board_active_matters(const ca_legal_board_t *b,
                                                 size_t *out_count);

/* AddContract(c) — deep-copies; ContractId keyed set. 0 / -1. */
int ca_legal_board_add_contract(ca_legal_board_t *b,
                                const ca_legal_contract_t *c);
/* ContractsExpiringBefore(date) -> fresh owned array (*out_count): ExpiryDate
 * present && <= date_ms, ordered by ExpiryDate ascending. NULL + 0 when empty;
 * NULL + SIZE_MAX on error. */
ca_legal_contract_t *ca_legal_board_contracts_expiring_before(
    const ca_legal_board_t *b, int64_t date_ms, size_t *out_count);

/* Add(d) deadline — deep-copies; DeadlineId keyed set. 0 / -1. */
int ca_legal_board_add_deadline(ca_legal_board_t *b,
                                const ca_legal_deadline_t *d);
/* UpcomingDeadlines(now) -> fresh owned array (*out_count): DueOn >= now_ms,
 * ordered by DueOn ascending. NULL + 0 when empty; NULL + SIZE_MAX on error. */
ca_legal_deadline_t *ca_legal_board_upcoming_deadlines(const ca_legal_board_t *b,
                                                       int64_t now_ms,
                                                       size_t *out_count);

/* AddClause(c) — deep-copies; ClauseId keyed set. 0 / -1. */
int ca_legal_board_add_clause(ca_legal_board_t *b, const ca_legal_clause_t *c);
/* ClausesByTag(tag) -> fresh owned array (*out_count): clauses whose Tags contain
 * tag (OrdinalIgnoreCase). tag required (non-null / non-whitespace -> SIZE_MAX
 * error, mirroring ArgumentException). NULL + 0 when no hits. */
ca_legal_clause_t *ca_legal_board_clauses_by_tag(const ca_legal_board_t *b,
                                                 const char *tag,
                                                 size_t *out_count);

#ifdef __cplusplus
}
#endif

#endif /* CIRCLE_AI_LEGAL_H */
