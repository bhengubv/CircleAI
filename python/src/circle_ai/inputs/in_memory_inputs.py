# in_memory_inputs.py
#
# Port of CircleAI.Inputs InMemoryInputs.cs (C# — the EXACT spec).
#
# (3.3.0) Real input adapters that work offline:
#   • HttpHtmlScraper   — fetches HTML via an injected fetcher seam and strips it
#     to text (regex script/style removal + tag removal + whitespace collapse +
#     HTML-entity decode), extracting <title> and resolving hrefs to absolute URLs.
#   • StealthHttpClient — swaps a rotating set of browser-fingerprint headers per
#     call and records the headers used (deterministic, no network).
#   • DefaultMcpWebScrape — wraps an IWebScraper (defaults to HttpHtmlScraper).
#   • AsciinemaTerminalCast — asciinema v2 parser: header line (width/height) +
#     line-delimited [time, type, data] events; keeps only "o" (output) events.
#
# The C# scrapers use System.Net.Http.HttpClient. Per the port rules the network
# dependency is injected behind an IHttpFetcher seam (reused from the integration
# package); the default in-memory fetcher makes the scrapers deterministic and
# network-free while the HTML→text logic is ported faithfully.

from __future__ import annotations

import html
import json
import re
import urllib.parse
from datetime import timedelta
from typing import List, Mapping, Optional

from ..integration.http import HttpRequest, IHttpFetcher, InMemoryHttpFetcher
from .contracts import (
    IMcpWebScrape,
    IStealthHttpClient,
    ITerminalCast,
    IVideoIngest,
    IWebScraper,
    McpScrapeJob,
    ScrapedPage,
    TerminalCast,
    TerminalCastSegment,
    VideoIngestResult,
)

_TITLE_RX = re.compile(r"<title>(.*?)</title>", re.IGNORECASE | re.DOTALL)
_SCRIPT_RX = re.compile(r"<(script|style)[^>]*>.*?</\1>", re.IGNORECASE | re.DOTALL)
_TAG_RX = re.compile(r"<[^>]+>")
_HREF_RX = re.compile(r"""href\s*=\s*["']([^"'#]+)["']""", re.IGNORECASE)
_WS_RX = re.compile(r"\s+")


def _resolve(base: str, ref: str) -> Optional[str]:
    # C# Uri.TryCreate(base, ref, out abs) — resolve `ref` against `base`.
    try:
        return urllib.parse.urljoin(base, ref)
    except ValueError:
        return None


class HttpHtmlScraper(IWebScraper):
    """HTML scraper (fetch + text extraction). Mirrors
    ``CircleAI.Inputs.HttpHtmlScraper``. The C# HttpClient is replaced by an
    injected :class:`IHttpFetcher` (default deterministic in-memory)."""

    def __init__(self, fetcher: Optional[IHttpFetcher] = None) -> None:
        self._fetcher = fetcher if fetcher is not None else InMemoryHttpFetcher()

    @property
    def backend_id(self) -> str:
        return "http-html"

    async def fetch_async(
        self, url: str, ct: Optional[object] = None
    ) -> ScrapedPage:
        if url is None:
            raise ValueError("url")
        rsp = await self._fetcher.send_async(HttpRequest("GET", url))
        html_text = rsp.text or ""

        m = _TITLE_RX.search(html_text)
        title = m.group(1) if m is not None else ""
        if title:
            title = html.unescape(title.strip())

        stripped = _SCRIPT_RX.sub(" ", html_text)
        text = _WS_RX.sub(" ", _TAG_RX.sub(" ", stripped)).strip()
        text = html.unescape(text)

        links: List[str] = []
        for hm in _HREF_RX.finditer(html_text):
            abs_url = _resolve(url, hm.group(1))
            if abs_url is not None:
                links.append(abs_url)

        return ScrapedPage(url, text, title if title else None, None, links)


