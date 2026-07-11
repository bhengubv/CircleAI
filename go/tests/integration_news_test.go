// integration_news_test.go
//
// Verifies the News sources (integration_news.go) over the injected
// FakeCarrierTransport — no real network. Covers Bluesky (searchPosts parse,
// AT-URI→bsky.app URL, tag facets, 80-char title truncation), Mastodon (public +
// hashtag paths, HTML strip, auth/UA headers, non-array→empty), NewsAPI (articles
// parse, X-Api-Key header, unconfigured error), and RSS/Atom parsing + take(max).

package circleai_test

import (
	"context"
	"strings"
	"testing"
	"time"

	circleai "github.com/bhengubv/CircleAI/go"
)

// ---------------------------------------------------------------------------
// Bluesky
// ---------------------------------------------------------------------------

func TestBluesky_FetchLatest(t *testing.T) {
	tr := circleai.NewFakeCarrierTransport()
	tr.EnqueueJSON(200, `{"posts":[
		{"uri":"at://did:plc:abc/app.bsky.feed.post/rkey123",
		 "author":{"handle":"alice.bsky.social"},
		 "record":{"text":"hello world","createdAt":"2026-07-11T10:00:00Z",
		           "facets":[{"features":[{"tag":"golang"},{"tag":"news"}]}]}}
	]}`)
	s, err := circleai.NewBlueskySource(tr, circleai.BlueskyOptions{Query: "golang"})
	if err != nil {
		t.Fatalf("new: %v", err)
	}
	if s.SourceID() != "bluesky:golang" || !s.IsConfigured() {
		t.Fatalf("bluesky id/configured wrong: %q", s.SourceID())
	}
	items, err := s.FetchLatest(context.Background(), 10)
	if err != nil {
		t.Fatalf("fetch: %v", err)
	}
	if len(items) != 1 {
		t.Fatalf("expected 1 item, got %d", len(items))
	}
	it := items[0]
	if it.ItemID != "at://did:plc:abc/app.bsky.feed.post/rkey123" || it.SourceID != "alice.bsky.social" ||
		it.Title != "hello world" || it.Summary != "hello world" {
		t.Fatalf("item wrong: %+v", it)
	}
	if it.URL != "https://bsky.app/profile/alice.bsky.social/post/rkey123" {
		t.Fatalf("post url wrong: %s", it.URL)
	}
	if len(it.Tags) != 2 || it.Tags[0] != "golang" || it.Tags[1] != "news" {
		t.Fatalf("tags wrong: %+v", it.Tags)
	}
	if !it.PublishedUtc.Equal(time.Date(2026, 7, 11, 10, 0, 0, 0, time.UTC)) {
		t.Fatalf("published wrong: %v", it.PublishedUtc)
	}
	req := tr.Requests()[0]
	if !strings.Contains(req.URL, "/xrpc/app.bsky.feed.searchPosts?q=golang") || !strings.Contains(req.URL, "sort=latest") {
		t.Fatalf("bluesky url wrong: %s", req.URL)
	}
}

func TestBluesky_TitleTruncation(t *testing.T) {
	long := strings.Repeat("a", 100)
	tr := circleai.NewFakeCarrierTransport()
	tr.EnqueueJSON(200, `{"posts":[{"uri":"at://x/y","author":{"handle":"h"},"record":{"text":"`+long+`"}}]}`)
	s, _ := circleai.NewBlueskySource(tr, circleai.BlueskyOptions{Query: "q"})
	items, err := s.FetchLatest(context.Background(), 5)
	if err != nil {
		t.Fatalf("fetch: %v", err)
	}
	// 80 chars + ellipsis; summary keeps full text.
	if items[0].Title != strings.Repeat("a", 80)+"…" {
		t.Fatalf("title truncation wrong: %q", items[0].Title)
	}
	if items[0].Summary != long {
		t.Fatalf("summary should be full text")
	}
}

func TestBluesky_MaxValidation(t *testing.T) {
	s, _ := circleai.NewBlueskySource(circleai.NewFakeCarrierTransport(), circleai.BlueskyOptions{Query: "q"})
	if _, err := s.FetchLatest(context.Background(), 0); err == nil {
		t.Fatalf("max<=0 should error")
	}
}

// ---------------------------------------------------------------------------
// Mastodon
// ---------------------------------------------------------------------------

