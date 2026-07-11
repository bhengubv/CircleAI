// skills_test.go
//
// Verifies the CircleAI.Skills port (skills.go): InMemorySkillStore
// upsert/list/get/search/delete with Name ordering + slug generation, the
// known-skill-packs catalogue, and the MapPackDownloader seam.

package circleai_test

import (
	"context"
	"testing"

	circleai "github.com/bhengubv/CircleAI/go"
)

func TestSkills_UpsertListGetSearch(t *testing.T) {
	s := circleai.NewInMemorySkillStore()
	// Blank id -> slug from name.
	d, err := s.Upsert(context.Background(), "", circleai.SkillDraft{Name: "Calendar Summariser", Description: "sums cals", Tags: []string{"productivity"}})
	if err != nil || d.ID != "calendar-summariser" || d.Source != circleai.SkillSourceInMemory {
		t.Fatalf("upsert = %+v err=%v", d, err)
	}
	_, _ = s.Upsert(context.Background(), "", circleai.SkillDraft{Name: "Abacus", Description: "maths"})

	list, _ := s.List(context.Background())
	if len(list) != 2 || list[0].Name != "Abacus" || list[1].Name != "Calendar Summariser" {
		t.Fatalf("list ordered by Name = %+v", list)
	}
	got, ok, _ := s.Get(context.Background(), "calendar-summariser")
	if !ok || got.Description != "sums cals" {
		t.Fatalf("get = %+v ok=%v", got, ok)
	}
	// Search matches name/description/tags.
	res, _ := s.Search(context.Background(), "productivity")
	if len(res) != 1 || res[0].ID != "calendar-summariser" {
		t.Fatalf("tag search = %+v", res)
	}
	if empty, _ := s.Search(context.Background(), "  "); len(empty) != 0 {
		t.Fatalf("blank query must return empty")
	}
	// Delete.
	_ = s.Delete(context.Background(), "calendar-summariser")
	if _, ok, _ := s.Get(context.Background(), "calendar-summariser"); ok {
		t.Fatalf("deleted skill should be gone")
	}
}

func TestSkills_SlugGeneration(t *testing.T) {
	if got := circleai.GenerateSkillSlug("My  Cool Skill!!"); got != "my-cool-skill" {
		t.Fatalf("slug = %q, want 'my-cool-skill'", got)
	}
	// Name that slugs to empty -> non-empty hex uuid.
	if got := circleai.GenerateSkillSlug("!!!"); len(got) != 32 {
		t.Fatalf("empty-slug fallback should be 32-char uuid, got %q", got)
	}
}

func TestSkills_KnownPacks(t *testing.T) {
	all := circleai.KnownSkillPacksAll()
	if len(all) != 8 {
		t.Fatalf("known packs = %d, want 8", len(all))
	}
	if all[0].Name != "awesome-agent-skills" || !all[0].IsDefaultEnabled {
		t.Fatalf("first pack = %+v", all[0])
	}
	if circleai.KnownSkillPackCareerOps.IsDefaultEnabled {
		t.Fatalf("career-ops should be disabled by default")
	}
}

func TestSkills_MapPackDownloader(t *testing.T) {
	d := circleai.NewMapPackDownloader()
	d.Add("awesome-agent-skills", "/cache/aas")
	p, err := d.Ensure(context.Background(), circleai.KnownSkillPackAwesomeAgentSkills, "/cache", 0)
	if err != nil || p != "/cache/aas" {
		t.Fatalf("ensure = %q err=%v", p, err)
	}
	if _, err := d.Ensure(context.Background(), circleai.KnownSkillPackClaudeBugHunter, "/cache", 0); err == nil {
		t.Fatalf("unregistered pack must error")
	}
}
