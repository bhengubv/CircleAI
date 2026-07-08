// hosting_observer.go
//
// Ports the event-based observer surface of CircleAI.Hosting.IAIObserver.cs:
//   AIChatEvent, AIStreamEvent, AIToolEvent (event records)
//   HostAIObserver (the IAIObserver interface — richer than the legacy
//     lifecycle-only IAIObserver already in observer.go, so it is named
//     distinctly to avoid a collision in the flat package)
//   BrownoutReason
//   IPushNotificationSender + PushAIObserver (PushAIObserver.cs)
//   ICircleAetherTransport + AetherAIObserver (AetherAIObserver.cs)
//
// All observer callbacks are optional — embed HostAIObserverBase for no-op
// defaults. AIService catches observer errors; they never reach the caller.

package circleai

import (
	"context"
	"encoding/json"
	"time"

	"github.com/google/uuid"
)

// AIChatEvent is delivered to HostAIObserver.OnChatCompleted. Ports
// CircleAI.Hosting.AIChatEvent. Elapsed is wall-clock first-to-last token.
type AIChatEvent struct {
	CorrelationID uuid.UUID
	Messages      []ChatMessage
	Response      string
	Elapsed       time.Duration
	Timestamp     time.Time
}

// AIStreamEvent is delivered to OnStreamStarted / OnStreamCompleted. Ports
// CircleAI.Hosting.AIStreamEvent. On start Elapsed=time-to-first-token and
// TokenCount=0; on completion Elapsed=total time and TokenCount=tokens yielded.
type AIStreamEvent struct {
	CorrelationID uuid.UUID
	Messages      []ChatMessage
	Elapsed       time.Duration
	TokenCount    int
	Timestamp     time.Time
}

// AIToolEvent is delivered to OnToolInvoked. Ports CircleAI.Hosting.AIToolEvent.
type AIToolEvent struct {
	CorrelationID uuid.UUID
	Invocation    ToolInvocation
	Result        ToolResult
	Elapsed       time.Duration
	Timestamp     time.Time
}

// BrownoutReason is why a brownout swap fired. Ports
// CircleAI.Hosting.BrownoutReason (stable ordinals).
type BrownoutReason int

const (
	// BrownoutMemoryPressure — OS-reported memory pressure.
	BrownoutMemoryPressure BrownoutReason = 0
	// BrownoutBatteryFloor — battery dropped below the brownout floor.
	BrownoutBatteryFloor BrownoutReason = 1
	// BrownoutThermalCritical — thermal throttle demanded a downshift.
	BrownoutThermalCritical BrownoutReason = 2
	// BrownoutManual — application requested the swap explicitly.
	BrownoutManual BrownoutReason = 3
)

// HostAIObserver is the event-based observability hook for AIService. Ports
// CircleAI.Hosting.IAIObserver (the record-carrying interface). All methods are
// optional — embed HostAIObserverBase. Implementations must be thread-safe.
type HostAIObserver interface {
	OnStarted(ctx context.Context) error
	OnStopped(ctx context.Context) error
	OnChatCompleted(ctx context.Context, ev AIChatEvent) error
	OnStreamStarted(ctx context.Context, ev AIStreamEvent) error
	OnStreamCompleted(ctx context.Context, ev AIStreamEvent) error
	OnToolInvoked(ctx context.Context, ev AIToolEvent) error
	OnModelFetching(ctx context.Context, modelID string, autoSelected bool) error
	OnUpgradeAvailable(ctx context.Context, upgrade UpgradeInfo) error
	OnBrownout(ctx context.Context, from, to string, reason BrownoutReason) error
}

// HostAIObserverBase provides no-op defaults for every HostAIObserver method.
// Embed it and override only what you need.
type HostAIObserverBase struct{}

func (HostAIObserverBase) OnStarted(context.Context) error                        { return nil }
func (HostAIObserverBase) OnStopped(context.Context) error                        { return nil }
func (HostAIObserverBase) OnChatCompleted(context.Context, AIChatEvent) error     { return nil }
func (HostAIObserverBase) OnStreamStarted(context.Context, AIStreamEvent) error   { return nil }
func (HostAIObserverBase) OnStreamCompleted(context.Context, AIStreamEvent) error { return nil }
func (HostAIObserverBase) OnToolInvoked(context.Context, AIToolEvent) error       { return nil }
func (HostAIObserverBase) OnModelFetching(context.Context, string, bool) error    { return nil }
func (HostAIObserverBase) OnUpgradeAvailable(context.Context, UpgradeInfo) error  { return nil }
func (HostAIObserverBase) OnBrownout(context.Context, string, string, BrownoutReason) error {
	return nil
}

