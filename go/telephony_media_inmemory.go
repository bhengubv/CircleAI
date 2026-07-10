// telephony_media_inmemory.go
//
// Deterministic, hermetic in-memory implementations of the telephony media
// surface. There is no C# 1:1 for these — the C# carrier packages ship only a
// PendingMediaStream (a not-yet-attached shell) and delegate the real media pipe
// to the ASP.NET host's WebSocket handler. Per the no-stubs rule, the Go port
// supplies a REAL, no-network IMediaStream so the whole contract (audio in/out,
// DTMF, status events, End/Close) is exercised without a socket:
//
//   PendingMediaStream (Twilio/Telnyx/Plivo, .cs) -> PendingMediaStream (shared)
//   (new)                                         -> InMemoryMediaStream
//   (new, backs the fake carrier)                 -> InMemoryTelephonyCarrier
//   (new)                                         -> InMemoryInboundCallDispatcher
//
// CONCURRENCY (this wave is stream/transport-heavy — the Wave-1 rules):
//   * Audio/DTMF are backed by the package unboundedChannel[T]: a frame written
//     before the peer starts receiving is BUFFERED, not dropped, matching an
//     unbounded C# Channel that retains writes until read.
//   * StatusChanged dispatch SNAPSHOTS the handler set, RELEASES the lock, then
//     invokes — so a handler that calls back into the stream (or unsubscribes)
//     cannot self-deadlock on the same non-reentrant mutex.
//   * The inbound dispatcher registers each subscriber SYNCHRONOUSLY; sessions
//     published after Subscribe returns are delivered, and none are lost to a
//     subscribe-vs-publish race.

package circleai

import (
	"context"
	"errors"
	"sync"
)

// statusNotifier is the shared status-change publisher embedded by every media
// stream / session in this slice. It ports `event EventHandler<CallStatus>` with
// lock-safe fan-out: handlers are snapshotted under the lock, then invoked after
// it is released (Wave-1 rule: never call a continuation while holding a lock its
// handler may re-acquire).
type statusNotifier struct {
	mu       sync.Mutex
	handlers map[int]func(CallStatus)
	nextID   int
}

// subscribe registers handler and returns an unsubscribe func.
func (n *statusNotifier) subscribe(handler func(CallStatus)) func() {
	if handler == nil {
		return func() {}
	}
	n.mu.Lock()
	if n.handlers == nil {
		n.handlers = make(map[int]func(CallStatus))
	}
	id := n.nextID
	n.nextID++
	n.handlers[id] = handler
	n.mu.Unlock()
	return func() {
		n.mu.Lock()
		delete(n.handlers, id)
		n.mu.Unlock()
	}
}

// fire snapshots the handlers under the lock, releases it, then invokes each —
// so a handler re-entering subscribe/unsubscribe or the stream cannot deadlock.
func (n *statusNotifier) fire(status CallStatus) {
	n.mu.Lock()
	snapshot := make([]func(CallStatus), 0, len(n.handlers))
	for _, h := range n.handlers {
		snapshot = append(snapshot, h)
	}
	n.mu.Unlock()
	for _, h := range snapshot {
		h(status)
	}
}

// ---------------------------------------------------------------------------
// PendingMediaStream — ports the C# carrier PendingMediaStream shells
// ---------------------------------------------------------------------------

// PendingMediaStream is the media stream for the moment between "carrier accepted
// dial" and "host's WebSocket attached." It yields no audio/DTMF and Send errors
// with a friendly message. Ports the identical (Twilio/Telnyx/Plivo)
// PendingMediaStream classes — one Go type since their bodies match.
type PendingMediaStream struct {
	info     CallInfo
	notifier statusNotifier

	mu      sync.Mutex
	current CallStatus
}

// NewPendingMediaStream constructs a pending stream in the Ringing state.
func NewPendingMediaStream(info CallInfo) *PendingMediaStream {
	return &PendingMediaStream{info: info, current: CallStatusRinging}
}

// CallInfo returns the captured metadata.
func (p *PendingMediaStream) CallInfo() CallInfo { return p.info }

// CurrentStatus returns the current lifecycle state.
func (p *PendingMediaStream) CurrentStatus() CallStatus {
	p.mu.Lock()
	defer p.mu.Unlock()
	return p.current
}

// ReceiveAudio yields nothing until the host attaches (closes immediately). Ports
// the `yield break` body.
func (p *PendingMediaStream) ReceiveAudio(_ context.Context) <-chan AudioFrame {
	ch := make(chan AudioFrame)
	close(ch)
	return ch
}

// SendAudio errors — cannot send before the host's WebSocket attaches. Ports the
// InvalidOperationException.
func (p *PendingMediaStream) SendAudio(_ context.Context, _ AudioFrame) error {
	return errors.New("Cannot send audio before the host's WebSocket has attached its IMediaStream.")
}

// ReceiveDtmf yields nothing until the host attaches. Ports the `yield break` body.
func (p *PendingMediaStream) ReceiveDtmf(_ context.Context) <-chan DtmfEvent {
	ch := make(chan DtmfEvent)
	close(ch)
	return ch
}

// End marks the call ended from our side and fires the status change. Ports EndAsync.
func (p *PendingMediaStream) End(_ context.Context) error {
	p.mu.Lock()
	p.current = CallStatusEndedByAgent
	p.mu.Unlock()
	p.notifier.fire(CallStatusEndedByAgent)
	return nil
}

// OnStatusChanged subscribes to status changes.
func (p *PendingMediaStream) OnStatusChanged(handler func(CallStatus)) func() {
	return p.notifier.subscribe(handler)
}

