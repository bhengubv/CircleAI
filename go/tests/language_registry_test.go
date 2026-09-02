// language_registry_test.go
//
// Asserts:
//   - KnownLanguagesAll has exactly 20 entries.
//   - Each entry matches the canonical fixture (fixtures/language_tags.json).
//   - WritingSystem values are correctly assigned.

package circleai_test

import (
	"encoding/json"
	"os"
	"path/filepath"
	"testing"

	circleai "github.com/bhengubv/CircleAI/go"
)

// ---------------------------------------------------------------------------
// Fixture types
// ---------------------------------------------------------------------------

type languageFixture struct {
	Languages  []languageFixtureEntry `json:"languages"`
	Assertions struct {
		TotalCount int `json:"totalCount"`
	} `json:"assertions"`
}

type languageFixtureEntry struct {
	BcpTag        string `json:"bcpTag"`
	EnglishName   string `json:"englishName"`
	NativeName    string `json:"nativeName"`
	WritingSystem string `json:"writingSystem"`
	IsRtl         bool   `json:"isRtl"`
	PrimaryRegion string `json:"primaryRegion"`
}

func loadLanguageFixture(t *testing.T) languageFixture {
	t.Helper()
	path := filepath.Join(fixturesDir(t), "language_tags.json")
	data, err := os.ReadFile(path)
	if err != nil {
		t.Fatalf("failed to read language_tags.json: %v", err)
	}
	var fix languageFixture
	if err := json.Unmarshal(data, &fix); err != nil {
		t.Fatalf("failed to parse language_tags.json: %v", err)
	}
	return fix
}

// writingSystemString maps from our enum to the fixture string.
func writingSystemString(ws circleai.WritingSystem) string {
	switch ws {
	case circleai.WritingSystemLatin:
		return "Latin"
	case circleai.WritingSystemArabic:
		return "Arabic"
	case circleai.WritingSystemEthiopic:
		return "Ethiopic"
	case circleai.WritingSystemDevanagari:
		return "Devanagari"
	case circleai.WritingSystemHan:
		return "Han"
	case circleai.WritingSystemCyrillic:
		return "Cyrillic"
	case circleai.WritingSystemHebrew:
		return "Hebrew"
	case circleai.WritingSystemGreek:
		return "Greek"
	default:
		return "Other"
	}
}

// ---------------------------------------------------------------------------
// Tests
// ---------------------------------------------------------------------------

func TestKnownLanguagesAll_Count(t *testing.T) {
	fix := loadLanguageFixture(t)
	want := fix.Assertions.TotalCount
	got := len(circleai.KnownLanguagesAll)
	if got != want {
		t.Errorf("KnownLanguagesAll has %d entries, want %d", got, want)
	}
}

func TestKnownLanguagesAll_MatchFixture(t *testing.T) {
	fix := loadLanguageFixture(t)

	// Build a map from BCP tag to fixture entry for quick lookup.
	fixtureByTag := make(map[string]languageFixtureEntry, len(fix.Languages))
	for _, e := range fix.Languages {
		fixtureByTag[e.BcpTag] = e
	}

	// Verify declaration order matches fixture order.
	if len(circleai.KnownLanguagesAll) != len(fix.Languages) {
		t.Fatalf("length mismatch: code=%d fixture=%d",
			len(circleai.KnownLanguagesAll), len(fix.Languages))
	}

	for i, tag := range circleai.KnownLanguagesAll {
		fEntry := fix.Languages[i]
		t.Run(tag.BcpTag, func(t *testing.T) {
			if tag.BcpTag != fEntry.BcpTag {
				t.Errorf("declaration order mismatch at index %d: got BcpTag %q, want %q",
					i, tag.BcpTag, fEntry.BcpTag)
			}
			if tag.EnglishName != fEntry.EnglishName {
				t.Errorf("EnglishName: got %q, want %q", tag.EnglishName, fEntry.EnglishName)
			}
			if tag.NativeName != fEntry.NativeName {
				t.Errorf("NativeName: got %q, want %q", tag.NativeName, fEntry.NativeName)
			}
			if writingSystemString(tag.WritingSystem) != fEntry.WritingSystem {
				t.Errorf("WritingSystem: got %q, want %q",
					writingSystemString(tag.WritingSystem), fEntry.WritingSystem)
			}
			if tag.IsRtl != fEntry.IsRtl {
				t.Errorf("IsRtl: got %v, want %v", tag.IsRtl, fEntry.IsRtl)
			}
			if tag.PrimaryRegion != fEntry.PrimaryRegion {
				t.Errorf("PrimaryRegion: got %q, want %q", tag.PrimaryRegion, fEntry.PrimaryRegion)
			}
		})
	}
}

func TestKnownLanguagesAll_OnlyArabicIsRtl(t *testing.T) {
	for _, tag := range circleai.KnownLanguagesAll {
		if tag.IsRtl && tag.BcpTag != "ar" {
			t.Errorf("unexpected RTL language: %q", tag.BcpTag)
		}
		if !tag.IsRtl && tag.BcpTag == "ar" {
			t.Errorf("Arabic should be RTL but IsRtl=false")
		}
	}
}

func TestLanguageTagUnknown(t *testing.T) {
	u := circleai.LanguageTagUnknown
	if u.BcpTag != "und" {
		t.Errorf("Unknown BcpTag: got %q, want %q", u.BcpTag, "und")
	}
	if u.IsRtl {
		t.Error("Unknown.IsRtl should be false")
	}
}
