// observer.go
//
// IAIObserver + Options (the host config bag) + AgentMessage with
// correlation ID. Port of CircleAI.Hosting + CircleAI.Agents.Peer.

package circleai

import (
	"context"
	"crypto/rand"
	"encoding/hex"
	"time"

	"github.com/google/uuid"
)

// IAIObserver is the observer for AIService lifecycle + inference events.
// Mirrors CircleAI.Hosting.IAIObserver. All methods take a context. Default
// implementations should be no-ops — embed AIObserverBase for that.
type IAIObserver interface {
	OnStarted(ctx context.Context) error
	OnStopped(ctx context.Context) error
	OnChatCompleted(ctx context.Context, response ChatResponse) error
	OnStreamStarted(ctx context.Context, modelID string) error
	OnStreamCompleted(ctx context.Context, modelID string, tokenCount int) error
	OnToolInvoked(ctx context.Context, toolName string, success bool) error
	OnModelFetching(ctx context.Context, modelID string, autoSelected bool) error
	OnUpgradeAvailable(ctx context.Context, upgrade UpgradeInfo) error
}

// AIObserverBase is a no-op implementation hosts can embed.
type AIObserverBase struct{}

func (AIObserverBase) OnStarted(_ context.Context) error                          { return nil }
func (AIObserverBase) OnStopped(_ context.Context) error                          { return nil }
func (AIObserverBase) OnChatCompleted(_ context.Context, _ ChatResponse) error    { return nil }
func (AIObserverBase) OnStreamStarted(_ context.Context, _ string) error          { return nil }
func (AIObserverBase) OnStreamCompleted(_ context.Context, _ string, _ int) error { return nil }
func (AIObserverBase) OnToolInvoked(_ context.Context, _ string, _ bool) error    { return nil }
func (AIObserverBase) OnModelFetching(_ context.Context, _ string, _ bool) error  { return nil }
func (AIObserverBase) OnUpgradeAvailable(_ context.Context, _ UpgradeInfo) error  { return nil }

// AIOptions is the host configuration bag.
type AIOptions struct {
	// Model selection (any nil/zero = "infer from device")
	ModelID   string
	ModelPath string

	// Inference
	SystemPrompt string
	ContextSize  int // 0 = derive from DeviceTierDefaults.ContextWindow
	ThreadCount  int // 0 = inference layer default
	WarmOnStart  bool

	// Sensorium
	DeviceContext IDeviceContext

	// Catalog
	CatalogClient *ModelScopeCatalogClient

	// Capability filter
	RequiredCapabilities ChatCapability

	// Agentic
	AgenticMaxIterations int // 0 = derive from tier

	// Observer
	Observer IAIObserver

	// Upgrade detection
	CheckForUpgradesOnStart bool
	ModelStorageDirectory   string
}

// DefaultAIOptions returns the SDK's recommended defaults.
func DefaultAIOptions() AIOptions {
	return AIOptions{
		SystemPrompt:         "You are B!, a helpful on-device assistant.",
		WarmOnStart:          true,
		RequiredCapabilities: CapDefault,
	}
}

// ── AgentMessage with CorrelationID ─────────────────────────────────────

// AgentMessageKind discriminates the kind of agent-to-agent exchange.
type AgentMessageKind int

const (
	AgentMessageDiscover        AgentMessageKind = 0
	AgentMessageGreet           AgentMessageKind = 1
	AgentMessageCapabilityQuery AgentMessageKind = 2
	AgentMessageInvoke          AgentMessageKind = 3
	AgentMessageResponse        AgentMessageKind = 4
	AgentMessageDecline         AgentMessageKind = 5
	AgentMessageHeartbeat       AgentMessageKind = 6
)

// AgentMessage is a signed, content-typed envelope between two agents.
type AgentMessage struct {
	ID            uuid.UUID
	Kind          AgentMessageKind
	FromUhid      string
	ToUhid        string
	ContentType   string
	Payload       []byte
	Signature     []byte
	SentAt        time.Time
	CorrelationID string
}

// CreateAgentMessage builds a new envelope. When `correlationID` is empty,
// a 32-char hex string is synthesised so every outbound envelope carries
// SOME trace anchor.
func CreateAgentMessage(
	kind AgentMessageKind,
	fromUhid, toUhid, contentType string,
	payload, signature []byte,
	correlationID string,
) AgentMessage {
	if correlationID == "" {
		var buf [16]byte
		_, _ = rand.Read(buf[:])
		correlationID = hex.EncodeToString(buf[:])
	}
	return AgentMessage{
		ID:            uuid.New(),
		Kind:          kind,
		FromUhid:      fromUhid,
		ToUhid:        toUhid,
		ContentType:   contentType,
		Payload:       payload,
		Signature:     signature,
		SentAt:        time.Now().UTC(),
		CorrelationID: correlationID,
	}
}