func TestMastodon_PublicTimeline(t *testing.T) {
	tr := circleai.NewFakeCarrierTransport()
	tr.EnqueueJSON(200, `[
		{"url":"https://mastodon.social/@bob/1","content":"<p>Breaking <b>news</b></p>",
		 "created_at":"2026-07-11T09:30:00Z","account":{"acct":"bob"},
		 "tags":[{"name":"news"}]}
	]`)
	s, err := circleai.NewMastodonSource(tr, circleai.MastodonOptions{Instance: "https://mastodon.social/"})
	if err != nil {
		t.Fatalf("new: %v", err)
	}
	if s.SourceID() != "mastodon:https://mastodon.social/:public" || !s.IsConfigured() {
		t.Fatalf("mastodon id wrong: %q", s.SourceID())
	}
	items, err := s.FetchLatest(context.Background(), 20)
	if err != nil {
		t.Fatalf("fetch: %v", err)
	}
	if len(items) != 1 {
		t.Fatalf("expected 1, got %d", len(items))
	}
	it := items[0]
	// HTML stripped to plain text with collapsed tags.
	if it.SourceID != "bob" || it.Summary != "Breaking  news" || it.Tags[0] != "news" ||
		it.URL != "https://mastodon.social/@bob/1" {
		t.Fatalf("mastodon item wrong: %+v", it)
	}
	req := tr.Requests()[0]
	if !strings.Contains(req.URL, "/api/v1/timelines/public?limit=20") ||
		req.Headers["User-Agent"] != "CircleAI/1.0 (MastodonSource)" {
		t.Fatalf("mastodon public url/headers wrong: %s %v", req.URL, req.Headers)
	}
	if _, ok := req.Headers["Authorization"]; ok {
		t.Fatalf("no token -> no Authorization header")
	}
}

func TestMastodon_HashtagAndAuth(t *testing.T) {
	tr := circleai.NewFakeCarrierTransport()
	tr.EnqueueJSON(200, `[]`)
	s, _ := circleai.NewMastodonSource(tr, circleai.MastodonOptions{Instance: "https://m.example", Hashtag: "golang", AccessToken: "sekret"})
	if s.SourceID() != "mastodon:https://m.example:#golang" {
		t.Fatalf("hashtag source id wrong: %q", s.SourceID())
	}
	if _, err := s.FetchLatest(context.Background(), 5); err != nil {
		t.Fatalf("fetch: %v", err)
	}
	req := tr.Requests()[0]
	if !strings.Contains(req.URL, "/api/v1/timelines/tag/golang?limit=5") || req.Headers["Authorization"] != "Bearer sekret" {
		t.Fatalf("hashtag url/auth wrong: %s %v", req.URL, req.Headers)
	}
}

func TestMastodon_NonArrayBodyEmpty(t *testing.T) {
	tr := circleai.NewFakeCarrierTransport()
	tr.EnqueueJSON(200, `{"error":"nope"}`)
	s, _ := circleai.NewMastodonSource(tr, circleai.MastodonOptions{Instance: "https://m"})
	items, err := s.FetchLatest(context.Background(), 5)
	if err != nil {
		t.Fatalf("fetch: %v", err)
	}
	if len(items) != 0 {
		t.Fatalf("non-array body should yield empty, got %+v", items)
	}
}

// ---------------------------------------------------------------------------
// NewsAPI
// ---------------------------------------------------------------------------

func TestNewsApi_FetchLatest(t *testing.T) {
	tr := circleai.NewFakeCarrierTransport()
	tr.EnqueueJSON(200, `{"articles":[
		{"title":"Headline","description":"summary text","url":"https://news.example/a",
		 "publishedAt":"2026-07-11T07:00:00Z","source":{"name":"Example News"}}
	]}`)
	s, err := circleai.NewNewsApiSource(tr, circleai.NewsApiOptions{APIKey: "key123", Query: "bitcoin"})
	if err != nil {
		t.Fatalf("new: %v", err)
	}
	if s.SourceID() != "newsapi:bitcoin" || !s.IsConfigured() {
		t.Fatalf("newsapi id/configured wrong")
	}
	items, err := s.FetchLatest(context.Background(), 10)
	if err != nil {
		t.Fatalf("fetch: %v", err)
	}
	if len(items) != 1 {
		t.Fatalf("expected 1, got %d", len(items))
	}
	it := items[0]
	if it.Title != "Headline" || it.Summary != "summary text" || it.SourceID != "Example News" ||
		it.URL != "https://news.example/a" || it.ItemID != "https://news.example/a" {
		t.Fatalf("newsapi item wrong: %+v", it)
	}
	req := tr.Requests()[0]
	if !strings.Contains(req.URL, "https://newsapi.org/v2/everything?q=bitcoin") ||
		!strings.Contains(req.URL, "sortBy=publishedAt") || req.Headers["X-Api-Key"] != "key123" {
		t.Fatalf("newsapi url/headers wrong: %s %v", req.URL, req.Headers)
	}
}

