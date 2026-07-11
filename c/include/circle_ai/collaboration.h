#ifndef CIRCLE_AI_COLLABORATION_H
#define CIRCLE_AI_COLLABORATION_H

/*
 * collaboration.h — CircleAI.Collaboration (C11 port of Contracts.cs +
 * InMemoryCollaboration.cs + NullImplementations.cs).
 *
 *   Records : Channel(ChannelId, Name, TeamId);
 *             Message(MessageId, ChannelId, AuthorId, Body,
 *                     DateTimeOffset AtUtc);
 *             PresenceState(UserId, bool Online, DateTimeOffset LastSeenUtc).
 *   Channels: IChannelStore -> InMemoryChannelStore — Upsert(c) (ChannelId
 *               keyed), Get(id) -> channel? (id required), ListForTeam(teamId)
 *               where TeamId matches, ordered by Name asc (teamId required).
 *               BackendId "in-memory". Null store -> Get null, list empty.
 *   Messages: IMessageStore -> InMemoryMessageStore — Post(msg) appends per
 *               ChannelId (ChannelId required), Read(channelId, limit=100)
 *               newest-first by AtUtc, Take(limit) (channelId required).
 *               BackendId "in-memory". Null store -> Post echoes, Read empty.
 *   Presence: IPresence -> InMemoryPresence — Set(s) (UserId keyed), Get(userId)
 *               -> presence? (userId required). BackendId "in-memory". Null ->
 *               Get null.
 *
 * Conventions: ca_ prefix, _t types, opaque handles, strdup-owning fields with
 * matching *_free, deep-copy getters, errors via NULL / count SIZE_MAX. AtUtc /
 * LastSeenUtc as int64 Unix ms UTC. Linear arrays, no pthreads. Pure C11 + libc.
 */

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

/* Channel(ChannelId, Name, TeamId). */
typedef struct {
    char *channel_id; /* owned, non-null */
    char *name;       /* owned, non-null */
    char *team_id;    /* owned, non-null */
} ca_collab_channel_t;

void ca_collab_channel_free(ca_collab_channel_t *c);
void ca_collab_channel_free_array(ca_collab_channel_t *arr, size_t count);

/* Message(MessageId, ChannelId, AuthorId, Body, AtUtc). */
typedef struct {
    char   *message_id; /* owned, non-null */
    char   *channel_id; /* owned, non-null */
    char   *author_id;  /* owned, non-null */
    char   *body;       /* owned, non-null */
    int64_t at_utc_ms;
} ca_collab_message_t;

void ca_collab_message_free(ca_collab_message_t *m);
void ca_collab_message_free_array(ca_collab_message_t *arr, size_t count);

/* PresenceState(UserId, bool Online, LastSeenUtc). */
typedef struct {
    char   *user_id;  /* owned, non-null */
    bool    online;
    int64_t last_seen_utc_ms;
} ca_collab_presence_t;

void ca_collab_presence_free(ca_collab_presence_t *p);

/* ── IChannelStore -> InMemoryChannelStore ──────────────────────────────── */

typedef struct ca_collab_channel_store ca_collab_channel_store_t;

ca_collab_channel_store_t *ca_collab_channel_store_create(void); /* NULL on OOM */
void ca_collab_channel_store_destroy(ca_collab_channel_store_t *s);
const char *ca_collab_channel_store_backend_id(const ca_collab_channel_store_t *s);

/* Upsert(c) — ChannelId keyed (replace). 0 / -1. */
int ca_collab_channel_store_upsert(ca_collab_channel_store_t *s,
                                   const ca_collab_channel_t *c);
/* Get(id) -> fresh copy into *out, true; false on miss / bad args (id required). */
bool ca_collab_channel_store_get(const ca_collab_channel_store_t *s,
                                 const char *id, ca_collab_channel_t *out);
/* ListForTeam(teamId) -> fresh owned array (*out_count) where TeamId matches,
 * ordered by Name asc. NULL + 0 empty; NULL + SIZE_MAX on error (teamId
 * required). */
ca_collab_channel_t *ca_collab_channel_store_list_for_team(
    const ca_collab_channel_store_t *s, const char *team_id, size_t *out_count);

const char *ca_collab_null_channel_store_backend_id(void); /* "null" */

/* ── IMessageStore -> InMemoryMessageStore ──────────────────────────────── */

typedef struct ca_collab_message_store ca_collab_message_store_t;

ca_collab_message_store_t *ca_collab_message_store_create(void); /* NULL on OOM */
void ca_collab_message_store_destroy(ca_collab_message_store_t *s);
const char *ca_collab_message_store_backend_id(const ca_collab_message_store_t *s);

/* Post(msg) — appends under ChannelId. 0 / -1 on bad args (null / empty
 * ChannelId) or OOM. */
int ca_collab_message_store_post(ca_collab_message_store_t *s,
                                 const ca_collab_message_t *msg);
/* Read(channelId, limit) newest-first by AtUtc, Take(limit). NULL + 0 empty;
 * NULL + SIZE_MAX on error (channelId required). limit <= 0 yields empty. */
ca_collab_message_t *ca_collab_message_store_read(
    const ca_collab_message_store_t *s, const char *channel_id, int limit,
    size_t *out_count);

const char *ca_collab_null_message_store_backend_id(void); /* "null" */

/* ── IPresence -> InMemoryPresence ──────────────────────────────────────── */

typedef struct ca_collab_presence_store ca_collab_presence_store_t;

ca_collab_presence_store_t *ca_collab_presence_store_create(void); /* NULL on OOM */
void ca_collab_presence_store_destroy(ca_collab_presence_store_t *s);
const char *ca_collab_presence_store_backend_id(const ca_collab_presence_store_t *s);

/* Set(s) — UserId keyed (replace). 0 / -1. */
int ca_collab_presence_store_set(ca_collab_presence_store_t *s,
                                 const ca_collab_presence_t *state);
/* Get(userId) -> fresh copy into *out, true; false on miss / bad args (userId
 * required). */
bool ca_collab_presence_store_get(const ca_collab_presence_store_t *s,
                                  const char *user_id, ca_collab_presence_t *out);

const char *ca_collab_null_presence_backend_id(void); /* "null" */

#ifdef __cplusplus
}
#endif

#endif /* CIRCLE_AI_COLLABORATION_H */
