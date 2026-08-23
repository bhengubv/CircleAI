// voice_piper_test.go
//
// Asserts the Go PiperVoiceConfig / LexiconTokeniser / AudioFormat ports against
// the same golden files the C# reference generates.
//
// The piper fixture carries TWO configs on purpose — one with pad 0 and one with
// pad 3 — so a port that hard-codes either fails on the other. That is THE PAD
// RULE, and getting it wrong is what made 42 MMS voices speak fluent nonsense.

package circleai_test

import (
	"fmt"
	"reflect"
	"sort"
	"strings"
	"testing"

	circleai "github.com/bhengubv/CircleAI/go"
)

type piperCase struct {
	Phonemes            []string `json:"phonemes"`
	IDs                 []int64  `json:"ids"`
	Skipped             int      `json:"skipped"`
	SkippedSymbols      []string `json:"skippedSymbols"`
	ApproximatedSymbols []string `json:"approximatedSymbols"`
}

type piperFixture struct {
	Configs []struct {
		Name          string             `json:"name"`
		ConfigJSON    map[string][]int64 `json:"configJson"`
		SampleRate    int                `json:"sampleRate"`
		PadID         int64              `json:"padId"`
		HasPhonemeMap bool               `json:"hasPhonemeMap"`
		Cases         []piperCase        `json:"cases"`
	} `json:"configs"`
	SplitPhonemeString []struct {
		Input    string   `json:"input"`
		Elements []string `json:"elements"`
	} `json:"splitPhonemeString"`
}

func TestVoicePiperConfigMatchesReference(t *testing.T) {
	var fix piperFixture
	readVoiceFixture(t, "voice_piper_config.json", &fix)
	if len(fix.Configs) != 2 {
		t.Fatalf("both pad conventions must be covered, got %d configs", len(fix.Configs))
	}

	for _, c := range fix.Configs {
		cfg := circleai.NewVoicePiperConfig(c.ConfigJSON)
		if got := cfg.PadID(); got != c.PadID {
			t.Errorf("%s: padId %d, want %d", c.Name, got, c.PadID)
		}
		if got := cfg.HasPhonemeMap(); got != c.HasPhonemeMap {
			t.Errorf("%s: hasPhonemeMap %v, want %v", c.Name, got, c.HasPhonemeMap)
		}

		for _, one := range c.Cases {
			got := cfg.PhonemesToIDs(one.Phonemes)
			if !reflect.DeepEqual(got.IDs, one.IDs) {
				t.Errorf("%s %v: ids %v, want %v", c.Name, one.Phonemes, got.IDs, one.IDs)
			}
			if got.Skipped != one.Skipped {
				t.Errorf("%s %v: skipped %d, want %d", c.Name, one.Phonemes, got.Skipped, one.Skipped)
			}
			if !reflect.DeepEqual(got.SkippedSymbols, one.SkippedSymbols) {
				t.Errorf("%s %v: skippedSymbols %v, want %v",
					c.Name, one.Phonemes, got.SkippedSymbols, one.SkippedSymbols)
			}
			if !reflect.DeepEqual(got.ApproximatedSymbols, one.ApproximatedSymbols) {
				t.Errorf("%s %v: approximatedSymbols %v, want %v",
					c.Name, one.Phonemes, got.ApproximatedSymbols, one.ApproximatedSymbols)
			}
		}
	}
}

func TestVoicePadIsReadFromTheModelNotAssumed(t *testing.T) {
	// THE PAD RULE. The two fixture configs disagree — 0 in the Piper-layout one,
	// 3 in the MMS-layout one — so a port that hard-codes either fails the other.
	var fix piperFixture
	readVoiceFixture(t, "voice_piper_config.json", &fix)

	seen := []int{}
	for _, c := range fix.Configs {
		seen = append(seen, int(c.PadID))
		if got := circleai.NewVoicePiperConfig(c.ConfigJSON).PadID(); got != c.PadID {
			t.Errorf("%s: padId %d, want %d", c.Name, got, c.PadID)
		}
	}
	sort.Ints(seen)
	if !reflect.DeepEqual(seen, []int{0, 3}) {
		t.Errorf("the fixture must cover BOTH pad conventions, got %v", seen)
	}
}

