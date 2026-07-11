// visualization_board.go
//
// Ports CircleAI.Visualization (Contracts.cs / InMemoryVisualization.cs /
// NullImplementations.cs):
//   DashboardDefinition / ApiDoc / GeneratedSite
//   IDashboardDefinitionStore / IApiDocBuilder / ISiteBuilder
//   InMemoryDashboardStore / JsonApiDocBuilder / StaticSiteBuilder
//   NullDashboardDefinitionStore / NullApiDocBuilder / NullSiteBuilder
//
// The ApiDoc builder parses the OpenAPI JSON, extracts info.title, and
// re-serialises canonically (Go's json.Marshal sorts object keys, giving
// deterministic output — the C# stable-key-ordering intent). The SiteBuilder
// renders a multi-file static site from {"pages":[{"path","html"}]}.

package circleai

import (
	"context"
	"encoding/json"
	"errors"
	"strings"
	"sync"

	"github.com/google/uuid"
)

// DashboardDefinition is a stored dashboard definition. Ports
// DashboardDefinition.
type DashboardDefinition struct {
	DashboardID string
	Title       string
	JSONSpec    string
}

// ApiDoc is a built API doc. Ports ApiDoc.
type ApiDoc struct {
	DocID       string
	Title       string
	OpenApiJSON string
}

// GeneratedSite is a rendered static site. Ports GeneratedSite. Files maps
// path -> file bytes.
type GeneratedSite struct {
	SiteID string
	Files  map[string][]byte
}

// IDashboardDefinitionStore persists dashboard definitions. Ports
// IDashboardDefinitionStore.
type IDashboardDefinitionStore interface {
	BackendID() string
	Upsert(ctx context.Context, d DashboardDefinition) error
	Get(ctx context.Context, id string) (DashboardDefinition, bool, error)
	List(ctx context.Context) ([]DashboardDefinition, error)
}

// IApiDocBuilder builds an API doc from an OpenAPI spec. Ports IApiDocBuilder.
type IApiDocBuilder interface {
	BackendID() string
	Build(ctx context.Context, openApiSpec string) (ApiDoc, error)
}

// ISiteBuilder builds a static site from a JSON spec. Ports ISiteBuilder.
type ISiteBuilder interface {
	BackendID() string
	Build(ctx context.Context, siteSpec string) (GeneratedSite, error)
}

// ---------------------------------------------------------------------------
// InMemoryDashboardStore
// ---------------------------------------------------------------------------

// InMemoryDashboardStore is a thread-safe dashboard store. Ports
// InMemoryDashboardStore.
type InMemoryDashboardStore struct {
	mu    sync.Mutex
	items map[string]DashboardDefinition
}

// NewInMemoryDashboardStore constructs an empty store.
func NewInMemoryDashboardStore() *InMemoryDashboardStore {
	return &InMemoryDashboardStore{items: make(map[string]DashboardDefinition)}
}

// BackendID returns "in-memory".
func (s *InMemoryDashboardStore) BackendID() string { return "in-memory" }

// Upsert stores (or replaces by DashboardId) a definition. Ports UpsertAsync.
func (s *InMemoryDashboardStore) Upsert(ctx context.Context, d DashboardDefinition) error {
	if strings.TrimSpace(d.DashboardID) == "" {
		return errors.New("DashboardId required")
	}
	s.mu.Lock()
	s.items[d.DashboardID] = d
	s.mu.Unlock()
	return nil
}

// Get returns the definition for id. Ports GetAsync.
func (s *InMemoryDashboardStore) Get(ctx context.Context, id string) (DashboardDefinition, bool, error) {
	if strings.TrimSpace(id) == "" {
		return DashboardDefinition{}, false, errors.New("id required")
	}
	s.mu.Lock()
	d, ok := s.items[id]
	s.mu.Unlock()
	return d, ok, nil
}

// List returns all definitions. Ports ListAsync.
func (s *InMemoryDashboardStore) List(ctx context.Context) ([]DashboardDefinition, error) {
	s.mu.Lock()
	out := make([]DashboardDefinition, 0, len(s.items))
	for _, v := range s.items {
		out = append(out, v)
	}
	s.mu.Unlock()
	return out, nil
}

var _ IDashboardDefinitionStore = (*InMemoryDashboardStore)(nil)

// ---------------------------------------------------------------------------
// JsonApiDocBuilder
// ---------------------------------------------------------------------------

// JsonApiDocBuilder normalises an OpenAPI JSON doc. Ports JsonApiDocBuilder.
type JsonApiDocBuilder struct{}

// BackendID returns "json-normaliser".
func (JsonApiDocBuilder) BackendID() string { return "json-normaliser" }

