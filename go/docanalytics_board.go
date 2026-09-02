// docanalytics_board.go
//
// Ports CircleAI.DocAnalytics (Contracts.cs / InMemoryDocumentTracker.cs /
// NullImplementations.cs):
//   DocumentView / DocumentInsight
//   IDocumentTracker / IDocumentInsights
//   InMemoryDocumentTracker (implements both)
//   NullDocumentTracker / NullDocumentInsights
//
// The tracker records every view and computes insights (total views, unique
// viewers, average duration seconds) on demand.

package circleai

import (
	"context"
	"errors"
	"sort"
	"strings"
	"sync"
	"time"
)

// DocumentView is one recorded document view. Ports DocumentView.
type DocumentView struct {
	DocumentID  string
	ViewerID    string
	AtUTC       time.Time
	Duration    time.Duration
	PagesViewed int
}

// DocumentInsight is computed document analytics. Ports DocumentInsight.
type DocumentInsight struct {
	DocumentID         string
	TotalViews         int
	UniqueViewers      int
	AvgDurationSeconds float64
}

// DocumentViewCount is one row of TopDocuments: a document and its view count.
// Ports the C# value tuple (string DocumentId, int Views).
type DocumentViewCount struct {
	DocumentID string
	Views      int
}

// IDocumentTracker records and lists document views. Ports IDocumentTracker.
type IDocumentTracker interface {
	BackendID() string
	RecordView(ctx context.Context, view DocumentView) error
	ListViews(ctx context.Context, documentID string) ([]DocumentView, error)
}

// IDocumentInsights computes document insights. Ports IDocumentInsights.
type IDocumentInsights interface {
	BackendID() string
	// Compute returns insights, or (zero,false) when no views exist.
	Compute(ctx context.Context, documentID string) (DocumentInsight, bool, error)
}

// ---------------------------------------------------------------------------
// InMemoryDocumentTracker
// ---------------------------------------------------------------------------

// InMemoryDocumentTracker is a thread-safe view tracker + insights computer.
// Ports InMemoryDocumentTracker (implements IDocumentTracker + IDocumentInsights).
type InMemoryDocumentTracker struct {
	mu    sync.Mutex
	byDoc map[string][]DocumentView
}

// NewInMemoryDocumentTracker constructs an empty tracker.
func NewInMemoryDocumentTracker() *InMemoryDocumentTracker {
	return &InMemoryDocumentTracker{byDoc: make(map[string][]DocumentView)}
}

// BackendID returns "in-memory".
func (t *InMemoryDocumentTracker) BackendID() string { return "in-memory" }

// RecordView appends a view. Ports RecordViewAsync.
func (t *InMemoryDocumentTracker) RecordView(ctx context.Context, view DocumentView) error {
	if strings.TrimSpace(view.DocumentID) == "" {
		return errors.New("DocumentId required")
	}
	t.mu.Lock()
	t.byDoc[view.DocumentID] = append(t.byDoc[view.DocumentID], view)
	t.mu.Unlock()
	return nil
}

// ListViews returns the views for documentID. Ports ListViewsAsync.
func (t *InMemoryDocumentTracker) ListViews(ctx context.Context, documentID string) ([]DocumentView, error) {
	if strings.TrimSpace(documentID) == "" {
		return nil, errors.New("documentId required")
	}
	t.mu.Lock()
	defer t.mu.Unlock()
	views, ok := t.byDoc[documentID]
	if !ok {
		return []DocumentView{}, nil
	}
	return append([]DocumentView(nil), views...), nil
}

// Compute returns insights for documentID, or (zero,false) when no views exist.
// Ports ComputeAsync.
func (t *InMemoryDocumentTracker) Compute(ctx context.Context, documentID string) (DocumentInsight, bool, error) {
	if strings.TrimSpace(documentID) == "" {
		return DocumentInsight{}, false, errors.New("documentId required")
	}
	t.mu.Lock()
	defer t.mu.Unlock()
	views, ok := t.byDoc[documentID]
	if !ok || len(views) == 0 {
		return DocumentInsight{}, false, nil
	}
	total := len(views)
	uniq := make(map[string]struct{})
	var totalSeconds float64
	for _, v := range views {
		uniq[v.ViewerID] = struct{}{}
		totalSeconds += v.Duration.Seconds()
	}
	return DocumentInsight{
		DocumentID:         documentID,
		TotalViews:         total,
		UniqueViewers:      len(uniq),
		AvgDurationSeconds: totalSeconds / float64(total),
	}, true, nil
}

// DocumentCount returns the number of distinct documents with at least one
// recorded view. Ports InMemoryDocumentTracker.DocumentCount.
func (t *InMemoryDocumentTracker) DocumentCount() int {
	t.mu.Lock()
	defer t.mu.Unlock()
	return len(t.byDoc)
}

// TotalViews returns the total views recorded across every tracked document.
// Ports InMemoryDocumentTracker.TotalViews (Sum(v => v.Count)).
func (t *InMemoryDocumentTracker) TotalViews() int {
	t.mu.Lock()
	defer t.mu.Unlock()
	total := 0
	for _, views := range t.byDoc {
		total += len(views)
	}
	return total
}

// Clear drops all recorded views for a document, returning true if anything was
// removed. Errors on a blank documentID. Ports InMemoryDocumentTracker.Clear
// (TryRemove).
func (t *InMemoryDocumentTracker) Clear(documentID string) (bool, error) {
	if strings.TrimSpace(documentID) == "" {
		return false, errors.New("documentId required")
	}
	t.mu.Lock()
	defer t.mu.Unlock()
	_, ok := t.byDoc[documentID]
	delete(t.byDoc, documentID)
	return ok, nil
}

