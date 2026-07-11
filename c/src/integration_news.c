/*
 * integration_news.c — CircleAI.Integration.News (C11 port).
 *
 * In-memory INewsSource backends for Bluesky / Mastodon / NewsApi / Rss. Each
 * carries the SourceId string the C# property computes from its options, plus an
 * in-memory NewsItem feed seeded via ca_int_news_seed (the injected network
 * data). FetchLatest returns items newest-first, Take(max). Pure C11 + libc.
 * No pthreads.
 */

#include "circle_ai/integration_news.h"
#include "board_common.h"
#include <stdio.h>

typedef struct {
    char               *source_id;   /* owned; the computed SourceId */
    bool                configured;
    bool                gate_config;  /* NewsApi: FetchLatest requires IsConfigured */
    ca_int_news_item_t *items;
    size_t              count, cap;
} news_impl_t;

static const char *news_source_id(void *impl) {
    return ((news_impl_t *)impl)->source_id;
}

static bool news_is_configured(void *impl) {
    return ((news_impl_t *)impl)->configured;
}

/* Stable descending sort of collected indices by PublishedUtc. */
static void news_sort_pub_desc(const news_impl_t *m, size_t *idx, size_t n) {
    for (size_t i = 1; i < n; ++i) {
        size_t key = idx[i];
        int64_t kt = m->items[key].published_utc_ms;
        size_t j = i;
        while (j > 0 && m->items[idx[j - 1]].published_utc_ms < kt) {
            idx[j] = idx[j - 1];
            j--;
        }
        idx[j] = key;
    }
}

static ca_int_news_item_t *news_fetch_latest(void *impl, int max,
                                             size_t *out_count) {
    if (!out_count) return NULL;
    news_impl_t *m = (news_impl_t *)impl;
    if (max <= 0) { *out_count = (size_t)-1; return NULL; }
    if (m->gate_config && !m->configured) { /* NewsApi InvalidOperationException */
        *out_count = (size_t)-1;
        return NULL;
    }
    if (m->count == 0) { *out_count = 0; return NULL; }

    size_t *idx = (size_t *)malloc(m->count * sizeof(size_t));
    if (!idx) { *out_count = (size_t)-1; return NULL; }
    for (size_t i = 0; i < m->count; ++i) idx[i] = i;
    news_sort_pub_desc(m, idx, m->count);
    size_t n = m->count;
    if ((size_t)max < n) n = (size_t)max;

    ca_int_news_item_t *out = (ca_int_news_item_t *)calloc(n, sizeof(*out));
    if (!out) { free(idx); *out_count = (size_t)-1; return NULL; }
    for (size_t i = 0; i < n; ++i) {
        if (!ca_int_news_item_copy(&out[i], &m->items[idx[i]])) {
            ca_int_news_item_free_array(out, i);
            free(idx);
            *out_count = (size_t)-1;
            return NULL;
        }
    }
    free(idx);
    *out_count = n;
    return out;
}

int ca_int_news_seed(ca_int_news_source_t *s, const ca_int_news_item_t *item) {
    if (!s || !item) return -1;
    news_impl_t *m = (news_impl_t *)s->impl;
    ca_int_news_item_t copy;
    if (!ca_int_news_item_copy(&copy, item)) return -1;
    if (m->count == m->cap) {
        size_t nc = m->cap ? m->cap * 2 : 4;
        void *nb = realloc(m->items, nc * sizeof(*m->items));
        if (!nb) { ca_int_news_item_free(&copy); return -1; }
        m->items = (ca_int_news_item_t *)nb;
        m->cap = nc;
    }
    m->items[m->count++] = copy;
    return 0;
}

/* ── construction ───────────────────────────────────────────────────────── */

/* Build a source over an owned source_id string (takes ownership). NULL on OOM
 * (frees source_id). */
static ca_int_news_source_t *news_new(char *source_id, bool configured,
                                      bool gate_config) {
    if (!source_id) return NULL;
    news_impl_t *m = (news_impl_t *)calloc(1, sizeof(news_impl_t));
    if (!m) { free(source_id); return NULL; }
    m->source_id   = source_id;
    m->configured  = configured;
    m->gate_config = gate_config;

    ca_int_news_source_t *s = (ca_int_news_source_t *)calloc(1, sizeof(*s));
    if (!s) { free(source_id); free(m); return NULL; }
    s->impl          = m;
    s->source_id     = news_source_id;
    s->is_configured = news_is_configured;
    s->fetch_latest  = news_fetch_latest;
    return s;
}

/* asprintf-free concat helper: "{prefix}{a}". NULL on OOM. */
static char *cat2(const char *prefix, const char *a) {
    size_t la = a ? strlen(a) : 0, lp = strlen(prefix);
    char *r = (char *)malloc(lp + la + 1);
    if (!r) return NULL;
    memcpy(r, prefix, lp);
    if (la) memcpy(r + lp, a, la);
    r[lp + la] = '\0';
    return r;
}

ca_int_news_source_t *ca_int_bluesky_source_create(const char *query) {
    char *sid = cat2("bluesky:", query ? query : "");
    bool configured = !cab_is_ws(query);
    return news_new(sid, configured, false);
}

ca_int_news_source_t *ca_int_mastodon_source_create(const char *instance,
                                                    const char *hashtag) {
    const char *inst = instance ? instance : "";
    char *sid;
    if (hashtag == NULL || hashtag[0] == '\0') {
        /* "mastodon:{instance}:public" */
        size_t need = strlen("mastodon:") + strlen(inst) + strlen(":public") + 1;
        sid = (char *)malloc(need);
        if (sid) snprintf(sid, need, "mastodon:%s:public", inst);
    } else {
        /* "mastodon:{instance}:#{hashtag}" */
        size_t need = strlen("mastodon:") + strlen(inst) + 2 + strlen(hashtag) + 1;
        sid = (char *)malloc(need);
        if (sid) snprintf(sid, need, "mastodon:%s:#%s", inst, hashtag);
    }
    bool configured = !cab_is_ws(instance);
    return news_new(sid, configured, false);
}

ca_int_news_source_t *ca_int_newsapi_source_create(const char *api_key,
                                                   const char *query) {
    char *sid = cat2("newsapi:", query ? query : "");
    bool configured = !cab_is_ws(api_key);
    return news_new(sid, configured, true); /* FetchLatest gated on IsConfigured */
}

ca_int_news_source_t *ca_int_rss_source_create(const char *feed_host,
                                               const char *source_id) {
    /* SourceId := source_id ?? feed_host (C# _opts.SourceId ?? _opts.FeedUrl.Host). */
    const char *pick = source_id ? source_id : (feed_host ? feed_host : "");
    char *sid = cab_strdup_empty(pick);
    return news_new(sid, true, false); /* Rss IsConfigured is always true */
}

void ca_int_news_source_destroy(ca_int_news_source_t *s) {
    if (!s) return;
    news_impl_t *m = (news_impl_t *)s->impl;
    if (m) {
        free(m->source_id);
        for (size_t i = 0; i < m->count; ++i) ca_int_news_item_free(&m->items[i]);
        free(m->items);
        free(m);
    }
    free(s);
}
