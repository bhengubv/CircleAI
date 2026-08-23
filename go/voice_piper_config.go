// voice_piper_config.go
//
// Ports of src/CircleAI.Voice/PiperVoiceConfig.cs, LexiconTokeniser.cs and
// AudioFormat.cs.
//
// Parity is asserted against fixtures/voice_piper_config.json,
// fixtures/voice_lexicon_tokeniser.json and fixtures/voice_audio_format.json.

package circleai

import (
	"strconv"
	"strings"
	"unicode"

	"golang.org/x/text/unicode/norm"
)

// AudioFormat IS ALREADY IN THIS PORT — AudioFormatPcm16Mono16k. It was ported with the
// original voice module and does not belong here; declaring it again would
// shadow the real one and split the type in two.

// VoicePhonemeMapping is what a PhonemesToIDs call did, beyond the ids.
type VoicePhonemeMapping struct {
	IDs []int64
	// Skipped counts symbols the vocabulary had no entry for.
	Skipped int
	// SkippedSymbols names them. A dropped symbol is inaudible, so this list is
	// the only evidence a front-end is broken.
	SkippedSymbols []string
	// ApproximatedSymbols were spoken as something near, not exactly — a
	// diacritic the voice lacks, folded to its base letter. A compromise, not a
	// success, so it is reported separately.
	ApproximatedSymbols []string
}

// VoicePiperConfig holds a Piper-layout voice's vocabulary and inference settings.
type VoicePiperConfig struct {
	phonemeIDMap map[string][]int64
	SampleRate   int
	NoiseScale   float32
	LengthScale  float32
	NoiseW       float32
	// PhonemeType is e.g. "espeak" (needs a phonemizer) or "text" (graphemes
	// are phonemes).
	PhonemeType string
}

// Piper's special phoneme symbols (piper-phonemize defaults).
const (
	voicePad = "_"
	voiceBos = "^"
	voiceEos = "$"
)

// NewVoicePiperConfig builds a config over a phoneme→id map.
func NewVoicePiperConfig(m map[string][]int64) *VoicePiperConfig {
	return &VoicePiperConfig{
		phonemeIDMap: m,
		SampleRate:   22050,
		NoiseScale:   0.667,
		LengthScale:  1.0,
		NoiseW:       0.8,
		PhonemeType:  "espeak",
	}
}

// HasPhonemeMap reports whether this config has a usable phoneme→id map.
func (c *VoicePiperConfig) HasPhonemeMap() bool { return len(c.phonemeIDMap) > 0 }

// PadID is THE PAD RULE: the id THIS voice uses for blank.
//
// It is 0 in sherpa/MMS exports and 3 in Piper-family ones, and pointing it at
// an ordinary vocabulary entry is what made 42 MMS voices speak fluent nonsense.
// Never assume a constant — read it from the model. Falls back to 0 only when
// the vocabulary has no "_" at all.
func (c *VoicePiperConfig) PadID() int64 {
	if p, ok := c.phonemeIDMap[voicePad]; ok && len(p) > 0 {
		return p[0]
	}
	return 0
}

// PhonemesToIDs turns a phoneme sequence into model token ids, in
// piper-phonemize's exact layout with interspersed pad:
//
//	[BOS, PAD, id(p1), PAD, id(p2), PAD, ..., id(pN), PAD, EOS]
//
// BOS and EOS appear only when the vocabulary HAS them — the MMS-family exports
// do not. Unknown symbols are SKIPPED and REPORTED, never fatal: one unknown
// symbol must not abort the whole utterance.
func (c *VoicePiperConfig) PhonemesToIDs(phonemes []string) VoicePhonemeMapping {
	ids := make([]int64, 0, 64)
	dropped := []string{}
	approximated := []string{}
	skipped := 0

	if b, ok := c.phonemeIDMap[voiceBos]; ok {
		ids = append(ids, b...)
	}
	pad, hasPad := c.phonemeIDMap[voicePad]
	if hasPad {
		ids = append(ids, pad...)
	}

	for _, p := range phonemes {
		mapped, wasApprox, ok := c.mapSymbol(p)
		if !ok {
			skipped++
			if !containsString(dropped, p) {
				dropped = append(dropped, p)
			}
			continue
		}
		if wasApprox && !containsString(approximated, p) {
			approximated = append(approximated, p)
		}
		ids = append(ids, mapped...)
		if hasPad {
			ids = append(ids, pad...)
		}
	}

	if e, ok := c.phonemeIDMap[voiceEos]; ok {
		ids = append(ids, e...)
	}

	return VoicePhonemeMapping{
		IDs: ids, Skipped: skipped,
		SkippedSymbols: dropped, ApproximatedSymbols: approximated,
	}
}

