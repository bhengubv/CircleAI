// network_http_test.go
//
// Verifies network_http.go:
//   - HttpStatusFamily classifiers + ShouldRetry
//   - HttpCacheKey value equality as a map key
//   - InMemoryHttpRequestMetrics: register/get endpoint, log + recent ordering,
//     avg-2xx-latency
//   - HttpNetworkTransport: lifecycle, always-available, POST delivery through
//     the in-memory sender to {baseUrl}/messages/{dest}, X-Payload headers,
//     request logging, retry+backoff on transport failure, non-retryable 404
//     surfaced as error, Receive of pushed payloads

package circleai_test

import (
	"context"
	"testing"
	"time"

	circleai "github.com/bhengubv/CircleAI/go"
)

func TestHttpStatusFamily(t *testing.T) {
	if !circleai.HttpStatusIs2xx(204) || circleai.HttpStatusIs2xx(300) {
		t.Error("2xx classifier wrong")
	}
	if !circleai.HttpStatusIs3xx(301) || !circleai.HttpStatusIs4xx(404) || !circleai.HttpStatusIs5xx(503) {
		t.Error("3xx/4xx/5xx classifier wrong")
	}
	for _, s := range []int{408, 425, 429, 500, 502, 503} {
		if !circleai.HttpStatusShouldRetry(s) {
			t.Errorf("ShouldRetry(%d) should be true", s)
		}
	}
	for _, s := range []int{200, 400, 404} {
		if circleai.HttpStatusShouldRetry(s) {
			t.Errorf("ShouldRetry(%d) should be false", s)
		}
	}
}

func TestHttpCacheKey_MapEquality(t *testing.T) {
	k1 := circleai.HttpCacheKey{Method: "GET", FullUri: "https://x/a", AcceptHeader: "application/json"}
	k2 := circleai.HttpCacheKey{Method: "GET", FullUri: "https://x/a", AcceptHeader: "application/json"}
	m := map[circleai.HttpCacheKey]int{k1: 1}
	if m[k2] != 1 {
		t.Error("equal HttpCacheKeys should hash/compare equal as map keys")
	}
	k3 := circleai.HttpCacheKey{Method: "POST", FullUri: "https://x/a", AcceptHeader: "application/json"}
	if _, ok := m[k3]; ok {
		t.Error("different HttpCacheKey should not match")
	}
}

func TestHttpRequestMetrics_LogAndAvg(t *testing.T) {
	m := circleai.NewInMemoryHttpRequestMetrics()
	m.Register("ep", circleai.HttpEndpointDescriptor{Method: "POST", BaseUri: "https://x", Path: "/m"})
	if got, ok := m.GetEndpoint("ep"); !ok || got.Method != "POST" {
		t.Errorf("GetEndpoint = %+v ok=%v", got, ok)
	}
	now := time.Now().UTC()
	m.Log(circleai.HttpRequestSummary{EndpointId: "ep", StatusCode: 200, Latency: 10 * time.Millisecond, AtUtc: now.Add(1 * time.Second)})
	m.Log(circleai.HttpRequestSummary{EndpointId: "ep", StatusCode: 200, Latency: 30 * time.Millisecond, AtUtc: now.Add(2 * time.Second)})
	m.Log(circleai.HttpRequestSummary{EndpointId: "ep", StatusCode: 500, Latency: 99 * time.Millisecond, AtUtc: now.Add(3 * time.Second)})

	if avg := m.Avg2xxLatencyMs("ep"); avg != 20 {
		t.Errorf("Avg2xxLatencyMs = %v want 20 (only 2xx counted)", avg)
	}
	recent := m.RecentRequests(1)
	if len(recent) != 1 || recent[0].StatusCode != 500 {
		t.Errorf("RecentRequests(1) = %+v want most-recent (500)", recent)
	}
}

