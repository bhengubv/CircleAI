// voice_xsampa_to_ipa.go
//
// Port of src/CircleAI.Voice/XsampaToIpa.cs. Turns the X-SAMPA that the NCHLT
// phonemiser emits into the IPA that Mimic3-family voices are trained on.
//
// Parity is asserted against fixtures/voice_xsampa_to_ipa.json, which the C#
// reference generates. If this file and that file disagree, one of them is
// wrong and the test names the case.

package circleai

import "strings"

// xsampaToIPA is every phone in the NCHLT Afrikaans dictionary, mapped to IPA.
//
// Derived from the corpus, not from memory: these are exactly the distinct
// phones in nchlt_afr.dict, and every IPA character was checked against the
// target voice's own token table before this table was written.
var xsampaToIPA = map[string]string{
	// Vowels
	"a": "a", "A:": "ɑː", "A:r": "ɑːr",
	"E": "ɛ", "O": "ɔ", "@": "ə",
	"i": "i", "u": "u", "y": "y",
	"9": "œ", "2:": "øː", "{": "æ",

	// Diphthongs — NCHLT gives one token, the voice wants both elements.
	"9y": "œy", "@i": "əi", "@u": "əu",
	"i@": "iə", "u@": "uə",

	// Consonants
	"b": "b", "d": "d", "f": "f",
	// U+0261 LATIN SMALL LETTER SCRIPT G — the IPA letter, NOT ASCII 'g'. The
	// voice's vocabulary carries ɡ; a plain 'g' would miss and be dropped.
	"g": "ɡ",
	"j": "j", "k": "k", "l": "l",
	"m": "m", "n": "n", "N": "ŋ",
	"p": "p", "r": "r", "s": "s",
	"S": "ʃ", "t": "t", "v": "v",
	"w": "w", "x": "x", "z": "z",
	"Z": "ʒ",

	// APPROXIMATION, DELIBERATE AND THE ONLY ONE. X-SAMPA h\ is ɦ, the voiced
	// glottal fricative Afrikaans uses in "hond". This voice's vocabulary has no
	// ɦ, only h. Voicing is lost; place and manner are right, so the word stays
	// recognisable.
	"h\\": "h",
}

// voiceLastUnmapped holds the phones the last XsampaToIPA call could not map.
//
// Empty is the good case. An unmapped phone produces NO SOUND and the audio is
// merely shorter — every acoustic measure still passes. Counting them is the
// only way a caller can refuse rather than speak a shorter sentence than it was
// given.
var voiceLastUnmapped = []string{}

// XsampaToIPA converts X-SAMPA phone tokens to a flat IPA symbol list.
//
// LONGEST MATCH ON WHOLE TOKENS. Several entries are multi-character (A:r, @i,
// 9y) and NCHLT emits them as single tokens; matching on the token — never
// character by character — is what keeps A:r from becoming A + : + r.
func XsampaToIPA(xsampa []string) []string {
	ipa := make([]string, 0, len(xsampa)+8)
	unmapped := []string{}

	for _, phone := range xsampa {
		if strings.TrimSpace(phone) == "" {
			continue
		}
		if mapped, ok := xsampaToIPA[phone]; ok {
			// Emit per-rune: the voice tokenises ɑ, ː and r separately, so "ɑːr"
			// must arrive as three symbols, not one. Ranging over a Go string
			// yields runes, which is what we want — indexing would yield bytes
			// and split every non-ASCII IPA character.
			for _, r := range mapped {
				ipa = append(ipa, string(r))
			}
			continue
		}
		if !containsString(unmapped, phone) {
			unmapped = append(unmapped, phone)
		}
	}

	voiceLastUnmapped = unmapped
	return ipa
}

// XsampaLastUnmapped returns the phones the last XsampaToIPA call could not map.
func XsampaLastUnmapped() []string { return voiceLastUnmapped }

// XsampaCanSayAll reports whether every phone in xsampa has a mapping.
func XsampaCanSayAll(xsampa []string) bool {
	for _, p := range xsampa {
		if strings.TrimSpace(p) == "" {
			continue
		}
		if _, ok := xsampaToIPA[p]; !ok {
			return false
		}
	}
	return true
}

// XsampaKnownPhones returns the X-SAMPA phones this table knows.
func XsampaKnownPhones() []string {
	out := make([]string, 0, len(xsampaToIPA))
	for k := range xsampaToIPA {
		out = append(out, k)
	}
	return out
}

func containsString(haystack []string, needle string) bool {
	for _, h := range haystack {
		if h == needle {
			return true
		}
	}
	return false
}