class StealthHttpClient(IStealthHttpClient):
    """Stealth HTTP client (rotating fingerprint headers). Mirrors
    ``CircleAI.Inputs.StealthHttpClient``. The C# HttpClient is replaced by an
    injected :class:`IHttpFetcher`; the exact headers chosen are recorded on
    :attr:`last_headers` for assertions."""

    _USER_AGENTS = [
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0 Safari/537.36",
        "Mozilla/5.0 (Macintosh; Intel Mac OS X 14_2) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.2 Safari/605.1.15",
        "Mozilla/5.0 (X11; Linux x86_64; rv:121.0) Gecko/20100101 Firefox/121.0",
    ]
    _ACCEPT_LANGUAGES = ["en-US,en;q=0.9", "en-GB,en;q=0.9", "en-ZA,en;q=0.9"]

    def __init__(self, fetcher: Optional[IHttpFetcher] = None) -> None:
        self._fetcher = fetcher if fetcher is not None else InMemoryHttpFetcher()
        self._seq = 0
        self.last_headers: Mapping[str, str] = {}

    @property
    def backend_id(self) -> str:
        return "stealth-http"

    async def get_async(
        self,
        url: str,
        headers: Optional[Mapping[str, str]] = None,
        ct: Optional[object] = None,
    ) -> ScrapedPage:
        if url is None:
            raise ValueError("url")
        # Interlocked.Increment(ref _seq) then index by seq % len.
        self._seq += 1
        seq = self._seq
        hdrs = {
            "User-Agent": self._USER_AGENTS[seq % len(self._USER_AGENTS)],
            "Accept-Language": self._ACCEPT_LANGUAGES[seq % len(self._ACCEPT_LANGUAGES)],
            "Accept": "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8",
            "Accept-Encoding": "gzip, deflate, br",
            "Cache-Control": "no-cache",
            "Connection": "keep-alive",
        }
        if headers is not None:
            for k, v in headers.items():
                hdrs[k] = v
        self.last_headers = dict(hdrs)
        rsp = await self._fetcher.send_async(HttpRequest("GET", url, headers=hdrs))
        rsp.ensure_success()
        return ScrapedPage(url, rsp.text or "")


class DefaultMcpWebScrape(IMcpWebScrape):
    """MCP-side scrape wrapping an :class:`IWebScraper`. Mirrors
    ``CircleAI.Inputs.DefaultMcpWebScrape``."""

    def __init__(self, inner: Optional[IWebScraper] = None) -> None:
        self._inner = inner if inner is not None else HttpHtmlScraper()

    @property
    def backend_id(self) -> str:
        return f"mcp:{self._inner.backend_id}"

    async def scrape_async(
        self, job: McpScrapeJob, ct: Optional[object] = None
    ) -> ScrapedPage:
        if job is None:
            raise ValueError("job")
        return await self._inner.fetch_async(job.url, ct)


class InMemoryVideoIngest(IVideoIngest):
    """Deterministic offline :class:`IVideoIngest`. The C# ships only
    :class:`NullVideoIngest` (real ingest needs ffmpeg on the host); this
    deterministic backing derives a transcript + shot list + duration + frame
    count from a pre-registered ingest table so the pipeline is exercised
    without a native decoder. Register with :meth:`register`."""

    def __init__(self) -> None:
        self._table: dict = {}

    @property
    def backend_id(self) -> str:
        return "in-memory"

    def register(self, file_path: str, result: VideoIngestResult) -> None:
        if file_path is None or file_path.strip() == "":
            raise ValueError("filePath required")
        if result is None:
            raise ValueError("result")
        self._table[file_path] = result

    async def ingest_async(
        self, file_path: str, ct: Optional[object] = None
    ) -> VideoIngestResult:
        if file_path is None or file_path.strip() == "":
            raise ValueError("filePath required")
        got = self._table.get(file_path)
        if got is not None:
            return got
        return VideoIngestResult("", [], timedelta(0), 0)


class AsciinemaTerminalCast(ITerminalCast):
    """asciinema v2 cast parser. Mirrors
    ``CircleAI.Inputs.AsciinemaTerminalCast``."""

    @property
    def backend_id(self) -> str:
        return "asciinema"

    async def load_async(
        self, file_path: str, ct: Optional[object] = None
    ) -> TerminalCast:
        if file_path is None or file_path.strip() == "":
            raise ValueError("filePath required")
        import os

        if not os.path.isfile(file_path):
            raise FileNotFoundError(f"cast file not found: {file_path}")

        width = 80
        height = 24
        segments: List[TerminalCastSegment] = []

        with open(file_path, "r", encoding="utf-8") as fh:
            lines = fh.read().split("\n")
        if not lines or lines[0] == "":
            raise ValueError("empty cast file")

        first = lines[0]
        try:
            hdr = json.loads(first)
            if isinstance(hdr, dict):
                if isinstance(hdr.get("width"), int):
                    width = hdr["width"]
                if isinstance(hdr.get("height"), int):
                    height = hdr["height"]
        except json.JSONDecodeError:
            pass  # header optional / non-standard cast

        for line in lines[1:]:
            if line is None or line.strip() == "":
                continue
            try:
                arr = json.loads(line)
            except json.JSONDecodeError:
                continue  # skip malformed event
            if not isinstance(arr, list) or len(arr) < 3:
                continue
            t = float(arr[0])
            typ = arr[1]
            txt = arr[2] if arr[2] is not None else ""
            if typ == "o":
                segments.append(TerminalCastSegment(timedelta(seconds=t), txt))

        return TerminalCast(segments, width, height)

    async def render_transcript_async(
        self, cast: TerminalCast, ct: Optional[object] = None
    ) -> str:
        if cast is None:
            raise ValueError("cast")
        return "".join(s.text for s in cast.segments)
