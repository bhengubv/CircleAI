// network_http.go
//
// Ports CircleAI.Networking.Http:
//   HttpTransportCommons.cs -> HttpEndpointDescriptor, HttpRequestSummary,
//                              HttpCacheKey, HttpStatusFamily,
//                              InMemoryHttpRequestMetrics
//   HttpNetworkTransport.cs -> HttpNetworkTransport (INetworkTransport)
//
// The C# HttpNetworkTransport POSTs a payload to {baseUrl}/messages/{dest} over
// an injected HttpClient with 3-attempt exponential backoff; ReceiveAsync yields
// nothing (HTTP is request-response — server push lives in the WebSocket
// transport). Per the porting rules (NO stubs — every contract gets a working
// deterministic implementation), the Go port injects an HttpSender seam (the
// HttpClient analogue) and supplies a working in-memory sender
// (InMemoryHttpSender) backed by a shared HttpFabric so a POST actually delivers
// the payload to the addressed endpoint's inbox; the transport's Receive drains
// that inbox (a working pull loop rather than an empty stream). The 3-attempt
// exponential backoff and per-request metrics are faithful ports.
//
// Concurrency (Wave-1 lessons): each endpoint's inbox is an unbounded channel —
// a POST delivered before the endpoint's Receive consumer attaches is BUFFERED,
// never lost; the backoff sleep honours ctx cancellation.

package circleai

import (
	"context"
	"errors"
	"fmt"
	"net/url"
	"sort"
	"strings"
	"sync"
	"time"
)

// ---------------------------------------------------------------------------
// Records — endpoint descriptor, request summary, cache key
// ---------------------------------------------------------------------------

// HttpEndpointDescriptor describes an HTTP endpoint. Ports the C#
// `sealed record HttpEndpointDescriptor(Method, BaseUri, Path, DefaultHeaders)`.
// DefaultHeaders may be nil (the C# `IReadOnlyDictionary<string,string>?`).
type HttpEndpointDescriptor struct {
	Method         string
	BaseUri        string
	Path           string
	DefaultHeaders map[string]string
}

// HttpRequestSummary is a per-request accounting record. Ports the C#
// `sealed record HttpRequestSummary(EndpointId, StatusCode, Latency, ResponseBytes, AtUtc)`.
type HttpRequestSummary struct {
	EndpointId    string
	StatusCode    int
	Latency       time.Duration
	ResponseBytes int
	AtUtc         time.Time
}

// HttpCacheKey identifies a cacheable response. Ports the C#
// `sealed record HttpCacheKey(Method, FullUri, AcceptHeader)`. As a record it is
// value-comparable; Go structs of comparable fields compare with == likewise,
// so it works directly as a map key.
type HttpCacheKey struct {
	Method       string
	FullUri      string
	AcceptHeader string
}

// ---------------------------------------------------------------------------
// HttpStatusFamily — HttpTransportCommons.cs static HttpStatusFamily
// ---------------------------------------------------------------------------

// HttpStatusFamily classifies HTTP status codes. Ports the C# static helper as
// package-level functions.

// HttpStatusIs2xx reports whether s is a 2xx success.
func HttpStatusIs2xx(s int) bool { return s >= 200 && s < 300 }

// HttpStatusIs3xx reports whether s is a 3xx redirect.
func HttpStatusIs3xx(s int) bool { return s >= 300 && s < 400 }

// HttpStatusIs4xx reports whether s is a 4xx client error.
func HttpStatusIs4xx(s int) bool { return s >= 400 && s < 500 }

// HttpStatusIs5xx reports whether s is a 5xx server error.
func HttpStatusIs5xx(s int) bool { return s >= 500 && s < 600 }

// HttpStatusShouldRetry reports whether s warrants a retry: 408, 425, 429, or
// any 5xx. Mirrors HttpStatusFamily.ShouldRetry.
func HttpStatusShouldRetry(s int) bool {
	return s == 408 || s == 425 || s == 429 || HttpStatusIs5xx(s)
}

