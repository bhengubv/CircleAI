// vision_capture_inmemory.go
//
// Deterministic in-memory IVideoCapture + IBluetoothAnomalyDetector for the Go
// port. The C# reference wires platform camera / Bluetooth-radio backends (out of
// scope, injected) plus the Null* defaults. Per the porting rules (NO stubs — every
// streaming/lifecycle contract gets a working deterministic implementation), this
// file supplies:
//
//   ScriptedVideoCapture           — the camera analogue of ScriptedAudioCapture:
//                                    replays a fixed list of VideoFrames in order,
//                                    then closes (finite recording); WithLoop re-emits
//                                    until ctx cancels (live camera). Mirrors the
//                                    IVideoCapture / NullVideoCapture stream contract.
//
//   InMemoryBluetoothAnomalyDetector — a real IBluetoothAnomalyDetector: Start/Stop
//                                    lifecycle, synchronous fan-out pub/sub to every
//                                    subscriber. A Publish before Start is buffered
//                                    and flushed to subscribers on Start (an unbounded
//                                    Channel<T> in the C# retains writes until read).
//
// CONCURRENCY (Wave-1 lessons, enforced here):
//   - Fan-out delivery snapshots the subscriber list UNDER the lock, RELEASES the
//     lock, THEN invokes handlers — so a handler that unsubscribes (which re-takes
//     the lock) never self-deadlocks.
//   - Subscribe attaches synchronously; there is no consumer goroutine that could
//     race a Publish issued right after Start.
//   - Pre-Start publishes are buffered unbounded (never dropped, never block).

package circleai

import (
	"context"
	"errors"
	"sync"
)

// errBluetoothAnomalyDisposed is returned by Start/Stop after Close.
var errBluetoothAnomalyDisposed = errors.New("bluetooth anomaly detector disposed")

// ---------------------------------------------------------------------------
// ScriptedVideoCapture (IVideoCapture)
// ---------------------------------------------------------------------------

// ScriptedVideoCapture replays a fixed list of VideoFrames as an IVideoCapture. It
// is the camera analogue of ScriptedAudioCapture.
type ScriptedVideoCapture struct {
	frames []VideoFrame
	loop   bool
}

// NewScriptedVideoCapture constructs a capture that yields frames in order, then
// closes. Frames are defensively copied (including their Bytes) so a caller mutating
// the input afterwards cannot affect emitted frames.
func NewScriptedVideoCapture(frames []VideoFrame) *ScriptedVideoCapture {
	cp := make([]VideoFrame, len(frames))
	for i, f := range frames {
		cp[i] = f
		cp[i].Bytes = append([]byte(nil), f.Bytes...)
		if f.RotationDegrees != nil {
			rot := *f.RotationDegrees
			cp[i].RotationDegrees = &rot
		}
	}
	return &ScriptedVideoCapture{frames: cp}
}

// WithLoop returns a shallow copy that re-emits its frames until ctx cancels
// (simulating a continuous camera). The receiver is unchanged.
func (c *ScriptedVideoCapture) WithLoop(loop bool) *ScriptedVideoCapture {
	clone := *c
	clone.loop = loop
	return &clone
}

// CaptureAsync yields the configured frames (looping if enabled), then closes.
// preferredWidth/preferredHeight are accepted for interface parity; the scripted
// frames carry their own dimensions. Cancellation is honoured between frames.
func (c *ScriptedVideoCapture) CaptureAsync(ctx context.Context, preferredWidth, preferredHeight int) <-chan VideoFrame {
	out := make(chan VideoFrame)
	go func() {
		defer close(out)
		for {
			for _, f := range c.frames {
				// Copy per emission so downstream mutation cannot bleed across loops.
				frame := f
				frame.Bytes = append([]byte(nil), f.Bytes...)
				if f.RotationDegrees != nil {
					rot := *f.RotationDegrees
					frame.RotationDegrees = &rot
				}
				select {
				case <-ctx.Done():
					return
				case out <- frame:
				}
			}
			if !c.loop {
				return
			}
			if ctx.Err() != nil {
				return
			}
		}
	}()
	return out
}

// Close is a no-op.
func (c *ScriptedVideoCapture) Close(context.Context) error { return nil }

// ---------------------------------------------------------------------------
// InMemoryBluetoothAnomalyDetector (IBluetoothAnomalyDetector)
// ---------------------------------------------------------------------------

