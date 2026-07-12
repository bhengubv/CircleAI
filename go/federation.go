// federation.go
//
// Ports CircleAI.Federation: the federated-learning round lifecycle,
// sample-weighted delta averaging, and the safe-by-default delta dispatcher.
//
//	RoundStatus / DeltaDispatchOutcome (enums)     -> int consts (stable ordinals)
//	FederationRound / ModelDelta (records)          -> value structs
//	IFederationParticipant                          -> FederationParticipant
//	IFederationAggregator                           -> FederationAggregator
//	IFederationDeltaDispatcher                       -> FederationDeltaDispatcher
//	FederatedAveraging (static)                      -> package funcs
//	InMemoryFederationAggregator                     -> InMemoryFederationAggregator
//	DefaultFederationDeltaDispatcher                 -> DefaultFederationDeltaDispatcher
//
// The C# InMemoryFederationAggregator derives from CircleAIComponentBase (a
// host-side telemetry wrapper: RunOperationAsync / metric counters). That
// wrapper is out of the portable contract — the port preserves the observable
// behaviour (round state machine + signature-validated commit + averaging with
// median fallback) exactly, matching how security_watchdog.go drops the same
// base class.
//
// Signature verification is delegated to a caller-supplied validator delegate,
// exactly as in C# — the aggregator drops deltas whose validator returns false
// at commit time.

package circleai

import (
	"crypto/hmac"
	"crypto/sha256"
	"encoding/binary"
	"errors"
	"math"
	"sort"
	"sync"
	"time"

	"github.com/google/uuid"
)

// RoundStatus is the lifecycle state of a FederationRound. Ports RoundStatus.
// The C# enum has no explicit ordinals; declaration order fixes them
// (Open=0, Aggregating=1, Committed=2, Aborted=3).
type RoundStatus int

const (
	// RoundStatusOpen — accepting deltas.
	RoundStatusOpen RoundStatus = 0
	// RoundStatusAggregating — has the minimum delta count and is averaging.
	RoundStatusAggregating RoundStatus = 1
	// RoundStatusCommitted — committed an aggregated model; further deltas rejected.
	RoundStatusCommitted RoundStatus = 2
	// RoundStatusAborted — abandoned.
	RoundStatusAborted RoundStatus = 3
)

// DeltaDispatchOutcome is the outcome of a VerifyAndSubmit call. Ports
// DeltaDispatchOutcome (explicit ordinals).
type DeltaDispatchOutcome int

const (
	// DeltaAccepted — accepted and recorded for the round.
	DeltaAccepted DeltaDispatchOutcome = 0
	// DeltaSignatureInvalid — signature did not verify.
	DeltaSignatureInvalid DeltaDispatchOutcome = 1
	// DeltaDuplicate — this delta id was already recorded for the round.
	DeltaDuplicate DeltaDispatchOutcome = 2
	// DeltaRoundUnknown — the round id is unknown to the aggregator.
	DeltaRoundUnknown DeltaDispatchOutcome = 3
	// DeltaRoundClosed — the round is not accepting deltas.
	DeltaRoundClosed DeltaDispatchOutcome = 4
)

// FederationRound is one coordinated round of federated learning. Ports the
// FederationRound record. CommittedAt is the zero Time when not yet committed
// (C# nullable DateTimeOffset).
type FederationRound struct {
	ID                      uuid.UUID
	ModelID                 string
	FromVersion             string
	ToVersion               string
	MinParticipants         int
	MaxParticipants         int
	CurrentParticipantCount int
	Status                  RoundStatus
	OpenedAt                time.Time
	CommittedAt             time.Time
}

// ModelDelta is one participant's signed contribution to a round. Ports the
// ModelDelta record.
type ModelDelta struct {
	ID              uuid.UUID
	RoundID         uuid.UUID
	ContributorUhid string
	ModelID         string
	FromVersion     string
	DeltaPayload    []byte
	SampleCount     int
	Signature       []byte
	SubmittedAt     time.Time
}

