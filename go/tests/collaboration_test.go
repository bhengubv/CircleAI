// collaboration_test.go
//
// Verifies the CircleAI.Collaboration port (collaboration.go): channel upsert +
// list-for-team (Name order), message post + read (desc + limit), presence set/get,
// and null impls.

package circleai_test

import (
	"context"
	"testing"
	"time"

	circleai "github.com/bhengubv/CircleAI/go"
)

func TestCollaboration_ChannelsListForTeamOrdered(t *testing.T) {
	s := circleai.NewInMemoryChannelStore()
	s.Upsert(circleai.ChatChannel{ChannelID: "c1", Name: "zeta", TeamID: "t1"})
	s.Upsert(circleai.ChatChannel{ChannelID: "c2", Name: "alpha", TeamID: "t1"})
	s.Upsert(circleai.ChatChannel{ChannelID: "c3", Name: "other", TeamID: "t2"})
	got, err := s.ListForTeam(context.Background(), "t1")
	if err != nil || len(got) != 2 || got[0].Name != "alpha" || got[1].Name != "zeta" {
		t.Fatalf("list-for-team ordered = %+v err=%v", got, err)
	}
	if c, ok := s.Get(context.Background(), "c1"); !ok || c.Name != "zeta" {
		t.Fatalf("get = %+v ok=%v", c, ok)
	}
}

func TestCollaboration_MessagesReadDescLimited(t *testing.T) {
	s := circleai.NewInMemoryMessageStore()
	base := time.Date(2026, 7, 1, 0, 0, 0, 0, time.UTC)
	_, _ = s.Post(context.Background(), circleai.ChatChannelMessage{MessageID: "m1", ChannelID: "c", Body: "1", AtUTC: base})
	_, _ = s.Post(context.Background(), circleai.ChatChannelMessage{MessageID: "m2", ChannelID: "c", Body: "3", AtUTC: base.Add(2 * time.Hour)})
	_, _ = s.Post(context.Background(), circleai.ChatChannelMessage{MessageID: "m3", ChannelID: "c", Body: "2", AtUTC: base.Add(time.Hour)})
	got, err := s.Read(context.Background(), "c", 2)
	if err != nil || len(got) != 2 || got[0].Body != "3" || got[1].Body != "2" {
		t.Fatalf("read desc+limit = %+v err=%v", got, err)
	}
	if _, err := s.Post(context.Background(), circleai.ChatChannelMessage{ChannelID: ""}); err == nil {
		t.Fatalf("blank channel id must error")
	}
}

func TestCollaboration_Presence(t *testing.T) {
	p := circleai.NewInMemoryPresence()
	p.Set(circleai.PresenceState{UserID: "u", Online: true, LastSeenUTC: time.Now().UTC()})
	got, ok := p.Get(context.Background(), "u")
	if !ok || !got.Online {
		t.Fatalf("presence = %+v ok=%v", got, ok)
	}
	if _, ok := p.Get(context.Background(), "absent"); ok {
		t.Fatalf("absent user should not be found")
	}
}

func TestCollaboration_NullImpls(t *testing.T) {
	if _, ok := circleai.NullChannelStoreInstance.Get(context.Background(), "x"); ok {
		t.Fatalf("null channel store")
	}
	m, _ := circleai.NullMessageStoreInstance.Post(context.Background(), circleai.ChatChannelMessage{MessageID: "echo"})
	if m.MessageID != "echo" {
		t.Fatalf("null message store must echo")
	}
	if _, ok := circleai.NullPresenceInstance.Get(context.Background(), "x"); ok {
		t.Fatalf("null presence")
	}
}
