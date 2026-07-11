// observability_board.go
//
// Ports CircleAI.Observability (Contracts.cs / InMemoryObservability.cs /
// NullImplementations.cs):
//   MetricSample / TraceSpan / DashboardSpec
//   IMetricSink / ITraceSink / IDashboardPublisher
//   InMemoryMetricSink / InMemoryTraceSink / InMemoryDashboardPublisher
//   NullMetricSink / NullTraceSink / NullDashboardPublisher
//
// TimeSpan Duration -> time.Duration; DateTimeOffset -> time.Time. The metric
// sink aggregates samples per name; the trace sink stores spans per traceId
// (Read orders by StartUtc); the dashboard publisher round-trips specs by id.

package circleai

import (
	"context"
	"errors"
	"sort"
	"strings"
	"sync"
	"time"
)

// MetricSample is one emitted metric. Ports MetricSample. Tags nil == none.
type MetricSample struct {
	Name  string
	Value float64
	Tags  map[string]string
}

// TraceSpan is one trace span. Ports TraceSpan. ParentSpanID is a *string;
// Attributes nil == none.
type TraceSpan struct {
	TraceID      string
	SpanID       string
	ParentSpanID *string
	Name         string
	StartUTC     time.Time
	Duration     time.Duration
	Attributes   map[string]string
}

// DashboardSpec is a published dashboard spec. Ports DashboardSpec.
type DashboardSpec struct {
	DashboardID string
	Title       string
	JSONBlob    string
}

// IMetricSink is a metric sink. Ports IMetricSink.
type IMetricSink interface {
	BackendID() string
	Emit(ctx context.Context, sample MetricSample) error
}

// ITraceSink is a trace sink. Ports ITraceSink.
type ITraceSink interface {
	BackendID() string
	Emit(ctx context.Context, span TraceSpan) error
}

// IDashboardPublisher is a dashboard publisher. Ports IDashboardPublisher.
type IDashboardPublisher interface {
	BackendID() string
	Publish(ctx context.Context, spec DashboardSpec) error
}

// ---------------------------------------------------------------------------
// InMemoryMetricSink
// ---------------------------------------------------------------------------

// InMemoryMetricSink aggregates samples per name. Ports InMemoryMetricSink.
type InMemoryMetricSink struct {
	mu     sync.Mutex
	byName map[string][]MetricSample
}

// NewInMemoryMetricSink constructs an empty sink.
func NewInMemoryMetricSink() *InMemoryMetricSink {
	return &InMemoryMetricSink{byName: make(map[string][]MetricSample)}
}

// BackendID returns "in-memory".
func (s *InMemoryMetricSink) BackendID() string { return "in-memory" }

// Emit records a sample. Ports EmitAsync. Errors on empty Name.
func (s *InMemoryMetricSink) Emit(ctx context.Context, sample MetricSample) error {
	if strings.TrimSpace(sample.Name) == "" {
		return errors.New("Name required")
	}
	s.mu.Lock()
	s.byName[sample.Name] = append(s.byName[sample.Name], sample)
	s.mu.Unlock()
	return nil
}

// Read returns all samples recorded under name. Ports Read.
func (s *InMemoryMetricSink) Read(name string) []MetricSample {
	s.mu.Lock()
	defer s.mu.Unlock()
	list, ok := s.byName[name]
	if !ok {
		return []MetricSample{}
	}
	return append([]MetricSample(nil), list...)
}

// MetricNames returns the sorted set of recorded metric names. Ports
// MetricNames.
func (s *InMemoryMetricSink) MetricNames() []string {
	s.mu.Lock()
	names := make([]string, 0, len(s.byName))
	for k := range s.byName {
		names = append(names, k)
	}
	s.mu.Unlock()
	sort.Strings(names)
	return names
}

var _ IMetricSink = (*InMemoryMetricSink)(nil)

// ---------------------------------------------------------------------------
// InMemoryTraceSink
// ---------------------------------------------------------------------------

// InMemoryTraceSink stores spans per traceId. Ports InMemoryTraceSink.
type InMemoryTraceSink struct {
	mu      sync.Mutex
	byTrace map[string][]TraceSpan
}

// NewInMemoryTraceSink constructs an empty sink.
func NewInMemoryTraceSink() *InMemoryTraceSink {
	return &InMemoryTraceSink{byTrace: make(map[string][]TraceSpan)}
}

// BackendID returns "in-memory".
func (s *InMemoryTraceSink) BackendID() string { return "in-memory" }

