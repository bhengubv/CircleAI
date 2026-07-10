// media_hub.go
//
// Ports the CircleAI.MediaHub server vertical (Contracts.cs + InMemoryMediaHub.cs):
//   MediaItem (record)          -> MediaItem
//   PlaybackPosition (record)   -> PlaybackPosition
//   IMediaLibrary               -> HubMediaLibrary interface
//   ISyncedPlayback             -> SyncedPlayback interface
//   InMemoryMediaLibrary        -> InMemoryHubMediaLibrary
//   InMemorySyncedPlayback      -> InMemorySyncedPlayback
//
// FLAT-PACKAGE DISAMBIGUATION: CircleAI.Media already occupies the concrete
// names MediaLibrary / InMemoryMediaLibrary (asset catalog, media_library.go).
// The MediaHub library types are therefore prefixed Hub (HubMediaLibrary /
// InMemoryHubMediaLibrary). MediaItem and PlaybackPosition are unique across the
// two modules and keep their spec names. The MediaHub IMediaLibrary is async
// (BackendId + GetAsync/SearchAsync); its Go form takes a context and returns
// errors — Async suffixes are dropped per the tree's Go convention.
//
// CONCURRENCY (stream/transport-heavy wave): InMemorySyncedPlayback.BroadcastPosition
// snapshots the subscriber list UNDER the per-session lock and invokes each
// handler OUTSIDE the lock, so a handler that subscribes/unsubscribes (its
// disposal path re-acquires the same lock) can never self-deadlock the
// broadcaster. Subscriptions live in an unbounded slice: a subscriber attached
// before any broadcast receives every subsequent position, none are dropped.
// A subscriber handler that returns an error is isolated (logged-equivalent:
// swallowed) exactly as the C# catch around each ValueTask await.

package circleai

import (
	"context"
	"errors"
	"sort"
	"strings"
	"sync"
	"time"
)

// MediaItem is one playable item exposed by a MediaHub library. Ports the
// MediaItem record. Duration here is a plain time.Duration (the C# field is a
// non-nullable TimeSpan, unlike CircleAI.Media's nullable MediaAsset.Duration).
type MediaItem struct {
	// ItemId is the library-unique identifier.
	ItemId string
	// Title is the human-readable title (searched by substring).
	Title string
	// Kind is a free-form classification string (e.g. "audio", "video").
	Kind string
	// Duration is the play length.
	Duration time.Duration
	// MimeType is the MIME type (e.g. "audio/mpeg").
	MimeType string
}

// PlaybackPosition is a timestamped playback cursor broadcast to a synced
// session. Ports the PlaybackPosition record.
type PlaybackPosition struct {
	// ItemId identifies the item being played.
	ItemId string
	// Position is the current offset into the item.
	Position time.Duration
	// AtUtc is the wall-clock instant the position was captured (UTC).
	AtUtc time.Time
}

// HubMediaLibrary is the MediaHub media-library contract: a backend-identified,
// query-able catalog of MediaItems. Ports the CircleAI.MediaHub IMediaLibrary
// (named HubMediaLibrary to avoid colliding with CircleAI.Media's MediaLibrary).
type HubMediaLibrary interface {
	// BackendId identifies the backing store (e.g. "in-memory").
	BackendId() string
	// Get returns the item for id and true, or (zero, false) when absent. Returns
	// an error when id is blank (ports the C# ArgumentException).
	Get(ctx context.Context, id string) (MediaItem, bool, error)
	// Search returns up to topK items whose Title contains query
	// (case-insensitive), ordered by Title ascending. Returns an error when
	// topK <= 0 (ports ArgumentOutOfRangeException).
	Search(ctx context.Context, query string, topK int) ([]MediaItem, error)
}

// SyncedPlayback is the broadcast/subscribe playback-sync contract. Ports the
// ISyncedPlayback interface (Subscribe returns an unsubscribe func in place of
// the C# IDisposable token).
type SyncedPlayback interface {
	// BackendId identifies the backing store (e.g. "in-memory").
	BackendId() string
	// JoinSession records userId as a member of sessionId. Returns an error when
	// either argument is blank (ports the C# ArgumentException).
	JoinSession(ctx context.Context, sessionId, userId string) error
	// BroadcastPosition delivers pos to every current subscriber of sessionId.
	// A subscriber that errors is isolated and does not stop the others. Returns
	// an error when sessionId is blank; an unknown session is a no-op.
	BroadcastPosition(ctx context.Context, sessionId string, pos PlaybackPosition) error
	// Subscribe registers handler for sessionId's positions and returns an
	// idempotent unsubscribe func (ports the IDisposable subscription token).
	Subscribe(sessionId string, handler func(PlaybackPosition) error) (unsubscribe func(), err error)
}

