// crm_board.go
//
// Ports the CircleAI.CRM vertical (Contracts.cs / InMemoryCrm.cs /
// NullImplementations.cs):
//   Contact / Company / Deal / Activity (records)   -> value structs
//   IContactStore / IDealPipeline / IActivityLog     -> ContactStore /
//       DealPipeline / ActivityLog interfaces (I-prefix dropped)
//   InMemoryContactStore / InMemoryDealPipeline /
//       InMemoryActivityLog                          -> in-memory impls
//   NullContactStore / NullDealPipeline /
//       NullActivityLog                              -> fail-closed defaults
//
// ASYNC: the C# ValueTask<...>(ct) methods become synchronous Go methods that
// also take a context.Context and return an error, matching the banking_board.go
// convention (business results carried in the return value, validation failures
// surfaced as errors, mirroring the ArgumentException / ArgumentNullException the
// C# throws). Nullable single-item returns (Contact?) become (Contact, bool).
//
// DETERMINISM: Search orders by FullName (case-insensitive, .NET OrderBy over
// OrdinalIgnoreCase — reproduced with a case-folded ordinal comparator).
// ListByStage orders by Value descending. ReadForContact orders by AtUtc
// descending (newest first). All three cap at the given limit/topK exactly like
// the C# Take.

package circleai

import (
	"context"
	"errors"
	"sort"
	"strings"
	"sync"
	"time"
)

// Contact is a CRM contact. Ports the Contact record. Email / Phone / CompanyId
// are pointers to mirror the nullable C# string? (nil == absent).
type Contact struct {
	ContactId string
	FullName  string
	Email     *string
	Phone     *string
	CompanyId *string
}

// Company is a CRM company. Ports the Company record. Industry is a pointer to
// mirror the nullable C# string?.
type Company struct {
	CompanyId string
	Name      string
	Industry  *string
}

// Deal is a sales deal. Ports the Deal record. Value uses the shared exact
// Decimal (C# decimal).
type Deal struct {
	DealId    string
	CompanyId string
	Name      string
	Value     Decimal
	Currency  string
	Stage     string
}

// Activity is a logged contact activity. Ports the Activity record.
type Activity struct {
	ActivityId string
	ContactId  string
	Kind       string
	Body       string
	AtUtc      time.Time
}

// DefaultContactSearchTopK is the C# default `topK = 20` for contact search.
const DefaultContactSearchTopK = 20

// DefaultActivityReadLimit is the C# default `limit = 100` for activity reads.
const DefaultActivityReadLimit = 100

// ContactStore stores and searches contacts. Ports IContactStore.
type ContactStore interface {
	// BackendId identifies the backing store (e.g. "in-memory", "null").
	BackendId() string
	// Upsert stores (or replaces by ContactId) a contact.
	Upsert(ctx context.Context, c Contact) error
	// Get returns the contact and true, or (zero, false) when absent.
	Get(ctx context.Context, id string) (Contact, bool, error)
	// Search returns up to topK contacts whose FullName or Email contains query
	// (case-insensitive), ordered by FullName.
	Search(ctx context.Context, query string, topK int) ([]Contact, error)
}

// DealPipeline stores deals and lists them by stage. Ports IDealPipeline.
type DealPipeline interface {
	BackendId() string
	// Upsert stores (or replaces by DealId) a deal.
	Upsert(ctx context.Context, d Deal) error
	// Get returns the deal and true, or (zero, false) when absent.
	Get(ctx context.Context, id string) (Deal, bool, error)
	// ListByStage returns deals in stage (case-insensitive), highest Value first.
	ListByStage(ctx context.Context, stage string) ([]Deal, error)
}

// ActivityLog appends and reads per-contact activities. Ports IActivityLog.
type ActivityLog interface {
	BackendId() string
	// Append records an activity.
	Append(ctx context.Context, a Activity) error
	// ReadForContact returns up to limit activities for contactId, newest first.
	ReadForContact(ctx context.Context, contactId string, limit int) ([]Activity, error)
}

// --- In-memory implementations ---

// InMemoryContactStore is a concurrency-safe in-memory ContactStore. Ports
// InMemoryContactStore. BackendId is "in-memory".
type InMemoryContactStore struct {
	mu    sync.RWMutex
	items map[string]Contact
}

// NewInMemoryContactStore constructs an empty contact store.
func NewInMemoryContactStore() *InMemoryContactStore {
	return &InMemoryContactStore{items: make(map[string]Contact)}
}

// BackendId ports the BackendId property.
func (s *InMemoryContactStore) BackendId() string { return "in-memory" }