// VoiceSplitPhonemeString splits into grapheme clusters: a base rune plus any
// combining marks that follow it, so "bát" is three elements and not four.
func VoiceSplitPhonemeString(s string) []string {
	out := []string{}
	var cur []rune
	for _, r := range s {
		if len(cur) > 0 && isCombiningMark(r) {
			cur = append(cur, r)
			continue
		}
		if len(cur) > 0 {
			out = append(out, string(cur))
		}
		cur = []rune{r}
	}
	if len(cur) > 0 {
		out = append(out, string(cur))
	}
	return out
}

func isCombiningMark(r rune) bool {
	return unicode.In(r, unicode.Mn, unicode.Mc, unicode.Me)
}

func (c *VoicePiperConfig) mapSymbol(symbol string) (ids []int64, approximated bool, ok bool) {
	if exact, hit := c.phonemeIDMap[symbol]; hit {
		return exact, false, true
	}

	// A grapheme voice's vocabulary is built AFTER the training text has been
	// through the model's own cleaner, and every cleaner in use here lower-cases.
	// Such a vocab contains no capitals at all, so matching on the raw character
	// silently discarded every sentence-initial letter — the model received
	// "awubona" for "Sawubona".
	lower := strings.ToLower(symbol)
	if lower != symbol {
		if l, hit := c.phonemeIDMap[lower]; hit {
			return l, false, true
		}
	}

	// A GRAPHEME CLUSTER the vocabulary stores as separate codepoints. Burmese
	// "ကြို" arrives as ONE symbol while the vocabulary holds each codepoint on
	// its own. Splitting it back keeps every mark, so this must be tried BEFORE
	// any approximation.
	if len([]rune(symbol)) > 1 {
		parts := []int64{}
		whole := true
		for _, r := range symbol {
			// Zero-width formatting characters shape how text is DRAWN and say
			// nothing about how it sounds. Persian writes them constantly, as do
			// most Indic scripts, and one invisible character was failing the
			// whole cluster.
			if unicode.In(r, unicode.Cf) {
				continue
			}
			s := string(r)
			if part, hit := c.phonemeIDMap[s]; hit {
				parts = append(parts, part...)
			} else if part, hit := c.phonemeIDMap[strings.ToLower(s)]; hit {
				parts = append(parts, part...)
			} else {
				whole = false
				break
			}
		}
		if whole && len(parts) > 0 {
			return parts, false, true // exact — nothing was lost
		}
	}

	// A letter the voice never learned. Dropping it deletes a consonant from the
	// middle of a word, so an approximation is worth more than a hole — so long
	// as it is declared rather than passed off as correct.
	for _, candidate := range voiceApproximations(symbol) {
		if a, hit := c.phonemeIDMap[candidate]; hit {
			return a, true, true
		}
		if a, hit := c.phonemeIDMap[strings.ToLower(candidate)]; hit {
			return a, true, true
		}
	}

	return nil, false, false
}

func voiceApproximations(symbol string) []string {
	out := []string{}

	// Where the vocabulary carries the true phoneme under a different spelling,
	// use it — Tshivenda's ṅ IS /ŋ/, so that substitution loses nothing at all.
	if symbol == "ṅ" || symbol == "Ṅ" {
		out = append(out, "ŋ")
	}
	if symbol == "š" || symbol == "Š" {
		out = append(out, "ʃ")
	}

	// Folding a diacritic away is only defensible where the mark modifies a
	// letter that still carries most of the sound without it — Latin š→s, ṱ→t.
	// In Thai, Burmese, Devanagari, Arabic and Vietnamese the marks ARE the
	// vowels and tones; dropping them does not approximate the word, it deletes
	// it. Thai measured 4.3 s instead of ~15 s because every vowel sign was
	// folded off a consonant and filed as a harmless approximation.
	stripped := voiceStripDiacritics(symbol)
	if stripped == "" || stripped == symbol || !voiceIsLatinBase(stripped) {
		return out
	}
	return append(out, stripped)
}

