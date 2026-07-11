// microagents.go
//
// Ports CircleAI.MicroAgents (Contracts.cs + InMemoryMicroAgents.cs +
// MicroAgentHelpers.cs + NullImplementations.cs):
//
//	MicroAgentDescriptor / MicroAgentResponse (records) -> value structs
//	IMicroAgent / IMicroAgentHost               -> MicroAgent / MicroAgentHost
//	FuncMicroAgent                              -> FuncMicroAgent (lambda wrapper)
//	InMemoryMicroAgentHost                      -> InMemoryMicroAgentHost
//	NullMicroAgent                             -> NullMicroAgent
//	MicroAgentInvocation                        -> value struct
//	MicroAgentSearch.ByCapability / .Search     -> package funcs
//	MicroAgentInvocationLog                     -> InvocationLog
//
// MicroAgentResponse.Metadata is an optional read-only dictionary in C#; here it
// is a plain map that may be nil (matching the nullable default).

package circleai

import (
	"context"
	"sort"
	"strings"
	"sync"
	"time"
)

// MicroAgentDescriptor describes a registered micro-agent. Ports the
// MicroAgentDescriptor record.
type MicroAgentDescriptor struct {
	AgentID      string
	Description  string
	Capabilities []string
}

// MicroAgentResponse is a micro-agent's output. Ports the MicroAgentResponse
// record. Metadata may be nil (C# nullable IReadOnlyDictionary default).
type MicroAgentResponse struct {
	AgentID  string
	Output   string
	Metadata map[string]string
}

// MicroAgent is a single invokable micro-agent. Ports IMicroAgent.
type MicroAgent interface {
	AgentID() string
	BackendID() string
	Descriptor() MicroAgentDescriptor
	Invoke(ctx context.Context, input string) (MicroAgentResponse, error)
}

// MicroAgentHost is a registry that routes Invoke calls to agents. Ports
// IMicroAgentHost.
type MicroAgentHost interface {
	BackendID() string
	Register(agent MicroAgent)
	List() []MicroAgentDescriptor
	// Invoke routes to the named agent, returning (response, true) or
	// (zero, false) when no agent is registered under agentID.
	Invoke(ctx context.Context, agentID, input string) (MicroAgentResponse, bool, error)
}

// FuncMicroAgent wraps a func in a MicroAgent so callers can register lambdas
// without authoring a new type per agent. Ports FuncMicroAgent.
type FuncMicroAgent struct {
	descriptor MicroAgentDescriptor
	impl       func(ctx context.Context, input string) (MicroAgentResponse, error)
}

// NewFuncMicroAgent constructs a FuncMicroAgent. Panics if agentID is blank or
// impl is nil (mirrors the C# ArgumentException / ArgumentNullException).
func NewFuncMicroAgent(agentID, description string, capabilities []string,
	impl func(ctx context.Context, input string) (MicroAgentResponse, error)) *FuncMicroAgent {
	if strings.TrimSpace(agentID) == "" {
		panic("agentId required")
	}
	if impl == nil {
		panic("impl must not be nil")
	}
	if capabilities == nil {
		capabilities = []string{}
	}
	return &FuncMicroAgent{
		descriptor: MicroAgentDescriptor{AgentID: agentID, Description: description, Capabilities: capabilities},
		impl:       impl,
	}
}

// AgentID returns the agent id. Ports the AgentId property.
func (a *FuncMicroAgent) AgentID() string { return a.descriptor.AgentID }

// BackendID returns "func". Ports the BackendId property.
func (a *FuncMicroAgent) BackendID() string { return "func" }

// Descriptor returns the descriptor. Ports the Descriptor property.
func (a *FuncMicroAgent) Descriptor() MicroAgentDescriptor { return a.descriptor }

// Invoke delegates to the wrapped func. Ports InvokeAsync.
func (a *FuncMicroAgent) Invoke(ctx context.Context, input string) (MicroAgentResponse, error) {
	return a.impl(ctx, input)
}

// InMemoryMicroAgentHost is a real IMicroAgentHost keeping a registry of agents.
// Ports InMemoryMicroAgentHost. The zero value is not usable — construct with
// NewInMemoryMicroAgentHost.
type InMemoryMicroAgentHost struct {
	mu     sync.RWMutex
	agents map[string]MicroAgent
}

// NewInMemoryMicroAgentHost constructs an empty host.
func NewInMemoryMicroAgentHost() *InMemoryMicroAgentHost {
	return &InMemoryMicroAgentHost{agents: make(map[string]MicroAgent)}
}

// BackendID returns "in-memory". Ports the BackendId property.
func (h *InMemoryMicroAgentHost) BackendID() string { return "in-memory" }

// Register stores (or replaces by AgentId) an agent. Ports Register.
func (h *InMemoryMicroAgentHost) Register(agent MicroAgent) {
	h.mu.Lock()
	h.agents[agent.AgentID()] = agent
	h.mu.Unlock()
}

// List returns every registered agent's descriptor. Ports List (no defined
// order — ConcurrentDictionary values).
func (h *InMemoryMicroAgentHost) List() []MicroAgentDescriptor {
	h.mu.RLock()
	out := make([]MicroAgentDescriptor, 0, len(h.agents))
	for _, a := range h.agents {
		out = append(out, a.Descriptor())
	}
	h.mu.RUnlock()
	return out
}

