// collaboration.go
//
// Ports CircleAI.Collaboration (Contracts.cs + InMemoryCollaboration.cs +
// NullImplementations.cs): team channel / message / presence stores.
//
//	Channel / Message / PresenceState (records) -> value structs
//	IChannelStore / IMessageStore / IPresence   -> interfaces (I-prefix dropped)
//	InMemoryChannelStore / MessageStore / Presence -> in-memory impls
//	NullChannelStore / NullMessageStore / NullPresence -> null impls
//
// C# Channel collides with the Go `chan` concept only nominally — the record is
// a chat channel and is named ChatChannel here to avoid confusion with the
// language keyword's connotation and the many transport channels in the package.
// Message is likewise renamed ChatChannelMessage to avoid colliding with
// telephony/network message types in the flat package.

package circleai

import (
	"context"
	"errors"
	"sort"
	"strings"
	"sync"
	"time"
)

// ChatChannel is a team collaboration channel. Ports the Channel record.
type ChatChannel struct {
	ChannelID string
	Name      string
	TeamID    string
}

// ChatChannelMessage is a posted message. Ports the Message record.
type ChatChannelMessage struct {
	MessageID string
	ChannelID string
	AuthorID  string
	Body      string
	AtUTC     time.Time
}

// PresenceState is a user's presence. Ports the PresenceState record.
type PresenceState struct {
	UserID     string
	Online     bool
	LastSeenUTC time.Time
}

// ChannelStore reads channels. Ports IChannelStore.
type ChannelStore interface {
	BackendID() string
	// Get returns the channel for id and true, or (zero, false) if absent.
	Get(ctx context.Context, id string) (ChatChannel, bool)
	// ListForTeam lists a team's channels ordered by Name ascending.
	ListForTeam(ctx context.Context, teamID string) ([]ChatChannel, error)
}

// MessageStore posts and reads messages. Ports IMessageStore.
type MessageStore interface {
	BackendID() string
	Post(ctx context.Context, msg ChatChannelMessage) (ChatChannelMessage, error)
	// Read returns up to limit of a channel's messages, most-recent first.
	Read(ctx context.Context, channelID string, limit int) ([]ChatChannelMessage, error)
}

// Presence reads user presence. Ports IPresence.
type Presence interface {
	BackendID() string
	// Get returns the presence for userID and true, or (zero, false) if absent.
	Get(ctx context.Context, userID string) (PresenceState, bool)
}

// InMemoryChannelStore is a real in-memory channel store. Ports
// InMemoryChannelStore. Construct with NewInMemoryChannelStore.
type InMemoryChannelStore struct {
	mu    sync.RWMutex
	items map[string]ChatChannel
}

// NewInMemoryChannelStore constructs an empty store.
func NewInMemoryChannelStore() *InMemoryChannelStore {
	return &InMemoryChannelStore{items: make(map[string]ChatChannel)}
}

// BackendID returns "in-memory".
func (s *InMemoryChannelStore) BackendID() string { return "in-memory" }

// Upsert stores (or replaces by ChannelId) a channel. Ports Upsert.
func (s *InMemoryChannelStore) Upsert(c ChatChannel) {
	s.mu.Lock()
	s.items[c.ChannelID] = c
	s.mu.Unlock()
}

// Get returns the channel for id. Ports GetAsync. Panics if id is blank.
func (s *InMemoryChannelStore) Get(ctx context.Context, id string) (ChatChannel, bool) {
	if strings.TrimSpace(id) == "" {
		panic("id required")
	}
	s.mu.RLock()
	c, ok := s.items[id]
	s.mu.RUnlock()
	return c, ok
}

// ListForTeam lists a team's channels ordered by Name ascending. Ports
// ListForTeamAsync (OrderBy(Name)). Returns an error if teamID is blank.
func (s *InMemoryChannelStore) ListForTeam(ctx context.Context, teamID string) ([]ChatChannel, error) {
	if strings.TrimSpace(teamID) == "" {
		return nil, errors.New("teamId required")
	}
	s.mu.RLock()
	out := make([]ChatChannel, 0)
	for _, c := range s.items {
		if c.TeamID == teamID {
			out = append(out, c)
		}
	}
	s.mu.RUnlock()
	sort.SliceStable(out, func(i, j int) bool { return cultureLess(out[i].Name, out[j].Name) })
	return out, nil
}

// InMemoryMessageStore keeps messages per channel in insertion order. Ports
// InMemoryMessageStore. Construct with NewInMemoryMessageStore.
type InMemoryMessageStore struct {
	mu        sync.Mutex
	byChannel map[string][]ChatChannelMessage
}

// NewInMemoryMessageStore constructs an empty store.
func NewInMemoryMessageStore() *InMemoryMessageStore {
	return &InMemoryMessageStore{byChannel: make(map[string][]ChatChannelMessage)}
}