// ---------------------------------------------------------------------------
// PushAIObserver
// ---------------------------------------------------------------------------

// pushMaxBodyLength mirrors PushAIObserver.MaxBodyLength.
const pushMaxBodyLength = 100

// IPushNotificationSender is the platform-agnostic push sender abstraction.
// Ports CircleAI.Hosting.IPushNotificationSender. Implement with APN/FCM.
type IPushNotificationSender interface {
	Send(ctx context.Context, deviceToken, title, body string) error
}

// PushAIObserver delivers butler responses as push notifications via an
// IPushNotificationSender. Ports CircleAI.Hosting.PushAIObserver.
type PushAIObserver struct {
	HostAIObserverBase
	sender      IPushNotificationSender
	deviceToken string
}

// NewPushAIObserver constructs the observer. Returns an error when sender is
// nil or deviceToken is blank (mirrors the C# ctor guards).
func NewPushAIObserver(sender IPushNotificationSender, deviceToken string) (*PushAIObserver, error) {
	if sender == nil {
		return nil, errNilArg("sender")
	}
	if isBlank(deviceToken) {
		return nil, errArg("device token is required")
	}
	return &PushAIObserver{sender: sender, deviceToken: deviceToken}, nil
}

// OnChatCompleted delivers the response as a push notification.
func (o *PushAIObserver) OnChatCompleted(_ context.Context, ev AIChatEvent) error {
	o.sendResponse(ev.Response)
	return nil
}

// OnError sends an error push notification. Call from error paths that cannot
// surface through the standard observer lifecycle. Ports PushAIObserver.OnError.
func (o *PushAIObserver) OnError(err error) {
	if err == nil {
		return
	}
	_ = o.sender.Send(context.Background(), o.deviceToken, "B! Error", truncateEllipsis(err.Error(), pushMaxBodyLength))
}

func (o *PushAIObserver) sendResponse(fullResponse string) {
	_ = o.sender.Send(context.Background(), o.deviceToken, "B!", truncateEllipsis(fullResponse, pushMaxBodyLength))
}

// ---------------------------------------------------------------------------
// AetherAIObserver
// ---------------------------------------------------------------------------

// ICircleAetherTransport is the publish/subscribe transport contract for the
// CircleAether mesh. Ports CircleAI.Hosting.ICircleAetherTransport. Host
// packages (AetherNet, Bluetooth, NearLink, gRPC) implement it.
type ICircleAetherTransport interface {
	Publish(ctx context.Context, topic string, payload []byte) error
}

// AetherAIObserver forwards butler events to a CircleAether mesh transport.
// Ports CircleAI.Hosting.AetherAIObserver.
type AetherAIObserver struct {
	HostAIObserverBase
	transport ICircleAetherTransport
}

// NewAetherAIObserver constructs the observer. Returns an error when transport
// is nil.
func NewAetherAIObserver(transport ICircleAetherTransport) (*AetherAIObserver, error) {
	if transport == nil {
		return nil, errNilArg("transport")
	}
	return &AetherAIObserver{transport: transport}, nil
}

// OnChatCompleted publishes the response to the butler/response topic. Payload
// is JSON {"response": ...}, matching the C# anonymous-object serialisation.
func (o *AetherAIObserver) OnChatCompleted(_ context.Context, ev AIChatEvent) error {
	payload, _ := json.Marshal(map[string]string{"response": ev.Response})
	_ = o.transport.Publish(context.Background(), "butler/response", payload)
	return nil
}

// OnError publishes an error payload to the butler/error topic. Ports
// AetherAIObserver.OnError. Payload is JSON {"error": <Type>, "message": ...}.
func (o *AetherAIObserver) OnError(err error) {
	if err == nil {
		return
	}
	payload, _ := json.Marshal(map[string]string{
		"error":   errorTypeName(err),
		"message": err.Error(),
	})
	_ = o.transport.Publish(context.Background(), "butler/error", payload)
}

var (
	_ HostAIObserver = (*PushAIObserver)(nil)
	_ HostAIObserver = (*AetherAIObserver)(nil)
)
