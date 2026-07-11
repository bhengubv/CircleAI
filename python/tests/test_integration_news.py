"""test_integration_news.py

Verifies the CircleAI.Integration.News port: Bluesky search, Mastodon timelines
(public + hashtag), newsapi.org adapter, and the RSS 2.0 / Atom 1.0 reader.
C# is the spec — title truncation, HTML stripping, url fallbacks, source ids.
"""
from __future__ import annotations

import json

import pytest

from circle_ai.integration import DATETIME_MIN, InMemoryHttpFetcher, HttpResponse
from circle_ai.integration_news import (
    BlueskyOptions,
    BlueskySource,
    MastodonOptions,
    MastodonSource,
    NewsApiOptions,
    NewsApiSource,
    RssNewsSource,
    RssOptions,
)


# -- Bluesky ---------------------------------------------------------------


async def test_bluesky_parses_post_and_builds_url() -> None:
    payload = {
        "posts": [
            {
                "uri": "at://did:plc:abc/app.bsky.feed.post/xyz",
                "record": {
                    "text": "Hello mesh world",
                    "createdAt": "2024-03-01T00:00:00Z",
                    "facets": [{"features": [{"tag": "mesh"}, {"tag": "p2p"}]}],
                },
                "author": {"handle": "alice.bsky.social"},
            }
        ]
    }
    f = InMemoryHttpFetcher().on_get(HttpResponse(200, json.dumps(payload)))
    src = BlueskySource(BlueskyOptions("mesh"), f)
    assert src.source_id == "bluesky:mesh"
    assert src.is_configured is True
    items = await src.fetch_latest_async(10)
    assert len(items) == 1
    it = items[0]
    assert it.item_id == "at://did:plc:abc/app.bsky.feed.post/xyz"
    assert it.source_id == "alice.bsky.social"  # author handle wins
    assert it.summary == "Hello mesh world"
    assert it.url == "https://bsky.app/profile/alice.bsky.social/post/xyz"
    assert list(it.tags) == ["mesh", "p2p"]
    assert it.published_utc.isoformat() == "2024-03-01T00:00:00+00:00"


async def test_bluesky_truncates_long_title() -> None:
    long_text = "x" * 100
    payload = {
        "posts": [
            {"uri": "at://d/app.bsky.feed.post/k", "record": {"text": long_text}}
        ]
    }
    f = InMemoryHttpFetcher().on_get(HttpResponse(200, json.dumps(payload)))
    src = BlueskySource(BlueskyOptions("q"), f)
    it = (await src.fetch_latest_async(5))[0]
    assert it.title == "x" * 80 + "…"
    assert it.summary == long_text
    # No author -> source id falls back to the source's own id.
    assert it.source_id == "bluesky:q"


async def test_bluesky_no_posts_key_returns_empty() -> None:
    f = InMemoryHttpFetcher().on_get(HttpResponse(200, json.dumps({})))
    src = BlueskySource(BlueskyOptions("q"), f)
    assert await src.fetch_latest_async(5) == []


async def test_bluesky_rejects_nonpositive_max() -> None:
    src = BlueskySource(BlueskyOptions("q"), InMemoryHttpFetcher())
    with pytest.raises(ValueError):
        await src.fetch_latest_async(0)


# -- Mastodon --------------------------------------------------------------


async def test_mastodon_public_timeline_strips_html() -> None:
    payload = [
        {
            "url": "https://mastodon.social/@bob/1",
            "content": "<p>Hello <b>world</b></p>",
            "created_at": "2024-02-01T10:00:00.000Z",
            "tags": [{"name": "intro"}],
            "account": {"acct": "bob"},
        }
    ]
    f = InMemoryHttpFetcher().on_get(HttpResponse(200, json.dumps(payload)))
    src = MastodonSource(MastodonOptions("https://mastodon.social/"), f)
    assert src.source_id == "mastodon:https://mastodon.social/:public"
    items = await src.fetch_latest_async(5)
    it = items[0]
    assert it.summary == "Hello  world"
    assert it.source_id == "bob"
    assert it.url == "https://mastodon.social/@bob/1"
    assert list(it.tags) == ["intro"]
    assert "/api/v1/timelines/public?limit=5" in f.last_request.url


async def test_mastodon_hashtag_source_id_and_path() -> None:
    f = InMemoryHttpFetcher().on_get(HttpResponse(200, "[]"))
    src = MastodonSource(
        MastodonOptions("https://mas.to", hashtag="opensource"), f
    )
    assert src.source_id == "mastodon:https://mas.to:#opensource"
    await src.fetch_latest_async(5)
    assert "/api/v1/timelines/tag/opensource" in f.last_request.url


async def test_mastodon_access_token_sets_bearer() -> None:
    f = InMemoryHttpFetcher().on_get(HttpResponse(200, "[]"))
    src = MastodonSource(
        MastodonOptions("https://mas.to", access_token="secret"), f
    )
    await src.fetch_latest_async(5)
    assert f.last_request.headers["Authorization"] == "Bearer secret"
    assert "MastodonSource" in f.last_request.headers["User-Agent"]


