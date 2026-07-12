// workflows_realtime.go
//
// Ports CircleAI.Workflows/PacaRealtime.cs — realtime fan-out for paca
// workflows: permission-aware rooms, query-invalidation events, collaborative
// doc editing, and an agent activity feed. The Socket.IO/Valkey transport is
// host-supplied via RealtimeBroadcaster.
//
//	RealtimePacaEvent (abstract record) -> RealtimePacaEvent interface
//	TaskUpdated/QueryInvalidation/DocCursorMove/AgentActivity/ConversationStep
//	  Event (records)                   -> event structs implementing the interface
//	IRealtimeBroadcaster               -> RealtimeBroadcaster interface
//	PermissionCheck (delegate)          -> func type
//	PacaRealtimeHub                    -> PacaRealtimeHub
//	QueryInvalidation (static)          -> QueryInvalidationKeysFor func
//
// The C# `ev switch { … }` pattern-match becomes a Go type switch. Room
// membership is a per-room set; JoinAsync gates on the permission check (nil
// permission = allow all, matching the C# default).

package circleai

import (
	"context"
	"sort"
	"sync"
	"time"
)

// RealtimePacaEvent is the realtime event union. Ports the abstract
// RealtimePacaEvent record. Every event carries a project id + timestamp.
type RealtimePacaEvent interface {
	ProjectID() string
	At() time.Time
	isRealtimePacaEvent()
}

// TaskUpdatedEvent signals a task changed. Ports TaskUpdatedEvent.
type TaskUpdatedEvent struct {
	Project    string
	When       time.Time
	TaskNumber int
}

// QueryInvalidationEvent carries an explicit query-invalidation key. Ports
// QueryInvalidationEvent.
type QueryInvalidationEvent struct {
	Project  string
	When     time.Time
	QueryKey string
}

// DocCursorMoveEvent signals a collaborator's cursor moved in a doc. Ports
// DocCursorMoveEvent.
type DocCursorMoveEvent struct {
	Project      string
	When         time.Time
	DocID        string
	MemberID     string
	CursorOffset int
}

// AgentActivityEvent signals an agent did something. Ports AgentActivityEvent.
type AgentActivityEvent struct {
	Project       string
	When          time.Time
	AgentMemberID string
	Action        string
	DetailJSON    string
}

// ConversationStepEvent signals a conversation emitted a step. Ports
// ConversationStepEvent.
type ConversationStepEvent struct {
	Project        string
	When           time.Time
	ConversationID string
	Step           ConversationStep
}

func (e TaskUpdatedEvent) ProjectID() string  { return e.Project }
func (e TaskUpdatedEvent) At() time.Time      { return e.When }
func (TaskUpdatedEvent) isRealtimePacaEvent() {}

func (e QueryInvalidationEvent) ProjectID() string  { return e.Project }
func (e QueryInvalidationEvent) At() time.Time      { return e.When }
func (QueryInvalidationEvent) isRealtimePacaEvent() {}

func (e DocCursorMoveEvent) ProjectID() string  { return e.Project }
func (e DocCursorMoveEvent) At() time.Time      { return e.When }
func (DocCursorMoveEvent) isRealtimePacaEvent() {}

func (e AgentActivityEvent) ProjectID() string  { return e.Project }
func (e AgentActivityEvent) At() time.Time      { return e.When }
func (AgentActivityEvent) isRealtimePacaEvent() {}

func (e ConversationStepEvent) ProjectID() string  { return e.Project }
func (e ConversationStepEvent) At() time.Time      { return e.When }
func (ConversationStepEvent) isRealtimePacaEvent() {}

// RealtimeBroadcaster is the host-supplied broadcaster (Socket.IO/Valkey
// Streams/etc.). Ports IRealtimeBroadcaster.
type RealtimeBroadcaster interface {
	Broadcast(ctx context.Context, room string, ev RealtimePacaEvent) error
}

