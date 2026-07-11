// community_board_test.go
//
// Verifies the CircleAI.Community port (community_board.go): group create/get,
// groups-for-member (sorted), announcements newest-first with limit, and future
// opportunities ordering.

package circleai_test

import (
	"testing"
	"time"

	circleai "github.com/bhengubv/CircleAI/go"
)

func TestCommunity_GroupsAndAnnouncements(t *testing.T) {
	b := circleai.NewInMemoryCommunityBoard()
	b.Create(circleai.CommunityGroup{GroupId: "g2", Name: "Runners", Purpose: "fitness", MemberIds: []string{"u1", "u2"}})
	b.Create(circleai.CommunityGroup{GroupId: "g1", Name: "Readers", Purpose: "books", MemberIds: []string{"u1"}})
	b.Create(circleai.CommunityGroup{GroupId: "g3", Name: "Cooks", Purpose: "food", MemberIds: []string{"u3"}})
	if got, ok := b.GetGroup("g1"); !ok || got.Name != "Readers" {
		t.Fatalf("get group = %+v ok=%v", got, ok)
	}
	mine := b.GroupsForMember("u1")
	if len(mine) != 2 || mine[0].GroupId != "g1" || mine[1].GroupId != "g2" {
		t.Fatalf("groups-for-member sorted failed: %+v", mine)
	}

	now := time.Now().UTC()
	b.Post(circleai.Announcement{AnnouncementId: "a1", GroupId: "g1", Title: "Old", AtUtc: now.Add(-time.Hour)})
	b.Post(circleai.Announcement{AnnouncementId: "a2", GroupId: "g1", Title: "New", AtUtc: now})
	ann := b.AnnouncementsFor("g1", 20)
	if len(ann) != 2 || ann[0].AnnouncementId != "a2" {
		t.Fatalf("announcements newest-first failed: %+v", ann)
	}
	if lim := b.AnnouncementsFor("g1", 1); len(lim) != 1 || lim[0].AnnouncementId != "a2" {
		t.Fatalf("announcements limit failed: %+v", lim)
	}
}

func TestCommunity_Opportunities(t *testing.T) {
	b := circleai.NewInMemoryCommunityBoard()
	now := time.Now().UTC()
	b.List(circleai.VolunteerOpportunity{OppId: "o1", GroupId: "g1", Description: "Later", VolunteersNeeded: 5, WhenUtc: now.Add(48 * time.Hour)})
	b.List(circleai.VolunteerOpportunity{OppId: "o2", GroupId: "g1", Description: "Soon", VolunteersNeeded: 2, WhenUtc: now.Add(24 * time.Hour)})
	b.List(circleai.VolunteerOpportunity{OppId: "o3", GroupId: "g1", Description: "Past", VolunteersNeeded: 1, WhenUtc: now.Add(-24 * time.Hour)})

	opps := b.Opportunities()
	if len(opps) != 2 || opps[0].OppId != "o2" || opps[1].OppId != "o1" {
		t.Fatalf("opportunities ordered failed: %+v", opps)
	}
}
