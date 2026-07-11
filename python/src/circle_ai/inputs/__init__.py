"""circle_ai.inputs — port of the CircleAI.Inputs assembly.

(2.5.0 contracts / 3.3.0 in-memory) Input adapters that normalise a URL / file /
video / terminal-cast into a model-ready text stream: web scraper, stealth HTTP
client, video ingest, MCP-side scrape, asciinema terminal cast — with offline
deterministic backends and fail-safe null defaults. C# is the exact spec.

The C# scrapers use ``System.Net.Http.HttpClient``; the Python port injects the
network behind the ``IHttpFetcher`` seam (reused from ``circle_ai.integration``)
so the adapters are deterministic and network-free.
"""
from __future__ import annotations

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
from .in_memory_inputs import (
    AsciinemaTerminalCast,
    DefaultMcpWebScrape,
    HttpHtmlScraper,
    InMemoryVideoIngest,
    StealthHttpClient,
)
from .null_implementations import (
    NullMcpWebScrape,
    NullStealthHttpClient,
    NullTerminalCast,
    NullVideoIngest,
    NullWebScraper,
)

__all__ = [
    "ScrapedPage",
    "VideoIngestResult",
    "McpScrapeJob",
    "TerminalCastSegment",
    "TerminalCast",
    "IWebScraper",
    "IStealthHttpClient",
    "IVideoIngest",
    "IMcpWebScrape",
    "ITerminalCast",
    "HttpHtmlScraper",
    "StealthHttpClient",
    "DefaultMcpWebScrape",
    "InMemoryVideoIngest",
    "AsciinemaTerminalCast",
    "NullWebScraper",
    "NullStealthHttpClient",
    "NullVideoIngest",
    "NullMcpWebScrape",
    "NullTerminalCast",
]