// Build parses the spec, extracts info.title, and re-serialises canonically.
// Ports BuildAsync.
func (JsonApiDocBuilder) Build(ctx context.Context, openApiSpec string) (ApiDoc, error) {
	if strings.TrimSpace(openApiSpec) == "" {
		return ApiDoc{}, errors.New("openApiSpec required")
	}
	var root map[string]any
	if err := json.Unmarshal([]byte(openApiSpec), &root); err != nil {
		return ApiDoc{}, err
	}
	title := "API"
	if info, ok := root["info"].(map[string]any); ok {
		if t, ok := info["title"].(string); ok && t != "" {
			title = t
		}
	}
	docID := strings.ToLower(strings.ReplaceAll(title, " ", "-"))
	canonicalBytes, err := json.Marshal(root)
	if err != nil {
		return ApiDoc{}, err
	}
	return ApiDoc{DocID: docID, Title: title, OpenApiJSON: string(canonicalBytes)}, nil
}

var _ IApiDocBuilder = JsonApiDocBuilder{}

// ---------------------------------------------------------------------------
// StaticSiteBuilder
// ---------------------------------------------------------------------------

// StaticSiteBuilder renders a static site from a JSON spec. Ports
// StaticSiteBuilder.
type StaticSiteBuilder struct{}

// BackendID returns "static".
func (StaticSiteBuilder) BackendID() string { return "static" }

// sitePage models one {"path","html"} entry.
type sitePage struct {
	Path *string `json:"path"`
	HTML *string `json:"html"`
}

type siteSpec struct {
	Pages []sitePage `json:"pages"`
}

// Build renders each page into an in-memory file. Ports BuildAsync.
func (StaticSiteBuilder) Build(ctx context.Context, spec string) (GeneratedSite, error) {
	if strings.TrimSpace(spec) == "" {
		return GeneratedSite{}, errors.New("siteSpec required")
	}
	// First validate that a pages[] array is present (matches the C# guard,
	// which throws before rendering when pages is missing or not an array).
	var probe map[string]json.RawMessage
	if err := json.Unmarshal([]byte(spec), &probe); err != nil {
		return GeneratedSite{}, err
	}
	pagesRaw, ok := probe["pages"]
	if !ok {
		return GeneratedSite{}, errors.New("siteSpec must contain a pages[] array")
	}
	var pagesArr []json.RawMessage
	if err := json.Unmarshal(pagesRaw, &pagesArr); err != nil {
		return GeneratedSite{}, errors.New("siteSpec must contain a pages[] array")
	}

	var parsed siteSpec
	if err := json.Unmarshal([]byte(spec), &parsed); err != nil {
		return GeneratedSite{}, err
	}
	files := make(map[string][]byte)
	for _, page := range parsed.Pages {
		if page.Path == nil || strings.TrimSpace(*page.Path) == "" || page.HTML == nil {
			continue
		}
		files[*page.Path] = []byte(*page.HTML)
	}
	siteID := "site-" + uuidNoDashes(uuid.New())
	return GeneratedSite{SiteID: siteID, Files: files}, nil
}

var _ ISiteBuilder = StaticSiteBuilder{}

// ---------------------------------------------------------------------------
// Null implementations
// ---------------------------------------------------------------------------

// NullDashboardDefinitionStore is a fail-safe store. Ports
// NullDashboardDefinitionStore.
type NullDashboardDefinitionStore struct{}

// NullDashboardDefinitionStoreInstance is the shared singleton.
var NullDashboardDefinitionStoreInstance = NullDashboardDefinitionStore{}

func (NullDashboardDefinitionStore) BackendID() string { return "null" }
func (NullDashboardDefinitionStore) Upsert(context.Context, DashboardDefinition) error {
	return nil
}
func (NullDashboardDefinitionStore) Get(context.Context, string) (DashboardDefinition, bool, error) {
	return DashboardDefinition{}, false, nil
}
func (NullDashboardDefinitionStore) List(context.Context) ([]DashboardDefinition, error) {
	return []DashboardDefinition{}, nil
}

// NullApiDocBuilder is a fail-safe builder. Ports NullApiDocBuilder.
type NullApiDocBuilder struct{}

// NullApiDocBuilderInstance is the shared singleton.
var NullApiDocBuilderInstance = NullApiDocBuilder{}

func (NullApiDocBuilder) BackendID() string { return "null" }
func (NullApiDocBuilder) Build(context.Context, string) (ApiDoc, error) {
	return ApiDoc{DocID: uuid.Nil.String(), Title: "", OpenApiJSON: "{}"}, nil
}

// NullSiteBuilder is a fail-safe builder. Ports NullSiteBuilder.
type NullSiteBuilder struct{}

// NullSiteBuilderInstance is the shared singleton.
var NullSiteBuilderInstance = NullSiteBuilder{}

func (NullSiteBuilder) BackendID() string { return "null" }
func (NullSiteBuilder) Build(context.Context, string) (GeneratedSite, error) {
	return GeneratedSite{SiteID: uuid.Nil.String(), Files: map[string][]byte{}}, nil
}

var (
	_ IDashboardDefinitionStore = NullDashboardDefinitionStore{}
	_ IApiDocBuilder            = NullApiDocBuilder{}
	_ ISiteBuilder              = NullSiteBuilder{}
)
