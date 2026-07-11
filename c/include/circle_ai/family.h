#ifndef CIRCLE_AI_FAMILY_H
#define CIRCLE_AI_FAMILY_H

/*
 * family.h — CircleAI.Family (C11 port of FamilyPrimitives.cs).
 *
 *   Records : FamilyMember(MemberId, Name, Role, DateTime DateOfBirth);
 *             FamilyEvent(EventId, Title, DateTimeOffset AtUtc,
 *                         IReadOnlyList<string> MemberIds);
 *             SharedExpense(ExpenseId, PaidById, decimal Amount, Currency,
 *                           Category, DateTimeOffset AtUtc).
 *   Board   : IFamilyBoard -> InMemoryFamilyBoard
 *               Add (MemberId keyed), GetMember(id) -> member?,
 *               Members ordered by Name asc, Schedule (EventId keyed),
 *               EventsForMember(memberId) where MemberIds contains memberId
 *               (ordinal), ordered by AtUtc asc, Record (appends SharedExpense),
 *               TotalPaidBy(memberId, since) = sum Amount where PaidById ==
 *               memberId && AtUtc >= since, SpendByCategory(category, since) =
 *               sum Amount where Category == category (OrdinalIgnoreCase) &&
 *               AtUtc >= since.
 *
 * Conventions: ca_ prefix, _t types, opaque handles, strdup-owning fields with
 * matching *_free, deep-copy getters, errors via NULL / count SIZE_MAX. decimal
 * Amount / sums as ca_fam_decimal_t (int64 scaled 1e6). DateOfBirth / AtUtc as
 * int64 Unix ms UTC. Linear arrays, no pthreads. Pure C11 + libc.
 */

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

typedef int64_t ca_fam_decimal_t;
#define CA_FAM_DECIMAL_SCALE 1000000LL

/* FamilyMember(MemberId, Name, Role, DateTime DateOfBirth). */
typedef struct {
    char   *member_id;      /* owned, non-null */
    char   *name;           /* owned, non-null */
    char   *role;           /* owned, non-null */
    int64_t date_of_birth_ms;
} ca_fam_member_t;

void ca_fam_member_free(ca_fam_member_t *m);
void ca_fam_member_free_array(ca_fam_member_t *arr, size_t count);

/* FamilyEvent(EventId, Title, DateTimeOffset AtUtc,
 * IReadOnlyList<string> MemberIds). */
typedef struct {
    char   *event_id;   /* owned, non-null */
    char   *title;      /* owned, non-null */
    int64_t at_utc_ms;
    char  **member_ids; /* owned array (may be NULL when count 0) */
    size_t  member_id_count;
} ca_fam_event_t;

void ca_fam_event_free(ca_fam_event_t *e);
void ca_fam_event_free_array(ca_fam_event_t *arr, size_t count);

/* SharedExpense(ExpenseId, PaidById, decimal Amount, Currency, Category,
 * DateTimeOffset AtUtc). */
typedef struct {
    char            *expense_id; /* owned, non-null */
    char            *paid_by_id; /* owned, non-null */
    ca_fam_decimal_t amount;
    char            *currency;   /* owned, non-null */
    char            *category;   /* owned, non-null */
    int64_t          at_utc_ms;
} ca_fam_expense_t;

void ca_fam_expense_free(ca_fam_expense_t *e);

typedef struct ca_fam_board ca_fam_board_t;

ca_fam_board_t *ca_fam_board_create(void); /* NULL on OOM */
void ca_fam_board_destroy(ca_fam_board_t *b);

/* Add(m) — MemberId keyed set. 0 / -1 on bad args/OOM. */
int ca_fam_board_add(ca_fam_board_t *b, const ca_fam_member_t *m);

/* GetMember(id) -> fresh owned copy into *out, true; false on miss/bad args. */
bool ca_fam_board_get_member(const ca_fam_board_t *b, const char *id,
                             ca_fam_member_t *out);

/* Members -> fresh owned array (*out_count) ordered by Name asc. NULL + 0 empty;
 * NULL + SIZE_MAX on error. */
ca_fam_member_t *ca_fam_board_members(const ca_fam_board_t *b,
                                      size_t *out_count);

/* Schedule(e) — EventId keyed set. 0 / -1. */
int ca_fam_board_schedule(ca_fam_board_t *b, const ca_fam_event_t *e);

/* EventsForMember(memberId) -> fresh owned array (*out_count): events whose
 * MemberIds contains memberId (ordinal), ordered by AtUtc asc. NULL + 0 empty;
 * NULL + SIZE_MAX on error. */
ca_fam_event_t *ca_fam_board_events_for_member(const ca_fam_board_t *b,
                                               const char *member_id,
                                               size_t *out_count);

/* Record(e) — appends the SharedExpense. 0 / -1. */
int ca_fam_board_record(ca_fam_board_t *b, const ca_fam_expense_t *e);

/* TotalPaidBy(memberId, since_ms) = sum Amount (micro-units) where PaidById ==
 * memberId && AtUtc >= since. */
ca_fam_decimal_t ca_fam_board_total_paid_by(const ca_fam_board_t *b,
                                            const char *member_id,
                                            int64_t since_ms);

/* SpendByCategory(category, since_ms) = sum Amount (micro-units) where Category
 * == category (OrdinalIgnoreCase) && AtUtc >= since. */
ca_fam_decimal_t ca_fam_board_spend_by_category(const ca_fam_board_t *b,
                                                const char *category,
                                                int64_t since_ms);

#ifdef __cplusplus
}
#endif

#endif /* CIRCLE_AI_FAMILY_H */