// ---------------------------------------------------------------------------
// InMemoryHttpRequestMetrics — HttpTransportCommons.cs
// ---------------------------------------------------------------------------

// InMemoryHttpRequestMetrics tracks endpoint descriptors and a request log.
// Ports the C# `InMemoryHttpRequestMetrics`. Safe for concurrent use.
type InMemoryHttpRequestMetrics struct {
	mu        sync.Mutex
	endpoints map[string]HttpEndpointDescriptor
	requests  []HttpRequestSummary
}

// NewInMemoryHttpRequestMetrics constructs an empty metrics store.
func NewInMemoryHttpRequestMetrics() *InMemoryHttpRequestMetrics {
	return &InMemoryHttpRequestMetrics{endpoints: make(map[string]HttpEndpointDescriptor)}
}

// Register records an endpoint descriptor under id.
func (m *InMemoryHttpRequestMetrics) Register(id string, d HttpEndpointDescriptor) {
	m.mu.Lock()
	m.endpoints[id] = d
	m.mu.Unlock()
}

// GetEndpoint returns the descriptor for id and true, or a zero value and false.
func (m *InMemoryHttpRequestMetrics) GetEndpoint(id string) (HttpEndpointDescriptor, bool) {
	m.mu.Lock()
	defer m.mu.Unlock()
	d, ok := m.endpoints[id]
	return d, ok
}

// Log appends a request summary.
func (m *InMemoryHttpRequestMetrics) Log(s HttpRequestSummary) {
	m.mu.Lock()
	m.requests = append(m.requests, s)
	m.mu.Unlock()
}

// RecentRequests returns up to limit requests, most recent first (ordered by
// AtUtc descending). Mirrors OrderByDescending(r => r.AtUtc).Take(limit).
func (m *InMemoryHttpRequestMetrics) RecentRequests(limit int) []HttpRequestSummary {
	if limit <= 0 {
		limit = 100
	}
	m.mu.Lock()
	snapshot := make([]HttpRequestSummary, len(m.requests))
	copy(snapshot, m.requests)
	m.mu.Unlock()
	sort.SliceStable(snapshot, func(i, j int) bool { return snapshot[i].AtUtc.After(snapshot[j].AtUtc) })
	if len(snapshot) > limit {
		snapshot = snapshot[:limit]
	}
	return snapshot
}

// Avg2xxLatencyMs returns the mean latency (ms) of endpointId's 2xx requests, or
// 0 when none. Mirrors the C# Avg2xxLatencyMs.
func (m *InMemoryHttpRequestMetrics) Avg2xxLatencyMs(endpointId string) float64 {
	m.mu.Lock()
	defer m.mu.Unlock()
	var sum float64
	var n int
	for _, r := range m.requests {
		if r.EndpointId == endpointId && HttpStatusIs2xx(r.StatusCode) {
			sum += float64(r.Latency) / float64(time.Millisecond)
			n++
		}
	}
	if n == 0 {
		return 0
	}
	return sum / float64(n)
}

// ---------------------------------------------------------------------------
// HttpSender — the injected HttpClient seam
// ---------------------------------------------------------------------------

// HttpResponse is the outcome of an HttpSender.Post — the minimal shape the
// transport's retry loop needs (the C# uses HttpResponseMessage +
// EnsureSuccessStatusCode).
type HttpResponse struct {
	// StatusCode is the HTTP status.
	StatusCode int
	// Body is the response body (bytes).
	Body []byte
}

// IsSuccess reports whether the response is 2xx (the EnsureSuccessStatusCode
// predicate).
func (r HttpResponse) IsSuccess() bool { return HttpStatusIs2xx(r.StatusCode) }

