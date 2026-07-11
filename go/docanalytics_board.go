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

func (NullDocumentTracker) BackendID() string                          { return "null" }
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