// Upsert stores (or replaces by ContactId) a contact. Ports UpsertAsync
// (ArgumentException on blank ContactId -> error).
func (s *InMemoryContactStore) Upsert(_ context.Context, c Contact) error {
	if strings.TrimSpace(c.ContactId) == "" {
		return errors.New("ContactId required")
	}
	s.mu.Lock()
	if s.items == nil {
		s.items = make(map[string]Contact)
	}
	s.items[c.ContactId] = c
	s.mu.Unlock()
	return nil
}

// Get returns the contact for id and true, or (zero, false) if absent. Ports
// GetAsync (ArgumentException on blank id -> error).
func (s *InMemoryContactStore) Get(_ context.Context, id string) (Contact, bool, error) {
	if strings.TrimSpace(id) == "" {
		return Contact{}, false, errors.New("id required")
	}
	s.mu.RLock()
	c, ok := s.items[id]
	s.mu.RUnlock()
	return c, ok, nil
}

// Search returns up to topK contacts whose FullName or Email contains query
// (case-insensitive), ordered by FullName (case-insensitive). Ports SearchAsync
// (ArgumentOutOfRange on topK <= 0 -> error).
func (s *InMemoryContactStore) Search(_ context.Context, query string, topK int) ([]Contact, error) {
	if topK <= 0 {
		return nil, errors.New("topK out of range")
	}
	q := strings.ToLower(query)
	s.mu.RLock()
	out := make([]Contact, 0)
	for _, c := range s.items {
		nameHit := strings.Contains(strings.ToLower(c.FullName), q)
		emailHit := c.Email != nil && strings.Contains(strings.ToLower(*c.Email), q)
		if nameHit || emailHit {
			out = append(out, c)
		}
	}
	s.mu.RUnlock()
	sort.SliceStable(out, func(i, j int) bool { return ordinalIgnoreCaseLess(out[i].FullName, out[j].FullName) })
	if len(out) > topK {
		out = out[:topK]
	}
	return out, nil
}

// InMemoryDealPipeline is a concurrency-safe in-memory DealPipeline. Ports
// InMemoryDealPipeline. BackendId is "in-memory".
type InMemoryDealPipeline struct {
	mu    sync.RWMutex
	items map[string]Deal
}

// NewInMemoryDealPipeline constructs an empty deal pipeline.
func NewInMemoryDealPipeline() *InMemoryDealPipeline {
	return &InMemoryDealPipeline{items: make(map[string]Deal)}
}

// BackendId ports the BackendId property.
func (p *InMemoryDealPipeline) BackendId() string { return "in-memory" }

// Upsert stores (or replaces by DealId) a deal. Ports UpsertAsync
// (ArgumentException on blank DealId -> error).
func (p *InMemoryDealPipeline) Upsert(_ context.Context, d Deal) error {
	if strings.TrimSpace(d.DealId) == "" {
		return errors.New("DealId required")
	}
	p.mu.Lock()
	if p.items == nil {
		p.items = make(map[string]Deal)
	}
	p.items[d.DealId] = d
	p.mu.Unlock()
	return nil
}

// Get returns the deal for id and true, or (zero, false) if absent. Ports
// GetAsync (no id validation in C#).
func (p *InMemoryDealPipeline) Get(_ context.Context, id string) (Deal, bool, error) {
	p.mu.RLock()
	d, ok := p.items[id]
	p.mu.RUnlock()
	return d, ok, nil
}

// ListByStage returns deals in stage (case-insensitive) ordered by Value
// descending. Ports ListByStageAsync (ArgumentException on blank stage -> error).
func (p *InMemoryDealPipeline) ListByStage(_ context.Context, stage string) ([]Deal, error) {
	if strings.TrimSpace(stage) == "" {
		return nil, errors.New("stage required")
	}
	p.mu.RLock()
	out := make([]Deal, 0)
	for _, d := range p.items {
		if strings.EqualFold(d.Stage, stage) {
			out = append(out, d)
		}
	}
	p.mu.RUnlock()
	sort.SliceStable(out, func(i, j int) bool { return out[i].Value.Cmp(out[j].Value) > 0 })
	return out, nil
}

// InMemoryActivityLog is a concurrency-safe in-memory ActivityLog. Ports
// InMemoryActivityLog (per-contact ordered lists). BackendId is "in-memory".
type InMemoryActivityLog struct {
	mu        sync.RWMutex
	byContact map[string][]Activity
}

// NewInMemoryActivityLog constructs an empty activity log.
func NewInMemoryActivityLog() *InMemoryActivityLog {
	return &InMemoryActivityLog{byContact: make(map[string][]Activity)}
}