// HttpSender is the injected HTTP client seam the transport posts through — the
// Go analogue of System.Net.Http.HttpClient. It returns a response and/or a
// transport-level error (the analogue of HttpRequestException). Injecting it
// keeps the transport wire-free and deterministic.
type HttpSender interface {
	// Post sends body to fullURL with contentType and headers, returning the
	// response. A non-nil error models a transport-level failure (connection
	// refused / timeout) analogous to HttpRequestException.
	Post(ctx context.Context, fullURL, contentType string, headers map[string]string, body []byte) (HttpResponse, error)
}

// ---------------------------------------------------------------------------
// HttpFabric + InMemoryHttpSender — working HttpSender
// ---------------------------------------------------------------------------

// HttpFabric is the in-process substitute for an HTTP server. It routes a POST
// to {baseUrl}/messages/{dest} into the destination endpoint's inbox, so a
// transport listening on that baseUrl receives the payload. Endpoints register
// their baseUrl; a POST whose URL prefix matches a registered baseUrl is
// delivered to that endpoint. Carries shared metrics for coherence.
type HttpFabric struct {
	// Metrics is the shared request/endpoint store.
	Metrics *InMemoryHttpRequestMetrics

	mu sync.Mutex
	// endpoints maps a normalised baseUrl to the set of inboxes listening on it.
	// A baseUrl may host several listeners (multiple transports fronting the same
	// logical server), so a POST fans out to all of them.
	endpoints map[string][]*httpFabricEndpoint
	// failURL, when a substring matches a POST url, forces a transport-level
	// error for the next failTimes POSTs, letting tests exercise retry/backoff.
	failURL   string
	failTimes int
}

type httpFabricEndpoint struct {
	inbox *unboundedChannel[NetworkPayload]
}

// NewHttpFabric constructs a fabric with fresh metrics (or m when non-nil).
func NewHttpFabric(m *InMemoryHttpRequestMetrics) *HttpFabric {
	if m == nil {
		m = NewInMemoryHttpRequestMetrics()
	}
	return &HttpFabric{
		Metrics:   m,
		endpoints: make(map[string][]*httpFabricEndpoint),
	}
}

// registerEndpoint attaches baseUrl's inbox so POSTs to it are delivered there.
// Returns the endpoint handle so it can be removed precisely on unregister.
func (f *HttpFabric) registerEndpoint(baseUrl string, inbox *unboundedChannel[NetworkPayload]) *httpFabricEndpoint {
	ep := &httpFabricEndpoint{inbox: inbox}
	key := normalizeBaseURL(baseUrl)
	f.mu.Lock()
	f.endpoints[key] = append(f.endpoints[key], ep)
	f.mu.Unlock()
	return ep
}

// unregisterEndpoint removes the specific endpoint handle from baseUrl's listener
// set, leaving any co-registered listeners intact.
func (f *HttpFabric) unregisterEndpoint(baseUrl string, ep *httpFabricEndpoint) {
	key := normalizeBaseURL(baseUrl)
	f.mu.Lock()
	list := f.endpoints[key]
	for i, e := range list {
		if e == ep {
			f.endpoints[key] = append(list[:i], list[i+1:]...)
			break
		}
	}
	if len(f.endpoints[key]) == 0 {
		delete(f.endpoints, key)
	}
	f.mu.Unlock()
}

// FailNext arms the fabric to return a transport error for the next `times`
// POSTs whose URL contains urlSubstring — a test hook to exercise the retry
// loop. Pass times<=0 to clear.
func (f *HttpFabric) FailNext(urlSubstring string, times int) {
	f.mu.Lock()
	f.failURL = urlSubstring
	f.failTimes = times
	f.mu.Unlock()
}

