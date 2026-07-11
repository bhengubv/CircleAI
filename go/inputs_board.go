// inputs_board.go
//
// Ports CircleAI.Inputs (Contracts.cs / InMemoryInputs.cs / NullImplementations.cs):
//   ScrapedPage / IWebScraper / IStealthHttpClient
//   VideoIngestResult / IVideoIngest
//   McpScrapeJob / IMcpWebScrape
//   TerminalCastSegment / TerminalCast / ITerminalCast
//   HttpHtmlScraper -> HTMLScraper (real HTML->text over an injected fetcher)
//   StealthHttpClient (rotating-header fetch over an injected fetcher)
//   DefaultMcpWebScrape / AsciinemaTerminalCast
//   Null* fail-safe defaults
//
// The C# scrapers hit the network via HttpClient. Per the port rules the socket
// dependency is injected behind HTTPBodyFetcher; the REAL, deterministic work —
// HTML tag stripping, <title> extraction, href resolution, header rotation, the
// asciinema v2 parser — is ported verbatim so it is fully testable in-process
// with an in-memory fetcher. Video ingest still needs a host codec, injected
// behind VideoProbe; the default probe reports NotSupported with how to enable.

package circleai

import (
	"bufio"
	"context"
	"encoding/json"
	"errors"
	"html"
	"net/url"
	"os"
	"regexp"
	"strings"
	"sync/atomic"
	"time"
)

// HTTPBodyFetcher fetches the body of a URL. Injection point replacing the C#
// HttpClient — production wires a net/http client, tests wire an in-memory map.
type HTTPBodyFetcher interface {
	// Fetch returns the response body for url, optionally influenced by headers.
	Fetch(ctx context.Context, u *url.URL, headers map[string]string) (string, error)
}

// ScrapedPage is one scraped page. Ports ScrapedPage. Title is *string;
// Metadata / ResolvedLinks nil == none.
type ScrapedPage struct {
	URL           *url.URL
	Text          string
	Title         *string
	Metadata      map[string]string
	ResolvedLinks []*url.URL
}

// IWebScraper converts a URL into text. Ports IWebScraper.
type IWebScraper interface {
	BackendID() string
	Fetch(ctx context.Context, u *url.URL) (ScrapedPage, error)
}

// IStealthHttpClient is a fingerprint-avoiding HTTPS client. Ports
// IStealthHttpClient.
type IStealthHttpClient interface {
	BackendID() string
	Get(ctx context.Context, u *url.URL, headers map[string]string) (ScrapedPage, error)
}

// VideoIngestResult is a model-ready video summary. Ports VideoIngestResult.
type VideoIngestResult struct {
	Transcript string
	Shots      []string
	Duration   time.Duration
	FrameCount int
}

// IVideoIngest brings a video file into a text stream. Ports IVideoIngest.
type IVideoIngest interface {
	BackendID() string
	Ingest(ctx context.Context, filePath string) (VideoIngestResult, error)
}

// McpScrapeJob is an MCP-side scrape request. Ports McpScrapeJob.
type McpScrapeJob struct {
	URL     string
	Headers map[string]string
}

// IMcpWebScrape is MCP-side delegated scraping. Ports IMcpWebScrape.
type IMcpWebScrape interface {
	BackendID() string
	Scrape(ctx context.Context, job McpScrapeJob) (ScrapedPage, error)
}

// TerminalCastSegment is one output segment. Ports TerminalCastSegment.
type TerminalCastSegment struct {
	Offset time.Duration
	Text   string
}

// TerminalCast is a parsed asciinema cast. Ports TerminalCast.
type TerminalCast struct {
	Segments []TerminalCastSegment
	Width    int
	Height   int
}

// ITerminalCast parses / replays asciinema casts. Ports ITerminalCast.
type ITerminalCast interface {
	BackendID() string
	Load(ctx context.Context, filePath string) (TerminalCast, error)
	RenderTranscript(ctx context.Context, cast TerminalCast) (string, error)
}

// ---------------------------------------------------------------------------
// HTMLScraper (HttpHtmlScraper)
// ---------------------------------------------------------------------------

var (
	scraperTitleRx  = regexp.MustCompile(`(?is)<title>(.*?)</title>`)
	scraperScriptRx = regexp.MustCompile(`(?is)<(script|style)[^>]*>.*?</(script|style)>`)
	scraperTagRx    = regexp.MustCompile(`(?s)<[^>]+>`)
	scraperHrefRx   = regexp.MustCompile(`(?i)href\s*=\s*["']([^"'#]+)["']`)
	scraperWsRx     = regexp.MustCompile(`\s+`)
)

