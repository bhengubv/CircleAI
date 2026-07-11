// community_board.go
//
// Ports the CircleAI.Community primitive vertical (CommunityPrimitives.cs):
//   CommunityGroup / Announcement / VolunteerOpportunity (records)
//                            -> value structs
//   ICommunityBoard          -> CommunityBoard interface (I-prefix dropped)
//   InMemoryCommunityBoard   -> InMemoryCommunityBoard
//
// The CommunityDomainContext / CommunityCompanionAdapter (LLM glue) are out of
// scope.
//
// DETERMINISM: GroupsForMember mirrors a ConcurrentDictionary in C# (no defined
// order); this port sorts by GroupId. AnnouncementsFor orders by AtUtc
// descending then caps at limit. Opportunities filters future WhenUtc and orders
// by WhenUtc ascending.

package circleai

import (
	"sort"
	"sync"
	"time"
)

// CommunityGroup is a community group. Ports the CommunityGroup record.
// MemberIds mirrors the C# IReadOnlyList<string>.
type CommunityGroup struct {
	GroupId   string
	Name      string
	Purpose   string
	MemberIds []string
}

// Announcement is a group announcement. Ports the Announcement record.
type Announcement struct {
	AnnouncementId string
	GroupId        string
	Title          string
	Body           string
	AtUtc          time.Time
}

// VolunteerOpportunity is a listed volunteer opportunity. Ports the
// VolunteerOpportunity record.
type VolunteerOpportunity struct {
	OppId            string
	GroupId          string
	Description      string
	VolunteersNeeded int
	WhenUtc          time.Time
}

// CommunityBoard is the groups/announcements/opportunities board. Ports
// ICommunityBoard.
type CommunityBoard interface {
	Create(g CommunityGroup)
	GetGroup(id string) (CommunityGroup, bool)
	// GroupsForMember lists groups a member belongs to, sorted by GroupId.
	GroupsForMember(memberId string) []CommunityGroup
	Post(a Announcement)
	// AnnouncementsFor lists a group's announcements newest-first, capped at limit.
	AnnouncementsFor(groupId string, limit int) []Announcement
	List(o VolunteerOpportunity)
	// Opportunities lists future opportunities ordered by WhenUtc.
	Opportunities() []VolunteerOpportunity
}

// InMemoryCommunityBoard is a concurrency-safe in-memory CommunityBoard. Ports
// InMemoryCommunityBoard.
type InMemoryCommunityBoard struct {
	mu     sync.Mutex
	groups map[string]CommunityGroup
	annc   []Announcement
	opps   map[string]VolunteerOpportunity
}

// NewInMemoryCommunityBoard constructs an empty board.
func NewInMemoryCommunityBoard() *InMemoryCommunityBoard {
	return &InMemoryCommunityBoard{
		groups: make(map[string]CommunityGroup),
		opps:   make(map[string]VolunteerOpportunity),
	}
}

// Create stores (or replaces by GroupId) a group. Ports Create.
func (b *InMemoryCommunityBoard) Create(g CommunityGroup) {
	b.mu.Lock()
	b.groups[g.GroupId] = g
	b.mu.Unlock()
}

// GetGroup returns the group for id, or (zero,false). Ports GetGroup.
func (b *InMemoryCommunityBoard) GetGroup(id string) (CommunityGroup, bool) {
	b.mu.Lock()
	g, ok := b.groups[id]
	b.mu.Unlock()
	return g, ok
}

// GroupsForMember lists groups a member belongs to, sorted by GroupId. Ports
// GroupsForMember.
func (b *InMemoryCommunityBoard) GroupsForMember(memberId string) []CommunityGroup {
	b.mu.Lock()
	out := make([]CommunityGroup, 0)
	for _, g := range b.groups {
		for _, m := range g.MemberIds {
			if m == memberId {
				out = append(out, g)
				break
			}
		}
	}
	b.mu.Unlock()
	sort.SliceStable(out, func(i, j int) bool { return out[i].GroupId < out[j].GroupId })
	return out
}

// Post appends an announcement. Ports Post.
func (b *InMemoryCommunityBoard) Post(a Announcement) {
	b.mu.Lock()
	b.annc = append(b.annc, a)
	b.mu.Unlock()
}

// AnnouncementsFor lists a group's announcements newest-first, capped at limit.
// Ports AnnouncementsFor.
func (b *InMemoryCommunityBoard) AnnouncementsFor(groupId string, limit int) []Announcement {
	b.mu.Lock()
	out := make([]Announcement, 0)
	for _, a := range b.annc {
		if a.GroupId == groupId {
			out = append(out, a)
		}
	}
	b.mu.Unlock()
	sort.SliceStable(out, func(i, j int) bool { return out[i].AtUtc.After(out[j].AtUtc) })
	if limit >= 0 && len(out) > limit {
		out = out[:limit]
	}
	return out
}

// List stores (or replaces by OppId) an opportunity. Ports List.
func (b *InMemoryCommunityBoard) List(o VolunteerOpportunity) {
	b.mu.Lock()
	b.opps[o.OppId] = o
	b.mu.Unlock()
}

// Opportunities lists future opportunities ordered by WhenUtc. Ports
// Opportunities (future = WhenUtc >= now UTC).
func (b *InMemoryCommunityBoard) Opportunities() []VolunteerOpportunity {
	now := time.Now().UTC()
	b.mu.Lock()
	out := make([]VolunteerOpportunity, 0)
	for _, o := range b.opps {
		if !o.WhenUtc.Before(now) {
			out = append(out, o)
		}
	}
	b.mu.Unlock()
	sort.SliceStable(out, func(i, j int) bool { return out[i].WhenUtc.Before(out[j].WhenUtc) })
	return out
}

// Interface guard.
var _ CommunityBoard = (*InMemoryCommunityBoard)(nil)
