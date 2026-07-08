// sync_bridges.go
//
// Ports the three companion-state sync bridges from CircleAI.Memory.Sync:
//   • PersonaStateSyncBridge          (PersonaStateSyncBridge.cs)
//   • LoraAdapterSyncBridge           (LoraAdapterSyncBridge.cs)
//   • CompanionConversationSyncBridge (CompanionConversationSyncBridge.cs)
//
// Each bridge serialises a strongly-typed record into the opaque
// SyncableEntry.Payload as JSON and pushes it through the sync engine; a
// receiving device's handler decodes it back. Payload field names mirror the
// C# record property names (PascalCase) so the wire shape matches the reference.

package circleai

import (
	"context"
	"encoding/base64"
	"encoding/json"
	"errors"
	"os"
	"path/filepath"
	"time"
)

// ─────────────────────────────────────────────────────────────────────────────
// PersonaStateSyncBridge
// ─────────────────────────────────────────────────────────────────────────────

// PersonaStateEntityType is the EntityType used on the wire for PersonaState.
const PersonaStateEntityType = "PersonaState"

// personaStateDTO is the JSON projection of PersonaState. Field names mirror the
// C# PersonaState property names so the payload shape matches the reference.
type personaStateDTO struct {
	UserID            string             `json:"UserId"`
	LastUpdatedUTC    time.Time          `json:"LastUpdatedUtc"`
	Verbosity         string             `json:"Verbosity"`
	Formality         string             `json:"Formality"`
	PreferredLocale   *string            `json:"PreferredLocale"`
	TopicWeights      map[string]float32 `json:"TopicWeights"`
	DisfavouredTopics []string           `json:"DisfavouredTopics"`
	TotalInteractions int                `json:"TotalInteractions"`
	PositiveSignals   int                `json:"PositiveSignals"`
	NegativeSignals   int                `json:"NegativeSignals"`
}

func personaToDTO(p PersonaState) personaStateDTO {
	topics := p.TopicWeights
	if topics == nil {
		topics = map[string]float32{}
	}
	disfav := make([]string, 0, len(p.DisfavouredTopics))
	for t := range p.DisfavouredTopics {
		disfav = append(disfav, t)
	}
	return personaStateDTO{
		UserID:            p.UserID,
		LastUpdatedUTC:    p.LastUpdatedUTC,
		Verbosity:         p.Verbosity,
		Formality:         p.Formality,
		PreferredLocale:   p.PreferredLocale,
		TopicWeights:      topics,
		DisfavouredTopics: disfav,
		TotalInteractions: p.TotalInteractions,
		PositiveSignals:   p.PositiveSignals,
		NegativeSignals:   p.NegativeSignals,
	}
}

func personaFromDTO(d personaStateDTO) PersonaState {
	topics := d.TopicWeights
	if topics == nil {
		topics = map[string]float32{}
	}
	disfav := make(map[string]struct{}, len(d.DisfavouredTopics))
	for _, t := range d.DisfavouredTopics {
		disfav[t] = struct{}{}
	}
	return PersonaState{
		UserID:            d.UserID,
		LastUpdatedUTC:    d.LastUpdatedUTC,
		Verbosity:         d.Verbosity,
		Formality:         d.Formality,
		PreferredLocale:   d.PreferredLocale,
		TopicWeights:      topics,
		DisfavouredTopics: disfav,
		TotalInteractions: d.TotalInteractions,
		PositiveSignals:   d.PositiveSignals,
		NegativeSignals:   d.NegativeSignals,
	}
}

// PersonaStateSyncBridge bridges IPersonaStore ↔ ICompanionStateSyncEngine. On
// Save, the persona is JSON-serialised and pushed.
type PersonaStateSyncBridge struct {
	store  IPersonaStore
	engine ICompanionStateSyncEngine
}

// NewPersonaStateSyncBridge wires a bridge over a persona store and sync engine.
func NewPersonaStateSyncBridge(store IPersonaStore, engine ICompanionStateSyncEngine) (*PersonaStateSyncBridge, error) {
	if store == nil {
		return nil, errors.New("store required")
	}
	if engine == nil {
		return nil, errors.New("engine required")
	}
	return &PersonaStateSyncBridge{store: store, engine: engine}, nil
}

