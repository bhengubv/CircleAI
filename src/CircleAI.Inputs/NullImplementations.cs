// NullImplementations.cs
//
// (2.5.0) Fail-safe defaults for the Inputs pack.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.Inputs;

public sealed class NullWebScraper : IWebScraper
{
    public static readonly NullWebScraper Instance = new();
    public string BackendId => "null";
    public ValueTask<ScrapedPage> FetchAsync(Uri url, CancellationToken ct = default)
        => ValueTask.FromResult(new ScrapedPage(url, ""));
}

public sealed class NullStealthHttpClient : IStealthHttpClient
{
    public static readonly NullStealthHttpClient Instance = new();
    public string BackendId => "null";
    public ValueTask<ScrapedPage> GetAsync(Uri url, IReadOnlyDictionary<string, string>? h = null, CancellationToken ct = default)
        => ValueTask.FromResult(new ScrapedPage(url, ""));
}

public sealed class NullVideoIngest : IVideoIngest
{
    public static readonly NullVideoIngest Instance = new();
    public string BackendId => "null";
    public ValueTask<VideoIngestResult> IngestAsync(string filePath, CancellationToken ct = default)
        => ValueTask.FromResult(new VideoIngestResult("", Array.Empty<string>(), TimeSpan.Zero, 0));
}

public sealed class NullMcpWebScrape : IMcpWebScrape
{
    public static readonly NullMcpWebScrape Instance = new();
    public string BackendId => "null";
    public ValueTask<ScrapedPage> ScrapeAsync(McpScrapeJob job, CancellationToken ct = default)
        => ValueTask.FromResult(new ScrapedPage(new Uri(job.Url), ""));
}

public sealed class NullTerminalCast : ITerminalCast
{
    public static readonly NullTerminalCast Instance = new();
    public string BackendId => "null";
    public ValueTask<TerminalCast> LoadAsync(string filePath, CancellationToken ct = default)
        => ValueTask.FromResult(new TerminalCast(Array.Empty<TerminalCastSegment>(), 80, 24));
    public ValueTask<string> RenderTranscriptAsync(TerminalCast cast, CancellationToken ct = default)
        => ValueTask.FromResult("");
}
