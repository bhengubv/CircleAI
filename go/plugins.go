// plugins.go
//
// Ports the portable core of CircleAI.Plugins (IPlugin.cs + PluginContext.cs):
//
//	IPlugin / IPluginContext / IPluginEvents  -> Plugin / PluginContext / PluginEvents (ifaces)
//	PluginEvents (class)                       -> DefaultPluginEvents (thread-safe bus)
//	PluginEventNames (static)                  -> package constants
//	PluginContext (class)                      -> DefaultPluginContext
//	PermissionedPluginContext                  -> PermissionedPluginContext (+ Permissions)
//
// Out of the portable surface (host-side .NET plumbing, deliberately not ported —
// same call the Go tree makes for ServiceCollection / IHostedService / telemetry):
//   - PluginLoader (System.Runtime.Loader.AssemblyLoadContext reflection),
//   - PluginLifecycleService (IHostedService + IConfiguration + DI),
//   - PluginRegistry / PluginMarketplace (JSON-file persistence + File I/O).
// The stable surface plugins actually consume (event bus, logger, workspace
// path, permission gating) is ported in full and is pure in-memory.
//
// The C# IPluginContext exposes an ILogger; here it is the minimal PluginLogger
// seam (nil = no-op), matching the RuntimeLogger convention in companion_runtime.go.

package circleai

import (
	"strings"
	"sync"
)

// PluginLogger is the minimal logging seam an IPluginContext exposes. Ports the
// ILogger dependency (narrowed to what a plugin needs). A nil PluginLogger is a
// no-op — mirroring NullLogger.Instance.
type PluginLogger interface {
	// Log records a message at an informational level with optional args.
	Log(message string, args ...any)
}

// Plugin is the contract every CircleAI plugin implements. Ports IPlugin.
type Plugin interface {
	ID() string
	DisplayName() string
	Version() string
	// Initialize is called once at host startup with the plugin's context.
	Initialize(ctx PluginContext) error
	// Shutdown is called when the host is stopping or the plugin is unloaded.
	Shutdown() error
}

// PluginContext is the stable surface plugins are allowed to use. Ports
// IPluginContext. WorkspacePath returns "" when not set (C# nullable string).
type PluginContext interface {
	WorkspacePath() string
	Events() PluginEvents
	Logger() PluginLogger
}

// PluginEvents is the string-keyed event bus. Ports IPluginEvents. Subscribe
// returns an unsubscribe func in place of the C# IDisposable handle.
type PluginEvents interface {
	// Subscribe registers handler for eventName and returns an unsubscribe func.
	Subscribe(eventName string, handler func(payload any)) (unsubscribe func())
	// Raise fires an event to all subscribers (host-only API).
	Raise(eventName string, payload any)
}

// Well-known event names. Ports PluginEventNames.
const (
	// PluginEventWorkspaceLoaded — "workspace.loaded".
	PluginEventWorkspaceLoaded = "workspace.loaded"
	// PluginEventChatMessage — "chat.message".
	PluginEventChatMessage = "chat.message"
	// PluginEventModelLoaded — "model.loaded".
	PluginEventModelLoaded = "model.loaded"
	// PluginEventModelUnloaded — "model.unloaded".
	PluginEventModelUnloaded = "model.unloaded"
)

// DefaultPluginEvents is the thread-safe default event bus. Ports the
// PluginEvents class. The zero value is not usable — construct with
// NewDefaultPluginEvents.
//
// CONCURRENCY: Raise snapshots the handler slice for the event UNDER the lock and
// invokes callbacks OUTSIDE it, so a handler that (un)subscribes cannot deadlock
// the raiser. Handler panics are swallowed (an unhealthy plugin must not corrupt
// the host — matching the C# empty catch).
type DefaultPluginEvents struct {
	mu       sync.Mutex
	handlers map[string][]*pluginHandler
}

type pluginHandler struct {
	fn func(payload any)
}

// NewDefaultPluginEvents constructs an empty event bus.
func NewDefaultPluginEvents() *DefaultPluginEvents {
	return &DefaultPluginEvents{handlers: make(map[string][]*pluginHandler)}
}

// Subscribe registers handler for eventName and returns an idempotent
// unsubscribe func. Ports Subscribe. Panics if eventName is blank or handler is
// nil (mirrors the C# ArgumentException / ArgumentNullException).
func (e *DefaultPluginEvents) Subscribe(eventName string, handler func(payload any)) (unsubscribe func()) {
	if strings.TrimSpace(eventName) == "" {
		panic("eventName required")
	}
	if handler == nil {
		panic("handler must not be nil")
	}
	h := &pluginHandler{fn: handler}
	e.mu.Lock()
	e.handlers[eventName] = append(e.handlers[eventName], h)
	e.mu.Unlock()
	var once sync.Once
	return func() { once.Do(func() { e.unsubscribe(eventName, h) }) }
}

