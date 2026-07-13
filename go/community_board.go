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
	// GroupCount returns the number of groups.
	GroupCount() int
	// RemoveGroup drops a group by id, returning true if present.
	RemoveGroup(groupId string) bool
	// AddMember adds a member to a group; false if group is unknown or already a member.
	AddMember(groupId, memberId string) bool
	// RemoveMember removes a member from a group; false if group is unknown or not a member.
	RemoveMember(groupId, memberId string) bool
	// OpportunitiesForGroup lists a group's opportunities ordered by WhenUtc.
	OpportunitiesForGroup(groupId string) []VolunteerOpportunity
	// TotalVolunteersNeeded sums VolunteersNeeded across future opportunities.
	TotalVolunteersNeeded() int
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

// GroupCount returns the number of groups. Ports InMemoryCommunityBoard.GroupCount.
func (b *InMemoryCommunityBoard) GroupCount() int {
	b.mu.Lock()
	defer b.mu.Unlock()
	return len(b.groups)
}

// RemoveGroup drops a group by id, returning true if present. Ports
// InMemoryCommunityBoard.RemoveGroup (TryRemove).
func (b *InMemoryCommunityBoard) RemoveGroup(groupId string) bool {
	b.mu.Lock()
	defer b.mu.Unlock()
	_, ok := b.groups[groupId]
	delete(b.groups, groupId)
	return ok
}

// AddMember adds memberId to groupId's roster. Returns false if the group is
// unknown or the member is already present. The member slice is copied (never
// mutated in place), mirroring the C# `g with { MemberIds = ...Append(...) }`.
// Ports InMemoryCommunityBoard.AddMember.
func (b *InMemoryCommunityBoard) AddMember(groupId, memberId string) bool {
	b.mu.Lock()
	defer b.mu.Unlock()
	g, ok := b.groups[groupId]
	if !ok {
		return false
	}
	for _, m := range g.MemberIds {
		if m == memberId {
			return false
		}
	}
	updated := make([]string, len(g.MemberIds), len(g.MemberIds)+1)
	copy(updated, g.MemberIds)
	updated = append(updated, memberId)
	g.MemberIds = updated
	b.groups[groupId] = g
	return true
}

// RemoveMember removes memberId from groupId's roster. Returns false if the group
// is unknown or the member is absent. The surviving members are copied into a new
// slice, mirroring the C# `g with { MemberIds = ...Where(...) }`. Ports
// InMemoryCommunityBoard.RemoveMember.
func (b *InMemoryCommunityBoard) RemoveMember(groupId, memberId string) bool {
	b.mu.Lock()
	defer b.mu.Unlock()
	g, ok := b.groups[groupId]
	if !ok {
		return false
	}
	found := false
	for _, m := range g.MemberIds {
		if m == memberId {
			found = true
			break
		}
	}
	if !found {
		return false
	}
	updated := make([]string, 0, len(g.MemberIds))
	for _, m := range g.MemberIds {
		if m != memberId {
			updated = append(updated, m)
		}
	}
	g.MemberIds = updated
	b.groups[groupId] = g
	return true
}

// OpportunitiesForGroup lists a group's opportunities (Ordinal GroupId match),
// ordered by WhenUtc ascending. Unlike Opportunities, this is NOT filtered to the
// future. Ports InMemoryCommunityBoard.OpportunitiesForGroup.
func (b *InMemoryCommunityBoard) OpportunitiesForGroup(groupId string) []VolunteerOpportunity {
	b.mu.Lock()
	out := make([]VolunteerOpportunity, 0)
	for _, o := range b.opps {
		if o.GroupId == groupId {
			out = append(out, o)
		}
	}
	b.mu.Unlock()
	sort.SliceStable(out, func(i, j int) bool { return out[i].WhenUtc.Before(out[j].WhenUtc) })
	return out
}

// TotalVolunteersNeeded sums VolunteersNeeded across future opportunities
// (WhenUtc >= now UTC), matching the C# Opportunities().Sum(...). Ports
// InMemoryCommunityBoard.TotalVolunteersNeeded.
func (b *InMemoryCommunityBoard) TotalVolunteersNeeded() int {
	now := time.Now().UTC()
	b.mu.Lock()
	defer b.mu.Unlock()
	total := 0
	for _, o := range b.opps {
		if !o.WhenUtc.Before(now) {
			total += o.VolunteersNeeded
		}
	}
	return total
}

// Interface guard.
var _ CommunityBoard = (*InMemoryCommunityBoard)(nil)
