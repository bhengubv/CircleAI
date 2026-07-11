# rss_news_source.py
#
# Port of CircleAI.Integration.News/RssNewsSource.cs (C# — the EXACT spec).
#
# (Phase B3) Generic RSS 2.0 / Atom 1.0 reader. One feed = one source.
#
# The C# takes an injected ``HttpClient`` and parses the feed body as XML. The
# Python port takes an injected :class:`IHttpFetcher`; the body is served as
# response text and parsed with :mod:`xml.etree.ElementTree`.
#
# ``ParseRss`` matches un-namespaced ``<item>`` elements (RSS 2.0); ``ParseAtom``
# matches ``<entry>`` in the ``http://www.w3.org/2005/Atom`` namespace. The C#
# concatenates RSS items then Atom entries, then takes the first ``max``.

from __future__ import annotations

import re
from dataclasses import dataclass
from typing import Iterable, List, Optional
from urllib.parse import urlparse
from xml.etree import ElementTree as ET

from circle_ai.integration._util import parse_utc
from circle_ai.integration.contracts import INewsSource, NewsItem
from circle_ai.integration.http import HttpRequest, IHttpFetcher

_ATOM = "{http://www.w3.org/2005/Atom}"
_TAG_RX = re.compile(r"<[^>]+>")


def _strip(html: str) -> str:
    return _TAG_RX.sub(" ", html).strip()


def _text(el: Optional[ET.Element]) -> str:
    if el is None:
        return ""
    return el.text or ""


def _try_absolute(link: str) -> str:
    if link:
        parsed = urlparse(link)
        if parsed.scheme and parsed.netloc:
            return link
    return "about:blank"


@dataclass(frozen=True, slots=True)
class RssOptions:
    """Mirrors ``CircleAI.Integration.News.RssOptions`` — ``record(Uri FeedUrl,
    string? SourceId = null)``. ``feed_url`` is a plain ``str``.
    """

    feed_url: str
    source_id: Optional[str] = None


class RssNewsSource(INewsSource):
    """Port of ``CircleAI.Integration.News.RssNewsSource``."""

    def __init__(self, opts: RssOptions, http: IHttpFetcher) -> None:
        if opts is None:
            raise ValueError("opts must not be None")
        if http is None:
            raise ValueError("http must not be None")
        self._opts = opts
        self._http = http

    @property
    def source_id(self) -> str:
        if self._opts.source_id is not None:
            return self._opts.source_id
        # C# FeedUrl.Host — the host component of the feed URL.
        return urlparse(self._opts.feed_url).netloc

    @property
    def is_configured(self) -> bool:
        return True

    async def fetch_latest_async(self, max: int) -> List[NewsItem]:
        if max <= 0:
            raise ValueError("max must be positive")
        resp = (
            await self._http.send_async(HttpRequest("GET", self._opts.feed_url))
        ).ensure_success()
        root = ET.fromstring(resp.text)
        sid = self.source_id
        combined: List[NewsItem] = []
        combined.extend(_parse_rss(root, sid))
        combined.extend(_parse_atom(root, sid))
        return combined[:max]


def _iter_descendants(root: ET.Element, tag: str) -> Iterable[ET.Element]:
    """Match LINQ ``XDocument.Descendants(tag)`` — every descendant matching
    ``tag``, excluding the root element itself.
    """
    for el in root.iter(tag):
        if el is root:
            continue
        yield el


def _parse_rss(root: ET.Element, source_id: str) -> List[NewsItem]:
    out: List[NewsItem] = []
    for item in _iter_descendants(root, "item"):
        title = _text(item.find("title"))
        link = _text(item.find("link"))
        pub = item.find("pubDate")
        pub_val = pub.text if pub is not None else None
        desc = _text(item.find("description"))
        guid_el = item.find("guid")
        # C# ``item.Element("guid")?.Value ?? link`` — only fall back to link
        # when the element is absent (a present-but-empty guid stays "").
        guid = _text(guid_el) if guid_el is not None else link
        tags = [c.text or "" for c in item.findall("category")]
        out.append(
            NewsItem(
                item_id=guid,
                source_id=source_id,
                title=title,
                summary=_strip(desc),
                url=_try_absolute(link),
                published_utc=parse_utc(pub_val),
                tags=tags,
            )
        )
    return out


def _parse_atom(root: ET.Element, source_id: str) -> List[NewsItem]:
    out: List[NewsItem] = []
    for entry in _iter_descendants(root, _ATOM + "entry"):
        title = _text(entry.find(_ATOM + "title"))
        link_el = entry.find(_ATOM + "link")
        link = link_el.get("href", "") if link_el is not None else ""
        updated = entry.find(_ATOM + "updated")
        published = entry.find(_ATOM + "published")
        pub_val = (
            updated.text
            if updated is not None
            else (published.text if published is not None else None)
        )
        summary = entry.find(_ATOM + "summary")
        content = entry.find(_ATOM + "content")
        desc = (
            _text(summary)
            if summary is not None
            else (_text(content) if content is not None else "")
        )
        id_el = entry.find(_ATOM + "id")
        # C# ``entry.Element(atom + "id")?.Value ?? link`` — absent-only fallback.
        guid = _text(id_el) if id_el is not None else link
        tags = [
            c.get("term", "")
            for c in entry.findall(_ATOM + "category")
            if c.get("term", "")
        ]
        out.append(
            NewsItem(
                item_id=guid,
                source_id=source_id,
                title=title,
                summary=_strip(desc),
                url=_try_absolute(link),
                published_utc=parse_utc(pub_val),
                tags=tags,
            )
        )
    return out