// FederationParticipant is a device contributing to federation rounds. Ports
// IFederationParticipant.
type FederationParticipant interface {
	// ProduceDelta trains locally and returns the resulting signed delta.
	ProduceDelta(round FederationRound) (ModelDelta, error)
	// ApplyAggregatedModel applies an aggregated model, reporting success.
	ApplyAggregatedModel(modelID, newVersion string, aggregatedPayload []byte) (bool, error)
}

// FederationAggregator coordinates federation rounds. Ports IFederationAggregator.
type FederationAggregator interface {
	OpenRound(modelID, fromVersion, toVersion string, minParticipants, maxParticipants int) (FederationRound, error)
	SubmitDelta(delta ModelDelta) error
	// TryCommit returns the aggregated payload when MinParticipants valid deltas
	// have been collected, or nil otherwise (payload present, second return true
	// only on a committed round).
	TryCommit(roundID uuid.UUID) ([]byte, error)
	GetRound(roundID uuid.UUID) (FederationRound, error)
}

// FederationDeltaDispatcher verifies + dedups + submits in one call. Ports
// IFederationDeltaDispatcher.
type FederationDeltaDispatcher interface {
	VerifyAndSubmit(delta ModelDelta) (DeltaDispatchOutcome, error)
}

// Sentinel errors mirroring the C# KeyNotFoundException / InvalidOperationException
// paths in InMemoryFederationAggregator.
var (
	// ErrFederationRoundUnknown mirrors KeyNotFoundException for an unknown round.
	ErrFederationRoundUnknown = errors.New("federation round is unknown")
	// ErrFederationRoundNotAccepting mirrors InvalidOperationException when a round
	// is not Open.
	ErrFederationRoundNotAccepting = errors.New("federation round is not accepting deltas")
	// ErrFederationMaxParticipants mirrors InvalidOperationException when a round
	// has reached MaxParticipants.
	ErrFederationMaxParticipants = errors.New("federation round has reached MaxParticipants")
)

// ── FederatedAveraging ──────────────────────────────────────────────────────

// FederatedAverage computes the sample-size-weighted average of the supplied
// deltas' payloads (interpreted as little-endian IEEE-754 float32) and returns
// the encoded result. Ports FederatedAveraging.Average. Returns an error when
// the list is empty, payloads are empty / inconsistent / not a float32 multiple,
// a SampleCount is negative, or total weight is zero.
func FederatedAverage(deltas []ModelDelta) ([]byte, error) {
	if len(deltas) == 0 {
		return nil, errors.New("Cannot average an empty delta list.")
	}
	expectedBytes := len(deltas[0].DeltaPayload)
	if expectedBytes == 0 {
		return nil, errors.New("Delta payloads must be non-empty.")
	}
	if expectedBytes%4 != 0 {
		return nil, errors.New("Delta payload length must be a multiple of 4 bytes.")
	}
	for i := 1; i < len(deltas); i++ {
		if len(deltas[i].DeltaPayload) != expectedBytes {
			return nil, errors.New("Delta payload length mismatch.")
		}
	}
	floatCount := expectedBytes / 4
	var totalSamples int64
	for _, d := range deltas {
		if d.SampleCount < 0 {
			return nil, errors.New("SampleCount must be non-negative.")
		}
		totalSamples += int64(d.SampleCount)
	}
	if totalSamples == 0 {
		return nil, errors.New("Total sample weight across deltas is zero — cannot perform weighted average.")
	}
	acc := make([]float64, floatCount)
	for _, d := range deltas {
		weight := float64(d.SampleCount) / float64(totalSamples)
		for i := 0; i < floatCount; i++ {
			bits := binary.LittleEndian.Uint32(d.DeltaPayload[i*4 : i*4+4])
			acc[i] += float64(math.Float32frombits(bits)) * weight
		}
	}
	out := make([]byte, expectedBytes)
	for i := 0; i < floatCount; i++ {
		binary.LittleEndian.PutUint32(out[i*4:i*4+4], math.Float32bits(float32(acc[i])))
	}
	return out, nil
}

// FederatedEncodeFloats encodes a float32 slice as little-endian IEEE-754 bytes.
// Ports FederatedAveraging.EncodeFloats.
func FederatedEncodeFloats(values []float32) []byte {
	out := make([]byte, len(values)*4)
	for i, v := range values {
		binary.LittleEndian.PutUint32(out[i*4:i*4+4], math.Float32bits(v))
	}
	return out
}