// HTMLScraper fetches HTML (via an injected fetcher) and extracts text + title
// + resolved links. Ports HttpHtmlScraper (the network call is the injected
// seam; the extraction is ported verbatim).
type HTMLScraper struct {
	fetcher HTTPBodyFetcher
}

// NewHTMLScraper constructs a scraper over fetcher. Panics if fetcher is nil.
func NewHTMLScraper(fetcher HTTPBodyFetcher) *HTMLScraper {
	if fetcher == nil {
		panic("fetcher must not be nil")
	}
	return &HTMLScraper{fetcher: fetcher}
}

// BackendID returns "http-html".
func (s *HTMLScraper) BackendID() string { return "http-html" }

// Fetch retrieves and strips the page at u. Ports FetchAsync.
func (s *HTMLScraper) Fetch(ctx context.Context, u *url.URL) (ScrapedPage, error) {
	if u == nil {
		return ScrapedPage{}, errors.New("url must not be nil")
	}
	htmlBody, err := s.fetcher.Fetch(ctx, u, nil)
	if err != nil {
		return ScrapedPage{}, err
	}

	var titlePtr *string
	if m := scraperTitleRx.FindStringSubmatch(htmlBody); m != nil {
		t := strings.TrimSpace(m[1])
		if t != "" {
			t = html.UnescapeString(t)
			titlePtr = &t
		}
	}

	stripped := scraperScriptRx.ReplaceAllString(htmlBody, " ")
	text := scraperWsRx.ReplaceAllString(scraperTagRx.ReplaceAllString(stripped, " "), " ")
	text = html.UnescapeString(strings.TrimSpace(text))

	var links []*url.URL
	for _, m := range scraperHrefRx.FindAllStringSubmatch(htmlBody, -1) {
		if abs, aerr := u.Parse(m[1]); aerr == nil {
			links = append(links, abs)
		}
	}

	return ScrapedPage{URL: u, Text: text, Title: titlePtr, Metadata: nil, ResolvedLinks: links}, nil
}

var _ IWebScraper = (*HTMLScraper)(nil)

// ---------------------------------------------------------------------------
// StealthHTTPClient (StealthHttpClient)
// ---------------------------------------------------------------------------

var stealthUserAgents = []string{
	"Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0 Safari/537.36",
	"Mozilla/5.0 (Macintosh; Intel Mac OS X 14_2) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.2 Safari/605.1.15",
	"Mozilla/5.0 (X11; Linux x86_64; rv:121.0) Gecko/20100101 Firefox/121.0",
}

var stealthAcceptLanguages = []string{"en-US,en;q=0.9", "en-GB,en;q=0.9", "en-ZA,en;q=0.9"}

// StealthHTTPClient rotates browser-like headers per call over an injected
// fetcher. Ports StealthHttpClient.
type StealthHTTPClient struct {
	fetcher HTTPBodyFetcher
	seq     int64
}

// NewStealthHTTPClient constructs the client over fetcher. Panics if fetcher is
// nil.
func NewStealthHTTPClient(fetcher HTTPBodyFetcher) *StealthHTTPClient {
	if fetcher == nil {
		panic("fetcher must not be nil")
	}
	return &StealthHTTPClient{fetcher: fetcher}
}

// BackendID returns "stealth-http".
func (c *StealthHTTPClient) BackendID() string { return "stealth-http" }

// Get fetches u with rotating stealth headers plus any caller headers. Ports
// GetAsync.
func (c *StealthHTTPClient) Get(ctx context.Context, u *url.URL, headers map[string]string) (ScrapedPage, error) {
	if u == nil {
		return ScrapedPage{}, errors.New("url must not be nil")
	}
	seq := atomic.AddInt64(&c.seq, 1)
	merged := map[string]string{
		"User-Agent":      stealthUserAgents[seq%int64(len(stealthUserAgents))],
		"Accept-Language": stealthAcceptLanguages[seq%int64(len(stealthAcceptLanguages))],
		"Accept":          "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8",
		"Accept-Encoding": "gzip, deflate, br",
		"Cache-Control":   "no-cache",
		"Connection":      "keep-alive",
	}
	for k, v := range headers {
		merged[k] = v
	}
	body, err := c.fetcher.Fetch(ctx, u, merged)
	if err != nil {
		return ScrapedPage{}, err
	}
	return ScrapedPage{URL: u, Text: body}, nil
}

