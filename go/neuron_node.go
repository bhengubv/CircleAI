// neuron_node.go
//
// NeuronNode facade — port of CircleAI.Hosting.Neuron.NeuronNode. A host-neutral
// IChatRuntime over the on-device brain (IAIService). Streaming rides the brain's
// full pipeline (enrichment + concierge routing + two-slot residency), so a host
// drives the whole Neuron without seeing inference types. Exposes Brain so a
// companion session can sit on top.

package circleai

import (
	"context"
	"os"
	"path/filepath"
)

// neuronSessionPersister is the optional brain surface for KV snapshots.
type neuronSessionPersister interface {
	SaveSession(ctx context.Context, path string) (bool, error)
	LoadSession(ctx context.Context, path string) (bool, error)
}

// neuronModelIDReporter is the optional brain surface for the engine label.
type neuronModelIDReporter interface {
	ResolvedModelID() string
}

// NeuronNode is a host-neutral IChatRuntime + IPersistableChatRuntime over a brain.
type NeuronNode struct {
	brain        IAIService
	id           string
	snapshotPath string
}

// NewNeuronNode builds the facade. id "" defaults to "circleai-neuron";
// snapshotPath "" defaults to {UserCacheDir}/CircleAI/sessions/active.session.
func NewNeuronNode(brain IAIService, id, snapshotPath string) *NeuronNode {
	if id == "" {
		id = "circleai-neuron"
	}
	if snapshotPath == "" {
		snapshotPath = defaultNeuronSnapshotPath()
	}
	return &NeuronNode{brain: brain, id: id, snapshotPath: snapshotPath}
}

// Brain returns the on-device brain. A companion session consumes it unchanged.
func (n *NeuronNode) Brain() IAIService { return n.brain }

// ID returns the runtime id.
func (n *NeuronNode) ID() string { return n.id }

// EngineLabel reflects the resolved model when the brain reports one.
func (n *NeuronNode) EngineLabel() string {
	if r, ok := n.brain.(neuronModelIDReporter); ok {
		if m := r.ResolvedModelID(); m != "" {
			return m + " (CircleAI)"
		}
	}
	return "CircleAI Neuron"
}

// IsReady reflects the brain.
func (n *NeuronNode) IsReady() bool { return n.brain.IsReady() }

// StatusMessage reflects the brain.
func (n *NeuronNode) StatusMessage() string {
	if n.brain.IsReady() {
		return "ready"
	}
	return "loading model…"
}

// Stream translates host-neutral turns and streams through the brain.
func (n *NeuronNode) Stream(ctx context.Context, messages []ChatTurn) (<-chan string, <-chan error) {
	mapped := make([]ChatMessage, len(messages))
	for i, t := range messages {
		mapped[i] = ChatMessage{Role: t.Role, Content: t.Content}
	}
	return n.brain.Stream(ctx, mapped, nil)
}

// SessionSnapshotPath returns the default snapshot path.
func (n *NeuronNode) SessionSnapshotPath() string { return n.snapshotPath }

// SaveSession snapshots the generalist floor via the brain (no-op when unsupported).
func (n *NeuronNode) SaveSession(ctx context.Context, path string) (bool, error) {
	if sp, ok := n.brain.(neuronSessionPersister); ok {
		return sp.SaveSession(ctx, path)
	}
	return false, nil
}

// LoadSession restores the generalist floor via the brain (no-op when unsupported).
func (n *NeuronNode) LoadSession(ctx context.Context, path string) (bool, error) {
	if sp, ok := n.brain.(neuronSessionPersister); ok {
		return sp.LoadSession(ctx, path)
	}
	return false, nil
}

func defaultNeuronSnapshotPath() string {
	dir, err := os.UserCacheDir()
	if err != nil || dir == "" {
		dir = os.TempDir()
	}
	return filepath.Join(dir, "CircleAI", "sessions", "active.session")
}

var (
	_ IChatRuntime            = (*NeuronNode)(nil)
	_ IPersistableChatRuntime = (*NeuronNode)(nil)
)