// FederatedDecodeFloats decodes little-endian IEEE-754 bytes into a float32
// slice. Ports FederatedAveraging.DecodeFloats. Returns an error when the length
// is not a float32 multiple.
func FederatedDecodeFloats(payload []byte) ([]float32, error) {
	if len(payload)%4 != 0 {
		return nil, errors.New("Payload length must be a multiple of 4 bytes.")
	}
	out := make([]float32, len(payload)/4)
	for i := range out {
		out[i] = math.Float32frombits(binary.LittleEndian.Uint32(payload[i*4 : i*4+4]))
	}
	return out, nil
}

// ── InMemoryFederationAggregator ────────────────────────────────────────────

type fedRoundState struct {
	mu               sync.Mutex
	snapshot         FederationRound
	deltas           []ModelDelta
	committedPayload []byte
}

// InMemoryFederationAggregator is the in-process reference FederationAggregator.
// Ports InMemoryFederationAggregator. Construct with
// NewInMemoryFederationAggregator, passing a signature validator (use a func
// returning true in tests where signatures are not the subject).
type InMemoryFederationAggregator struct {
	mu                 sync.Mutex
	rounds             map[uuid.UUID]*fedRoundState
	signatureValidator func(ModelDelta) bool
}

// NewInMemoryFederationAggregator constructs the aggregator. Panics if
// signatureValidator is nil (mirrors ArgumentNullException).
func NewInMemoryFederationAggregator(signatureValidator func(ModelDelta) bool) *InMemoryFederationAggregator {
	if signatureValidator == nil {
		panic("signatureValidator must not be nil")
	}
	return &InMemoryFederationAggregator{
		rounds:             make(map[uuid.UUID]*fedRoundState),
		signatureValidator: signatureValidator,
	}
}

// OpenRound opens a new round. Ports OpenRoundAsync. Returns an error when
// modelId/fromVersion/toVersion is empty, minParticipants <= 0, or
// maxParticipants < minParticipants.
func (a *InMemoryFederationAggregator) OpenRound(modelID, fromVersion, toVersion string, minParticipants, maxParticipants int) (FederationRound, error) {
	if modelID == "" {
		return FederationRound{}, errors.New("modelId required")
	}
	if fromVersion == "" {
		return FederationRound{}, errors.New("fromVersion required")
	}
	if toVersion == "" {
		return FederationRound{}, errors.New("toVersion required")
	}
	if minParticipants <= 0 {
		return FederationRound{}, errors.New("minParticipants must be positive.")
	}
	if maxParticipants < minParticipants {
		return FederationRound{}, errors.New("maxParticipants must be >= minParticipants.")
	}
	round := FederationRound{
		ID:              uuid.New(),
		ModelID:         modelID,
		FromVersion:     fromVersion,
		ToVersion:       toVersion,
		MinParticipants: minParticipants,
		MaxParticipants: maxParticipants,
		Status:          RoundStatusOpen,
		OpenedAt:        time.Now().UTC(),
	}
	state := &fedRoundState{snapshot: round}
	a.mu.Lock()
	a.rounds[round.ID] = state
	a.mu.Unlock()
	return round, nil
}

// SubmitDelta submits a signed delta to its round. Ports SubmitDeltaAsync.
// Returns ErrFederationRoundUnknown for an unknown round,
// ErrFederationRoundNotAccepting when the round is not Open, and
// ErrFederationMaxParticipants when the round is full. An empty payload is
// silently ignored (not stored, not counted) — matching the C# behaviour that
// keeps the round viable.
func (a *InMemoryFederationAggregator) SubmitDelta(delta ModelDelta) error {
	a.mu.Lock()
	state, ok := a.rounds[delta.RoundID]
	a.mu.Unlock()
	if !ok {
		return ErrFederationRoundUnknown
	}
	if len(delta.DeltaPayload) == 0 {
		return nil
	}
	state.mu.Lock()
	defer state.mu.Unlock()
	if state.snapshot.Status != RoundStatusOpen {
		return ErrFederationRoundNotAccepting
	}
	if len(state.deltas) >= state.snapshot.MaxParticipants {
		return ErrFederationMaxParticipants
	}
	state.deltas = append(state.deltas, delta)
	state.snapshot.CurrentParticipantCount = len(state.deltas)
	return nil
}