var _ IStealthHttpClient = (*StealthHTTPClient)(nil)

// ---------------------------------------------------------------------------
// DefaultMcpWebScrape (DefaultMcpWebScrape)
// ---------------------------------------------------------------------------

// DefaultMcpWebScrape delegates MCP-side scraping to an inner IWebScraper. Ports
// DefaultMcpWebScrape.
type DefaultMcpWebScrape struct {
	inner IWebScraper
}

// NewDefaultMcpWebScrape constructs the delegate over inner. Panics if inner is
// nil.
func NewDefaultMcpWebScrape(inner IWebScraper) *DefaultMcpWebScrape {
	if inner == nil {
		panic("inner must not be nil")
	}
	return &DefaultMcpWebScrape{inner: inner}
}

// BackendID returns "mcp:{inner.BackendID}".
func (m *DefaultMcpWebScrape) BackendID() string { return "mcp:" + m.inner.BackendID() }

// Scrape parses job.URL and delegates to the inner scraper. Ports ScrapeAsync.
func (m *DefaultMcpWebScrape) Scrape(ctx context.Context, job McpScrapeJob) (ScrapedPage, error) {
	u, err := url.Parse(job.URL)
	if err != nil {
		return ScrapedPage{}, err
	}
	return m.inner.Fetch(ctx, u)
}

var _ IMcpWebScrape = (*DefaultMcpWebScrape)(nil)

// ---------------------------------------------------------------------------
// AsciinemaTerminalCast (AsciinemaTerminalCast)
// ---------------------------------------------------------------------------

// AsciinemaTerminalCast parses asciinema v2 cast files (header line + [time,
// type, data] event array). Ports AsciinemaTerminalCast.
type AsciinemaTerminalCast struct{}

// BackendID returns "asciinema".
func (AsciinemaTerminalCast) BackendID() string { return "asciinema" }

// Load parses the cast at filePath. Ports LoadAsync.
func (AsciinemaTerminalCast) Load(ctx context.Context, filePath string) (TerminalCast, error) {
	if strings.TrimSpace(filePath) == "" {
		return TerminalCast{}, errors.New("filePath required")
	}
	f, err := os.Open(filePath)
	if err != nil {
		if errors.Is(err, os.ErrNotExist) {
			return TerminalCast{}, errors.New("cast file not found: " + filePath)
		}
		return TerminalCast{}, err
	}
	defer func() { _ = f.Close() }()

	width, height := 80, 24
	segments := make([]TerminalCastSegment, 0)

	scanner := bufio.NewScanner(f)
	scanner.Buffer(make([]byte, 0, 64*1024), 16*1024*1024)

	if !scanner.Scan() {
		return TerminalCast{}, errors.New("empty cast file")
	}
	first := scanner.Text()
	var header map[string]json.RawMessage
	if json.Unmarshal([]byte(first), &header) == nil {
		if w, ok := header["width"]; ok {
			var wi int
			if json.Unmarshal(w, &wi) == nil {
				width = wi
			}
		}
		if h, ok := header["height"]; ok {
			var hi int
			if json.Unmarshal(h, &hi) == nil {
				height = hi
			}
		}
	}

	for scanner.Scan() {
		line := scanner.Text()
		if strings.TrimSpace(line) == "" {
			continue
		}
		var ev []json.RawMessage
		if json.Unmarshal([]byte(line), &ev) != nil || len(ev) < 3 {
			continue
		}
		var t float64
		var typ, txt string
		if json.Unmarshal(ev[0], &t) != nil {
			continue
		}
		_ = json.Unmarshal(ev[1], &typ)
		_ = json.Unmarshal(ev[2], &txt)
		if typ == "o" {
			segments = append(segments, TerminalCastSegment{
				Offset: time.Duration(t * float64(time.Second)),
				Text:   txt,
			})
		}
	}
	if err := scanner.Err(); err != nil {
		return TerminalCast{}, err
	}
	return TerminalCast{Segments: segments, Width: width, Height: height}, nil
}

// RenderTranscript concatenates the cast's output segments. Ports
// RenderTranscriptAsync.
func (AsciinemaTerminalCast) RenderTranscript(ctx context.Context, cast TerminalCast) (string, error) {
	var sb strings.Builder
	for _, s := range cast.Segments {
		sb.WriteString(s.Text)
	}
	return sb.String(), nil
}

var _ ITerminalCast = AsciinemaTerminalCast{}

// ---------------------------------------------------------------------------
// VideoProbe + probe-backed video ingest
// ---------------------------------------------------------------------------