// Invoke routes to the named agent. Ports InvokeAsync (C# returns
// MicroAgentResponse? -> (response, found)).
func (h *InMemoryMicroAgentHost) Invoke(ctx context.Context, agentID, input string) (MicroAgentResponse, bool, error) {
	h.mu.RLock()
	a, ok := h.agents[agentID]
	h.mu.RUnlock()
	if !ok {
		return MicroAgentResponse{}, false, nil
	}
	resp, err := a.Invoke(ctx, input)
	return resp, true, err
}

// NullMicroAgent is a no-op micro-agent. Ports NullMicroAgent.
type NullMicroAgent struct{}

// AgentID returns "null".
func (NullMicroAgent) AgentID() string { return "null" }

// BackendID returns "null".
func (NullMicroAgent) BackendID() string { return "null" }

// Descriptor returns the fixed no-op descriptor. Ports the Descriptor property.
func (NullMicroAgent) Descriptor() MicroAgentDescriptor {
	return MicroAgentDescriptor{AgentID: "null", Description: "No-op micro agent", Capabilities: []string{}}
}

// Invoke returns an empty response. Ports InvokeAsync.
func (NullMicroAgent) Invoke(ctx context.Context, input string) (MicroAgentResponse, error) {
	return MicroAgentResponse{AgentID: "null", Output: ""}, nil
}

// MicroAgentInvocation is one recorded invocation. Ports the MicroAgentInvocation
// record.
type MicroAgentInvocation struct {
	AgentID      string
	Input        string
	ResponseText string
	AtUTC        time.Time
}

// MicroAgentByCapability returns descriptors advertising capability (case-
// insensitive), ordered by AgentId ascending. Ports MicroAgentSearch.ByCapability
// (OrderBy(AgentId), ordinal). Panics if capability is blank.
func MicroAgentByCapability(all []MicroAgentDescriptor, capability string) []MicroAgentDescriptor {
	if strings.TrimSpace(capability) == "" {
		panic("capability required")
	}
	out := make([]MicroAgentDescriptor, 0)
	for _, d := range all {
		for _, c := range d.Capabilities {
			if strings.EqualFold(c, capability) {
				out = append(out, d)
				break
			}
		}
	}
	sort.SliceStable(out, func(i, j int) bool { return out[i].AgentID < out[j].AgentID })
	return out
}

// MicroAgentSearch returns up to topK descriptors whose AgentId, Description, or
// any Capability contains query (case-insensitive substring). Ports
// MicroAgentSearch.Search (Take(topK), no reorder). Panics if topK <= 0.
func MicroAgentSearch(all []MicroAgentDescriptor, query string, topK int) []MicroAgentDescriptor {
	if topK <= 0 {
		panic("topK must be positive")
	}
	lq := strings.ToLower(query)
	out := make([]MicroAgentDescriptor, 0)
	for _, d := range all {
		if len(out) >= topK {
			break
		}
		if strings.Contains(strings.ToLower(d.AgentID), lq) ||
			strings.Contains(strings.ToLower(d.Description), lq) {
			out = append(out, d)
			continue
		}
		for _, c := range d.Capabilities {
			if strings.Contains(strings.ToLower(c), lq) {
				out = append(out, d)
				break
			}
		}
	}
	return out
}

// MicroAgentInvocationLog keeps an in-memory invocation log. Ports
// MicroAgentInvocationLog. The zero value is ready to use.
type MicroAgentInvocationLog struct {
	mu    sync.Mutex
	items []MicroAgentInvocation
}

// Append records an invocation. Ports Append.
func (l *MicroAgentInvocationLog) Append(i MicroAgentInvocation) {
	l.mu.Lock()
	l.items = append(l.items, i)
	l.mu.Unlock()
}

// ForAgent returns up to limit invocations for agentID, most-recent first.
// Ports ForAgent (OrderByDescending(AtUtc).Take(limit)). Panics if limit <= 0.
func (l *MicroAgentInvocationLog) ForAgent(agentID string, limit int) []MicroAgentInvocation {
	if limit <= 0 {
		panic("limit must be positive")
	}
	l.mu.Lock()
	filtered := make([]MicroAgentInvocation, 0)
	for _, i := range l.items {
		if i.AgentID == agentID {
			filtered = append(filtered, i)
		}
	}
	l.mu.Unlock()
	sort.SliceStable(filtered, func(a, b int) bool { return filtered[a].AtUTC.After(filtered[b].AtUTC) })
	if len(filtered) > limit {
		filtered = filtered[:limit]
	}
	return filtered
}

// TotalInvocations returns the number of recorded invocations. Ports
// TotalInvocations.
func (l *MicroAgentInvocationLog) TotalInvocations() int {
	l.mu.Lock()
	defer l.mu.Unlock()
	return len(l.items)
}

// Interface guards.
var (
	_ MicroAgent     = (*FuncMicroAgent)(nil)
	_ MicroAgent     = NullMicroAgent{}
	_ MicroAgentHost = (*InMemoryMicroAgentHost)(nil)
)
