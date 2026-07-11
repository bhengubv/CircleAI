// web_board.go
//
// Ports the portable surface of CircleAI.Web:
//   WebPrimitives.cs -> RouteDescriptor, PageMetadata, CachedResponse,
//                       WebBoard (IWebBoard), InMemoryWebBoard
//
// CircleAI.Web is the browser-hosted surface adapter. Its WebCompanionService +
// ServiceCollectionExtensions are a Blazor Server / WebAssembly DI wrapper around
// ICompanionSession (per-circuit lifecycle) — that is a .NET UI-host concern and
// is NOT ported, matching the CircleAI.Maui / CircleAI.Desktop exclusions. The
// portable domain — the route registry, page-metadata store, and in-memory
// response cache with expiry — is ported here. Concrete web servers implement
// WebBoard, or use InMemoryWebBoard directly.

package circleai

import (
	"sort"
	"strings"
	"sync"
	"time"
)

// ---------------------------------------------------------------------------
// Domain records
// ---------------------------------------------------------------------------

// RouteDescriptor describes one HTTP route: its path, method, the handler that
// serves it, and free-form tags. Ports the RouteDescriptor record.
type RouteDescriptor struct {
	Path        string   `json:"path"`
	Method      string   `json:"method"`
	HandlerName string   `json:"handlerName"`
	Tags        []string `json:"tags"`
}

// PageMetadata is SEO/page metadata for a path. Ports the PageMetadata record.
// Description is empty when absent (the C# field is nullable).
type PageMetadata struct {
	Path        string   `json:"path"`
	Title       string   `json:"title"`
	Description string   `json:"description,omitempty"`
	Keywords    []string `json:"keywords"`
}

// CachedResponse is a cached HTTP response body with a MIME type and an absolute
// expiry. Ports the CachedResponse record.
type CachedResponse struct {
	Key        string    `json:"key"`
	Body       []byte    `json:"body"`
	Mime       string    `json:"mime"`
	ExpiresUtc time.Time `json:"expiresUtc"`
}

// ---------------------------------------------------------------------------
// WebBoard
// ---------------------------------------------------------------------------

// WebBoard is the web vertical's registry: routes, page metadata, and a response
// cache. Ports IWebBoard.
type WebBoard interface {
	// Register adds or replaces a route, keyed by "METHOD path".
	Register(r RouteDescriptor)
	// RoutesByMethod returns the routes for an HTTP method, ordered by path.
	RoutesByMethod(method string) []RouteDescriptor
	// SetMetadata stores page metadata for a path.
	SetMetadata(m PageMetadata)
	// GetMetadata returns the metadata for a path, or (zero, false) if absent.
	GetMetadata(path string) (PageMetadata, bool)
	// Cache stores a response, ignoring already-expired entries.
	Cache(c CachedResponse)
	// Lookup returns a live cached response, evicting and missing expired ones.
	Lookup(key string) (CachedResponse, bool)
}

// ---------------------------------------------------------------------------
// InMemoryWebBoard
// ---------------------------------------------------------------------------

// InMemoryWebBoard is the in-memory WebBoard. Ports InMemoryWebBoard. Route keys
// are case-sensitive ("METHOD path"); metadata paths are matched case-
// insensitively, mirroring the C# StringComparer choices. Safe for concurrent use.
type InMemoryWebBoard struct {
	mu     sync.Mutex
	routes map[string]RouteDescriptor
	meta   map[string]PageMetadata // key = lowercased path
	cache  map[string]CachedResponse
}

// NewInMemoryWebBoard constructs an empty board.
func NewInMemoryWebBoard() *InMemoryWebBoard {
	return &InMemoryWebBoard{
		routes: make(map[string]RouteDescriptor),
		meta:   make(map[string]PageMetadata),
		cache:  make(map[string]CachedResponse),
	}
}

// Register adds or replaces a route, keyed by upper-cased method + path. Ports
// InMemoryWebBoard.Register.
func (b *InMemoryWebBoard) Register(r RouteDescriptor) {
	key := strings.ToUpper(r.Method) + " " + r.Path
	b.mu.Lock()
	b.routes[key] = r
	b.mu.Unlock()
}

// RoutesByMethod returns routes whose method matches (case-insensitively),
// ordered by path. Ports InMemoryWebBoard.RoutesByMethod (a blank method yields
// an empty slice rather than panicking).
func (b *InMemoryWebBoard) RoutesByMethod(method string) []RouteDescriptor {
	if strings.TrimSpace(method) == "" {
		return []RouteDescriptor{}
	}
	b.mu.Lock()
	out := make([]RouteDescriptor, 0)
	for _, r := range b.routes {
		if strings.EqualFold(r.Method, method) {
			out = append(out, r)
		}
	}
	b.mu.Unlock()
	sort.Slice(out, func(i, j int) bool { return out[i].Path < out[j].Path })
	return out
}

// SetMetadata stores page metadata keyed by path (case-insensitive). Ports
// InMemoryWebBoard.SetMetadata.
func (b *InMemoryWebBoard) SetMetadata(m PageMetadata) {
	b.mu.Lock()
	b.meta[strings.ToLower(m.Path)] = m
	b.mu.Unlock()
}

// GetMetadata returns the metadata for a path. Ports InMemoryWebBoard.GetMetadata.
func (b *InMemoryWebBoard) GetMetadata(path string) (PageMetadata, bool) {
	b.mu.Lock()
	defer b.mu.Unlock()
	m, ok := b.meta[strings.ToLower(path)]
	return m, ok
}

// Cache stores a response, skipping entries that are already expired. Ports
// InMemoryWebBoard.Cache.
func (b *InMemoryWebBoard) Cache(c CachedResponse) {
	if !c.ExpiresUtc.After(time.Now().UTC()) {
		return // already expired; skip
	}
	b.mu.Lock()
	b.cache[c.Key] = c
	b.mu.Unlock()
}

// Lookup returns a live cached response, evicting and reporting a miss for expired
// entries. Ports InMemoryWebBoard.Lookup (a blank key yields a miss rather than
// panicking).
func (b *InMemoryWebBoard) Lookup(key string) (CachedResponse, bool) {
	if strings.TrimSpace(key) == "" {
		return CachedResponse{}, false
	}
	b.mu.Lock()
	defer b.mu.Unlock()
	c, ok := b.cache[key]
	if !ok {
		return CachedResponse{}, false
	}
	if !c.ExpiresUtc.After(time.Now().UTC()) {
		delete(b.cache, key)
		return CachedResponse{}, false
	}
	return c, true
}

// ---------------------------------------------------------------------------
// Interface guards
// ---------------------------------------------------------------------------

var _ WebBoard = (*InMemoryWebBoard)(nil)