// Emit records a span. Ports EmitAsync. Errors on empty TraceId.
func (s *InMemoryTraceSink) Emit(ctx context.Context, span TraceSpan) error {
	if strings.TrimSpace(span.TraceID) == "" {
		return errors.New("TraceId required")
	}
	s.mu.Lock()
	s.byTrace[span.TraceID] = append(s.byTrace[span.TraceID], span)
	s.mu.Unlock()
	return nil
}

// Read returns the spans for traceId ordered by StartUtc. Ports Read.
func (s *InMemoryTraceSink) Read(traceID string) []TraceSpan {
	s.mu.Lock()
	list, ok := s.byTrace[traceID]
	out := append([]TraceSpan(nil), list...)
	s.mu.Unlock()
	if !ok {
		return []TraceSpan{}
	}
	sort.SliceStable(out, func(i, j int) bool { return out[i].StartUTC.Before(out[j].StartUTC) })
	return out
}

var _ ITraceSink = (*InMemoryTraceSink)(nil)

// ---------------------------------------------------------------------------
// InMemoryDashboardPublisher
// ---------------------------------------------------------------------------

// InMemoryDashboardPublisher round-trips specs by id. Ports
// InMemoryDashboardPublisher.
type InMemoryDashboardPublisher struct {
	mu    sync.Mutex
	specs map[string]DashboardSpec
}

// NewInMemoryDashboardPublisher constructs an empty publisher.
func NewInMemoryDashboardPublisher() *InMemoryDashboardPublisher {
	return &InMemoryDashboardPublisher{specs: make(map[string]DashboardSpec)}
}

// BackendID returns "in-memory".
func (p *InMemoryDashboardPublisher) BackendID() string { return "in-memory" }

// Publish stores (or replaces by DashboardId) a spec. Ports PublishAsync.
func (p *InMemoryDashboardPublisher) Publish(ctx context.Context, spec DashboardSpec) error {
	if strings.TrimSpace(spec.DashboardID) == "" {
		return errors.New("DashboardId required")
	}
	p.mu.Lock()
	p.specs[spec.DashboardID] = spec
	p.mu.Unlock()
	return nil
}

// Get returns the spec for dashboardID, or (zero,false). Ports Get.
func (p *InMemoryDashboardPublisher) Get(dashboardID string) (DashboardSpec, bool) {
	p.mu.Lock()
	s, ok := p.specs[dashboardID]
	p.mu.Unlock()
	return s, ok
}

// All returns all specs sorted by DashboardId. Ports All.
func (p *InMemoryDashboardPublisher) All() []DashboardSpec {
	p.mu.Lock()
	out := make([]DashboardSpec, 0, len(p.specs))
	for _, v := range p.specs {
		out = append(out, v)
	}
	p.mu.Unlock()
	sort.SliceStable(out, func(i, j int) bool { return out[i].DashboardID < out[j].DashboardID })
	return out
}

var _ IDashboardPublisher = (*InMemoryDashboardPublisher)(nil)

// ---------------------------------------------------------------------------
// Null implementations
// ---------------------------------------------------------------------------

// NullMetricSink drops all metrics. Ports NullMetricSink.
type NullMetricSink struct{}

// NullMetricSinkInstance is the shared singleton.
var NullMetricSinkInstance = NullMetricSink{}

func (NullMetricSink) BackendID() string                        { return "null" }
func (NullMetricSink) Emit(context.Context, MetricSample) error { return nil }

// NullTraceSink drops all spans. Ports NullTraceSink.
type NullTraceSink struct{}

// NullTraceSinkInstance is the shared singleton.
var NullTraceSinkInstance = NullTraceSink{}

func (NullTraceSink) BackendID() string                     { return "null" }
func (NullTraceSink) Emit(context.Context, TraceSpan) error { return nil }

// NullDashboardPublisher drops all specs. Ports NullDashboardPublisher.
type NullDashboardPublisher struct{}

// NullDashboardPublisherInstance is the shared singleton.
var NullDashboardPublisherInstance = NullDashboardPublisher{}

func (NullDashboardPublisher) BackendID() string                          { return "null" }
func (NullDashboardPublisher) Publish(context.Context, DashboardSpec) error { return nil }

var (
	_ IMetricSink         = NullMetricSink{}
	_ ITraceSink          = NullTraceSink{}
	_ IDashboardPublisher = NullDashboardPublisher{}
)
