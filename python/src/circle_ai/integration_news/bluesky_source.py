# bluesky_source.py
#
# Port of CircleAI.Integration.News/BlueskySource.cs (C# — the EXACT spec).
#
# (Phase B3) Bluesky AT-protocol "app.bsky.feed.searchPosts" endpoint reader.
# Pulls posts matching a user-supplied query from the public AppView. Anonymous
# reads work for keyword search; following a user's feed requires session auth
# (left to the host).
#
# The C# takes an injected ``HttpClient``; the Python port takes an injected
# :class:`IHttpFetcher` and parses the identical JSON shape.

from __future__ import annotations

from dataclasses import dataclass
from typing import List, Optional
from urllib.parse import quote

from circle_ai.integration._util import parse_utc
from circle_ai.integration.contracts import DATETIME_MIN, INewsSource, NewsItem
from circle_ai.integration.http import HttpRequest, IHttpFetcher


@dataclass(frozen=True, slots=True)
class BlueskyOptions:
    """Mirrors ``CircleAI.Integration.News.BlueskyOptions`` — ``record(string
    Query, string Host = "https://public.api.bsky.app")``.
    """

    query: str
    host: str = "https://public.api.bsky.app"


class BlueskySource(INewsSource):
    """Port of ``CircleAI.Integration.News.BlueskySource``."""

    def __init__(self, opts: BlueskyOptions, http: IHttpFetcher) -> None:
        if opts is None:
            raise ValueError("opts must not be None")
        if http is None:
            raise ValueError("http must not be None")
        self._opts = opts
        self._http = http

    @property
    def source_id(self) -> str:
        return f"bluesky:{self._opts.query}"

    @property
    def is_configured(self) -> bool:
        return bool(self._opts.query and self._opts.query.strip())

    async def fetch_latest_async(self, max: int) -> List[NewsItem]:
        if max <= 0:
            raise ValueError("max must be positive")
        url = (
            f"{self._opts.host}/xrpc/app.bsky.feed.searchPosts"
            f"?q={quote(self._opts.query, safe='')}&limit={min(max, 100)}&sort=latest"
        )
        resp = (await self._http.send_async(HttpRequest("GET", url))).ensure_success()
        root = resp.json()

        result: List[NewsItem] = []
        arr = root.get("posts") if isinstance(root, dict) else None
        if not isinstance(arr, list):
            return result
        for post in arr:
            if not isinstance(post, dict):
                continue
            uri = post.get("uri") or ""
            record = post.get("record")
            record = record if isinstance(record, dict) else None
            text = (record.get("text") or "") if record is not None else ""
            ts = record.get("createdAt") if record is not None else None
            author = None
            a = post.get("author")
            if isinstance(a, dict):
                author = a.get("handle")
            tags: List[str] = []
            if record is not None:
                facets = record.get("facets")
                if isinstance(facets, list):
                    for f in facets:
                        if not isinstance(f, dict):
                            continue
                        feats = f.get("features")
                        if isinstance(feats, list):
                            for feat in feats:
                                if isinstance(feat, dict) and "tag" in feat:
                                    tags.append(feat.get("tag") or "")
            published_utc = parse_utc(ts) if ts is not None else DATETIME_MIN
            result.append(
                NewsItem(
                    item_id=uri,
                    source_id=author if author else self.source_id,
                    title=(text[:80] + "…") if len(text) > 80 else text,
                    summary=text,
                    url=_build_post_url(author, uri),
                    published_utc=published_utc,
                    tags=tags,
                )
            )
        return result


def _build_post_url(handle: Optional[str], at_uri: str) -> str:
    """Mirror the C# ``BuildPostUrl`` helper.

    ``at://did:plc:.../app.bsky.feed.post/<rkey>`` ->
    ``https://bsky.app/profile/<handle>/post/<rkey>``. Returns ``about:blank``
    when the handle/URI is missing or malformed.
    """
    if not (handle and handle.strip()) or not (at_uri and at_uri.strip()):
        return "about:blank"
    idx = at_uri.rfind("/")
    if idx < 0 or idx == len(at_uri) - 1:
        return "about:blank"
    rkey = at_uri[idx + 1 :]
    return f"https://bsky.app/profile/{handle}/post/{rkey}"
