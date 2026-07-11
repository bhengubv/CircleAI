#ifndef CIRCLE_AI_COMMUNITY_H
#define CIRCLE_AI_COMMUNITY_H

/*
 * community.h — CircleAI.Community (C11 port of CommunityPrimitives.cs).
 *
 *   Records : CommunityGroup(GroupId, Name, Purpose,
 *                        IReadOnlyList<string> MemberIds);
 *             Announcement(AnnouncementId, GroupId, Title, Body,
 *                        DateTimeOffset AtUtc);
 *             VolunteerOpportunity(OppId, GroupId, Description,
 *                        int VolunteersNeeded, DateTimeOffset WhenUtc).
 *   Board   : ICommunityBoard -> InMemoryCommunityBoard
 *               Create (GroupId keyed), GetGroup(id), GroupsForMember(memberId) —
 *               groups whose MemberIds contains memberId (ordinal; insertion
 *               order), Post (appends), AnnouncementsFor(groupId, limit=20)
 *               newest-first by AtUtc top-limit, List (OppId keyed),
 *               Opportunities(now) — WhenUtc >= now, ordered by WhenUtc asc.
 *
 * The C# Opportunities reads DateTimeOffset.UtcNow; the port takes an explicit
 * now_ms. DateTimeOffset as Unix ms UTC.
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

/* CommunityGroup(GroupId, Name, Purpose, IReadOnlyList<string> MemberIds). */
typedef struct {
    char   *group_id;   /* owned, non-null */
    char   *name;       /* owned, non-null */
    char   *purpose;    /* owned, non-null */
    char  **member_ids; /* owned array of owned strings (may be NULL if 0) */
    size_t  member_count;
} ca_community_group_t;

void ca_community_group_free(ca_community_group_t *g);
void ca_community_group_free_array(ca_community_group_t *arr, size_t count);

/* Announcement(AnnouncementId, GroupId, Title, Body, DateTimeOffset AtUtc). */
typedef struct {
    char   *announcement_id; /* owned, non-null */
    char   *group_id;        /* owned, non-null */
    char   *title;           /* owned, non-null */
    char   *body;            /* owned, non-null */
    int64_t at_utc_ms;
} ca_community_announcement_t;

void ca_community_announcement_free(ca_community_announcement_t *a);
void ca_community_announcement_free_array(ca_community_announcement_t *arr,
                                          size_t count);

/* VolunteerOpportunity(OppId, GroupId, Description, int VolunteersNeeded,
 * DateTimeOffset WhenUtc). */
typedef struct {
    char   *opp_id;      /* owned, non-null */
    char   *group_id;    /* owned, non-null */
    char   *description; /* owned, non-null */
    int     volunteers_needed;
    int64_t when_utc_ms;
} ca_community_opportunity_t;

void ca_community_opportunity_free(ca_community_opportunity_t *o);
void ca_community_opportunity_free_array(ca_community_opportunity_t *arr,
                                         size_t count);

typedef struct ca_community_board ca_community_board_t;

ca_community_board_t *ca_community_board_create(void); /* NULL on OOM */
void ca_community_board_destroy(ca_community_board_t *b);

/* Create(g) — GroupId keyed set. 0 / -1. */
int ca_community_board_create_group(ca_community_board_t *b,
                                    const ca_community_group_t *g);

/* GetGroup(id) -> fresh owned copy into *out, true; false on miss/bad args. */
bool ca_community_board_get_group(const ca_community_board_t *b, const char *id,
                                  ca_community_group_t *out);

/* GroupsForMember(memberId) -> fresh owned array (insertion order) of groups whose
 * MemberIds contains memberId (ordinal). NULL + 0 empty; NULL + SIZE_MAX error. */
ca_community_group_t *ca_community_board_groups_for_member(
    const ca_community_board_t *b, const char *member_id, size_t *out_count);

/* Post(a) — appends. 0 / -1. */
int ca_community_board_post(ca_community_board_t *b,
                            const ca_community_announcement_t *a);

/* AnnouncementsFor(groupId, limit) -> fresh owned array newest-first by AtUtc,
 * first `limit`. limit must be > 0 (SIZE_MAX on limit<=0 / bad args). Use 20 for
 * the C# default. NULL + 0 empty. */
ca_community_announcement_t *ca_community_board_announcements_for(
    const ca_community_board_t *b, const char *group_id, int limit,
    size_t *out_count);

/* List(o) — OppId keyed set. 0 / -1. */
int ca_community_board_list_opportunity(ca_community_board_t *b,
                                        const ca_community_opportunity_t *o);

/* Opportunities(now_ms) -> fresh owned array (WhenUtc >= now) ordered by WhenUtc
 * asc. NULL + 0 empty; NULL + SIZE_MAX on error. */
ca_community_opportunity_t *ca_community_board_opportunities(
    const ca_community_board_t *b, int64_t now_ms, size_t *out_count);

#ifdef __cplusplus
}
#endif

#endif /* CIRCLE_AI_COMMUNITY_H */
