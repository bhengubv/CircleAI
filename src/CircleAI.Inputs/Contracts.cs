// Contracts.cs
//
// (2.5.0) Input-adapter contracts. Any payload — URL, raw file, video,
// terminal cast — is normalised to a model-ready text/embedding stream.
// Real backends land in 2.5.1.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.Inputs;

// ─── Web scraping (ConvertX + Scrapling) ────────────────────────────────

/// <summary>One scraped page.</summary>
public sealed record ScrapedPage(
    Uri                                 Url,
    string                              Text,
    string?                             Title         = null,
    IReadOnlyDictionary<string, string>? Metadata     = null,
    IReadOnlyList<Uri>?                 ResolvedLinks = null);

/// <summary>(2.5.0) Convert a URL into markdown/text (ConvertX pattern).</summary>
public interface IWebScraper
{
    string BackendId { get; }

    ValueTask<ScrapedPage> FetchAsync(Uri url, CancellationToken ct = default);
}

/// <summary>(2.5.0) HTTPS client that avoids common fingerprinting (Scrapling pattern).</summary>
public interface IStealthHttpClient
{
    string BackendId { get; }

    ValueTask<ScrapedPage> GetAsync(
        Uri               url,
        IReadOnlyDictionary<string, string>? headers = null,
        CancellationToken ct = default);
}

// ─── Video ingest (openvid) ─────────────────────────────────────────────

public sealed record VideoIngestResult(
    string                Transcript,
    IReadOnlyList<string> Shots,
    TimeSpan              Duration,
    int                   FrameCount);

/// <summary>(2.5.0) Bring a video file into a model-ready text stream (openvid).</summary>
public interface IVideoIngest
{
    string BackendId { get; }

    ValueTask<VideoIngestResult> IngestAsync(string filePath, CancellationToken ct = default);
}

// ─── MCP-side web scrape ────────────────────────────────────────────────

public sealed record McpScrapeJob(string Url, IReadOnlyDictionary<string, string>? Headers = null);

/// <summary>(2.5.0) MCP-side delegated scraping (mcp-web-scrape pattern).</summary>
public interface IMcpWebScrape
{
    string BackendId { get; }

    ValueTask<ScrapedPage> ScrapeAsync(McpScrapeJob job, CancellationToken ct = default);
}

// ─── Terminal cast (ASCILINE / asciinema) ───────────────────────────────

public sealed record TerminalCastSegment(TimeSpan Offset, string Text);

public sealed record TerminalCast(IReadOnlyList<TerminalCastSegment> Segments, int Width, int Height);

/// <summary>(2.5.0) Parse / replay asciinema-format terminal casts (ASCILINE pattern).</summary>
public interface ITerminalCast
{
    string BackendId { get; }

    ValueTask<TerminalCast> LoadAsync(string filePath, CancellationToken ct = default);

    ValueTask<string> RenderTranscriptAsync(TerminalCast cast, CancellationToken ct = default);
}