async def test_mastodon_non_array_returns_empty() -> None:
    f = InMemoryHttpFetcher().on_get(HttpResponse(200, json.dumps({"error": "x"})))
    src = MastodonSource(MastodonOptions("https://mas.to"), f)
    assert await src.fetch_latest_async(5) == []


# -- NewsAPI ---------------------------------------------------------------


async def test_newsapi_parses_articles() -> None:
    payload = {
        "articles": [
            {
                "title": "Big News",
                "description": "Something happened",
                "url": "https://news.example.com/1",
                "publishedAt": "2024-04-01T08:00:00Z",
                "source": {"name": "Example Times"},
            }
        ]
    }
    f = InMemoryHttpFetcher().on_get(HttpResponse(200, json.dumps(payload)))
    src = NewsApiSource(NewsApiOptions("key123", "elections"), f)
    assert src.source_id == "newsapi:elections"
    assert src.is_configured is True
    items = await src.fetch_latest_async(10)
    it = items[0]
    assert it.title == "Big News"
    assert it.summary == "Something happened"
    assert it.source_id == "Example Times"
    assert it.url == "https://news.example.com/1"
    assert list(it.tags) == []
    assert f.last_request.headers["X-Api-Key"] == "key123"


async def test_newsapi_unconfigured_raises() -> None:
    src = NewsApiSource(NewsApiOptions("", "q"), InMemoryHttpFetcher())
    assert src.is_configured is False
    with pytest.raises(RuntimeError):
        await src.fetch_latest_async(5)


async def test_newsapi_bad_url_falls_back_to_about_blank() -> None:
    payload = {"articles": [{"title": "t", "url": "not-a-url"}]}
    f = InMemoryHttpFetcher().on_get(HttpResponse(200, json.dumps(payload)))
    src = NewsApiSource(NewsApiOptions("k", "q"), f)
    it = (await src.fetch_latest_async(5))[0]
    assert it.url == "about:blank"
    assert it.published_utc == DATETIME_MIN  # no publishedAt


# -- RSS / Atom ------------------------------------------------------------

_RSS = """<?xml version="1.0"?>
<rss version="2.0"><channel>
  <title>Feed</title>
  <item>
    <title>Item One</title>
    <link>https://blog.example.com/one</link>
    <description>&lt;p&gt;Body one&lt;/p&gt;</description>
    <pubDate>Mon, 01 Jan 2024 12:00:00 GMT</pubDate>
    <guid>guid-1</guid>
    <category>tech</category>
    <category>news</category>
  </item>
  <item>
    <title>Item Two</title>
    <link>https://blog.example.com/two</link>
    <description>Body two</description>
  </item>
</channel></rss>
"""

_ATOM = """<?xml version="1.0"?>
<feed xmlns="http://www.w3.org/2005/Atom">
  <title>Atom Feed</title>
  <entry>
    <title>Atom Item</title>
    <link href="https://atom.example.com/a"/>
    <updated>2024-05-01T00:00:00Z</updated>
    <summary>Atom summary</summary>
    <id>atom-id-1</id>
    <category term="cat1"/>
    <category term=""/>
  </entry>
</feed>
"""


async def test_rss_parses_items_and_source_id_from_host() -> None:
    f = InMemoryHttpFetcher().on_get(HttpResponse(200, _RSS))
    src = RssNewsSource(RssOptions("https://blog.example.com/feed.xml"), f)
    assert src.source_id == "blog.example.com"
    assert src.is_configured is True
    items = await src.fetch_latest_async(10)
    assert len(items) == 2
    one = items[0]
    assert one.title == "Item One"
    assert one.item_id == "guid-1"
    assert one.summary == "Body one"  # html stripped
    assert one.url == "https://blog.example.com/one"
    assert list(one.tags) == ["tech", "news"]
    assert one.published_utc.isoformat() == "2024-01-01T12:00:00+00:00"
    # second item: guid absent -> falls back to link.
    assert items[1].item_id == "https://blog.example.com/two"


async def test_rss_explicit_source_id_overrides_host() -> None:
    f = InMemoryHttpFetcher().on_get(HttpResponse(200, _RSS))
    src = RssNewsSource(
        RssOptions("https://blog.example.com/feed.xml", source_id="MyBlog"), f
    )
    assert src.source_id == "MyBlog"
    items = await src.fetch_latest_async(10)
    assert items[0].source_id == "MyBlog"


async def test_rss_take_limits_results() -> None:
    f = InMemoryHttpFetcher().on_get(HttpResponse(200, _RSS))
    src = RssNewsSource(RssOptions("https://blog.example.com/feed.xml"), f)
    items = await src.fetch_latest_async(1)
    assert len(items) == 1


async def test_atom_feed_parsed() -> None:
    f = InMemoryHttpFetcher().on_get(HttpResponse(200, _ATOM))
    src = RssNewsSource(RssOptions("https://atom.example.com/feed"), f)
    items = await src.fetch_latest_async(10)
    assert len(items) == 1
    it = items[0]
    assert it.title == "Atom Item"
    assert it.item_id == "atom-id-1"
    assert it.summary == "Atom summary"
    assert it.url == "https://atom.example.com/a"
    assert list(it.tags) == ["cat1"]  # empty term filtered out
    assert it.published_utc.isoformat() == "2024-05-01T00:00:00+00:00"