// deliver routes payload to every inbox registered on the LONGEST baseUrl that
// is a prefix of fullURL (longest-match so a more-specific server wins over a
// broader one). Delivery to the matched inboxes happens off-lock. Returns
// whether at least one listener matched.
func (f *HttpFabric) deliver(fullURL string, payload NetworkPayload) bool {
	f.mu.Lock()
	var bestBase string
	found := false
	for base := range f.endpoints {
		if strings.HasPrefix(fullURL, base) && (!found || len(base) > len(bestBase)) {
			bestBase = base
			found = true
		}
	}
	var targets []*httpFabricEndpoint
	if found {
		targets = append(targets, f.endpoints[bestBase]...)
	}
	f.mu.Unlock()
	if len(targets) == 0 {
		return false
	}
	for _, ep := range targets {
		ep.inbox.Write(payload)
	}
	return true
}

// shouldFail reports (and consumes) whether the next POST to fullURL must fail.
func (f *HttpFabric) shouldFail(fullURL string) bool {
	f.mu.Lock()
	defer f.mu.Unlock()
	if f.failTimes > 0 && f.failURL != "" && strings.Contains(fullURL, f.failURL) {
		f.failTimes--
		return true
	}
	return false
}

// InMemoryHttpSender is a working HttpSender that delivers POSTs through an
// HttpFabric. A POST to a registered baseUrl returns 200 and enqueues the body
// into that endpoint's inbox; to an unregistered URL it returns 404 (a 4xx that
// the transport's retry loop does not retry); when the fabric is armed to fail
// it returns a transport error (analogous to HttpRequestException) so the retry
// path is exercised. Deterministic; no sockets.
type InMemoryHttpSender struct {
	fabric *HttpFabric
}

// NewInMemoryHttpSender builds a sender over fabric (required).
func NewInMemoryHttpSender(fabric *HttpFabric) (*InMemoryHttpSender, error) {
	if fabric == nil {
		return nil, errors.New("http fabric required")
	}
	return &InMemoryHttpSender{fabric: fabric}, nil
}

// Post delivers body to fullURL via the fabric. See InMemoryHttpSender for the
// status/error semantics.
func (s *InMemoryHttpSender) Post(ctx context.Context, fullURL, contentType string, headers map[string]string, body []byte) (HttpResponse, error) {
	if err := ctx.Err(); err != nil {
		return HttpResponse{}, err
	}
	if s.fabric.shouldFail(fullURL) {
		return HttpResponse{}, errors.New("http request failed (simulated transport error)")
	}
	payload := NewNetworkPayloadWith(body, "", MessagePriorityNormal, contentType, nil)
	// Preserve the payload id/priority headers the transport stamped so the
	// receiver can read them back (mirrors the C# X-Payload-* headers).
	if headers != nil {
		if id := headers["X-Payload-Id"]; id != "" {
			payload.ID = id
		}
		payload = payload.WithMetadata("X-Payload-Id", headers["X-Payload-Id"])
		payload = payload.WithMetadata("X-Payload-Priority", headers["X-Payload-Priority"])
	}
	if s.fabric.deliver(fullURL, payload) {
		return HttpResponse{StatusCode: 200, Body: nil}, nil
	}
	// No endpoint listening — a 404 (not retried by the transport).
	return HttpResponse{StatusCode: 404, Body: nil}, nil
}

var _ HttpSender = (*InMemoryHttpSender)(nil)

// ---------------------------------------------------------------------------
// HttpNetworkTransport — HttpNetworkTransport.cs
// ---------------------------------------------------------------------------

// httpMaxAttempts is the C# retry count (for attempt := 0; attempt < 3).
const httpMaxAttempts = 3

// HttpNetworkTransport is an INetworkTransport backed by an injected HttpSender.
// Kind() is TransportKindHttp; IsAvailable() is always true when configured
// (matches the C# `IsAvailable => true`). Send POSTs the payload to
// {baseUrl}/messages/{dest} (or /messages when no destination) with up to 3
// attempts and exponential backoff on transport failures, stamping X-Payload-Id
// / X-Payload-Priority headers, and logs an HttpRequestSummary. Receive drains
// the endpoint inbox registered on the fabric (a working pull loop). Safe for
// concurrent use.
type HttpNetworkTransport struct {
	sender  HttpSender
	baseURL string
	// fabric is optional: when the sender is an InMemoryHttpSender over a fabric,
	// the transport registers its inbox so Receive works. May be nil (pure send).
	fabric  *HttpFabric
	metrics *InMemoryHttpRequestMetrics

	mu       sync.Mutex
	running  bool
	inbox    *unboundedChannel[NetworkPayload]
	endpoint *httpFabricEndpoint // fabric handle while running; nil otherwise
}

