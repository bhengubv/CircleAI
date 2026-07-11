// gaming_board_test.go
//
// Verifies the CircleAI.Gaming port (gaming_board.go): title add/get, titles by
// genre (sorted), total play time, achievements newest-first, and most-played
// top-K ranking.

package circleai_test

import (
	"testing"
	"time"

	circleai "github.com/bhengubv/CircleAI/go"
)

func TestGaming_TitlesAndPlayTime(t *testing.T) {
	b := circleai.NewInMemoryGamingBoard()
	b.AddTitle(circleai.GameTitle{TitleId: "g2", Name: "Beta", Genre: "RPG", Platform: "PC"})
	b.AddTitle(circleai.GameTitle{TitleId: "g1", Name: "Alpha", Genre: "rpg", Platform: "PC"})
	b.AddTitle(circleai.GameTitle{TitleId: "g3", Name: "Gamma", Genre: "FPS", Platform: "PC"})
	if got, ok := b.GetTitle("g1"); !ok || got.Name != "Alpha" {
		t.Fatalf("get title = %+v ok=%v", got, ok)
	}
	rpg := b.TitlesByGenre("RPG")
	if len(rpg) != 2 || rpg[0].TitleId != "g1" || rpg[1].TitleId != "g2" {
		t.Fatalf("titles-by-genre sorted failed: %+v", rpg)
	}

	now := time.Now().UTC()
	b.RecordSession(circleai.PlaySession{SessionId: "s1", UserId: "u1", TitleId: "g1", Duration: 30 * time.Minute, AtUtc: now})
	b.RecordSession(circleai.PlaySession{SessionId: "s2", UserId: "u1", TitleId: "g1", Duration: 90 * time.Minute, AtUtc: now})
	if tp := b.TotalPlayTime("u1", "g1"); tp != 120*time.Minute {
		t.Fatalf("total play time = %v, want 2h", tp)
	}
}

func TestGaming_AchievementsAndMostPlayed(t *testing.T) {
	b := circleai.NewInMemoryGamingBoard()
	b.AddTitle(circleai.GameTitle{TitleId: "g1", Name: "Alpha"})
	b.AddTitle(circleai.GameTitle{TitleId: "g2", Name: "Beta"})
	now := time.Now().UTC()
	b.Unlock(circleai.AchievementUnlock{UnlockId: "x1", UserId: "u1", TitleId: "g1", Achievement: "First", AtUtc: now.Add(-time.Hour)})
	b.Unlock(circleai.AchievementUnlock{UnlockId: "x2", UserId: "u1", TitleId: "g1", Achievement: "Second", AtUtc: now})
	ach := b.AchievementsFor("u1")
	if len(ach) != 2 || ach[0].UnlockId != "x2" {
		t.Fatalf("achievements newest-first failed: %+v", ach)
	}

	b.RecordSession(circleai.PlaySession{SessionId: "s1", UserId: "u1", TitleId: "g1", Duration: 30 * time.Minute, AtUtc: now})
	b.RecordSession(circleai.PlaySession{SessionId: "s2", UserId: "u1", TitleId: "g2", Duration: 120 * time.Minute, AtUtc: now})
	top := b.MostPlayed("u1", 5)
	if len(top) != 2 || top[0].TitleId != "g2" || top[1].TitleId != "g1" {
		t.Fatalf("most-played ranking failed: %+v", top)
	}
	if one := b.MostPlayed("u1", 1); len(one) != 1 || one[0].TitleId != "g2" {
		t.Fatalf("most-played topK failed: %+v", one)
	}
}
