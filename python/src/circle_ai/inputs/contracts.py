# contracts.py
#
# Port of CircleAI.Inputs Contracts.cs (C# — the EXACT spec).
#
# (2.5.0) Input-adapter contracts. Any payload — URL, raw file, video, terminal
# cast — is normalised to a model-ready text stream: web scraper (ConvertX),
# stealth HTTP client (Scrapling), video ingest (openvid), MCP-side scrape, and
# asciinema terminal-cast parse/replay.
#
# C# ValueTask<T> -> async def -> T. C# records -> frozen slotted dataclasses.
# System.Uri -> str (Python has no first-class Uri; the C# Url is opaque here).
# TimeSpan -> timedelta. IReadOnlyDictionary<string,string>? -> Optional[Mapping].

from __future__ import annotations

from abc import ABC, abstractmethod
from dataclasses import dataclass
from datetime import timedelta
from typing import Mapping, Optional, Sequence


@dataclass(frozen=True, slots=True)
class ScrapedPage:
    """Mirrors ``CircleAI.Inputs.ScrapedPage`` — ``record(Uri Url, string Text,
    string? Title = null, IReadOnlyDictionary<string,string>? Metadata = null,
    IReadOnlyList<Uri>? ResolvedLinks = null)``. Uris are carried as ``str``.
    """

    url: str
    text: str
    title: Optional[str] = None
    metadata: Optional[Mapping[str, str]] = None
    resolved_links: Optional[Sequence[str]] = None


@dataclass(frozen=True, slots=True)
class VideoIngestResult:
    """Mirrors ``CircleAI.Inputs.VideoIngestResult`` — ``record(string Transcript,
    IReadOnlyList<string> Shots, TimeSpan Duration, int FrameCount)``.
    """

    transcript: str
    shots: Sequence[str]
    duration: timedelta
    frame_count: int


@dataclass(frozen=True, slots=True)
class McpScrapeJob:
    """Mirrors ``CircleAI.Inputs.McpScrapeJob`` — ``record(string Url,
    IReadOnlyDictionary<string,string>? Headers = null)``.
    """

    url: str
    headers: Optional[Mapping[str, str]] = None


@dataclass(frozen=True, slots=True)
class TerminalCastSegment:
    """Mirrors ``CircleAI.Inputs.TerminalCastSegment`` — ``record(TimeSpan Offset,
    string Text)``.
    """

    offset: timedelta
    text: str


@dataclass(frozen=True, slots=True)
class TerminalCast:
    """Mirrors ``CircleAI.Inputs.TerminalCast`` — ``record(
    IReadOnlyList<TerminalCastSegment> Segments, int Width, int Height)``.
    """

    segments: Sequence[TerminalCastSegment]
    width: int
    height: int


class IWebScraper(ABC):
    """(2.5.0) Convert a URL into markdown/text (ConvertX pattern)."""

    @property
    @abstractmethod
    def backend_id(self) -> str:
        ...

    @abstractmethod
    async def fetch_async(
        self, url: str, ct: Optional[object] = None
    ) -> ScrapedPage:
        ...


class IStealthHttpClient(ABC):
    """(2.5.0) HTTPS client that avoids common fingerprinting (Scrapling pattern)."""

    @property
    @abstractmethod
    def backend_id(self) -> str:
        ...

    @abstractmethod
    async def get_async(
        self,
        url: str,
        headers: Optional[Mapping[str, str]] = None,
        ct: Optional[object] = None,
    ) -> ScrapedPage:
        ...


class IVideoIngest(ABC):
    """(2.5.0) Bring a video file into a model-ready text stream (openvid)."""

    @property
    @abstractmethod
    def backend_id(self) -> str:
        ...

    @abstractmethod
    async def ingest_async(
        self, file_path: str, ct: Optional[object] = None
    ) -> VideoIngestResult:
        ...


class IMcpWebScrape(ABC):
    """(2.5.0) MCP-side delegated scraping (mcp-web-scrape pattern)."""

    @property
    @abstractmethod
    def backend_id(self) -> str:
        ...

    @abstractmethod
    async def scrape_async(
        self, job: McpScrapeJob, ct: Optional[object] = None
    ) -> ScrapedPage:
        ...


class ITerminalCast(ABC):
    """(2.5.0) Parse / replay asciinema-format terminal casts (ASCILINE pattern)."""

    @property
    @abstractmethod
    def backend_id(self) -> str:
        ...

    @abstractmethod
    async def load_async(
        self, file_path: str, ct: Optional[object] = None
    ) -> TerminalCast:
        ...

    @abstractmethod
    async def render_transcript_async(
        self, cast: TerminalCast, ct: Optional[object] = None
    ) -> str:
        ...