// Close is a no-op (ports DisposeAsync => ValueTask.CompletedTask).
func (p *PendingMediaStream) Close(_ context.Context) error { return nil }

// ---------------------------------------------------------------------------
// InMemoryMediaStream — a real, no-network duplex media pipe
// ---------------------------------------------------------------------------

// InMemoryMediaStream is a deterministic IMediaStream backed by unbounded FIFOs.
// It models one endpoint of a call: the AI side reads ReceiveAudio/ReceiveDtmf
// (fed by the "far end" via PushAudio/PushDtmf) and writes SendAudio (captured
// for a test/host to inspect via DrainSentAudio, and delivered to any attached
// far-end sink). Frames pushed before a receiver attaches are buffered, not lost.
type InMemoryMediaStream struct {
	info     CallInfo
	notifier statusNotifier

	inboundAudio *unboundedChannel[AudioFrame]
	inboundDtmf  *unboundedChannel[DtmfEvent]

	mu        sync.Mutex
	current   CallStatus
	ended     bool
	sentAudio []AudioFrame // everything SendAudio delivered, in order
}

// NewInMemoryMediaStream constructs an in-memory stream in the given initial
// status (typically Active once "connected", or Ringing).
func NewInMemoryMediaStream(info CallInfo, initial CallStatus) *InMemoryMediaStream {
	return &InMemoryMediaStream{
		info:         info,
		current:      initial,
		inboundAudio: newUnboundedChannel[AudioFrame](),
		inboundDtmf:  newUnboundedChannel[DtmfEvent](),
	}
}

// CallInfo returns the captured metadata.
func (m *InMemoryMediaStream) CallInfo() CallInfo { return m.info }

// CurrentStatus returns the current lifecycle state.
func (m *InMemoryMediaStream) CurrentStatus() CallStatus {
	m.mu.Lock()
	defer m.mu.Unlock()
	return m.current
}

// ReceiveAudio streams inbound audio frames (pushed by the far end) until End or
// ctx cancels. Buffered frames pushed before this call are delivered.
func (m *InMemoryMediaStream) ReceiveAudio(ctx context.Context) <-chan AudioFrame {
	return m.inboundAudio.ReadAll(ctx)
}

// SendAudio records and delivers one outbound frame. Errors once the stream has
// ended.
func (m *InMemoryMediaStream) SendAudio(ctx context.Context, frame AudioFrame) error {
	if err := ctx.Err(); err != nil {
		return err
	}
	m.mu.Lock()
	if m.ended {
		m.mu.Unlock()
		return errors.New("media stream has ended")
	}
	// Copy the payload so a caller mutating its buffer after Send cannot corrupt
	// an already-captured frame.
	cp := frame
	cp.Pcm = append([]byte(nil), frame.Pcm...)
	m.sentAudio = append(m.sentAudio, cp)
	m.mu.Unlock()
	return nil
}

// ReceiveDtmf streams inbound DTMF events (pushed by the far end) until End or
// ctx cancels.
func (m *InMemoryMediaStream) ReceiveDtmf(ctx context.Context) <-chan DtmfEvent {
	return m.inboundDtmf.ReadAll(ctx)
}

// End marks the call ended from our side, completes the inbound streams so
// in-flight receivers drain then finish, and fires the status change. Idempotent.
func (m *InMemoryMediaStream) End(_ context.Context) error {
	m.mu.Lock()
	if m.ended {
		m.mu.Unlock()
		return nil
	}
	m.ended = true
	m.current = CallStatusEndedByAgent
	m.mu.Unlock()
	m.inboundAudio.Complete()
	m.inboundDtmf.Complete()
	m.notifier.fire(CallStatusEndedByAgent)
	return nil
}

// OnStatusChanged subscribes to status changes.
func (m *InMemoryMediaStream) OnStatusChanged(handler func(CallStatus)) func() {
	return m.notifier.subscribe(handler)
}

// Close ends the stream (idempotent). Ports DisposeAsync for the real pipe.
func (m *InMemoryMediaStream) Close(ctx context.Context) error { return m.End(ctx) }

// PushAudio injects an inbound audio frame from the far-end / caller side. Test
// and host code use this to simulate the caller speaking. Buffered if no
// receiver has attached yet.
func (m *InMemoryMediaStream) PushAudio(frame AudioFrame) {
	m.inboundAudio.Write(frame)
}

// PushDtmf injects an inbound DTMF event from the far-end / caller side.
func (m *InMemoryMediaStream) PushDtmf(ev DtmfEvent) {
	m.inboundDtmf.Write(ev)
}

// SetStatus updates the lifecycle status and fires the change (deduped: no-op if
// unchanged). Lets a host/test drive Active→Voicemail→EndedByCaller etc.
func (m *InMemoryMediaStream) SetStatus(status CallStatus) {
	m.mu.Lock()
	if m.current == status {
		m.mu.Unlock()
		return
	}
	m.current = status
	m.mu.Unlock()
	m.notifier.fire(status)
}

// DrainSentAudio returns and clears the frames SendAudio has delivered so far, in
// order. Lets a test assert what the AI sent to the caller.
func (m *InMemoryMediaStream) DrainSentAudio() []AudioFrame {
	m.mu.Lock()
	defer m.mu.Unlock()
	out := m.sentAudio
	m.sentAudio = nil
	return out
}

// Interface guards.
var (
	_ IMediaStream = (*PendingMediaStream)(nil)
	_ IMediaStream = (*InMemoryMediaStream)(nil)
)
