// hosting_tool_catalog.go
//
// Ports CircleAI.Hosting.Tools (the searchable tool catalog, 2.0.3):
//   ToolDescriptor, ToolExecutionResult (IToolDescriptor.cs)
//   IToolCatalog, IToolProvider, IToolExecutor (IToolCatalog.cs)
//   InMemoryToolCatalog + ImportFrom (InMemoryToolCatalog.cs)
//
// The in-memory catalog does keyword-substring search over name+description+tags
// with the exact 5/2/3 scoring weights and Name-ordinal tie-break the C# uses.

package circleai

import (
	"context"
	"sort"
	"strings"
	"sync"
)

// ToolDescriptor describes one LLM-callable tool. Data-only — execution lives in
// IToolExecutor. Ports CircleAI.Hosting.Tools.ToolDescriptor (record). Tags and
// Examples are nil when unset.
type ToolDescriptor struct {
	// Name is a stable identifier, e.g. "gmail.send". Unique within a catalog.
	Name string
	// Description is the one/two-line summary the model reads.
	Description string
	// Provider is the plug-in id that owns this tool.
	Provider string
	// JSONSchema is the JSON Schema for the argument object ("" when arg-less).
	JSONSchema string
	// AuthScheme is how auth is brokered: "none", "oauth2", "api-key", "host".
	AuthScheme string
	// Tags are free-form filtering tags.
	Tags []string
	// Examples are optional natural-language examples surfaced during search.
	Examples []string
}

// NewToolDescriptor mirrors the C# record's defaulted parameters
// (JsonSchema="", AuthScheme="none", Tags/Examples=nil).
func NewToolDescriptor(name, description, provider string) ToolDescriptor {
	return ToolDescriptor{Name: name, Description: description, Provider: provider, AuthScheme: "none"}
}

// ToolExecutionResult is the result of one tool execution. Ports
// CircleAI.Hosting.Tools.ToolExecutionResult (record).
type ToolExecutionResult struct {
	Success    bool
	Result     interface{}
	Error      string
	DurationMs int64
}

// IToolCatalog is the searchable registry of every tool the host knows about.
// Ports CircleAI.Hosting.Tools.IToolCatalog.
type IToolCatalog interface {
	// Count is how many tools are currently registered.
	Count() int
	// Upsert registers or replaces one tool (idempotent for the same Name).
	Upsert(ctx context.Context, descriptor ToolDescriptor) error
	// Remove removes a tool by name (idempotent); reports whether it existed.
	Remove(ctx context.Context, name string) (bool, error)
	// Get returns exactly one descriptor by name, or nil when unknown.
	Get(ctx context.Context, name string) (*ToolDescriptor, error)
	// List enumerates every descriptor, ordered by Name (case-insensitive).
	List() []ToolDescriptor
	// Search is keyword-substring search over name+description+tags.
	Search(query string, topK int) []ToolDescriptor
	// ListByProvider filters by provider id (case-insensitive exact match).
	ListByProvider(provider string) []ToolDescriptor
}

// IToolProvider is a source of tools — vendored integrations, MCP server, an
// AetherNet peer, etc. Ports CircleAI.Hosting.Tools.IToolProvider.
type IToolProvider interface {
	// ProviderID is a stable provider id, e.g. "local"/"composio"/"mcp".
	ProviderID() string
	// Discover returns every tool this provider exposes.
	Discover(ctx context.Context) ([]ToolDescriptor, error)
	// IsAvailable is a cheap availability probe.
	IsAvailable(ctx context.Context) (bool, error)
}

// IToolExecutor is the sandboxed execution surface. Ports
// CircleAI.Hosting.Tools.IToolExecutor.
type IToolExecutor interface {
	// Execute runs one tool call. argumentsJSON is the model-emitted JSON object;
	// the executor validates it against tool.JSONSchema before dispatch.
	Execute(ctx context.Context, tool ToolDescriptor, argumentsJSON string) (ToolExecutionResult, error)
}

// InMemoryToolCatalog is the default in-memory IToolCatalog with keyword search.
// Ports CircleAI.Hosting.Tools.InMemoryToolCatalog. Thread-safe.
type InMemoryToolCatalog struct {
	mu     sync.RWMutex
	byName map[string]ToolDescriptor // key: lower(Name) for case-insensitive semantics
}

// NewInMemoryToolCatalog builds an empty catalog.
func NewInMemoryToolCatalog() *InMemoryToolCatalog {
	return &InMemoryToolCatalog{byName: make(map[string]ToolDescriptor)}
}

// Count returns the number of registered tools.
func (c *InMemoryToolCatalog) Count() int {
	c.mu.RLock()
	defer c.mu.RUnlock()
	return len(c.byName)
}

// Upsert registers or replaces a tool. Returns an error when the name is blank.
func (c *InMemoryToolCatalog) Upsert(_ context.Context, descriptor ToolDescriptor) error {
	if isBlank(descriptor.Name) {
		return errArg("descriptor.Name must not be null or whitespace")
	}
	c.mu.Lock()
	c.byName[strings.ToLower(descriptor.Name)] = descriptor
	c.mu.Unlock()
	return nil
}