// NewHttpNetworkTransport builds a transport posting through sender to baseUrl.
// sender and a non-blank baseUrl are required (mirrors the C# null/whitespace
// guards). fabric may be nil; pass the same fabric the sender uses to enable
// Receive. metrics may be nil (a fresh store is created).
func NewHttpNetworkTransport(sender HttpSender, baseUrl string, fabric *HttpFabric, metrics *InMemoryHttpRequestMetrics) (*HttpNetworkTransport, error) {
	if sender == nil {
		return nil, errors.New("http sender required")
	}
	if isBlank(baseUrl) {
		return nil, errors.New("baseUrl required")
	}
	if metrics == nil {
		if fabric != nil {
			metrics = fabric.Metrics
		} else {
			metrics = NewInMemoryHttpRequestMetrics()
		}
	}
	return &HttpNetworkTransport{
		sender:  sender,
		baseURL: trimTrailingSlash(baseUrl),
		fabric:  fabric,
		metrics: metrics,
		inbox:   newUnboundedChannel[NetworkPayload](),
	}, nil
}

// Kind returns TransportKindHttp.
func (t *HttpNetworkTransport) Kind() TransportKind { return TransportKindHttp }

// IsAvailable is always true when configured (matches the C# constant).
func (t *HttpNetworkTransport) IsAvailable() bool { return true }

// Metrics exposes the request metrics store.
func (t *HttpNetworkTransport) Metrics() *InMemoryHttpRequestMetrics { return t.metrics }

// Start marks the transport running and, when a fabric is present, registers its
// inbox so POSTs to this baseUrl are received. Idempotent.
func (t *HttpNetworkTransport) Start(ctx context.Context) error {
	if err := ctx.Err(); err != nil {
		return err
	}
	t.mu.Lock()
	if t.running {
		t.mu.Unlock()
		return nil
	}
	t.inbox = newUnboundedChannel[NetworkPayload]()
	inbox := t.inbox
	t.running = true
	t.mu.Unlock()
	if t.fabric != nil {
		ep := t.fabric.registerEndpoint(t.baseURL, inbox)
		t.mu.Lock()
		t.endpoint = ep
		t.mu.Unlock()
	}
	return nil
}

// Stop marks the transport not running, unregisters the inbox, and completes it
// so active Receive streams drain and close. Idempotent.
func (t *HttpNetworkTransport) Stop(ctx context.Context) error {
	t.mu.Lock()
	if !t.running {
		t.mu.Unlock()
		return nil
	}
	t.running = false
	inbox := t.inbox
	ep := t.endpoint
	t.endpoint = nil
	t.mu.Unlock()
	if t.fabric != nil && ep != nil {
		t.fabric.unregisterEndpoint(t.baseURL, ep)
	}
	inbox.Complete()
	return nil
}

