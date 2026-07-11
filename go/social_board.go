// social_board.go
//
// Ports the CircleAI.Social primitive vertical (SocialPrimitives.cs):
//   SocialPost / Reaction / Follow (records) -> value structs
//   ISocialBoard             -> SocialBoard interface (I-prefix dropped)
//   InMemorySocialBoard      -> InMemorySocialBoard
//
// The SocialDomainContext / SocialCompanionAdapter (LLM glue) are out of scope.
//
// DETERMINISM: FeedFor orders by AtUtc descending then caps at limit. Followers
// preserves the follow-insertion order (C# backing List). ReactionCount matches
// the C# Kind comparison (case-insensitive). Follow rejects self-follows with an
// error (the C# InvalidOperationException).

package circleai

import (
	"errors"
	"sort"
	"strings"
	"sync"
	"time"
)

// SocialPost is a post. Ports the SocialPost record. Tags mirrors the C#
// IReadOnlyList<string>.
type SocialPost struct {
	PostId   string
	AuthorId string
	Body     string
	AtUtc    time.Time
	Tags     []string
}

// Reaction is a reaction to a post. Ports the Reaction record.
type Reaction struct {
	PostId string
	UserId string
	Kind   string
	AtUtc  time.Time
}

// Follow is a follower/followee edge. Ports the Follow record.
type Follow struct {
	FollowerId string
	FolloweeId string
	AtUtc      time.Time
}

// SocialBoard is the posts/reactions/follows board. Ports ISocialBoard.
type SocialBoard interface {
	Post(p SocialPost)
	GetPost(id string) (SocialPost, bool)
	React(r Reaction)
	// ReactionCount counts a post's reactions of a kind (case-insensitive).
	ReactionCount(postId, kind string) int
	// Follow adds a follow edge; errors when follower == followee.
	Follow(f Follow) error
	Unfollow(followerId, followeeId string)
	// FeedFor lists posts by users the given user follows, newest-first, capped at
	// limit. Panics on limit <= 0 (the C# ArgumentOutOfRangeException).
	FeedFor(userId string, limit int) []SocialPost
	// Followers lists the ids following the given user, in follow order.
	Followers(userId string) []string
}

// InMemorySocialBoard is a concurrency-safe in-memory SocialBoard. Ports
// InMemorySocialBoard.
type InMemorySocialBoard struct {
	mu      sync.Mutex
	posts   map[string]SocialPost
	reacts  []Reaction
	follows []Follow
}

// NewInMemorySocialBoard constructs an empty board.
func NewInMemorySocialBoard() *InMemorySocialBoard {
	return &InMemorySocialBoard{posts: make(map[string]SocialPost)}
}

// Post stores (or replaces by PostId) a post. Ports Post.
func (b *InMemorySocialBoard) Post(p SocialPost) {
	b.mu.Lock()
	b.posts[p.PostId] = p
	b.mu.Unlock()
}

// GetPost returns the post for id, or (zero,false). Ports GetPost.
func (b *InMemorySocialBoard) GetPost(id string) (SocialPost, bool) {
	b.mu.Lock()
	p, ok := b.posts[id]
	b.mu.Unlock()
	return p, ok
}

// React appends a reaction. Ports React.
func (b *InMemorySocialBoard) React(r Reaction) {
	b.mu.Lock()
	b.reacts = append(b.reacts, r)
	b.mu.Unlock()
}

// ReactionCount counts a post's reactions of a kind (case-insensitive). Ports
// ReactionCount.
func (b *InMemorySocialBoard) ReactionCount(postId, kind string) int {
	b.mu.Lock()
	defer b.mu.Unlock()
	n := 0
	for _, r := range b.reacts {
		if r.PostId == postId && strings.EqualFold(r.Kind, kind) {
			n++
		}
	}
	return n
}

// Follow adds a follow edge. Ports Follow (throws on self-follow -> error).
func (b *InMemorySocialBoard) Follow(f Follow) error {
	if f.FollowerId == f.FolloweeId {
		return errors.New("Cannot follow yourself.")
	}
	b.mu.Lock()
	b.follows = append(b.follows, f)
	b.mu.Unlock()
	return nil
}

// Unfollow removes all matching follow edges. Ports Unfollow.
func (b *InMemorySocialBoard) Unfollow(followerId, followeeId string) {
	b.mu.Lock()
	kept := b.follows[:0]
	for _, f := range b.follows {
		if f.FollowerId == followerId && f.FolloweeId == followeeId {
			continue
		}
		kept = append(kept, f)
	}
	b.follows = kept
	b.mu.Unlock()
}

// FeedFor lists posts by users the given user follows, newest-first, capped at
// limit. Ports FeedFor.
func (b *InMemorySocialBoard) FeedFor(userId string, limit int) []SocialPost {
	if limit <= 0 {
		panic("limit out of range")
	}
	b.mu.Lock()
	following := make(map[string]struct{})
	for _, f := range b.follows {
		if f.FollowerId == userId {
			following[f.FolloweeId] = struct{}{}
		}
	}
	out := make([]SocialPost, 0)
	for _, p := range b.posts {
		if _, ok := following[p.AuthorId]; ok {
			out = append(out, p)
		}
	}
	b.mu.Unlock()
	sort.SliceStable(out, func(i, j int) bool { return out[i].AtUtc.After(out[j].AtUtc) })
	if len(out) > limit {
		out = out[:limit]
	}
	return out
}

// Followers lists the ids following the given user, in follow order. Ports
// Followers.
func (b *InMemorySocialBoard) Followers(userId string) []string {
	b.mu.Lock()
	out := make([]string, 0)
	for _, f := range b.follows {
		if f.FolloweeId == userId {
			out = append(out, f.FollowerId)
		}
	}
	b.mu.Unlock()
	return out
}

// Interface guard.
var _ SocialBoard = (*InMemorySocialBoard)(nil)
