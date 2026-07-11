// social_board_test.go
//
// Verifies the CircleAI.Social port (social_board.go): post/get, reaction counts
// (case-insensitive), follow/unfollow with self-follow rejection, feed
// newest-first, and followers listing.

package circleai_test

import (
	"testing"
	"time"

	circleai "github.com/bhengubv/CircleAI/go"
)

func TestSocial_ReactionsAndFollow(t *testing.T) {
	b := circleai.NewInMemorySocialBoard()
	now := time.Now().UTC()
	b.Post(circleai.SocialPost{PostId: "p1", AuthorId: "u2", Body: "hi", AtUtc: now, Tags: []string{"greeting"}})
	if got, ok := b.GetPost("p1"); !ok || got.Body != "hi" {
		t.Fatalf("get post = %+v ok=%v", got, ok)
	}
	b.React(circleai.Reaction{PostId: "p1", UserId: "u1", Kind: "like", AtUtc: now})
	b.React(circleai.Reaction{PostId: "p1", UserId: "u3", Kind: "LIKE", AtUtc: now})
	b.React(circleai.Reaction{PostId: "p1", UserId: "u4", Kind: "love", AtUtc: now})
	if n := b.ReactionCount("p1", "like"); n != 2 {
		t.Fatalf("reaction count = %d, want 2", n)
	}

	if err := b.Follow(circleai.Follow{FollowerId: "u1", FolloweeId: "u1", AtUtc: now}); err == nil {
		t.Fatalf("self-follow must error")
	}
	if err := b.Follow(circleai.Follow{FollowerId: "u1", FolloweeId: "u2", AtUtc: now}); err != nil {
		t.Fatalf("follow: %v", err)
	}
	if f := b.Followers("u2"); len(f) != 1 || f[0] != "u1" {
		t.Fatalf("followers failed: %+v", f)
	}
	b.Unfollow("u1", "u2")
	if f := b.Followers("u2"); len(f) != 0 {
		t.Fatalf("followers after unfollow failed: %+v", f)
	}
}

func TestSocial_Feed(t *testing.T) {
	b := circleai.NewInMemorySocialBoard()
	now := time.Now().UTC()
	b.Follow(circleai.Follow{FollowerId: "u1", FolloweeId: "u2", AtUtc: now})
	b.Post(circleai.SocialPost{PostId: "p1", AuthorId: "u2", Body: "old", AtUtc: now.Add(-time.Hour)})
	b.Post(circleai.SocialPost{PostId: "p2", AuthorId: "u2", Body: "new", AtUtc: now})
	b.Post(circleai.SocialPost{PostId: "p3", AuthorId: "u9", Body: "unfollowed", AtUtc: now})

	feed := b.FeedFor("u1", 20)
	if len(feed) != 2 || feed[0].PostId != "p2" || feed[1].PostId != "p1" {
		t.Fatalf("feed newest-first (followed only) failed: %+v", feed)
	}
	if lim := b.FeedFor("u1", 1); len(lim) != 1 || lim[0].PostId != "p2" {
		t.Fatalf("feed limit failed: %+v", lim)
	}
}