// Send POSTs payload.Data to {baseUrl}/messages/{escaped dest} (or /messages).
// It retries up to 3 times with exponential backoff (2^attempt seconds) on a
// transport-level error, exactly like the C#, and logs an HttpRequestSummary for
// the terminal outcome. A successful (2xx) or non-retryable response returns
// immediately; exhausting retries returns the last error.
func (t *HttpNetworkTransport) Send(ctx context.Context, payload NetworkPayload) error {
	if err := ctx.Err(); err != nil {
		return err
	}
	dest := payload.DestinationID
	var target string
	if len(dest) > 0 {
		target = t.baseURL + "/messages/" + url.PathEscape(dest)
	} else {
		target = t.baseURL + "/messages"
	}

	headers := map[string]string{
		"X-Payload-Id":       payload.ID,
		"X-Payload-Priority": payload.Priority.String(),
	}

	start := time.Now()
	var lastErr error
	lastStatus := 0
	for attempt := 0; attempt < httpMaxAttempts; attempt++ {
		resp, err := t.sender.Post(ctx, target, payload.ContentType, headers, payload.Data)
		if err == nil {
			lastStatus = resp.StatusCode
			if resp.IsSuccess() {
				t.logRequest(target, resp.StatusCode, len(resp.Body), start)
				return nil
			}
			// Non-2xx: retry only when the status warrants it and attempts remain.
			if HttpStatusShouldRetry(resp.StatusCode) && attempt < httpMaxAttempts-1 {
				if serr := sleepCtx(ctx, backoffSeconds(attempt)); serr != nil {
					t.logRequest(target, resp.StatusCode, len(resp.Body), start)
					return serr
				}
				continue
			}
			// Terminal non-success (e.g. 404) — surface as the C# EnsureSuccess
			// would (an error), after logging.
			t.logRequest(target, resp.StatusCode, len(resp.Body), start)
			return fmt.Errorf("http send failed with status %d", resp.StatusCode)
		}
		// Transport error (HttpRequestException analogue): retry with backoff.
		lastErr = err
		if attempt < httpMaxAttempts-1 {
			if serr := sleepCtx(ctx, backoffSeconds(attempt)); serr != nil {
				return serr
			}
			continue
		}
	}
	t.logRequest(target, lastStatus, 0, start)
	if lastErr != nil {
		return lastErr
	}
	return nil
}

func (t *HttpNetworkTransport) logRequest(endpointID string, status, respBytes int, start time.Time) {
	t.metrics.Log(HttpRequestSummary{
		EndpointId:    endpointID,
		StatusCode:    status,
		Latency:       time.Since(start),
		ResponseBytes: respBytes,
		AtUtc:         time.Now().UTC(),
	})
}

// Receive returns a stream of inbound payloads delivered to this endpoint's
// baseUrl via the fabric. Payloads delivered before this call are replayed first
// (unbounded buffering). When no fabric is wired the stream simply blocks until
// ctx cancellation or Stop (HTTP pull with no server-push source), mirroring the
// C# empty ReceiveAsync while remaining a real, terminating stream. The stream
// closes on ctx cancellation or Stop.
func (t *HttpNetworkTransport) Receive(ctx context.Context) <-chan NetworkPayload {
	t.mu.Lock()
	inbox := t.inbox
	t.mu.Unlock()
	return inbox.ReadAll(ctx)
}

var _ INetworkTransport = (*HttpNetworkTransport)(nil)

// ---------------------------------------------------------------------------
// small helpers
// ---------------------------------------------------------------------------

// backoffSeconds returns the C# Task.Delay(2^attempt seconds) backoff.
func backoffSeconds(attempt int) time.Duration {
	secs := 1 << uint(attempt) // 2^attempt
	return time.Duration(secs) * time.Second
}

// sleepCtx sleeps for d honouring ctx cancellation. Returns ctx.Err() if
// cancelled during the wait.
func sleepCtx(ctx context.Context, d time.Duration) error {
	if d <= 0 {
		return ctx.Err()
	}
	timer := time.NewTimer(d)
	defer timer.Stop()
	select {
	case <-timer.C:
		return nil
	case <-ctx.Done():
		return ctx.Err()
	}
}

// trimTrailingSlash strips a single trailing '/' (matches TrimEnd('/') for the
// common single-slash case; repeated slashes are also stripped).
func trimTrailingSlash(s string) string {
	for len(s) > 0 && s[len(s)-1] == '/' {
		s = s[:len(s)-1]
	}
	return s
}

// normalizeBaseURL trims a trailing slash for stable fabric keying.
func normalizeBaseURL(s string) string { return trimTrailingSlash(s) }