// VideoProbe is the injected host codec seam for video ingest. Production wires
// an ffmpeg-backed probe; the default reports NotSupported with how to enable.
type VideoProbe interface {
	Probe(ctx context.Context, filePath string) (VideoIngestResult, error)
}

// ProbeVideoIngest is a real IVideoIngest that delegates to an injected
// VideoProbe. There is no in-process InMemory video ingest in the C# reference
// (only the Null default), so this is the seam a host uses for real ingest.
type ProbeVideoIngest struct {
	probe VideoProbe
}

// NewProbeVideoIngest constructs the ingest over probe. Panics if probe is nil.
func NewProbeVideoIngest(probe VideoProbe) *ProbeVideoIngest {
	if probe == nil {
		panic("probe must not be nil")
	}
	return &ProbeVideoIngest{probe: probe}
}

// BackendID returns "probe".
func (v *ProbeVideoIngest) BackendID() string { return "probe" }

// Ingest delegates to the probe. Ports IngestAsync.
func (v *ProbeVideoIngest) Ingest(ctx context.Context, filePath string) (VideoIngestResult, error) {
	if strings.TrimSpace(filePath) == "" {
		return VideoIngestResult{}, errors.New("filePath required")
	}
	return v.probe.Probe(ctx, filePath)
}

var _ IVideoIngest = (*ProbeVideoIngest)(nil)

// ---------------------------------------------------------------------------
// Null implementations
// ---------------------------------------------------------------------------

// NullWebScraper is a fail-safe scraper. Ports NullWebScraper.
type NullWebScraper struct{}

// NullWebScraperInstance is the shared singleton.
var NullWebScraperInstance = NullWebScraper{}

func (NullWebScraper) BackendID() string { return "null" }
func (NullWebScraper) Fetch(_ context.Context, u *url.URL) (ScrapedPage, error) {
	return ScrapedPage{URL: u, Text: ""}, nil
}

// NullStealthHttpClient is a fail-safe client. Ports NullStealthHttpClient.
type NullStealthHttpClient struct{}

// NullStealthHttpClientInstance is the shared singleton.
var NullStealthHttpClientInstance = NullStealthHttpClient{}

func (NullStealthHttpClient) BackendID() string { return "null" }
func (NullStealthHttpClient) Get(_ context.Context, u *url.URL, _ map[string]string) (ScrapedPage, error) {
	return ScrapedPage{URL: u, Text: ""}, nil
}

// NullVideoIngest is a fail-safe ingest. Ports NullVideoIngest.
type NullVideoIngest struct{}

// NullVideoIngestInstance is the shared singleton.
var NullVideoIngestInstance = NullVideoIngest{}

func (NullVideoIngest) BackendID() string { return "null" }
func (NullVideoIngest) Ingest(context.Context, string) (VideoIngestResult, error) {
	return VideoIngestResult{Transcript: "", Shots: []string{}, Duration: 0, FrameCount: 0}, nil
}

// NullMcpWebScrape is a fail-safe MCP scrape. Ports NullMcpWebScrape.
type NullMcpWebScrape struct{}

// NullMcpWebScrapeInstance is the shared singleton.
var NullMcpWebScrapeInstance = NullMcpWebScrape{}

func (NullMcpWebScrape) BackendID() string { return "null" }
func (NullMcpWebScrape) Scrape(_ context.Context, job McpScrapeJob) (ScrapedPage, error) {
	u, err := url.Parse(job.URL)
	if err != nil {
		return ScrapedPage{}, err
	}
	return ScrapedPage{URL: u, Text: ""}, nil
}

// NullTerminalCast is a fail-safe cast. Ports NullTerminalCast.
type NullTerminalCast struct{}

// NullTerminalCastInstance is the shared singleton.
var NullTerminalCastInstance = NullTerminalCast{}

func (NullTerminalCast) BackendID() string { return "null" }
func (NullTerminalCast) Load(context.Context, string) (TerminalCast, error) {
	return TerminalCast{Segments: []TerminalCastSegment{}, Width: 80, Height: 24}, nil
}
func (NullTerminalCast) RenderTranscript(context.Context, TerminalCast) (string, error) {
	return "", nil
}

var (
	_ IWebScraper        = NullWebScraper{}
	_ IStealthHttpClient = NullStealthHttpClient{}
	_ IVideoIngest       = NullVideoIngest{}
	_ IMcpWebScrape      = NullMcpWebScrape{}
	_ ITerminalCast      = NullTerminalCast{}
)