// Remove removes a tool by name; reports whether it existed.
func (c *InMemoryToolCatalog) Remove(_ context.Context, name string) (bool, error) {
	if isBlank(name) {
		return false, errArg("name must not be null or whitespace")
	}
	key := strings.ToLower(name)
	c.mu.Lock()
	defer c.mu.Unlock()
	_, ok := c.byName[key]
	delete(c.byName, key)
	return ok, nil
}

// Get returns exactly one descriptor by name, or nil.
func (c *InMemoryToolCatalog) Get(_ context.Context, name string) (*ToolDescriptor, error) {
	if isBlank(name) {
		return nil, nil
	}
	c.mu.RLock()
	defer c.mu.RUnlock()
	if d, ok := c.byName[strings.ToLower(name)]; ok {
		cp := d
		return &cp, nil
	}
	return nil, nil
}

// List enumerates every descriptor ordered by Name (case-insensitive).
func (c *InMemoryToolCatalog) List() []ToolDescriptor {
	c.mu.RLock()
	out := make([]ToolDescriptor, 0, len(c.byName))
	for _, d := range c.byName {
		out = append(out, d)
	}
	c.mu.RUnlock()
	sortByNameOrdinal(out)
	return out
}

// Search does keyword-substring search. Returns nil when query is blank or
// topK<=0. Ports InMemoryToolCatalog.Search: score>0 kept, ordered by score
// desc then Name (ordinal, case-insensitive), capped at topK.
func (c *InMemoryToolCatalog) Search(query string, topK int) []ToolDescriptor {
	if isBlank(query) || topK <= 0 {
		return nil
	}
	terms := strings.Fields(query)

	type scored struct {
		tool  ToolDescriptor
		score int
	}
	c.mu.RLock()
	var hits []scored
	for _, d := range c.byName {
		if s := scoreToolMatch(d, terms); s > 0 {
			hits = append(hits, scored{tool: d, score: s})
		}
	}
	c.mu.RUnlock()

	sort.SliceStable(hits, func(i, j int) bool {
		if hits[i].score != hits[j].score {
			return hits[i].score > hits[j].score
		}
		return ordinalLess(hits[i].tool.Name, hits[j].tool.Name)
	})
	if len(hits) > topK {
		hits = hits[:topK]
	}
	out := make([]ToolDescriptor, len(hits))
	for i, h := range hits {
		out[i] = h.tool
	}
	return out
}

// ListByProvider filters by provider id (case-insensitive), ordered by Name.
func (c *InMemoryToolCatalog) ListByProvider(provider string) []ToolDescriptor {
	if isBlank(provider) {
		return nil
	}
	c.mu.RLock()
	var out []ToolDescriptor
	for _, d := range c.byName {
		if strings.EqualFold(d.Provider, provider) {
			out = append(out, d)
		}
	}
	c.mu.RUnlock()
	sortByNameOrdinal(out)
	return out
}

// scoreToolMatch mirrors InMemoryToolCatalog.ScoreMatch: name+5, desc+2, tags+3
// per case-insensitive substring hit.
func scoreToolMatch(d ToolDescriptor, terms []string) int {
	name := strings.ToLower(d.Name)
	desc := strings.ToLower(d.Description)
	tagBlob := strings.ToLower(strings.Join(d.Tags, " "))
	score := 0
	for _, t := range terms {
		lt := strings.ToLower(t)
		if strings.Contains(name, lt) {
			score += 5
		}
		if strings.Contains(desc, lt) {
			score += 2
		}
		if strings.Contains(tagBlob, lt) {
			score += 3
		}
	}
	return score
}

// ImportToolsFrom discovers every tool from provider and upserts it into
// catalog. Returns how many were imported. Ports
// ToolCatalogExtensions.ImportFromAsync.
func ImportToolsFrom(ctx context.Context, catalog IToolCatalog, provider IToolProvider) (int, error) {
	if catalog == nil {
		return 0, errNilArg("catalog")
	}
	if provider == nil {
		return 0, errNilArg("provider")
	}
	tools, err := provider.Discover(ctx)
	if err != nil {
		return 0, err
	}
	count := 0
	for _, tool := range tools {
		if err := ctx.Err(); err != nil {
			return count, err
		}
		if err := catalog.Upsert(ctx, tool); err != nil {
			return count, err
		}
		count++
	}
	return count, nil
}

// sortByNameOrdinal orders descriptors by Name using ordinal (case-insensitive)
// comparison, matching C#'s StringComparer.OrdinalIgnoreCase ordering.
func sortByNameOrdinal(ds []ToolDescriptor) {
	sort.SliceStable(ds, func(i, j int) bool { return ordinalLess(ds[i].Name, ds[j].Name) })
}

// ordinalLess compares two strings case-insensitively but by ordinal (byte)
// value on the lower-cased forms — the tie-break the C# catalog uses.
func ordinalLess(a, b string) bool {
	return strings.ToLower(a) < strings.ToLower(b)
}

var _ IToolCatalog = (*InMemoryToolCatalog)(nil)
