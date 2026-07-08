// server_model_registry.go
//
// Ports CircleAI.Inference.Server.Models.IInferenceServerModelRegistry +
// InferenceServerModelRegistry (ModelRegistry.cs).
//
// In-process registry mapping logical model IDs (the value clients pass in the
// `model` field of an OpenAI request) to the IInferenceBridge that serves them
// (chat) or the ITextEmbedder (embeddings). Thread-safe; the host populates it
// at startup and the endpoints look up by request.Model.

package circleai

import (
	"errors"
	"sort"
	"strings"
	"sync"
)

// IInferenceServerModelRegistry is the in-process bridge/embedder registry.
// Ports CircleAI.Inference.Server.Models.IInferenceServerModelRegistry.
type IInferenceServerModelRegistry interface {
	// Register registers a chat bridge under modelId.
	Register(modelID string, bridge IInferenceBridge) error
	// RegisterEmbedder registers an embedder under modelId.
	RegisterEmbedder(modelID string, embedder ITextEmbedder) error
	// Deregister removes the chat bridge under modelId. Returns true when removed.
	Deregister(modelID string) bool
	// Resolve looks up a chat bridge. Returns nil when not registered.
	Resolve(modelID string) IInferenceBridge
	// ResolveEmbedder looks up an embedder. Returns nil when not registered.
	ResolveEmbedder(modelID string) ITextEmbedder
	// AllModelIDs lists every model id currently served (chat + embedding).
	AllModelIDs() []string
	// ChatModelIDs lists chat-capable model ids only.
	ChatModelIDs() []string
}

// InferenceServerModelRegistry is the default thread-safe implementation. Ports
// CircleAI.Inference.Server.Models.InferenceServerModelRegistry.
type InferenceServerModelRegistry struct {
	mu    sync.RWMutex
	chat  map[string]IInferenceBridge
	embed map[string]ITextEmbedder
}

// NewInferenceServerModelRegistry builds an empty registry.
func NewInferenceServerModelRegistry() *InferenceServerModelRegistry {
	return &InferenceServerModelRegistry{
		chat:  make(map[string]IInferenceBridge),
		embed: make(map[string]ITextEmbedder),
	}
}

// Register ports Register.
func (r *InferenceServerModelRegistry) Register(modelID string, bridge IInferenceBridge) error {
	if strings.TrimSpace(modelID) == "" {
		return errors.New("modelId is required")
	}
	if bridge == nil {
		return errors.New("bridge is required")
	}
	r.mu.Lock()
	r.chat[modelID] = bridge
	r.mu.Unlock()
	return nil
}

// RegisterEmbedder ports RegisterEmbedder.
func (r *InferenceServerModelRegistry) RegisterEmbedder(modelID string, embedder ITextEmbedder) error {
	if strings.TrimSpace(modelID) == "" {
		return errors.New("modelId is required")
	}
	if embedder == nil {
		return errors.New("embedder is required")
	}
	r.mu.Lock()
	r.embed[modelID] = embedder
	r.mu.Unlock()
	return nil
}

// Deregister ports Deregister.
func (r *InferenceServerModelRegistry) Deregister(modelID string) bool {
	r.mu.Lock()
	defer r.mu.Unlock()
	if _, ok := r.chat[modelID]; ok {
		delete(r.chat, modelID)
		return true
	}
	return false
}

// Resolve ports Resolve.
func (r *InferenceServerModelRegistry) Resolve(modelID string) IInferenceBridge {
	r.mu.RLock()
	defer r.mu.RUnlock()
	return r.chat[modelID]
}

// ResolveEmbedder ports ResolveEmbedder.
func (r *InferenceServerModelRegistry) ResolveEmbedder(modelID string) ITextEmbedder {
	r.mu.RLock()
	defer r.mu.RUnlock()
	return r.embed[modelID]
}

// AllModelIDs ports AllModelIds (union of chat + embed, deduped, sorted for
// determinism — C# uses Distinct which is unordered; sorting is a stable superset).
func (r *InferenceServerModelRegistry) AllModelIDs() []string {
	r.mu.RLock()
	defer r.mu.RUnlock()
	set := make(map[string]struct{}, len(r.chat)+len(r.embed))
	for k := range r.chat {
		set[k] = struct{}{}
	}
	for k := range r.embed {
		set[k] = struct{}{}
	}
	ids := make([]string, 0, len(set))
	for k := range set {
		ids = append(ids, k)
	}
	sort.Strings(ids)
	return ids
}

// ChatModelIDs ports ChatModelIds.
func (r *InferenceServerModelRegistry) ChatModelIDs() []string {
	r.mu.RLock()
	defer r.mu.RUnlock()
	ids := make([]string, 0, len(r.chat))
	for k := range r.chat {
		ids = append(ids, k)
	}
	sort.Strings(ids)
	return ids
}

var _ IInferenceServerModelRegistry = (*InferenceServerModelRegistry)(nil)