// BackendID returns "in-memory".
func (s *InMemoryMessageStore) BackendID() string { return "in-memory" }

// Post appends a message to its channel and echoes it back. Ports PostAsync.
// Returns an error if ChannelId is blank.
func (s *InMemoryMessageStore) Post(ctx context.Context, msg ChatChannelMessage) (ChatChannelMessage, error) {
	if strings.TrimSpace(msg.ChannelID) == "" {
		return ChatChannelMessage{}, errors.New("ChannelId required")
	}
	s.mu.Lock()
	s.byChannel[msg.ChannelID] = append(s.byChannel[msg.ChannelID], msg)
	s.mu.Unlock()
	return msg, nil
}

// Read returns up to limit of a channel's messages, most-recent first. Ports
// ReadAsync (OrderByDescending(AtUtc).Take(limit)). Returns an error if
// channelID is blank.
func (s *InMemoryMessageStore) Read(ctx context.Context, channelID string, limit int) ([]ChatChannelMessage, error) {
	if strings.TrimSpace(channelID) == "" {
		return nil, errors.New("channelId required")
	}
	s.mu.Lock()
	list, ok := s.byChannel[channelID]
	if !ok {
		s.mu.Unlock()
		return []ChatChannelMessage{}, nil
	}
	out := make([]ChatChannelMessage, len(list))
	copy(out, list)
	s.mu.Unlock()
	sort.SliceStable(out, func(i, j int) bool { return out[i].AtUTC.After(out[j].AtUTC) })
	if len(out) > limit {
		out = out[:limit]
	}
	return out, nil
}

// InMemoryPresence is a real in-memory presence store. Ports InMemoryPresence.
// Construct with NewInMemoryPresence.
type InMemoryPresence struct {
	mu     sync.RWMutex
	states map[string]PresenceState
}

// NewInMemoryPresence constructs an empty store.
func NewInMemoryPresence() *InMemoryPresence {
	return &InMemoryPresence{states: make(map[string]PresenceState)}
}

// BackendID returns "in-memory".
func (p *InMemoryPresence) BackendID() string { return "in-memory" }

// Set stores (or replaces by UserId) a presence. Ports Set.
func (p *InMemoryPresence) Set(s PresenceState) {
	p.mu.Lock()
	p.states[s.UserID] = s
	p.mu.Unlock()
}

// Get returns the presence for userID. Ports GetAsync. Panics if userID is blank.
func (p *InMemoryPresence) Get(ctx context.Context, userID string) (PresenceState, bool) {
	if strings.TrimSpace(userID) == "" {
		panic("userId required")
	}
	p.mu.RLock()
	s, ok := p.states[userID]
	p.mu.RUnlock()
	return s, ok
}

// ── Null implementations ────────────────────────────────────────────────────

// NullChannelStore is a no-op channel store. Ports NullChannelStore.
type NullChannelStore struct{}

// NullChannelStoreInstance mirrors NullChannelStore.Instance.
var NullChannelStoreInstance = NullChannelStore{}

// BackendID returns "null".
func (NullChannelStore) BackendID() string { return "null" }
func (NullChannelStore) Get(context.Context, string) (ChatChannel, bool) {
	return ChatChannel{}, false
}
func (NullChannelStore) ListForTeam(context.Context, string) ([]ChatChannel, error) {
	return []ChatChannel{}, nil
}

// NullMessageStore is a no-op message store. Ports NullMessageStore.
type NullMessageStore struct{}

// NullMessageStoreInstance mirrors NullMessageStore.Instance.
var NullMessageStoreInstance = NullMessageStore{}

// BackendID returns "null".
func (NullMessageStore) BackendID() string { return "null" }

// Post echoes the message back (matching the C# NullMessageStore). Ports
// PostAsync.
func (NullMessageStore) Post(ctx context.Context, m ChatChannelMessage) (ChatChannelMessage, error) {
	return m, nil
}
func (NullMessageStore) Read(context.Context, string, int) ([]ChatChannelMessage, error) {
	return []ChatChannelMessage{}, nil
}

// NullPresence is a no-op presence store. Ports NullPresence.
type NullPresence struct{}

// NullPresenceInstance mirrors NullPresence.Instance.
var NullPresenceInstance = NullPresence{}

// BackendID returns "null".
func (NullPresence) BackendID() string { return "null" }
func (NullPresence) Get(context.Context, string) (PresenceState, bool) {
	return PresenceState{}, false
}

// Interface guards.
var (
	_ ChannelStore = (*InMemoryChannelStore)(nil)
	_ MessageStore = (*InMemoryMessageStore)(nil)
	_ Presence     = (*InMemoryPresence)(nil)
	_ ChannelStore = NullChannelStore{}
	_ MessageStore = NullMessageStore{}
	_ Presence     = NullPresence{}
)
