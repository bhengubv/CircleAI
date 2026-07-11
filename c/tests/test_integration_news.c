/*
 * test_integration_news.c — CircleAI.Integration.News (C11 port) verification of
 * the in-memory Bluesky / Mastodon / NewsApi / Rss sources.
 */

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <assert.h>
#include "circle_ai/circle_ai.h"

static ca_int_news_item_t mk_item(const char *id, int64_t pub) {
    ca_int_news_item_t n; memset(&n, 0, sizeof(n));
    n.item_id = (char *)id; n.source_id = (char *)"s";
    n.title = (char *)"T"; n.summary = (char *)"S";
    n.url = (char *)"https://x.io/a"; n.published_utc_ms = pub;
    return n;
}

static void test_source_ids_and_config(void) {
    ca_int_news_source_t *bs = ca_int_bluesky_source_create("dotnet");
    ca_int_news_source_t *bs0 = ca_int_bluesky_source_create("  ");
    ca_int_news_source_t *mp = ca_int_mastodon_source_create("https://m.io", NULL);
    ca_int_news_source_t *mh = ca_int_mastodon_source_create("https://m.io", "news");
    ca_int_news_source_t *na = ca_int_newsapi_source_create("KEY", "world");
    ca_int_news_source_t *na0 = ca_int_newsapi_source_create("", "world");
    ca_int_news_source_t *rs = ca_int_rss_source_create("example.com", NULL);
    ca_int_news_source_t *rs2 = ca_int_rss_source_create("example.com", "MyFeed");
    assert(bs && bs0 && mp && mh && na && na0 && rs && rs2);

    assert(strcmp(bs->source_id(bs->impl), "bluesky:dotnet") == 0);
    assert(strcmp(mp->source_id(mp->impl), "mastodon:https://m.io:public") == 0);
    assert(strcmp(mh->source_id(mh->impl), "mastodon:https://m.io:#news") == 0);
    assert(strcmp(na->source_id(na->impl), "newsapi:world") == 0);
    assert(strcmp(rs->source_id(rs->impl), "example.com") == 0);   /* host fallback */
    assert(strcmp(rs2->source_id(rs2->impl), "MyFeed") == 0);       /* explicit */

    assert(bs->is_configured(bs->impl) && !bs0->is_configured(bs0->impl));
    assert(mp->is_configured(mp->impl));
    assert(na->is_configured(na->impl) && !na0->is_configured(na0->impl));
    assert(rs->is_configured(rs->impl)); /* Rss always configured */

    ca_int_news_source_destroy(bs);
    ca_int_news_source_destroy(bs0);
    ca_int_news_source_destroy(mp);
    ca_int_news_source_destroy(mh);
    ca_int_news_source_destroy(na);
    ca_int_news_source_destroy(na0);
    ca_int_news_source_destroy(rs);
    ca_int_news_source_destroy(rs2);
    printf("  source_ids_and_config: ok\n");
}

static void test_fetch_latest(void) {
    ca_int_news_source_t *s = ca_int_bluesky_source_create("dotnet");
    assert(s);

    ca_int_news_item_t i1 = mk_item("i1", 100);
    ca_int_news_item_t i2 = mk_item("i2", 300);
    ca_int_news_item_t i3 = mk_item("i3", 200);
    assert(ca_int_news_seed(s, &i1) == 0);
    assert(ca_int_news_seed(s, &i2) == 0);
    assert(ca_int_news_seed(s, &i3) == 0);

    /* FetchLatest newest-first: i2(300), i3(200), i1(100). */
    size_t n = 0;
    ca_int_news_item_t *arr = s->fetch_latest(s->impl, 10, &n);
    assert(n == 3);
    assert(strcmp(arr[0].item_id, "i2") == 0);
    assert(strcmp(arr[1].item_id, "i3") == 0);
    assert(strcmp(arr[2].item_id, "i1") == 0);
    ca_int_news_item_free_array(arr, n);

    /* max caps after sort. */
    arr = s->fetch_latest(s->impl, 2, &n);
    assert(n == 2 && strcmp(arr[1].item_id, "i3") == 0);
    ca_int_news_item_free_array(arr, n);

    /* max<=0 -> error. */
    assert(s->fetch_latest(s->impl, 0, &n) == NULL && n == (size_t)-1);

    ca_int_news_source_destroy(s);
    printf("  fetch_latest: ok\n");
}

static void test_newsapi_gate(void) {
    /* NewsApi FetchLatest requires IsConfigured (InvalidOperationException). */
    ca_int_news_source_t *na0 = ca_int_newsapi_source_create("", "world");
    assert(na0);
    ca_int_news_item_t i1 = mk_item("i1", 100);
    assert(ca_int_news_seed(na0, &i1) == 0);
    size_t n = 0;
    assert(na0->fetch_latest(na0->impl, 10, &n) == NULL && n == (size_t)-1);
    ca_int_news_source_destroy(na0);

    /* configured NewsApi fetches fine. */
    ca_int_news_source_t *na = ca_int_newsapi_source_create("KEY", "world");
    assert(na);
    assert(ca_int_news_seed(na, &i1) == 0);
    ca_int_news_item_t *arr = na->fetch_latest(na->impl, 10, &n);
    assert(n == 1 && strcmp(arr[0].item_id, "i1") == 0);
    ca_int_news_item_free_array(arr, n);
    ca_int_news_source_destroy(na);
    printf("  newsapi_gate: ok\n");
}

int main(void) {
    test_source_ids_and_config();
    test_fetch_latest();
    test_newsapi_gate();
    printf("test_integration_news: all assertions passed\n");
    return 0;
}
