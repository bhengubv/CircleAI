# null_implementations.py
#
# Port of CircleAI.Inputs NullImplementations.cs (C# — the EXACT spec).
#
# (2.5.0) Fail-safe defaults for the Inputs pack. Each exposes a singleton
# `INSTANCE` mirroring the C# `static readonly ... Instance`.

from __future__ import annotations

from datetime import timedelta
from typing import Mapping, Optional

from .contracts import (
    IMcpWebScrape,
    IStealthHttpClient,
    ITerminalCast,
    IVideoIngest,
    IWebScraper,
    McpScrapeJob,
    ScrapedPage,
    TerminalCast,
    VideoIngestResult,
)


class NullWebScraper(IWebScraper):
    INSTANCE: "NullWebScraper"

    @property
    def backend_id(self) -> str:
        return "null"

    async def fetch_async(
        self, url: str, ct: Optional[object] = None
    ) -> ScrapedPage:
        return ScrapedPage(url, "")


class NullStealthHttpClient(IStealthHttpClient):
    INSTANCE: "NullStealthHttpClient"

    @property
    def backend_id(self) -> str:
        return "null"

    async def get_async(
        self,
        url: str,
        headers: Optional[Mapping[str, str]] = None,
        ct: Optional[object] = None,
    ) -> ScrapedPage:
        return ScrapedPage(url, "")


class NullVideoIngest(IVideoIngest):
    INSTANCE: "NullVideoIngest"

    @property
    def backend_id(self) -> str:
        return "null"

    async def ingest_async(
        self, file_path: str, ct: Optional[object] = None
    ) -> VideoIngestResult:
        return VideoIngestResult("", [], timedelta(0), 0)


class NullMcpWebScrape(IMcpWebScrape):
    INSTANCE: "NullMcpWebScrape"

    @property
    def backend_id(self) -> str:
        return "null"

    async def scrape_async(
        self, job: McpScrapeJob, ct: Optional[object] = None
    ) -> ScrapedPage:
        return ScrapedPage(job.url, "")


class NullTerminalCast(ITerminalCast):
    INSTANCE: "NullTerminalCast"

    @property
    def backend_id(self) -> str:
        return "null"

    async def load_async(
        self, file_path: str, ct: Optional[object] = None
    ) -> TerminalCast:
        return TerminalCast([], 80, 24)

    async def render_transcript_async(
        self, cast: TerminalCast, ct: Optional[object] = None
    ) -> str:
        return ""


NullWebScraper.INSTANCE = NullWebScraper()
NullStealthHttpClient.INSTANCE = NullStealthHttpClient()
NullVideoIngest.INSTANCE = NullVideoIngest()
NullMcpWebScrape.INSTANCE = NullMcpWebScrape()
NullTerminalCast.INSTANCE = NullTerminalCast()
