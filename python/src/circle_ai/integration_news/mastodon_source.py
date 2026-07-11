# mastodon_source.py
#
# Port of CircleAI.Integration.News/MastodonSource.cs (C# — the EXACT spec).
#
# (Phase B3) Mastodon public-timeline / hashtag-timeline reader. Anonymous
# access works for public timelines on most instances.
#
# The C# takes an injected ``HttpClient``; the Python port takes an injected
# :class:`IHttpFetcher` and parses the identical JSON array. HTML is stripped
# with the same ``<[^>]+>`` -> " " regex then trimmed.

from __future__ import annotations

import re
from dataclasses import dataclass
from typing import List, Optional
from urllib.parse import quote, urlparse

from circle_ai.integration._util import parse_utc
from circle_ai.integration.contracts import DATETIME_MIN, INewsSource, NewsItem
from circle_ai.integration.http import HttpRequest, IHttpFetcher

_TAG_RX = re.compile(r"<[^>]+>")


def _strip_html(html: str) -> str:
    return _TAG_RX.sub(" ", html).strip()


def _try_absolute(url: str) -> str:
    """Mirror ``Uri.TryCreate(url, UriKind.Absolute)`` — keep an absolute URL,
    else fall back to ``about:blank``.
    """
    if url:
        parsed = urlparse(url)
        if parsed.scheme and parsed.netloc:
            return url
    return "about:blank"


@dataclass(frozen=True, slots=True)
class MastodonOptions:
    """Mirrors ``CircleAI.Integration.News.MastodonOptions`` — ``record(string
    Instance, string? Hashtag = null, string? AccessToken = null)``.
    """

    instance: str
    hashtag: Optional[str] = None
    access_token: Optional[str] = None


class MastodonSource(INewsSource):
    """Port of ``CircleAI.Integration.News.MastodonSource``."""

    def __init__(self, opts: MastodonOptions, http: IHttpFetcher) -> None:
        if opts is None:
            raise ValueError("opts must not be None")
        if http is None:
            raise ValueError("http must not be None")
        self._opts = opts
        self._http = http

    @property
    def source_id(self) -> str:
        if not self._opts.hashtag:
            return f"mastodon:{self._opts.instance}:public"
        return f"mastodon:{self._opts.instance}:#{self._opts.hashtag}"

    @property
    def is_configured(self) -> bool:
        return bool(self._opts.instance and self._opts.instance.strip())

    async def fetch_latest_async(self, max: int) -> List[NewsItem]:
        if max <= 0:
            raise ValueError("max must be positive")
        if not self._opts.hashtag:
            path = f"/api/v1/timelines/public?limit={min(max, 40)}"
        else:
            path = (
                f"/api/v1/timelines/tag/{quote(self._opts.hashtag, safe='')}"
                f"?limit={min(max, 40)}"
            )
        headers = {"User-Agent": "CircleAI/1.0 (MastodonSource)"}
        if self._opts.access_token and self._opts.access_token.strip():
            headers["Authorization"] = f"Bearer {self._opts.access_token}"
        url = self._opts.instance.rstrip("/") + path
        resp = (
            await self._http.send_async(HttpRequest("GET", url, headers))
        ).ensure_success()
        root = resp.json()

        if not isinstance(root, list):
            return []
        result: List[NewsItem] = []
        for s in root:
            if not isinstance(s, dict):
                continue
            url_s = s.get("url") or ""
            content_html = s.get("content") or ""
            pub = s.get("created_at")
            tags: List[str] = []
            tags_arr = s.get("tags")
            if isinstance(tags_arr, list):
                for tg in tags_arr:
                    if isinstance(tg, dict) and "name" in tg:
                        tags.append(tg.get("name") or "")
            acct = None
            a = s.get("account")
            if isinstance(a, dict):
                acct = a.get("acct")
            text = _strip_html(content_html)
            result.append(
                NewsItem(
                    item_id=url_s,
                    source_id=acct if acct else self.source_id,
                    title=(text[:80] + "…") if len(text) > 80 else text,
                    summary=text,
                    url=_try_absolute(url_s),
                    published_utc=parse_utc(pub) if pub is not None else DATETIME_MIN,
                    tags=tags,
                )
            )
        return result