func TestNewsApi_Unconfigured(t *testing.T) {
	tr := circleai.NewFakeCarrierTransport()
	s, _ := circleai.NewNewsApiSource(tr, circleai.NewsApiOptions{APIKey: "", Query: "x"})
	if s.IsConfigured() {
		t.Fatalf("blank key should be unconfigured")
	}
	if _, err := s.FetchLatest(context.Background(), 5); err == nil {
		t.Fatalf("unconfigured fetch should error")
	}
	if len(tr.Requests()) != 0 {
		t.Fatalf("unconfigured fetch should not issue a request")
	}
}

// ---------------------------------------------------------------------------
// RSS / Atom
// ---------------------------------------------------------------------------

func TestRss_ParsesRssItems(t *testing.T) {
	tr := circleai.NewFakeCarrierTransport()
	tr.EnqueueJSON(200, `<?xml version="1.0"?><rss version="2.0"><channel>
		<title>Feed</title>
		<item><title>First</title><link>https://ex.com/1</link>
			<pubDate>Fri, 11 Jul 2026 06:00:00 GMT</pubDate>
			<description>&lt;p&gt;hello&lt;/p&gt;</description>
			<guid>guid-1</guid><category>tech</category><category>go</category></item>
		<item><title>Second</title><link>https://ex.com/2</link><description>plain</description></item>
	</channel></rss>`)
	s, err := circleai.NewRssNewsSource(tr, circleai.RssOptions{FeedURL: "https://ex.com/rss.xml"})
	if err != nil {
		t.Fatalf("new: %v", err)
	}
	if s.SourceID() != "ex.com" || !s.IsConfigured() {
		t.Fatalf("rss source id wrong: %q", s.SourceID())
	}
	items, err := s.FetchLatest(context.Background(), 10)
	if err != nil {
		t.Fatalf("fetch: %v", err)
	}
	if len(items) != 2 {
		t.Fatalf("expected 2 items, got %d", len(items))
	}
	first := items[0]
	if first.ItemID != "guid-1" || first.Title != "First" || first.Summary != "hello" ||
		first.URL != "https://ex.com/1" || len(first.Tags) != 2 || first.Tags[0] != "tech" {
		t.Fatalf("rss first item wrong: %+v", first)
	}
	if first.PublishedUtc.IsZero() {
		t.Fatalf("rss pubDate not parsed: %v", first.PublishedUtc)
	}
	// Second item: guid falls back to link.
	if items[1].ItemID != "https://ex.com/2" {
		t.Fatalf("rss second guid fallback wrong: %q", items[1].ItemID)
	}
}

func TestRss_ParsesAtomEntries(t *testing.T) {
	tr := circleai.NewFakeCarrierTransport()
	tr.EnqueueJSON(200, `<?xml version="1.0" encoding="utf-8"?>
	<feed xmlns="http://www.w3.org/2005/Atom">
		<title>Atom Feed</title>
		<entry>
			<title>Atom One</title>
			<link href="https://atom.example/1"/>
			<updated>2026-07-11T05:00:00Z</updated>
			<summary>the summary</summary>
			<id>tag:atom,1</id>
			<category term="science"/>
		</entry>
	</feed>`)
	s, _ := circleai.NewRssNewsSource(tr, circleai.RssOptions{FeedURL: "https://atom.example/feed", SourceID: "atom-custom"})
	if s.SourceID() != "atom-custom" {
		t.Fatalf("custom source id override failed: %q", s.SourceID())
	}
	items, err := s.FetchLatest(context.Background(), 10)
	if err != nil {
		t.Fatalf("fetch: %v", err)
	}
	if len(items) != 1 {
		t.Fatalf("expected 1 atom entry, got %d", len(items))
	}
	it := items[0]
	if it.ItemID != "tag:atom,1" || it.Title != "Atom One" || it.Summary != "the summary" ||
		it.URL != "https://atom.example/1" || it.SourceID != "atom-custom" || it.Tags[0] != "science" {
		t.Fatalf("atom item wrong: %+v", it)
	}
	if !it.PublishedUtc.Equal(time.Date(2026, 7, 11, 5, 0, 0, 0, time.UTC)) {
		t.Fatalf("atom updated wrong: %v", it.PublishedUtc)
	}
}

func TestRss_TakesMax(t *testing.T) {
	tr := circleai.NewFakeCarrierTransport()
	tr.EnqueueJSON(200, `<rss><channel>
		<item><title>1</title><link>https://e/1</link></item>
		<item><title>2</title><link>https://e/2</link></item>
		<item><title>3</title><link>https://e/3</link></item>
	</channel></rss>`)
	s, _ := circleai.NewRssNewsSource(tr, circleai.RssOptions{FeedURL: "https://e/rss"})
	items, err := s.FetchLatest(context.Background(), 2)
	if err != nil {
		t.Fatalf("fetch: %v", err)
	}
	if len(items) != 2 {
		t.Fatalf("take(max) failed: got %d", len(items))
	}
}