// TopDocuments returns the most-viewed documents, highest first, capped at topK.
// Errors on topK <= 0. Ties resolve deterministically by DocumentID ascending
// (the underlying map is unordered; the port sorts for stable output). Ports
// InMemoryDocumentTracker.TopDocuments.
func (t *InMemoryDocumentTracker) TopDocuments(topK int) ([]DocumentViewCount, error) {
	if topK <= 0 {
		return nil, errors.New("topK out of range")
	}
	t.mu.Lock()
	out := make([]DocumentViewCount, 0, len(t.byDoc))
	for id, views := range t.byDoc {
		out = append(out, DocumentViewCount{DocumentID: id, Views: len(views)})
	}
	t.mu.Unlock()
	// DocumentID-ascending pre-sort makes the count-descending sort deterministic
	// on ties (stable sort preserves the id order for equal counts).
	sort.SliceStable(out, func(i, j int) bool { return out[i].DocumentID < out[j].DocumentID })
	sort.SliceStable(out, func(i, j int) bool { return out[i].Views > out[j].Views })
	if len(out) > topK {
		out = out[:topK]
	}
	return out, nil
}

// RecentViews returns the most recent views for a document, newest first, capped
// at limit. Errors on a blank documentID or limit <= 0. Ports
// InMemoryDocumentTracker.RecentViews.
func (t *InMemoryDocumentTracker) RecentViews(documentID string, limit int) ([]DocumentView, error) {
	if strings.TrimSpace(documentID) == "" {
		return nil, errors.New("documentId required")
	}
	if limit <= 0 {
		return nil, errors.New("limit out of range")
	}
	t.mu.Lock()
	views, ok := t.byDoc[documentID]
	out := make([]DocumentView, len(views))
	copy(out, views)
	t.mu.Unlock()
	if !ok {
		return []DocumentView{}, nil
	}
	sort.SliceStable(out, func(i, j int) bool { return out[i].AtUTC.After(out[j].AtUTC) })
	if len(out) > limit {
		out = out[:limit]
	}
	return out, nil
}

// TotalPagesViewed returns the sum of PagesViewed across every recorded view of a
// document (0 when unknown). Errors on a blank documentID. Ports
// InMemoryDocumentTracker.TotalPagesViewed.
func (t *InMemoryDocumentTracker) TotalPagesViewed(documentID string) (int, error) {
	if strings.TrimSpace(documentID) == "" {
		return 0, errors.New("documentId required")
	}
	t.mu.Lock()
	defer t.mu.Unlock()
	total := 0
	for _, v := range t.byDoc[documentID] {
		total += v.PagesViewed
	}
	return total, nil
}

// MostEngagedViewer returns the viewer who spent the most cumulative time on a
// document, or ("",false) when the document has no views. Errors on a blank
// documentID. Viewers are grouped in first-encounter order over the view list and
// stably ordered by cumulative duration descending, matching the C#
// GroupBy(Ordinal) + OrderByDescending + First. Ports
// InMemoryDocumentTracker.MostEngagedViewer.
func (t *InMemoryDocumentTracker) MostEngagedViewer(documentID string) (string, bool, error) {
	if strings.TrimSpace(documentID) == "" {
		return "", false, errors.New("documentId required")
	}
	t.mu.Lock()
	defer t.mu.Unlock()
	views, ok := t.byDoc[documentID]
	if !ok || len(views) == 0 {
		return "", false, nil
	}
	type group struct {
		viewer  string
		seconds float64
		order   int
	}
	byViewer := make(map[string]*group)
	ordered := make([]*group, 0)
	for _, v := range views {
		g, exists := byViewer[v.ViewerID]
		if !exists {
			g = &group{viewer: v.ViewerID, order: len(ordered)}
			byViewer[v.ViewerID] = g
			ordered = append(ordered, g)
		}
		g.seconds += v.Duration.Seconds()
	}
	sort.SliceStable(ordered, func(i, j int) bool { return ordered[i].seconds > ordered[j].seconds })
	return ordered[0].viewer, true, nil
}

var (
	_ IDocumentTracker  = (*InMemoryDocumentTracker)(nil)
	_ IDocumentInsights = (*InMemoryDocumentTracker)(nil)
)

// ---------------------------------------------------------------------------
// Null implementations
// ---------------------------------------------------------------------------

// NullDocumentTracker is a fail-safe tracker. Ports NullDocumentTracker.
type NullDocumentTracker struct{}

// NullDocumentTrackerInstance is the shared singleton.
var NullDocumentTrackerInstance = NullDocumentTracker{}

func (NullDocumentTracker) BackendID() string                              { return "null" }
func (NullDocumentTracker) RecordView(context.Context, DocumentView) error { return nil }
func (NullDocumentTracker) ListViews(context.Context, string) ([]DocumentView, error) {
	return []DocumentView{}, nil
}

// NullDocumentInsights is a fail-safe insights computer. Ports
// NullDocumentInsights.
type NullDocumentInsights struct{}

// NullDocumentInsightsInstance is the shared singleton.
var NullDocumentInsightsInstance = NullDocumentInsights{}

func (NullDocumentInsights) BackendID() string { return "null" }
func (NullDocumentInsights) Compute(context.Context, string) (DocumentInsight, bool, error) {
	return DocumentInsight{}, false, nil
}

var (
	_ IDocumentTracker  = NullDocumentTracker{}
	_ IDocumentInsights = NullDocumentInsights{}
)