func TestVoiceThaiIsNotFoldedButTshivendaIs(t *testing.T) {
	// The asymmetry is the whole point. Latin ṱ still sounds like a t with the
	// mark gone; Thai ก's marks ARE the vowels, so folding deletes the word
	// rather than approximating it.
	var fix piperFixture
	readVoiceFixture(t, "voice_piper_config.json", &fix)
	cfg := circleai.NewVoicePiperConfig(fix.Configs[0].ConfigJSON)

	if got := cfg.PhonemesToIDs([]string{"ṱ"}).ApproximatedSymbols; !reflect.DeepEqual(got, []string{"ṱ"}) {
		t.Errorf("ṱ should fold to a Latin base and be REPORTED as approximate, got %v", got)
	}
	if got := cfg.PhonemesToIDs([]string{"ก"}).SkippedSymbols; !reflect.DeepEqual(got, []string{"ก"}) {
		t.Errorf("Thai must be skipped, not folded, got %v", got)
	}
}

func TestVoiceSplitPhonemeStringMatchesReference(t *testing.T) {
	var fix piperFixture
	readVoiceFixture(t, "voice_piper_config.json", &fix)
	for _, c := range fix.SplitPhonemeString {
		if got := circleai.VoiceSplitPhonemeString(c.Input); !reflect.DeepEqual(got, c.Elements) {
			t.Errorf("clusters for %q: got %v, want %v", c.Input, got, c.Elements)
		}
	}
}

// ── LexiconTokeniser ────────────────────────────────────────────────────────

type lexFixture struct {
	Tokens  map[string]int64 `json:"tokens"`
	Lexicon []struct {
		Word     string   `json:"word"`
		Phonemes []string `json:"phonemes"`
	} `json:"lexicon"`
	Blank int64 `json:"blank"`
	Cases []struct {
		Text         string   `json:"text"`
		IDs          []int64  `json:"ids"`
		IDsWithBlank []int64  `json:"idsWithBlank"`
		Unmapped     []string `json:"unmapped"`
	} `json:"cases"`
}

func loadLexicon(t *testing.T) (*circleai.VoiceLexiconTokeniser, lexFixture) {
	t.Helper()
	var fix lexFixture
	readVoiceFixture(t, "voice_lexicon_tokeniser.json", &fix)

	tokenLines := []string{}
	for sym, id := range fix.Tokens {
		tokenLines = append(tokenLines, fmt.Sprintf("%s %d", sym, id))
	}
	lexLines := []string{}
	for _, e := range fix.Lexicon {
		lexLines = append(lexLines, e.Word+" "+strings.Join(e.Phonemes, " "))
	}

	lex := circleai.NewVoiceLexiconTokeniser(
		strings.Join(tokenLines, "\n"), strings.Join(lexLines, "\n"), fix.Blank)
	if lex == nil {
		t.Fatal("fixture lexicon failed to load")
	}
	return lex, fix
}

func TestVoiceLexiconTokeniserMatchesReference(t *testing.T) {
	lex, fix := loadLexicon(t)
	if len(fix.Cases) == 0 {
		t.Fatal("fixture has no cases")
	}

	for _, c := range fix.Cases {
		bare := lex.Encode(c.Text, false)
		if !reflect.DeepEqual(bare, c.IDs) {
			t.Errorf("ids for %q: got %v, want %v", c.Text, bare, c.IDs)
		}
		if !reflect.DeepEqual(lex.LastUnmapped, c.Unmapped) {
			t.Errorf("unmapped for %q: got %v, want %v", c.Text, lex.LastUnmapped, c.Unmapped)
		}
		if padded := lex.Encode(c.Text, true); !reflect.DeepEqual(padded, c.IDsWithBlank) {
			t.Errorf("idsWithBlank for %q: got %v, want %v", c.Text, padded, c.IDsWithBlank)
		}
	}
}

func TestVoiceLexiconTakesTheLongestMatch(t *testing.T) {
	// あい, あいさつ and あいかわらず all start the same way. Taking the shortest
	// pronounces a different word.
	lex, _ := loadLexicon(t)
	full := lex.Encode("あいさつ", false)
	short := lex.Encode("あい", false)
	if len(full) <= len(short) {
		t.Errorf("あいさつ matched only the あい prefix — this is shortest-match: %v vs %v", full, short)
	}
}

// ── AudioFormat ─────────────────────────────────────────────────────────────

func TestVoiceAudioFormatMatchesReference(t *testing.T) {
	var fix struct {
		Pcm16Mono16k struct {
			SampleRate    int `json:"sampleRate"`
			Channels      int `json:"channels"`
			BitsPerSample int `json:"bitsPerSample"`
		} `json:"pcm16Mono16k"`
	}
	readVoiceFixture(t, "voice_audio_format.json", &fix)

	got := circleai.VoicePcm16Mono16k
	if got.SampleRate != fix.Pcm16Mono16k.SampleRate ||
		got.Channels != fix.Pcm16Mono16k.Channels ||
		got.BitsPerSample != fix.Pcm16Mono16k.BitsPerSample {
		t.Errorf("got %+v, want %+v", got, fix.Pcm16Mono16k)
	}
}
