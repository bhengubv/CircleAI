# news_api_source.py
#
# Port of CircleAI.Integration.News/NewsApiSource.cs (C# — the EXACT spec).
#
# (Phase B3) Adapter for newsapi.org / gnews.io style REST endpoints (both
# follow the "articles" array shape).
#
# The C# takes an injected ``HttpClient``; the Python port takes an injected
# :class:`IHttpFetcher` and parses the identical JSON shape.

from __future__ import annotations

from dataclasses import dataclass
from typing import List
from urllib.parse import quote

from circle_ai.integration._util import parse_utc
from circle_ai.integration.contracts import DATETIME_MIN, INewsSource, NewsItem
from circle_ai.integration.http import HttpRequest, IHttpFetcher
from circle_ai.integration_news.mastodon_source import _try_absolute


@dataclass(frozen=True, slots=True)
class NewsApiOptions:
    """Mirrors ``CircleAI.Integration.News.NewsApiOptions`` — ``record(string
    ApiKey, string Query, string Endpoint = "https://newsapi.org/v2/everything")``.
    """

    api_key: str
    query: str
    endpoint: str = "https://newsapi.org/v2/everything"


class NewsApiSource(INewsSource):
    """Port of ``CircleAI.Integration.News.NewsApiSource``."""

    def __init__(self, opts: NewsApiOptions, http: IHttpFetcher) -> None:
        if opts is None:
            raise ValueError("opts must not be None")
        if http is None:
            raise ValueError("http must not be None")
        self._opts = opts
        self._http = http

    @property
    def source_id(self) -> str:
        return f"newsapi:{self._opts.query}"

    @property
    def is_configured(self) -> bool:
        return bool(self._opts.api_key and self._opts.api_key.strip())

    async def fetch_latest_async(self, max: int) -> List[NewsItem]:
        if max <= 0:
            raise ValueError("max must be positive")
        if not self.is_configured:
            raise RuntimeError("NewsAPI key not configured.")
        url = (
            f"{self._opts.endpoint}?q={quote(self._opts.query, safe='')}"
            f"&pageSize={min(max, 100)}&sortBy=publishedAt&language=en"
        )
        headers = {
            "X-Api-Key": self._opts.api_key,
            "User-Agent": "CircleAI/1.0 (NewsApiSource)",
        }
        resp = (
            await self._http.send_async(HttpRequest("GET", url, headers))
        ).ensure_success()
        root = resp.json()

        result: List[NewsItem] = []
        arr = root.get("articles") if isinstance(root, dict) else None
        if isinstance(arr, list):
            for a in arr:
                if not isinstance(a, dict):
                    continue
                title = a.get("title") or ""
                desc = a.get("description") or ""
                url2 = a.get("url") or ""
                pub = a.get("publishedAt")
                src = None
                s = a.get("source")
                if isinstance(s, dict):
                    src = s.get("name")
                result.append(
                    NewsItem(
                        item_id=url2,
                        source_id=src if src else self.source_id,
                        title=title,
                        summary=desc,
                        url=_try_absolute(url2),
                        published_utc=parse_utc(pub)
                        if pub is not None
                        else DATETIME_MIN,
                        tags=(),
                    )
                )
        return result