// Save persists persona locally AND broadcasts it via sync.
func (b *PersonaStateSyncBridge) Save(ctx context.Context, persona PersonaState) error {
	if err := b.store.Save(ctx, persona); err != nil {
		return err
	}
	payload, err := json.Marshal(personaToDTO(persona))
	if err != nil {
		return err
	}
	_, err = b.engine.WriteLocal(ctx, PersonaStateEntityType, persona.UserID, string(payload), false)
	return err
}

// TryDecodePersonaState decodes a SyncableEntry back into a PersonaState. Ok is
// false for tombstones, mismatched entity types, or malformed payloads.
func TryDecodePersonaState(entry SyncableEntry) (PersonaState, bool) {
	if entry.IsTombstone {
		return PersonaState{}, false
	}
	if entry.EntityType != PersonaStateEntityType {
		return PersonaState{}, false
	}
	var dto personaStateDTO
	if err := json.Unmarshal([]byte(entry.Payload), &dto); err != nil {
		return PersonaState{}, false
	}
	return personaFromDTO(dto), true
}

// ─────────────────────────────────────────────────────────────────────────────
// LoraAdapterSyncBridge
// ─────────────────────────────────────────────────────────────────────────────

// LoraAdapterEntityType is the EntityType used on the wire for LoRA adapters.
const LoraAdapterEntityType = "LoraAdapter"

// LoraAdapterSnapshot is the payload of a synced LoRA adapter snapshot.
type LoraAdapterSnapshot struct {
	// AdapterID is a stable id (typically "personal-{userId}").
	AdapterID string `json:"AdapterId"`
	// Base64Bytes is the adapter file contents, base64-encoded.
	Base64Bytes string `json:"Base64Bytes"`
	// TrainedAtUTC is when training that produced these bytes finished.
	TrainedAtUTC time.Time `json:"TrainedAtUtc"`
	// StepCount is the total training steps so far (monotonic).
	StepCount int64 `json:"StepCount"`
}

// LoraAdapterSyncBridge bridges trained LoRA adapter bytes across devices
// through the sync engine.
type LoraAdapterSyncBridge struct {
	engine ICompanionStateSyncEngine
	now    func() time.Time
}

// NewLoraAdapterSyncBridge wires a bridge over the sync engine.
func NewLoraAdapterSyncBridge(engine ICompanionStateSyncEngine) (*LoraAdapterSyncBridge, error) {
	if engine == nil {
		return nil, errors.New("engine required")
	}
	return &LoraAdapterSyncBridge{engine: engine, now: func() time.Time { return time.Now().UTC() }}, nil
}

// Publish reads the adapter file at adapterPath, base64-encodes it, and pushes a
// snapshot to peer devices.
func (b *LoraAdapterSyncBridge) Publish(ctx context.Context, adapterID, adapterPath string, stepCount int64) error {
	if isBlank(adapterID) {
		return errors.New("adapterId required")
	}
	if isBlank(adapterPath) {
		return errors.New("adapterPath required")
	}
	bytes, err := os.ReadFile(adapterPath)
	if err != nil {
		return err
	}
	snapshot := LoraAdapterSnapshot{
		AdapterID:    adapterID,
		Base64Bytes:  base64.StdEncoding.EncodeToString(bytes),
		TrainedAtUTC: b.now(),
		StepCount:    stepCount,
	}
	payload, err := json.Marshal(snapshot)
	if err != nil {
		return err
	}
	_, err = b.engine.WriteLocal(ctx, LoraAdapterEntityType, adapterID, string(payload), false)
	return err
}