// ---------------------------------------------------------------------------
// InMemoryHubMediaLibrary — title-substring searchable MediaItem catalog
// ---------------------------------------------------------------------------

// InMemoryHubMediaLibrary is a concurrency-safe in-memory HubMediaLibrary keyed
// on ItemId with ordinal comparison, matching the C#
// ConcurrentDictionary<string, MediaItem>(StringComparer.Ordinal). Ports the
// CircleAI.MediaHub InMemoryMediaLibrary. BackendId is always "in-memory".
type InMemoryHubMediaLibrary struct {
	mu    sync.RWMutex
	items map[string]MediaItem
}

// NewInMemoryHubMediaLibrary constructs an empty library.
func NewInMemoryHubMediaLibrary() *InMemoryHubMediaLibrary {
	return &InMemoryHubMediaLibrary{items: make(map[string]MediaItem)}
}

// BackendId returns "in-memory".
func (l *InMemoryHubMediaLibrary) BackendId() string { return "in-memory" }

// Add stores or replaces item by ItemId (ports the seed helper).
func (l *InMemoryHubMediaLibrary) Add(item MediaItem) {
	l.mu.Lock()
	l.items[item.ItemId] = item
	l.mu.Unlock()
}

// Get returns the item for id and true, or (zero, false) when absent. Blank id
// is rejected (ports the C# ArgumentException("id required")).
func (l *InMemoryHubMediaLibrary) Get(ctx context.Context, id string) (MediaItem, bool, error) {
	if err := ctx.Err(); err != nil {
		return MediaItem{}, false, err
	}
	if strings.TrimSpace(id) == "" {
		return MediaItem{}, false, errors.New("id required")
	}
	l.mu.RLock()
	item, ok := l.items[id]
	l.mu.RUnlock()
	return item, ok, nil
}

// Search returns up to topK items whose Title contains query (case-insensitive),
// ordered by Title ascending (ordinal-ignore-case). topK must be positive. Ports
// Where(Title.Contains(query, OrdinalIgnoreCase)).OrderBy(Title, OrdinalIgnoreCase).Take(topK).
func (l *InMemoryHubMediaLibrary) Search(ctx context.Context, query string, topK int) ([]MediaItem, error) {
	if err := ctx.Err(); err != nil {
		return nil, err
	}
	if topK <= 0 {
		return nil, errors.New("topK must be positive")
	}
	needle := strings.ToLower(query)
	l.mu.RLock()
	hits := make([]MediaItem, 0)
	for _, i := range l.items {
		if strings.Contains(strings.ToLower(i.Title), needle) {
			hits = append(hits, i)
		}
	}
	l.mu.RUnlock()
	// OrderBy Title ascending, case-insensitive; stable ItemId tiebreak keeps
	// equal-title ordering deterministic (map iteration order is unspecified).
	sort.SliceStable(hits, func(a, b int) bool {
		la, lb := strings.ToLower(hits[a].Title), strings.ToLower(hits[b].Title)
		if la != lb {
			return la < lb
		}
		return hits[a].ItemId < hits[b].ItemId
	})
	if len(hits) > topK {
		hits = hits[:topK]
	}
	return hits, nil
}

// ---------------------------------------------------------------------------
// InMemorySyncedPlayback — broadcast/subscribe position sync
// ---------------------------------------------------------------------------

// playbackSub is one live subscription (identity via pointer, like the C#
// delegate reference removed on dispose).
type playbackSub struct {
	handler func(PlaybackPosition) error
}

// playbackSession is the per-session member set + subscriber list. It carries
// its own lock so a broadcast on one session never contends with another.
type playbackSession struct {
	mu      sync.Mutex
	members map[string]struct{}
	subs    []*playbackSub
}

// InMemorySyncedPlayback is a concurrency-safe in-memory SyncedPlayback. Ports
// InMemorySyncedPlayback. BackendId is always "in-memory". Sessions are created
// lazily on first Join/Subscribe (ports GetOrAdd).
type InMemorySyncedPlayback struct {
	mu       sync.Mutex
	sessions map[string]*playbackSession
}