// voiceIsLatinBase judges the BASE that remains, not the composed character:
// Tshivenda ṱ lives at U+1E71, far above the Latin block, yet strips to a plain
// 't'. Thai วั strips to ว, which is not Latin at all — the case to refuse.
func voiceIsLatinBase(stripped string) bool {
	if stripped == "" {
		return false
	}
	for _, r := range stripped {
		if r > 0x024F { // beyond Latin Extended-B
			return false
		}
	}
	return true
}

// voiceStripDiacritics decomposes and removes combining marks: ṱ → t.
func voiceStripDiacritics(s string) string {
	var b strings.Builder
	for _, r := range norm.NFD.String(s) {
		if isCombiningMark(r) {
			continue
		}
		b.WriteRune(r)
	}
	return b.String()
}

// ─────────────────────────────────────────────────────────────────────────────
// LexiconTokeniser
// ─────────────────────────────────────────────────────────────────────────────

// VoiceLexiconTokeniser turns text into model tokens using a voice's own lexicon
// files — a word→phoneme table and a phoneme→id table beside the model. No
// phonemizer process, no second package, no licence wall.
type VoiceLexiconTokeniser struct {
	words   map[string][]int64
	longest int
	// Blank is interleaved between tokens when the model expects it.
	Blank int64
	// LastUnmapped names symbols the lexicon had no entry for on the last call.
	LastUnmapped []string
}

// NewVoiceLexiconTokeniser builds from tokens.txt and lexicon.txt content.
func NewVoiceLexiconTokeniser(tokensText, lexiconText string, blank int64) *VoiceLexiconTokeniser {
	// tokens.txt is "<symbol> <id>" per line. The symbol MAY BE A SPACE, so
	// split on the LAST space rather than the first.
	ids := map[string]int64{}
	for _, line := range strings.Split(tokensText, "\n") {
		line = strings.TrimRight(line, "\r")
		cut := strings.LastIndex(line, " ")
		if cut <= 0 {
			continue
		}
		id, err := strconv.ParseInt(line[cut+1:], 10, 64)
		if err != nil {
			continue
		}
		ids[line[:cut]] = id
	}
	if len(ids) == 0 {
		return nil
	}

	// lexicon.txt is "<word> <phoneme> <phoneme> ...".
	words := map[string][]int64{}
	longest := 1
	for _, line := range strings.Split(lexiconText, "\n") {
		parts := strings.Fields(strings.TrimRight(line, "\r"))
		if len(parts) < 2 {
			continue
		}
		seq := []int64{}
		for _, p := range parts[1:] {
			if id, ok := ids[p]; ok {
				seq = append(seq, id)
			}
		}
		if len(seq) == 0 {
			continue
		}
		words[parts[0]] = seq
		if n := len([]rune(parts[0])); n > longest {
			longest = n
		}
	}
	if len(words) == 0 {
		return nil
	}
	return &VoiceLexiconTokeniser{words: words, longest: longest, Blank: blank}
}

// Encode segments text and returns the model's tokens.
//
// LONGEST MATCH FIRST, because these lexicons are word-keyed and the words
// overlap: あい, あいさつ and あいかわらず all start the same way, and taking the
// shortest would pronounce a different word. Falls back to the single character
// when no word matches.
func (t *VoiceLexiconTokeniser) Encode(text string, interleaveBlank bool) []int64 {
	out := []int64{}
	unmapped := []string{}
	// RUNES, NOT BYTES: these lexicons are keyed on CJK words, and a byte index
	// would cut a character in half and match nothing.
	chars := []rune(text)

	for i := 0; i < len(chars); {
		taken := 0
		max := t.longest
		if len(chars)-i < max {
			max = len(chars) - i
		}
		for length := max; length > 0; length-- {
			if seq, ok := t.words[string(chars[i:i+length])]; ok {
				out = append(out, seq...)
				taken = length
				break
			}
		}
		if taken == 0 {
			if !unicode.IsSpace(chars[i]) {
				unmapped = append(unmapped, string(chars[i]))
			}
			taken = 1
		}
		i += taken
	}

	t.LastUnmapped = unmapped
	if !interleaveBlank {
		return out
	}

	// add_blank: a blank opens the utterance and follows every token.
	padded := make([]int64, 0, len(out)*2+1)
	padded = append(padded, t.Blank)
	for _, id := range out {
		padded = append(padded, id, t.Blank)
	}
	return padded
}