// TryCommit attempts to commit the round. Ports TryCommitAsync. Returns the
// aggregated payload (and nil error) when MinParticipants signature-valid deltas
// are present; returns (nil, nil) when below threshold or the round is Aborted;
// re-returns the committed payload idempotently when already Committed. Falls
// back to the median-by-SampleCount payload when averaging fails (inconsistent
// encoding), matching the C# ArgumentException catch.
func (a *InMemoryFederationAggregator) TryCommit(roundID uuid.UUID) ([]byte, error) {
	a.mu.Lock()
	state, ok := a.rounds[roundID]
	a.mu.Unlock()
	if !ok {
		return nil, ErrFederationRoundUnknown
	}
	state.mu.Lock()
	defer state.mu.Unlock()
	if state.snapshot.Status == RoundStatusCommitted {
		return state.committedPayload, nil
	}
	if state.snapshot.Status == RoundStatusAborted {
		return nil, nil
	}
	valid := make([]ModelDelta, 0, len(state.deltas))
	for _, d := range state.deltas {
		if a.signatureValidator(d) {
			valid = append(valid, d)
		}
	}
	if len(valid) < state.snapshot.MinParticipants {
		return nil, nil
	}
	state.snapshot.Status = RoundStatusAggregating
	aggregated, err := FederatedAverage(valid)
	if err != nil {
		aggregated = fedFallbackMedianPayload(valid)
	}
	state.committedPayload = aggregated
	state.snapshot.Status = RoundStatusCommitted
	state.snapshot.CommittedAt = time.Now().UTC()
	return aggregated, nil
}

// GetRound returns the current round snapshot. Ports GetRoundAsync. Returns
// ErrFederationRoundUnknown when the round is unknown.
func (a *InMemoryFederationAggregator) GetRound(roundID uuid.UUID) (FederationRound, error) {
	a.mu.Lock()
	state, ok := a.rounds[roundID]
	a.mu.Unlock()
	if !ok {
		return FederationRound{}, ErrFederationRoundUnknown
	}
	state.mu.Lock()
	defer state.mu.Unlock()
	return state.snapshot, nil
}

// RoundCount returns the number of tracked rounds (diagnostic). Ports RoundCount.
func (a *InMemoryFederationAggregator) RoundCount() int {
	a.mu.Lock()
	defer a.mu.Unlock()
	return len(a.rounds)
}

// fedFallbackMedianPayload copies the payload of the median delta by SampleCount.
// Ports FallbackMedianPayload.
func fedFallbackMedianPayload(deltas []ModelDelta) []byte {
	ordered := make([]ModelDelta, len(deltas))
	copy(ordered, deltas)
	sort.SliceStable(ordered, func(i, j int) bool { return ordered[i].SampleCount < ordered[j].SampleCount })
	median := ordered[len(ordered)/2]
	out := make([]byte, len(median.DeltaPayload))
	copy(out, median.DeltaPayload)
	return out
}

// ── DefaultFederationDeltaDispatcher ────────────────────────────────────────

// DefaultFederationDeltaDispatcher composes an aggregator with a signature
// validator and a replay-dedup set so a consumer cannot accept an unsigned or
// replayed delta. It realises IFederationDeltaDispatcher's documented three-step
// behaviour (verify signature -> dedup by delta id -> submit) as a real,
// non-stub implementation. Ports DefaultFederationDeltaDispatcher. Construct with
// NewDefaultFederationDeltaDispatcher.
//
// CONCURRENCY: the dedup set is guarded by its own mutex; the delta id is claimed
// atomically *before* submit (a replay loses the race) and un-claimed when the
// submit is rejected (round unknown / closed), so a rejected submit does not
// poison future retries — mirroring the C# ConcurrentDictionary TryAdd/TryRemove.
type DefaultFederationDeltaDispatcher struct {
	agg      FederationAggregator
	validate func(ModelDelta) bool
	mu       sync.Mutex
	seen     map[uuid.UUID]bool // key = delta id
}

