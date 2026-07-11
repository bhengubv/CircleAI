// windows_automation.go
//
// Ports CircleAI.WindowsAutomation (the portable contract + logic surface):
//   Contracts.cs                 -> UiElement, UiAutomationDriver
//   InMemoryWindowsAutomation.cs -> UiAutomationEvent, InMemoryUiAutomationDriver
//   NullImplementations.cs       -> NullUiAutomationDriver
//   WindowsAutomationHelpers.cs  -> UiElement.ContainsPoint, HitTest, DumpUiElements
//
// WindowsAutomation is a UI-automation (UIA) abstraction. The real backend is a
// Win32-UIA implementation that hosts snap in for production — that native layer
// is platform-specific and NOT ported; only the driver contract, the in-memory
// virtual driver (used by tests to drive a UI without touching a desktop), the
// null fail-safe driver, and the pure hit-test/containment/dump helpers are
// ported. Native drivers implement UiAutomationDriver behind this seam, exactly
// as the C# Win32 driver implements IUiAutomationDriver.

package circleai

import (
	"context"
	"errors"
	"strconv"
	"strings"
	"sync"
)

// ---------------------------------------------------------------------------
// UiElement
// ---------------------------------------------------------------------------

// UiElement is an immutable snapshot of a single UI element: its stable id,
// accessible name, control kind, and screen rectangle. Ports the
// UiElement record.
type UiElement struct {
	ElementID string `json:"elementId"`
	Name      string `json:"name"`
	Kind      string `json:"kind"`
	X         int    `json:"x"`
	Y         int    `json:"y"`
	Width     int    `json:"width"`
	Height    int    `json:"height"`
}

// ContainsPoint reports whether (x,y) falls inside this element's rectangle.
// The right/bottom edges are exclusive, matching the C# helper exactly. Ports
// UiElementHelpers.ContainsPoint.
func (e UiElement) ContainsPoint(x, y int) bool {
	return x >= e.X && y >= e.Y && x < e.X+e.Width && y < e.Y+e.Height
}

// HitTest returns every element whose rectangle contains (x,y), preserving input
// order. Ports UiElementHelpers.HitTest.
func HitTest(elements []UiElement, x, y int) []UiElement {
	hits := make([]UiElement, 0)
	for _, e := range elements {
		if e.ContainsPoint(x, y) {
			hits = append(hits, e)
		}
	}
	return hits
}

// DumpUiElements renders elements as one debug line each, byte-for-byte matching
// the C# StringBuilder format: `id "name" kind @ (x,y) WxH\n`. Ports
// UiElementHelpers.Dump.
func DumpUiElements(elements []UiElement) string {
	var sb strings.Builder
	for _, e := range elements {
		sb.WriteString(e.ElementID)
		sb.WriteString(" \"")
		sb.WriteString(e.Name)
		sb.WriteString("\" ")
		sb.WriteString(e.Kind)
		sb.WriteString(" @ (")
		sb.WriteString(strconv.Itoa(e.X))
		sb.WriteString(",")
		sb.WriteString(strconv.Itoa(e.Y))
		sb.WriteString(") ")
		sb.WriteString(strconv.Itoa(e.Width))
		sb.WriteByte('x')
		sb.WriteString(strconv.Itoa(e.Height))
		sb.WriteByte('\n')
	}
	return sb.String()
}

// ---------------------------------------------------------------------------
// UiAutomationDriver
// ---------------------------------------------------------------------------

// UiAutomationDriver drives a UI-automation backend: enumerate elements, click,
// type, and press keys. A native Win32-UIA driver implements this in production;
// InMemoryUiAutomationDriver and NullUiAutomationDriver implement it here. Ports
// IUiAutomationDriver (the C# ValueTask methods become context.Context + error).
type UiAutomationDriver interface {
	// BackendID identifies the concrete backend ("in-memory", "null", ...).
	BackendID() string
	// Snapshot returns the current set of UI elements.
	Snapshot(ctx context.Context) ([]UiElement, error)
	// Click activates the element with the given id.
	Click(ctx context.Context, elementID string) error
	// Type sends literal text to the focused element.
	Type(ctx context.Context, text string) error
	// Key presses a named key (e.g. "Enter", "Tab").
	Key(ctx context.Context, keyName string) error
}

// ---------------------------------------------------------------------------
// UiAutomationEvent + InMemoryUiAutomationDriver
// ---------------------------------------------------------------------------

// UiAutomationEvent is raised by the in-memory driver on each interaction so a
// host can observe a virtual session. Kind is one of "click", "type", "key".
// For "click" ElementID is set; for "type"/"key" Payload carries the text/key.
// Ports the UiAutomationEvent record (ElementID / Payload optionality is modelled
// by empty strings, matching the nullable C# fields).
type UiAutomationEvent struct {
	Kind      string `json:"kind"`
	ElementID string `json:"elementId,omitempty"`
	Payload   string `json:"payload,omitempty"`
}