func TestHttpTransport_LifecycleAndDelivery(t *testing.T) {
	fab := circleai.NewHttpFabric(nil)
	sender, err := circleai.NewInMemoryHttpSender(fab)
	if err != nil {
		t.Fatal(err)
	}
	// The receiver models the server: it Starts (registering a listener at its
	// baseUrl) and is consumed via Receive. The client sender targets that same
	// baseUrl but is a pure client (not Started), so it registers no listener —
	// the POST is routed to the server only, no self-delivery.
	recv, err := circleai.NewHttpNetworkTransport(sender, "https://node-b/api/", fab, nil)
	if err != nil {
		t.Fatal(err)
	}
	client, _ := circleai.NewHttpNetworkTransport(sender, "https://node-b/api", fab, nil)

	if client.Kind() != circleai.TransportKindHttp {
		t.Errorf("Kind = %v", client.Kind())
	}
	if !client.IsAvailable() {
		t.Error("http transport should always report available")
	}
	if err := recv.Start(context.Background()); err != nil {
		t.Fatal(err)
	}

	rctx, cancel := context.WithCancel(context.Background())
	defer cancel()
	inbound := recv.Receive(rctx)

	payload := circleai.NewNetworkPayloadWith([]byte("hello"), "user42", circleai.MessagePriorityHigh, "application/json", nil)
	if err := client.Send(context.Background(), payload); err != nil {
		t.Fatalf("send failed: %v", err)
	}

	got := recvOne(t, inbound)
	if string(got.Data) != "hello" {
		t.Errorf("delivered body = %q", string(got.Data))
	}
	// X-Payload headers round-tripped into metadata.
	if got.Metadata["X-Payload-Priority"] != "High" {
		t.Errorf("priority header = %q want High", got.Metadata["X-Payload-Priority"])
	}
	if got.Metadata["X-Payload-Id"] != payload.ID {
		t.Errorf("id header = %q want %q", got.Metadata["X-Payload-Id"], payload.ID)
	}

	// A 2xx request was logged for the /messages/user42 endpoint.
	recent := client.Metrics().RecentRequests(10)
	if len(recent) == 0 || recent[0].StatusCode != 200 {
		t.Errorf("expected a 200 request log, got %+v", recent)
	}
}

func TestHttpTransport_RetryThenSucceed(t *testing.T) {
	fab := circleai.NewHttpFabric(nil)
	sender, _ := circleai.NewInMemoryHttpSender(fab)
	recv, _ := circleai.NewHttpNetworkTransport(sender, "https://n/api", fab, nil)
	client, _ := circleai.NewHttpNetworkTransport(sender, "https://n/api", fab, nil)
	_ = recv.Start(context.Background()) // server listens; client is pure sender

	// Force the first POST to fail (transport error) -> retried, second succeeds.
	fab.FailNext("/messages/dest", 1)

	rctx, cancel := context.WithCancel(context.Background())
	defer cancel()
	inbound := recv.Receive(rctx)

	// The attempt-0 backoff is 2^0 = 1s (real); run the send off-goroutine and
	// wait on delivery with headroom.
	done := make(chan error, 1)
	go func() {
		done <- client.Send(context.Background(), circleai.NewNetworkPayload([]byte("retryme"), "dest"))
	}()

	got := recvOneLong(t, inbound, 4*time.Second)
	if string(got.Data) != "retryme" {
		t.Errorf("delivered after retry = %q", string(got.Data))
	}
	select {
	case err := <-done:
		if err != nil {
			t.Errorf("send should ultimately succeed: %v", err)
		}
	case <-time.After(4 * time.Second):
		t.Error("send did not complete")
	}
}

func TestHttpTransport_NoEndpointIs404Error(t *testing.T) {
	fab := circleai.NewHttpFabric(nil)
	sender, _ := circleai.NewInMemoryHttpSender(fab)
	// Client posts to a baseUrl no transport is listening on (nothing Started
	// there), so the POST 404s. Not Started -> registers no self-listener.
	client, _ := circleai.NewHttpNetworkTransport(sender, "https://void/api", fab, nil)

	err := client.Send(context.Background(), circleai.NewNetworkPayload([]byte("x"), "d"))
	if err == nil {
		t.Error("send to a URL with no listener should error (404, not retried)")
	}
	recent := client.Metrics().RecentRequests(10)
	if len(recent) == 0 || recent[0].StatusCode != 404 {
		t.Errorf("expected a 404 request log, got %+v", recent)
	}
}

func TestHttpTransport_Guards(t *testing.T) {
	fab := circleai.NewHttpFabric(nil)
	sender, _ := circleai.NewInMemoryHttpSender(fab)
	if _, err := circleai.NewHttpNetworkTransport(nil, "https://x", fab, nil); err == nil {
		t.Error("nil sender should be rejected")
	}
	if _, err := circleai.NewHttpNetworkTransport(sender, "   ", fab, nil); err == nil {
		t.Error("blank baseUrl should be rejected")
	}
	if _, err := circleai.NewInMemoryHttpSender(nil); err == nil {
		t.Error("nil fabric should be rejected for sender")
	}
}

// recvOneLong reads one payload from ch or fails after a longer timeout (used
// where a real backoff delay is in play).
func recvOneLong(t *testing.T, ch <-chan circleai.NetworkPayload, d time.Duration) circleai.NetworkPayload {
	t.Helper()
	select {
	case p, ok := <-ch:
		if !ok {
			t.Fatal("channel closed before a payload arrived")
		}
		return p
	case <-time.After(d):
		t.Fatal("timed out waiting for a payload")
		return circleai.NetworkPayload{}
	}
}
