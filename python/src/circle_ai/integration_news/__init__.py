"""circle_ai.integration_news — port of the CircleAI.Integration.News assembly.

(Phase B3) News + social feed sources: Bluesky search, Mastodon timelines,
newsapi.org/gnews.io adapters, and a generic RSS 2.0 / Atom 1.0 reader. Each is
an :class:`~circle_ai.integration.contracts.INewsSource`. C# is the exact spec.
The C# sources take an injected ``HttpClient``; the Python ports take an
injected :class:`~circle_ai.integration.http.IHttpFetcher` and parse the
identical JSON/XML so no real network is needed.

Public surface:

  * BlueskySource / BlueskyOptions
  * MastodonSource / MastodonOptions
  * NewsApiSource / NewsApiOptions
  * RssNewsSource / RssOptions
"""
from __future__ import annotations

from .bluesky_source import BlueskyOptions, BlueskySource
from .mastodon_source import MastodonOptions, MastodonSource
from .news_api_source import NewsApiOptions, NewsApiSource
from .rss_news_source import RssNewsSource, RssOptions

__all__ = [
    "BlueskySource",
    "BlueskyOptions",
    "MastodonSource",
    "MastodonOptions",
    "NewsApiSource",
    "NewsApiOptions",
    "RssNewsSource",
    "RssOptions",
]