// NewDefaultFederationDeltaDispatcher constructs a dispatcher over aggregator
// using signatureValidator for signature verification. Panics if either is nil
// (mirrors ArgumentNullException).
// NewInMemoryFederationDeltaDispatcher is a back-compat alias for the renamed
// NewDefaultFederationDeltaDispatcher (canonical name now matches the C# reference).
func NewInMemoryFederationDeltaDispatcher(aggregator FederationAggregator, signatureValidator func(ModelDelta) bool) *DefaultFederationDeltaDispatcher {
	return NewDefaultFederationDeltaDispatcher(aggregator, signatureValidator)
}

func NewDefaultFederationDeltaDispatcher(aggregator FederationAggregator, signatureValidator func(ModelDelta) bool) *DefaultFederationDeltaDispatcher {
	if aggregator == nil {
		panic("aggregator must not be nil")
	}
	if signatureValidator == nil {
		panic("signatureValidator must not be nil")
	}
	return &DefaultFederationDeltaDispatcher{agg: aggregator, validate: signatureValidator, seen: make(map[uuid.UUID]bool)}
}

// VerifyAndSubmit verifies the signature, atomically claims the delta id (a
// replay loses the race), then submits — un-claiming the id if the aggregator
// rejects the delta. Ports VerifyAndSubmitAsync — it returns an outcome rather
// than raising so the caller can branch without try/catch.
func (d *DefaultFederationDeltaDispatcher) VerifyAndSubmit(delta ModelDelta) (DeltaDispatchOutcome, error) {
	// 1. Verify the signature first — a forged or unsigned delta never touches the round.
	if !d.validate(delta) {
		return DeltaSignatureInvalid, nil
	}
	// 2. De-duplicate: atomically claim the delta id; a replay loses the race.
	d.mu.Lock()
	if d.seen[delta.ID] {
		d.mu.Unlock()
		return DeltaDuplicate, nil
	}
	d.seen[delta.ID] = true
	d.mu.Unlock()
	// 3. Submit, translating the aggregator's errors into outcomes so the caller
	//    can branch on the result without a try/catch of its own. On rejection the
	//    id is un-claimed so a later legitimate retry is not blocked.
	if err := d.agg.SubmitDelta(delta); err != nil {
		d.mu.Lock()
		delete(d.seen, delta.ID)
		d.mu.Unlock()
		switch {
		case errors.Is(err, ErrFederationRoundUnknown):
			return DeltaRoundUnknown, nil
		case errors.Is(err, ErrFederationRoundNotAccepting), errors.Is(err, ErrFederationMaxParticipants):
			return DeltaRoundClosed, nil
		default:
			return DeltaRoundClosed, err
		}
	}
	return DeltaAccepted, nil
}

// HMACSignatureValidator returns a signature validator that verifies each
// delta's Signature as HMAC-SHA256(key, ContributorUhid|ModelId|FromVersion|Payload).
// It is a concrete, deterministic validator suitable for wiring the aggregator /
// dispatcher end-to-end without a real UHID key ring.
func HMACSignatureValidator(key []byte) func(ModelDelta) bool {
	return func(delta ModelDelta) bool {
		mac := hmac.New(sha256.New, key)
		mac.Write([]byte(delta.ContributorUhid + "|" + delta.ModelID + "|" + delta.FromVersion + "|"))
		mac.Write(delta.DeltaPayload)
		expected := mac.Sum(nil)
		return hmac.Equal(expected, delta.Signature)
	}
}

// HMACSignDelta computes the HMAC-SHA256 signature HMACSignatureValidator
// expects, for use by participants / tests that need to produce a valid delta.
func HMACSignDelta(key []byte, delta ModelDelta) []byte {
	mac := hmac.New(sha256.New, key)
	mac.Write([]byte(delta.ContributorUhid + "|" + delta.ModelID + "|" + delta.FromVersion + "|"))
	mac.Write(delta.DeltaPayload)
	return mac.Sum(nil)
}

// Interface guards.
var (
	_ FederationAggregator      = (*InMemoryFederationAggregator)(nil)
	_ FederationDeltaDispatcher = (*DefaultFederationDeltaDispatcher)(nil)
)
