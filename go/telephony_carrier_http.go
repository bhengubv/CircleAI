// telephony_carrier_http.go
//
// The injected HTTP seam the real carrier bindings (Twilio/Telnyx/Plivo) speak
// instead of a live HttpClient. The C# carriers issue GET/POST/DELETE/PATCH with
// form or JSON bodies against a base address + relative path, using Basic or
// Bearer auth headers, then parse specific JSON fields from the response. Per the
// porting rules the transport is injected so the bindings are deterministic and
// make no real calls. CarrierHTTP.Do carries the HTTP method (unlike the POST-only
// package HTTPDoer), the resolved absolute URL, headers, and body, and returns
// the status code + body — everything the carriers need off the wire.
//
// FakeCarrierTransport is a scripted, deterministic implementation for tests and
// hermetic hosts: it records every request and replies from a queue (or per
// method+path matcher), with no network.

package circleai

import (
	"errors"
	"strings"
	"sync"
)

// CarrierHTTPRequest is one outbound carrier request. URL is already absolute
// (base address + path resolved by the binding). Method is "GET"/"POST"/
// "DELETE"/"PATCH". Body is nil for bodyless verbs.
type CarrierHTTPRequest struct {
	Method  string
	URL     string
	Headers map[string]string
	Body    []byte
}

// CarrierHTTPResponse is the transport's reply: an HTTP status code and body.
type CarrierHTTPResponse struct {
	StatusCode int
	Body       []byte
}

// CarrierHTTP is the minimal HTTP surface the carrier bindings need. Ports the
// dependency the C# carriers get from HttpClient; injecting it keeps them
// hermetic. A binding treats any 2xx as success (EnsureSuccessStatusCode) and
// otherwise fails or logs per the C# method.
type CarrierHTTP interface {
	// Do performs the request and returns the response, or a transport error.
	Do(req *CarrierHTTPRequest) (*CarrierHTTPResponse, error)
}

// carrierHTTPStatusOK reports whether code is a 2xx (EnsureSuccessStatusCode /
// IsSuccessStatusCode semantics).
func carrierHTTPStatusOK(code int) bool { return code >= 200 && code < 300 }

// ---------------------------------------------------------------------------
// FakeCarrierTransport — scripted deterministic transport
// ---------------------------------------------------------------------------

// FakeCarrierTransport is a deterministic CarrierHTTP for tests/hermetic hosts.
// It matches queued responses by (method, path-prefix) when matchers are set,
// otherwise pops from a FIFO queue. Every request is recorded for assertions.
type FakeCarrierTransport struct {
	mu       sync.Mutex
	queue    []*CarrierHTTPResponse
	matchers []fakeMatcher
	requests []CarrierHTTPRequest
	failNext error
}

type fakeMatcher struct {
	method     string // "" matches any
	pathPrefix string // matched against the URL's path+query substring
	resp       *CarrierHTTPResponse
	err        error
}

// NewFakeCarrierTransport constructs an empty transport (every Do errors until
// responses are enqueued or matchers added).
func NewFakeCarrierTransport() *FakeCarrierTransport { return &FakeCarrierTransport{} }

// EnqueueJSON queues a JSON-body response with the given status code, consumed in
// FIFO order by unmatched requests.
func (f *FakeCarrierTransport) EnqueueJSON(statusCode int, body string) *FakeCarrierTransport {
	f.mu.Lock()
	defer f.mu.Unlock()
	f.queue = append(f.queue, &CarrierHTTPResponse{StatusCode: statusCode, Body: []byte(body)})
	return f
}

// EnqueueStatus queues an empty-body response with the given status code.
func (f *FakeCarrierTransport) EnqueueStatus(statusCode int) *FakeCarrierTransport {
	f.mu.Lock()
	defer f.mu.Unlock()
	f.queue = append(f.queue, &CarrierHTTPResponse{StatusCode: statusCode})
	return f
}

// OnRequest registers a matcher: the next request whose method matches (or method
// == "") and whose absolute URL contains pathContains replies with (statusCode,
// body). Matchers take priority over the FIFO queue and are consumed on use.
func (f *FakeCarrierTransport) OnRequest(method, pathContains string, statusCode int, body string) *FakeCarrierTransport {
	f.mu.Lock()
	defer f.mu.Unlock()
	f.matchers = append(f.matchers, fakeMatcher{
		method:     strings.ToUpper(method),
		pathPrefix: pathContains,
		resp:       &CarrierHTTPResponse{StatusCode: statusCode, Body: []byte(body)},
	})
	return f
}

// FailNext makes the next Do return err (a transport-level failure).
func (f *FakeCarrierTransport) FailNext(err error) *FakeCarrierTransport {
	f.mu.Lock()
	defer f.mu.Unlock()
	f.failNext = err
	return f
}

// Do records the request and returns the matched/queued response, or an error
// when nothing is available (surfacing test-setup gaps loudly).
func (f *FakeCarrierTransport) Do(req *CarrierHTTPRequest) (*CarrierHTTPResponse, error) {
	f.mu.Lock()
	defer f.mu.Unlock()
	f.requests = append(f.requests, cloneCarrierRequest(req))

	if f.failNext != nil {
		err := f.failNext
		f.failNext = nil
		return nil, err
	}

	// Matchers first (consume on use).
	for i, m := range f.matchers {
		if (m.method == "" || m.method == strings.ToUpper(req.Method)) && strings.Contains(req.URL, m.pathPrefix) {
			f.matchers = append(f.matchers[:i:i], f.matchers[i+1:]...)
			if m.err != nil {
				return nil, m.err
			}
			return m.resp, nil
		}
	}

	if len(f.queue) > 0 {
		resp := f.queue[0]
		f.queue = f.queue[1:]
		return resp, nil
	}
	return nil, errors.New("FakeCarrierTransport: no response queued for " + req.Method + " " + req.URL)
}

// Requests returns a copy of every request recorded so far, in order.
func (f *FakeCarrierTransport) Requests() []CarrierHTTPRequest {
	f.mu.Lock()
	defer f.mu.Unlock()
	return append([]CarrierHTTPRequest(nil), f.requests...)
}

// LastRequest returns the most recent request and true, or (zero,false).
func (f *FakeCarrierTransport) LastRequest() (CarrierHTTPRequest, bool) {
	f.mu.Lock()
	defer f.mu.Unlock()
	if len(f.requests) == 0 {
		return CarrierHTTPRequest{}, false
	}
	return f.requests[len(f.requests)-1], true
}

func cloneCarrierRequest(req *CarrierHTTPRequest) CarrierHTTPRequest {
	c := CarrierHTTPRequest{Method: req.Method, URL: req.URL}
	if req.Headers != nil {
		c.Headers = make(map[string]string, len(req.Headers))
		for k, v := range req.Headers {
			c.Headers[k] = v
		}
	}
	if req.Body != nil {
		c.Body = append([]byte(nil), req.Body...)
	}
	return c
}

var _ CarrierHTTP = (*FakeCarrierTransport)(nil)
