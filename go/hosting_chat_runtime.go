// hosting_chat_runtime.go
//
// Host-neutral chat runtime seam — port of CircleAI.Hosting.Chat.IChatRuntime.
// Lets a UI / harness drive the on-device engine without importing inference
// types. NeuronNode implements these over an IAIService brain.

package circleai

import "context"

// ChatTurn is a host-neutral chat turn. Mirrors ChatTurn (role / content).
type ChatTurn struct {
	Role    string
	Content string
}

// IChatRuntime is the host-neutral chat surface. Mirrors IChatRuntime.
type IChatRuntime interface {
	ID() string
	EngineLabel() string
	IsReady() bool
	StatusMessage() string
	// Stream streams the assistant reply chunk-by-chunk. The tokens channel is
	// closed when the stream ends; errs receives at most one error.
	Stream(ctx context.Context, messages []ChatTurn) (<-chan string, <-chan error)
}

// IPersistableChatRuntime is the optional KV-snapshot capability. Mirrors
// IPersistableChatRuntime.
type IPersistableChatRuntime interface {
	SessionSnapshotPath() string
	SaveSession(ctx context.Context, path string) (bool, error)
	LoadSession(ctx context.Context, path string) (bool, error)
}

const nullChatRuntimeStatus = "No chat engine is wired. Add a NeuronNode (or another IChatRuntime adapter) to enable conversations."

// NullChatRuntime is an honest "engine offline" runtime. Mirrors NullChatRuntime.
type NullChatRuntime struct{}

// ID returns the null runtime id.
func (NullChatRuntime) ID() string { return "null" }

// EngineLabel returns the null engine label.
func (NullChatRuntime) EngineLabel() string { return "No engine wired" }

// IsReady always reports false.
func (NullChatRuntime) IsReady() bool { return false }

// StatusMessage returns the honest offline message.
func (NullChatRuntime) StatusMessage() string { return nullChatRuntimeStatus }

// Stream yields the offline status once.
func (NullChatRuntime) Stream(ctx context.Context, messages []ChatTurn) (<-chan string, <-chan error) {
	out := make(chan string, 1)
	errc := make(chan error, 1)
	out <- nullChatRuntimeStatus
	close(out)
	close(errc)
	return out, errc
}

var _ IChatRuntime = NullChatRuntime{}