// InMemoryUiAutomationDriver is a real-but-virtual driver: it holds a registry of
// elements and raises observable events on Click/Type/Key without touching a real
// desktop. Ports InMemoryUiAutomationDriver. Safe for concurrent use.
type InMemoryUiAutomationDriver struct {
	mu        sync.Mutex
	elements  map[string]UiElement
	observers []func(UiAutomationEvent)
}

// NewInMemoryUiAutomationDriver constructs an empty in-memory driver.
func NewInMemoryUiAutomationDriver() *InMemoryUiAutomationDriver {
	return &InMemoryUiAutomationDriver{elements: make(map[string]UiElement)}
}

// BackendID returns "in-memory".
func (d *InMemoryUiAutomationDriver) BackendID() string { return "in-memory" }

// Register adds or replaces an element in the virtual UI, keyed by ElementID.
// Ports InMemoryUiAutomationDriver.Register.
func (d *InMemoryUiAutomationDriver) Register(el UiElement) {
	d.mu.Lock()
	defer d.mu.Unlock()
	d.elements[el.ElementID] = el
}

// Observe registers a callback invoked for every subsequent event. Ports
// InMemoryUiAutomationDriver.Observe. A nil callback is ignored.
func (d *InMemoryUiAutomationDriver) Observe(obs func(UiAutomationEvent)) {
	if obs == nil {
		return
	}
	d.mu.Lock()
	defer d.mu.Unlock()
	d.observers = append(d.observers, obs)
}

// Snapshot returns a copy of the currently registered elements.
func (d *InMemoryUiAutomationDriver) Snapshot(ctx context.Context) ([]UiElement, error) {
	if err := ctx.Err(); err != nil {
		return nil, err
	}
	d.mu.Lock()
	defer d.mu.Unlock()
	out := make([]UiElement, 0, len(d.elements))
	for _, el := range d.elements {
		out = append(out, el)
	}
	return out, nil
}

// Click raises a "click" event for elementID, rejecting blank or unknown ids —
// matching the C# ArgumentException / InvalidOperationException guards.
func (d *InMemoryUiAutomationDriver) Click(ctx context.Context, elementID string) error {
	if err := ctx.Err(); err != nil {
		return err
	}
	if strings.TrimSpace(elementID) == "" {
		return errors.New("elementId required")
	}
	d.mu.Lock()
	_, ok := d.elements[elementID]
	d.mu.Unlock()
	if !ok {
		return errors.New("unknown element '" + elementID + "'.")
	}
	d.notify(UiAutomationEvent{Kind: "click", ElementID: elementID})
	return nil
}

// Type raises a "type" event carrying text. An empty string is valid (matching
// the C# guard, which only rejects nil).
func (d *InMemoryUiAutomationDriver) Type(ctx context.Context, text string) error {
	if err := ctx.Err(); err != nil {
		return err
	}
	d.notify(UiAutomationEvent{Kind: "type", Payload: text})
	return nil
}

// Key raises a "key" event for keyName, rejecting blank names.
func (d *InMemoryUiAutomationDriver) Key(ctx context.Context, keyName string) error {
	if err := ctx.Err(); err != nil {
		return err
	}
	if strings.TrimSpace(keyName) == "" {
		return errors.New("keyName required")
	}
	d.notify(UiAutomationEvent{Kind: "key", Payload: keyName})
	return nil
}

// notify dispatches ev to every observer under a snapshot of the observer slice,
// swallowing observer panics so one bad observer cannot break dispatch — mirroring
// the C# try/catch-per-observer.
func (d *InMemoryUiAutomationDriver) notify(ev UiAutomationEvent) {
	d.mu.Lock()
	snap := make([]func(UiAutomationEvent), len(d.observers))
	copy(snap, d.observers)
	d.mu.Unlock()
	for _, o := range snap {
		func() {
			defer func() { _ = recover() }()
			o(ev)
		}()
	}
}

// ---------------------------------------------------------------------------
// NullUiAutomationDriver
// ---------------------------------------------------------------------------

// NullUiAutomationDriver is the fail-safe no-op driver: it enumerates nothing and
// accepts every command silently. Ports NullUiAutomationDriver. Use
// NullUiAutomationDriverInstance for the shared singleton.
type NullUiAutomationDriver struct{}

// NullUiAutomationDriverInstance is the shared no-op driver. Ports
// NullUiAutomationDriver.Instance.
var NullUiAutomationDriverInstance = &NullUiAutomationDriver{}

// BackendID returns "null".
func (NullUiAutomationDriver) BackendID() string { return "null" }

// Snapshot returns no elements.
func (NullUiAutomationDriver) Snapshot(context.Context) ([]UiElement, error) {
	return []UiElement{}, nil
}

// Click does nothing.
func (NullUiAutomationDriver) Click(context.Context, string) error { return nil }

// Type does nothing.
func (NullUiAutomationDriver) Type(context.Context, string) error { return nil }

// Key does nothing.
func (NullUiAutomationDriver) Key(context.Context, string) error { return nil }

// ---------------------------------------------------------------------------
// Interface guards
// ---------------------------------------------------------------------------

var _ UiAutomationDriver = (*InMemoryUiAutomationDriver)(nil)
var _ UiAutomationDriver = (*NullUiAutomationDriver)(nil)