// BackendId ports the BackendId property.
func (l *InMemoryActivityLog) BackendId() string { return "in-memory" }

// Append records an activity under its ContactId. Ports AppendAsync
// (ArgumentException on blank ContactId -> error).
func (l *InMemoryActivityLog) Append(_ context.Context, a Activity) error {
	if strings.TrimSpace(a.ContactId) == "" {
		return errors.New("ContactId required")
	}
	l.mu.Lock()
	if l.byContact == nil {
		l.byContact = make(map[string][]Activity)
	}
	l.byContact[a.ContactId] = append(l.byContact[a.ContactId], a)
	l.mu.Unlock()
	return nil
}

// ReadForContact returns up to limit activities for contactId ordered by AtUtc
// descending (newest first). Ports ReadForContactAsync (ArgumentException on
// blank contactId -> error; empty slice for an unknown contact). Equal
// timestamps break by ActivityId for determinism.
func (l *InMemoryActivityLog) ReadForContact(_ context.Context, contactId string, limit int) ([]Activity, error) {
	if strings.TrimSpace(contactId) == "" {
		return nil, errors.New("contactId required")
	}
	l.mu.RLock()
	list, ok := l.byContact[contactId]
	cp := make([]Activity, len(list))
	copy(cp, list)
	l.mu.RUnlock()
	if !ok {
		return []Activity{}, nil
	}
	sort.SliceStable(cp, func(i, j int) bool {
		if !cp[i].AtUtc.Equal(cp[j].AtUtc) {
			return cp[i].AtUtc.After(cp[j].AtUtc)
		}
		return cp[i].ActivityId < cp[j].ActivityId
	})
	if limit < 0 {
		limit = 0
	}
	if len(cp) > limit {
		cp = cp[:limit]
	}
	return cp, nil
}

// --- Null (fail-closed) backends ---

// NullContactStore stores nothing and returns no contacts. Ports NullContactStore.
type NullContactStore struct{}

// NullContactStoreInstance is the shared fail-closed store (ports the static Instance).
var NullContactStoreInstance = NullContactStore{}

// BackendId ports the BackendId property ("null").
func (NullContactStore) BackendId() string { return "null" }

// Upsert is a no-op. Ports NullContactStore.UpsertAsync.
func (NullContactStore) Upsert(context.Context, Contact) error { return nil }

// Get always reports absent. Ports NullContactStore.GetAsync.
func (NullContactStore) Get(context.Context, string) (Contact, bool, error) {
	return Contact{}, false, nil
}

// Search always returns empty. Ports NullContactStore.SearchAsync.
func (NullContactStore) Search(context.Context, string, int) ([]Contact, error) {
	return []Contact{}, nil
}

// NullDealPipeline stores nothing and returns no deals. Ports NullDealPipeline.
type NullDealPipeline struct{}

// NullDealPipelineInstance is the shared fail-closed pipeline.
var NullDealPipelineInstance = NullDealPipeline{}

// BackendId ports the BackendId property ("null").
func (NullDealPipeline) BackendId() string { return "null" }

// Upsert is a no-op. Ports NullDealPipeline.UpsertAsync.
func (NullDealPipeline) Upsert(context.Context, Deal) error { return nil }

// Get always reports absent. Ports NullDealPipeline.GetAsync.
func (NullDealPipeline) Get(context.Context, string) (Deal, bool, error) { return Deal{}, false, nil }

// ListByStage always returns empty. Ports NullDealPipeline.ListByStageAsync.
func (NullDealPipeline) ListByStage(context.Context, string) ([]Deal, error) { return []Deal{}, nil }

// NullActivityLog accepts appends but stores nothing and reads empty. Ports
// NullActivityLog.
type NullActivityLog struct{}

// NullActivityLogInstance is the shared fail-closed log.
var NullActivityLogInstance = NullActivityLog{}

// BackendId ports the BackendId property ("null").
func (NullActivityLog) BackendId() string { return "null" }

// Append is a no-op. Ports NullActivityLog.AppendAsync.
func (NullActivityLog) Append(context.Context, Activity) error { return nil }

// ReadForContact always returns empty. Ports NullActivityLog.ReadForContactAsync.
func (NullActivityLog) ReadForContact(context.Context, string, int) ([]Activity, error) {
	return []Activity{}, nil
}

// Interface guards.
var (
	_ ContactStore = (*InMemoryContactStore)(nil)
	_ DealPipeline = (*InMemoryDealPipeline)(nil)
	_ ActivityLog  = (*InMemoryActivityLog)(nil)
	_ ContactStore = NullContactStore{}
	_ DealPipeline = NullDealPipeline{}
	_ ActivityLog  = NullActivityLog{}
)