func (e *DefaultPluginEvents) unsubscribe(eventName string, h *pluginHandler) {
	e.mu.Lock()
	defer e.mu.Unlock()
	list := e.handlers[eventName]
	for i, x := range list {
		if x == h {
			e.handlers[eventName] = append(list[:i], list[i+1:]...)
			return
		}
	}
}

// Raise fires payload to all subscribers of eventName. Ports Raise.
func (e *DefaultPluginEvents) Raise(eventName string, payload any) {
	e.mu.Lock()
	list := e.handlers[eventName]
	snap := make([]*pluginHandler, len(list))
	copy(snap, list)
	e.mu.Unlock()
	for _, h := range snap {
		func() {
			defer func() { _ = recover() }()
			h.fn(payload)
		}()
	}
}

// DefaultPluginContext is the default IPluginContext. Ports the PluginContext
// class. Construct with NewDefaultPluginContext.
type DefaultPluginContext struct {
	workspacePath func() string
	events        PluginEvents
	logger        PluginLogger
}

// NewDefaultPluginContext constructs a context. workspacePathAccessor may be nil
// (WorkspacePath then returns ""). events must not be nil.
func NewDefaultPluginContext(workspacePathAccessor func() string, events PluginEvents, logger PluginLogger) *DefaultPluginContext {
	if events == nil {
		panic("events must not be nil")
	}
	if workspacePathAccessor == nil {
		workspacePathAccessor = func() string { return "" }
	}
	return &DefaultPluginContext{workspacePath: workspacePathAccessor, events: events, logger: logger}
}

// WorkspacePath returns the host-configured workspace path. Ports the
// WorkspacePath property.
func (c *DefaultPluginContext) WorkspacePath() string { return c.workspacePath() }

// Events returns the event bus. Ports the Events property.
func (c *DefaultPluginContext) Events() PluginEvents { return c.events }

// Logger returns the plugin logger. Ports the Logger property.
func (c *DefaultPluginContext) Logger() PluginLogger { return c.logger }

// Plugin permission constants. Ports PermissionedPluginContext.Permissions.
const (
	// PluginPermissionWorkspaceRead — "workspace.read".
	PluginPermissionWorkspaceRead = "workspace.read"
	// PluginPermissionWorkspaceWrite — "workspace.write".
	PluginPermissionWorkspaceWrite = "workspace.write"
	// PluginPermissionEventsSubscribe — "events.subscribe".
	PluginPermissionEventsSubscribe = "events.subscribe"
)

// PermissionedPluginContext wraps an inner context and gates capabilities by a
// granted-permission set. Ports PermissionedPluginContext. Construct with
// NewPermissionedPluginContext.
type PermissionedPluginContext struct {
	inner   PluginContext
	granted map[string]bool
	events  PluginEvents
}

// NewPermissionedPluginContext wraps inner, gating by grantedPermissions
// (case-insensitive). Without events.subscribe, Events returns a drop-on-the-floor
// bus; without workspace.read/write, WorkspacePath returns "". Panics if inner is
// nil.
func NewPermissionedPluginContext(inner PluginContext, grantedPermissions []string) *PermissionedPluginContext {
	if inner == nil {
		panic("inner must not be nil")
	}
	granted := make(map[string]bool, len(grantedPermissions))
	for _, p := range grantedPermissions {
		granted[strings.ToLower(p)] = true
	}
	var events PluginEvents
	if granted[PluginPermissionEventsSubscribe] {
		events = inner.Events()
	} else {
		events = silentPluginEvents{}
	}
	return &PermissionedPluginContext{inner: inner, granted: granted, events: events}
}

// WorkspacePath returns the inner path only if a workspace permission is granted.
// Ports the WorkspacePath property.
func (c *PermissionedPluginContext) WorkspacePath() string {
	if c.granted[PluginPermissionWorkspaceRead] || c.granted[PluginPermissionWorkspaceWrite] {
		return c.inner.WorkspacePath()
	}
	return ""
}

// Events returns the (possibly silent) event bus. Ports the Events property.
func (c *PermissionedPluginContext) Events() PluginEvents { return c.events }

// Logger returns the inner logger. Ports the Logger property.
func (c *PermissionedPluginContext) Logger() PluginLogger { return c.inner.Logger() }

// silentPluginEvents drops every event (permission-denied plugins). Ports the
// SilentEvents nested class.
type silentPluginEvents struct{}

func (silentPluginEvents) Subscribe(eventName string, handler func(payload any)) (unsubscribe func()) {
	return func() {}
}
func (silentPluginEvents) Raise(eventName string, payload any) {}

// Interface guards.
var (
	_ PluginEvents  = (*DefaultPluginEvents)(nil)
	_ PluginEvents  = silentPluginEvents{}
	_ PluginContext = (*DefaultPluginContext)(nil)
	_ PluginContext = (*PermissionedPluginContext)(nil)
)