// NewInMemorySyncedPlayback constructs an empty playback coordinator.
func NewInMemorySyncedPlayback() *InMemorySyncedPlayback {
	return &InMemorySyncedPlayback{sessions: make(map[string]*playbackSession)}
}

// BackendId returns "in-memory".
func (p *InMemorySyncedPlayback) BackendId() string { return "in-memory" }

// getOrAddSession returns the session for id, creating it if absent (ports
// ConcurrentDictionary.GetOrAdd).
func (p *InMemorySyncedPlayback) getOrAddSession(id string) *playbackSession {
	p.mu.Lock()
	s, ok := p.sessions[id]
	if !ok {
		s = &playbackSession{members: make(map[string]struct{})}
		p.sessions[id] = s
	}
	p.mu.Unlock()
	return s
}

// tryGetSession returns the session for id and true, or (nil, false) when absent.
func (p *InMemorySyncedPlayback) tryGetSession(id string) (*playbackSession, bool) {
	p.mu.Lock()
	s, ok := p.sessions[id]
	p.mu.Unlock()
	return s, ok
}

// JoinSession records userId as a member of sessionId, creating the session if
// needed. Blank arguments are rejected (ports the C# ArgumentException guards).
func (p *InMemorySyncedPlayback) JoinSession(ctx context.Context, sessionId, userId string) error {
	if err := ctx.Err(); err != nil {
		return err
	}
	if strings.TrimSpace(sessionId) == "" {
		return errors.New("sessionId required")
	}
	if strings.TrimSpace(userId) == "" {
		return errors.New("userId required")
	}
	s := p.getOrAddSession(sessionId)
	s.mu.Lock()
	s.members[userId] = struct{}{}
	s.mu.Unlock()
	return nil
}

// BroadcastPosition delivers pos to every current subscriber of sessionId. Blank
// sessionId is rejected; an unknown session is a silent no-op (ports the early
// return in the C#). Subscribers are snapshotted under the session lock and
// invoked OUTSIDE it so a (un)subscribing handler cannot self-deadlock; a
// handler error is swallowed exactly as the C# per-subscriber try/catch.
func (p *InMemorySyncedPlayback) BroadcastPosition(ctx context.Context, sessionId string, pos PlaybackPosition) error {
	if err := ctx.Err(); err != nil {
		return err
	}
	if strings.TrimSpace(sessionId) == "" {
		return errors.New("sessionId required")
	}
	s, ok := p.tryGetSession(sessionId)
	if !ok {
		return nil
	}

	s.mu.Lock()
	snapshot := make([]*playbackSub, len(s.subs))
	copy(snapshot, s.subs)
	s.mu.Unlock()

	for _, sub := range snapshot {
		func() {
			// Isolate a panicking or erroring handler; one bad subscriber must
			// not stop delivery to the rest (mirrors the C# catch-per-subscriber).
			defer func() { _ = recover() }()
			_ = sub.handler(pos)
		}()
	}
	return nil
}

// Subscribe registers handler for sessionId's positions, creating the session if
// needed, and returns an idempotent unsubscribe func. Blank sessionId or nil
// handler is rejected (ports the C# ArgumentException / ArgumentNullException).
func (p *InMemorySyncedPlayback) Subscribe(sessionId string, handler func(PlaybackPosition) error) (func(), error) {
	if strings.TrimSpace(sessionId) == "" {
		return nil, errors.New("sessionId required")
	}
	if handler == nil {
		return nil, errors.New("handler required")
	}
	s := p.getOrAddSession(sessionId)
	sub := &playbackSub{handler: handler}
	s.mu.Lock()
	s.subs = append(s.subs, sub)
	s.mu.Unlock()

	var once sync.Once
	return func() {
		once.Do(func() {
			// Dispose re-acquires the same session lock the broadcaster holds only
			// while snapshotting — never while a handler runs — so this is safe.
			s.mu.Lock()
			for i, existing := range s.subs {
				if existing == sub {
					s.subs = append(s.subs[:i], s.subs[i+1:]...)
					break
				}
			}
			s.mu.Unlock()
		})
	}, nil
}

// Interface guards.
var (
	_ HubMediaLibrary = (*InMemoryHubMediaLibrary)(nil)
	_ SyncedPlayback  = (*InMemorySyncedPlayback)(nil)
)