// PermissionCheck returns true if the member may join the room. Ports the
// PermissionCheck delegate.
type PermissionCheck func(ctx context.Context, memberID, room string) (bool, error)

// PacaRealtimeHub routes events into rooms and gates joins with a permission
// check. Ports PacaRealtimeHub. Construct with NewPacaRealtimeHub.
type PacaRealtimeHub struct {
	broadcaster   RealtimeBroadcaster
	permission    PermissionCheck
	mu            sync.Mutex
	membersByRoom map[string]map[string]struct{}
}

// NewPacaRealtimeHub constructs the hub over broadcaster. permission may be nil
// (defaults to allow-all). Panics if broadcaster is nil.
func NewPacaRealtimeHub(broadcaster RealtimeBroadcaster, permission PermissionCheck) *PacaRealtimeHub {
	if broadcaster == nil {
		panic("broadcaster must not be nil")
	}
	if permission == nil {
		permission = func(context.Context, string, string) (bool, error) { return true, nil }
	}
	return &PacaRealtimeHub{
		broadcaster:   broadcaster,
		permission:    permission,
		membersByRoom: make(map[string]map[string]struct{}),
	}
}

// Join attempts to add memberID to room, gated by the permission check. Returns
// true if allowed. Ports JoinAsync.
func (h *PacaRealtimeHub) Join(ctx context.Context, memberID, room string) (bool, error) {
	allowed, err := h.permission(ctx, memberID, room)
	if err != nil {
		return false, err
	}
	if !allowed {
		return false, nil
	}
	h.mu.Lock()
	bucket, ok := h.membersByRoom[room]
	if !ok {
		bucket = make(map[string]struct{})
		h.membersByRoom[room] = bucket
	}
	bucket[memberID] = struct{}{}
	h.mu.Unlock()
	return true, nil
}

// Leave removes memberID from room. Ports Leave.
func (h *PacaRealtimeHub) Leave(memberID, room string) {
	h.mu.Lock()
	if bucket, ok := h.membersByRoom[room]; ok {
		delete(bucket, memberID)
	}
	h.mu.Unlock()
}

// Members returns the members of a room (unordered → sorted for determinism).
// Ports Members.
func (h *PacaRealtimeHub) Members(room string) []string {
	h.mu.Lock()
	bucket := h.membersByRoom[room]
	out := make([]string, 0, len(bucket))
	for m := range bucket {
		out = append(out, m)
	}
	h.mu.Unlock()
	sort.Strings(out)
	return out
}

// Publish broadcasts an event to the project's main room. Ports PublishAsync.
func (h *PacaRealtimeHub) Publish(ctx context.Context, ev RealtimePacaEvent) error {
	return h.broadcaster.Broadcast(ctx, "project:"+ev.ProjectID(), ev)
}

// PublishToDoc broadcasts to a doc collaboration sub-room. Ports
// PublishToDocAsync.
func (h *PacaRealtimeHub) PublishToDoc(ctx context.Context, docID string, ev RealtimePacaEvent) error {
	return h.broadcaster.Broadcast(ctx, "doc:"+docID, ev)
}

// QueryInvalidationKeysFor maps a known event to the client query-invalidation
// keys it should trigger. Ports the static QueryInvalidation.KeysFor.
func QueryInvalidationKeysFor(ev RealtimePacaEvent) []string {
	switch e := ev.(type) {
	case TaskUpdatedEvent:
		return []string{"tasks/" + e.Project, "task/" + e.Project + "/" + itoa(e.TaskNumber)}
	case AgentActivityEvent:
		return []string{"activity/" + e.Project, "agent/" + e.AgentMemberID}
	case ConversationStepEvent:
		return []string{"conversation/" + e.ConversationID, "conversations/" + e.Project}
	case DocCursorMoveEvent:
		return []string{"doc/" + e.DocID + "/cursors"}
	case QueryInvalidationEvent:
		return []string{e.QueryKey}
	default:
		return []string{}
	}
}