// InMemoryBluetoothAnomalyDetector is a working IBluetoothAnomalyDetector for hosts
// and tests. Anomalies pushed via Publish fan out to every current subscriber while
// the detector is started. Anomalies published before Start are buffered and flushed
// on Start (matching an unbounded Channel<T> that retains writes until read).
type InMemoryBluetoothAnomalyDetector struct {
	backendID string

	mu       sync.Mutex
	started  bool
	disposed bool
	nextID   int
	subs     map[int]func(context.Context, BluetoothAnomaly)
	// buffered holds pre-Start publishes to flush on Start (unbounded, never dropped).
	buffered []BluetoothAnomaly
}

// NewInMemoryBluetoothAnomalyDetector constructs a detector. backendID defaults to
// "in-memory" when empty.
func NewInMemoryBluetoothAnomalyDetector(backendID string) *InMemoryBluetoothAnomalyDetector {
	if backendID == "" {
		backendID = "in-memory"
	}
	return &InMemoryBluetoothAnomalyDetector{
		backendID: backendID,
		subs:      make(map[int]func(context.Context, BluetoothAnomaly)),
	}
}

// BackendID returns the configured backend id.
func (d *InMemoryBluetoothAnomalyDetector) BackendID() string { return d.backendID }

// Subscribe registers an anomaly handler and returns an unsubscribe func. The
// handler is attached synchronously before this returns, so a Publish issued right
// after Subscribe cannot race the subscription. The unsubscribe func is idempotent
// and safe to call from inside a handler (it re-takes the lock only after fan-out
// has released it).
func (d *InMemoryBluetoothAnomalyDetector) Subscribe(handler func(context.Context, BluetoothAnomaly)) func() {
	if handler == nil {
		return func() {}
	}
	d.mu.Lock()
	id := d.nextID
	d.nextID++
	d.subs[id] = handler
	d.mu.Unlock()
	return func() {
		d.mu.Lock()
		delete(d.subs, id)
		d.mu.Unlock()
	}
}

// Start begins monitoring. Idempotent. On the transition to started, any anomalies
// buffered before Start are flushed to current subscribers in order.
func (d *InMemoryBluetoothAnomalyDetector) Start(ctx context.Context) error {
	if err := ctx.Err(); err != nil {
		return err
	}
	d.mu.Lock()
	if d.disposed {
		d.mu.Unlock()
		return errBluetoothAnomalyDisposed
	}
	if d.started {
		d.mu.Unlock()
		return nil
	}
	d.started = true
	pending := d.buffered
	d.buffered = nil
	d.mu.Unlock()

	for _, a := range pending {
		d.fanOut(ctx, a)
	}
	return nil
}

// Stop stops monitoring. Idempotent. Publishes while stopped are buffered again for
// the next Start (the C# radio stops emitting; a Channel keeps retaining writes).
func (d *InMemoryBluetoothAnomalyDetector) Stop(ctx context.Context) error {
	if err := ctx.Err(); err != nil {
		return err
	}
	d.mu.Lock()
	defer d.mu.Unlock()
	if d.disposed {
		return errBluetoothAnomalyDisposed
	}
	d.started = false
	return nil
}

// Publish delivers an anomaly. When started it fans out to every current subscriber;
// when stopped (or before Start) it is buffered for the next Start. This is the
// in-memory seam a host's real Bluetooth radio would drive.
func (d *InMemoryBluetoothAnomalyDetector) Publish(ctx context.Context, anomaly BluetoothAnomaly) {
	d.mu.Lock()
	if d.disposed {
		d.mu.Unlock()
		return
	}
	if !d.started {
		d.buffered = append(d.buffered, anomaly)
		d.mu.Unlock()
		return
	}
	d.mu.Unlock()
	d.fanOut(ctx, anomaly)
}

// fanOut snapshots subscribers under the lock, releases it, then invokes each handler
// — so a handler that unsubscribes (re-taking the lock) does not self-deadlock.
func (d *InMemoryBluetoothAnomalyDetector) fanOut(ctx context.Context, anomaly BluetoothAnomaly) {
	d.mu.Lock()
	snapshot := make([]func(context.Context, BluetoothAnomaly), 0, len(d.subs))
	for _, h := range d.subs {
		snapshot = append(snapshot, h)
	}
	d.mu.Unlock()
	for _, h := range snapshot {
		h(ctx, anomaly)
	}
}

// Close disposes the detector. Idempotent. After Close, Start/Stop report disposed
// and Publish is a no-op.
func (d *InMemoryBluetoothAnomalyDetector) Close(context.Context) error {
	d.mu.Lock()
	defer d.mu.Unlock()
	d.disposed = true
	d.started = false
	d.subs = make(map[int]func(context.Context, BluetoothAnomaly))
	d.buffered = nil
	return nil
}

// Interface guards.
var (
	_ IVideoCapture             = (*ScriptedVideoCapture)(nil)
	_ IBluetoothAnomalyDetector = (*InMemoryBluetoothAnomalyDetector)(nil)
)