// TryWriteLoraAdapter decodes an inbound SyncableEntry and writes the adapter to
// destinationPath. Returns the decoded snapshot for caller-side bookkeeping.
// Ok is false for tombstones, mismatched entity types, or undecodable payloads.
// A snapshot with empty Base64Bytes is returned (ok=true) without writing.
func TryWriteLoraAdapter(entry SyncableEntry, destinationPath string) (LoraAdapterSnapshot, bool, error) {
	if entry.IsTombstone {
		return LoraAdapterSnapshot{}, false, nil
	}
	if entry.EntityType != LoraAdapterEntityType {
		return LoraAdapterSnapshot{}, false, nil
	}
	var snapshot LoraAdapterSnapshot
	if err := json.Unmarshal([]byte(entry.Payload), &snapshot); err != nil {
		// C# swallows the decode error and returns null; mirror that as !ok.
		return LoraAdapterSnapshot{}, false, nil
	}
	if snapshot.Base64Bytes == "" {
		return snapshot, true, nil
	}
	dir := filepath.Dir(destinationPath)
	if dir != "" && dir != "." {
		if err := os.MkdirAll(dir, 0o755); err != nil {
			return snapshot, true, err
		}
	}
	bytes, err := base64.StdEncoding.DecodeString(snapshot.Base64Bytes)
	if err != nil {
		return snapshot, true, err
	}
	if err := os.WriteFile(destinationPath, bytes, 0o644); err != nil {
		return snapshot, true, err
	}
	return snapshot, true, nil
}

// ─────────────────────────────────────────────────────────────────────────────
// CompanionConversationSyncBridge
// ─────────────────────────────────────────────────────────────────────────────

// ConversationStateEntityType is the EntityType used on the wire for
// conversation-state entries.
const ConversationStateEntityType = "ConversationState"

// ConversationStateDelta is the wire-format payload of an in-flight conversation
// turn. The EntityID is the SessionID so multiple sessions converge independently.
type ConversationStateDelta struct {
	// SessionID is the stable identifier the originating device uses.
	SessionID string `json:"SessionId"`
	// UserText is the latest user utterance (may be a partial transcript).
	UserText string `json:"UserText"`
	// AssistantText is the assistant reply so far — empty until tokens emit.
	AssistantText string `json:"AssistantText"`
	// IsTurnComplete is true once the turn finished; false during streaming.
	IsTurnComplete bool `json:"IsTurnComplete"`
	// StartedAtUTC is when the originating device started the turn.
	StartedAtUTC time.Time `json:"StartedAtUtc"`
	// UpdatedAtUTC is when this delta was authored.
	UpdatedAtUTC time.Time `json:"UpdatedAtUtc"`
}

// CompanionConversationSyncBridge bridges live ConversationStateDelta snapshots
// to the sync engine so any peer subscribing to "ConversationState" can mirror
// or hand off the conversation.
type CompanionConversationSyncBridge struct {
	engine ICompanionStateSyncEngine
}

// NewCompanionConversationSyncBridge wires a bridge over the sync engine.
func NewCompanionConversationSyncBridge(engine ICompanionStateSyncEngine) (*CompanionConversationSyncBridge, error) {
	if engine == nil {
		return nil, errors.New("engine required")
	}
	return &CompanionConversationSyncBridge{engine: engine}, nil
}

// Publish broadcasts a conversation-state snapshot to peer devices.
func (b *CompanionConversationSyncBridge) Publish(ctx context.Context, delta ConversationStateDelta) error {
	if isBlank(delta.SessionID) {
		return errors.New("SessionId required")
	}
	payload, err := json.Marshal(delta)
	if err != nil {
		return err
	}
	_, err = b.engine.WriteLocal(ctx, ConversationStateEntityType, delta.SessionID, string(payload), false)
	return err
}

// Terminate marks the session as ended (a tombstone) so peers can clean up.
func (b *CompanionConversationSyncBridge) Terminate(ctx context.Context, sessionID string) error {
	if isBlank(sessionID) {
		return errors.New("sessionId required")
	}
	_, err := b.engine.WriteLocal(ctx, ConversationStateEntityType, sessionID, "", true)
	return err
}

// TryDecodeConversationDelta decodes a sync-layer entry back to a typed delta.
// Ok is false for tombstones, mismatched entity types, or malformed payloads.
func TryDecodeConversationDelta(entry SyncableEntry) (ConversationStateDelta, bool) {
	if entry.IsTombstone {
		return ConversationStateDelta{}, false
	}
	if entry.EntityType != ConversationStateEntityType {
		return ConversationStateDelta{}, false
	}
	var delta ConversationStateDelta
	if err := json.Unmarshal([]byte(entry.Payload), &delta); err != nil {
		return ConversationStateDelta{}, false
	}
	return delta, true
}
