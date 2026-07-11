#ifndef CIRCLE_AI_INTEGRATION_NEWS_H
#define CIRCLE_AI_INTEGRATION_NEWS_H

/*
 * integration_news.h — CircleAI.Integration.News (C11 port).
 *
 * Deterministic in-memory INewsSource implementations standing in for the four
 * sources (BlueskySource, MastodonSource, NewsApiSource, RssNewsSource). The real
 * sources read Bluesky AppView / Mastodon timelines / newsapi.org / RSS+Atom over
 * an injected HttpClient; here the feed is an in-memory NewsItem array (populated
 * via ca_int_news_seed — the injected network data). The SourceId/IsConfigured
 * derivations match the C# spec exactly:
 *
 *   Bluesky  SourceId "bluesky:{query}";           IsConfigured := query non-blank.
 *   Mastodon SourceId "mastodon:{instance}:public" (no hashtag) or
 *                     "mastodon:{instance}:#{hashtag}"; IsConfigured := instance
 *                     non-blank.
 *   NewsApi  SourceId "newsapi:{query}";            IsConfigured := apiKey non-blank.
 *   Rss      SourceId := sourceId ?? feedHost;      IsConfigured := true.
 *
 *   FetchLatest(max) : seeded items newest-first (PublishedUtc desc), Take(max).
 *                      max<=0 -> ArgumentOutOfRangeException (NULL + SIZE_MAX);
 *                      NewsApi additionally errors when !IsConfigured
 *                      (InvalidOperationException).
 *
 * Conventions per integration.h. Linear arrays, no pthreads. Pure C11 + libc.
 */

#include <stdbool.h>

#include "integration.h"

#ifdef __cplusplus
extern "C" {
#endif

/* Bluesky source (SourceId "bluesky:{query}"). query may be NULL. NULL on OOM. */
ca_int_news_source_t *ca_int_bluesky_source_create(const char *query);

/* Mastodon source. hashtag NULL/empty -> ":public" variant. instance may be NULL. */
ca_int_news_source_t *ca_int_mastodon_source_create(const char *instance,
                                                    const char *hashtag);

/* NewsApi source (SourceId "newsapi:{query}"). api_key/query may be NULL. */
ca_int_news_source_t *ca_int_newsapi_source_create(const char *api_key,
                                                   const char *query);

/* Rss source. source_id NULL -> feed_host is used as the SourceId. feed_host is
 * the Uri.Host of the feed URL (required for the fallback SourceId). NULL on OOM. */
ca_int_news_source_t *ca_int_rss_source_create(const char *feed_host,
                                               const char *source_id);

/* Seed a NewsItem into the source's feed (deep-copied; the injected network
 * payload). 0 success; -1 bad args/OOM. Available on all four sources. */
int ca_int_news_seed(ca_int_news_source_t *s, const ca_int_news_item_t *item);

/* Destroy any news source returned above (frees feed + vtable). */
void ca_int_news_source_destroy(ca_int_news_source_t *s);

#ifdef __cplusplus
}
#endif

#endif /* CIRCLE_AI_INTEGRATION_NEWS_H */
