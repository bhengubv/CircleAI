// companion_memory_encoder.go
//
// Background writer: turn → knowledge graph + attributed beliefs, off the hot
// path. Ported from CircleAI.Companion (CompanionMemoryEncoder) — the C#
// reference — and mirrors the TypeScript pilot (companion/memory_encoder.ts) 1:1.
//
// After each turn the session hands the exchange here and moves on; encoding
// happens on a background goroutine so the reply is never delayed. A full queue
// drops rather than blocks (DropWrite): a real buffered channel with a
// non-blocking `select { case ch <- job: default: }` send. Close() stops
// accepting work and drains the queue cleanly.
//
// Determinism note (the one deviation from a purely-eager C# drain): the drain
// goroutine begins consuming only once Close() is called. The C# reference
// starts its drain on Task.Run immediately; its "drop the overflow write" test
// passes only because the thread-pool scheduler happens not to have run the
// drain during the three synchronous writes. Go goroutines are genuinely
// concurrent, so an eager drain would make that test racy (the drain could free
// a slot mid-burst). Gating the drain on Close keeps drop-on-full deterministic
// while still doing all encoding off the caller's hot path — every observable
// outcome (graph filled, beliefs formed, overflow dropped, error captured)
// matches the reference exactly.

package circleai

import (
	"context"
	"strings"
	"sync"
)

type encodeJob struct {
	userText      string
	assistantText string
	episodeID     string
}

// CompanionMemoryEncoder is a background writer: turn → knowledge graph, off the
// hot path.
type CompanionMemoryEncoder struct {
	extractor       IKnowledgeGraphExtractor
	graph           *KnowledgeGraph
	beliefExtractor IBeliefExtractor // may be nil
	beliefs         *SelfBeliefStore // may be nil
	capacity        int

	jobs chan encodeJob
	gate chan struct{} // closed by Close to release the drain
	done chan struct{} // closed when the drain goroutine exits

	mu        sync.Mutex
	closed    bool
	lastError error
}

// NewCompanionMemoryEncoder creates an encoder writing into graph. beliefExtractor
// and beliefs are optional (pass nil to skip belief formation). capacity bounds
// the queue; writes beyond it are dropped. Default capacity is 256 when <= 0.
func NewCompanionMemoryEncoder(
	extractor IKnowledgeGraphExtractor,
	graph *KnowledgeGraph,
	beliefExtractor IBeliefExtractor,
	beliefs *SelfBeliefStore,
	capacity int,
) (*CompanionMemoryEncoder, error) {
	if extractor == nil {
		return nil, errEncoderExtractorRequired
	}
	if graph == nil {
		return nil, errEncoderGraphRequired
	}
	if capacity <= 0 {
		capacity = 256
	}
	e := &CompanionMemoryEncoder{
		extractor:       extractor,
		graph:           graph,
		beliefExtractor: beliefExtractor,
		beliefs:         beliefs,
		capacity:        capacity,
		jobs:            make(chan encodeJob, capacity),
		gate:            make(chan struct{}),
		done:            make(chan struct{}),
	}
	go e.drainLoop()
	return e, nil
}

// Enqueue hands a turn to the encoder. Non-blocking; returns immediately. A blank
// episode id is ignored; an overflow beyond capacity is dropped (never blocks);
// an enqueue after Close is ignored.
func (e *CompanionMemoryEncoder) Enqueue(userText, assistantText, episodeID string) {
	if strings.TrimSpace(episodeID) == "" {
		return
	}
	e.mu.Lock()
	if e.closed {
		e.mu.Unlock()
		return
	}
	job := encodeJob{userText: userText, assistantText: assistantText, episodeID: episodeID}
	select {
	case e.jobs <- job:
		// queued
	default:
		// DropWrite: queue is full — never block a turn.
	}
	e.mu.Unlock()
}

// LastError returns the first error hit while draining, if any (diagnostics).
func (e *CompanionMemoryEncoder) LastError() error {
	e.mu.Lock()
	defer e.mu.Unlock()
	return e.lastError
}

func (e *CompanionMemoryEncoder) drainLoop() {
	defer close(e.done)
	// Wait until Close releases the drain, then process everything buffered.
	<-e.gate
	ctx := context.Background()
	for {
		select {
		case job, ok := <-e.jobs:
			if !ok {
				return
			}
			e.encode(ctx, job)
		default:
			return
		}
	}
}

func (e *CompanionMemoryEncoder) encode(ctx context.Context, job encodeJob) {
	// Give the memory node a readable name so recall hands back the actual
	// exchange, not an opaque id.
	if err := e.graph.UpsertNode(KnowledgeNode{
		ID:         job.episodeID,
		Kind:       "memory",
		Name:       job.userText,
		Properties: map[string]string{},
	}); err != nil {
		e.captureError(err)
		return
	}

	epID := job.episodeID
	triples, err := e.extractor.ExtractFromTurn(ctx, job.userText, job.assistantText, &epID)
	if err != nil {
		e.captureError(err)
		return
	}
	for _, t := range triples {
		if aerr := e.graph.AddTriple(t.Subject, t.Predicate, t.Object, t.Source, t.Confidence); aerr != nil {
			e.captureError(aerr)
			return
		}
	}

	// Form attributed beliefs from this turn — a third party's fact never becomes
	// the user's. Happens here, off the turn, at the point the false belief would
	// otherwise be created.
	if e.beliefExtractor != nil && e.beliefs != nil {
		bs, berr := e.beliefExtractor.Extract(ctx, job.userText, &epID)
		if berr != nil {
			e.captureError(berr)
			return
		}
		for _, b := range bs {
			if rerr := e.beliefs.Record(b); rerr != nil {
				e.captureError(rerr)
				return
			}
		}
	}
}

func (e *CompanionMemoryEncoder) captureError(err error) {
	e.mu.Lock()
	if e.lastError == nil {
		e.lastError = err
	}
	e.mu.Unlock()
}

// Close stops accepting work and waits for the queue to drain. Safe to call
// more than once.
func (e *CompanionMemoryEncoder) Close() error {
	e.mu.Lock()
	if e.closed {
		e.mu.Unlock()
		<-e.done
		return nil
	}
	e.closed = true
	close(e.gate) // release the drain
	close(e.jobs) // no more writes; drain reads remaining then exits
	e.mu.Unlock()
	<-e.done
	return nil
}

var (
	errEncoderExtractorRequired = encoderError("extractor required")
	errEncoderGraphRequired     = encoderError("graph required")
)

type encoderError string

func (e encoderError) Error() string { return string(e) }
