// voice_text.go
//
// Ports of the five text-side voice modules:
//
//	src/CircleAI.Voice/SentenceSplitter.cs
//	src/CircleAI.Voice/LanguageSpanSplitter.cs
//	src/CircleAI.Voice/GeezRomanizer.cs
//	src/CircleAI.Voice/ToneShaper.cs
//	src/CircleAI.Voice/NchltPhonemizer.cs
//
// Parity is asserted against fixtures/voice_sentence_splitter.json,
// voice_language_spans.json, voice_geez_romanizer.json, voice_tone_shaper.json
// and voice_nchlt_phonemizer.json, which the C# reference generates.

package circleai

import (
	"math"
	"sort"
	"strconv"
	"strings"
	"unicode"
)

// ── SentenceSplitter ────────────────────────────────────────────────────────
//
// Why this has to exist: the voices in use here were trained on text with the
// punctuation stripped out, so their vocabularies contain no '.', ',', '?' or
// ':' at all. Feeding a paragraph in one pass produces one unbroken run of
// speech — no pause between sentences, because there is no token that could
// encode one. The pause has to come from outside the model.
//
// It splits at SENTENCE boundaries only, never at commas. Each synthesis is an
// independent utterance and a VITS model ends every utterance with falling,
// sentence-final prosody, so cutting at a comma would make each clause land like
// a finished sentence — worse prosody than the run-on it was meant to fix.

// SpeechSegment is one unit of speech, plus the silence that should follow it.
type SpeechSegment struct {
	// Text to synthesise. Never empty or whitespace.
	Text string
	// TrailingPauseMs is the silence to append after this segment. 0 for the
	// final segment — trailing silence at the end of a passage serves nothing.
	TrailingPauseMs int
}

// Pause lengths are the perceptual point of this file, so they are named rather
// than buried. A full stop reads longer than a colon; a paragraph break longer
// than either.
const (
	sentencePauseMs  = 280
	clausePauseMs    = 200 // ':' and ';' — a lighter break
	paragraphPauseMs = 400
	forcedPauseMs    = 60 // an over-long run cut for latency
)

// MaxCharsPerSegment is the length beyond which a segment is cut even without
// punctuation. A single unbroken clause of this size is already several seconds
// of audio, and on a phone the whole segment must render before ANY of it can
// play. The cut is taken at a word boundary and given only a token pause.
const MaxCharsPerSegment = 220

// Characters that end a sentence, across the scripts we speak.
//
// A Latin-only list silently under-splits every language that punctuates
// differently. Measured on the P30: Hindi, Bengali and Urdu produced THREE
// segments from the same five-sentence text that gave six in eleven other
// languages, because Devanagari and Bengali end sentences with the danda and
// Urdu with its own full stop — none of which were listed. The paragraph ran
// together exactly as it did before the splitter existed, for about a billion
// people, and nothing failed loudly enough to notice.
var terminators = map[rune]bool{
	'.': true, '!': true, '?': true, ':': true, ';': true, // Latin / Cyrillic / Greek
	'।': true, '॥': true, // danda, double danda — Devanagari, Bengali, Gurmukhi
	'۔': true, '؟': true, '؛': true, // Arabic script — Urdu, Arabic, Persian, Pashto
	'。': true, '！': true, '？': true, // CJK ideographic + fullwidth
	'．': true, '：': true, '；': true, // fullwidth
	'።': true,            // Ethiopic — Amharic, Tigrinya
	'។': true,            // Khmer khan
	'၊': true, '။': true, // Myanmar little/section
}

// Terminators that can legitimately appear inside a token, and so need a
// following space before they may be read as ending a sentence.
var mayOccurInsideAToken = map[rune]bool{'.': true, ':': true, ';': true}

var closers = map[rune]bool{'"': true, '\'': true, ')': true, ']': true}

// SplitSentences splits text into segments. Returns a single segment when there
// is no sentence punctuation, and an empty slice for blank input.
//
// INDEXED BY UTF-16 CODE UNIT, not by rune or byte, because the reference walks
// a C# string. Every terminator in the table is in the BMP, so the two agree on
// where the splits fall — but MaxCharsPerSegment counts units, and a port that
// counted runes or bytes would cut over-long text in a different place.
func SplitSentences(text string) []SpeechSegment {
	segments := []SpeechSegment{}
	if strings.TrimSpace(text) == "" {
		return segments
	}

	units := utf16Units(text)
	var current []uint16
	pending := sentencePauseMs

	for i := 0; i < len(units); i++ {
		c := units[i]

		if c == '\r' {
			continue
		}
		if c == '\n' {
			current = flushSegment(&segments, current, paragraphPauseMs)
			continue
		}

		current = append(current, c)

		if terminators[rune(c)] && endsSentence(units, i) {
			pause := sentencePauseMs
			if c == ':' || c == ';' {
				pause = clausePauseMs
			}
			current = flushSegment(&segments, current, pause)
			continue
		}

		if len(current) >= MaxCharsPerSegment {
			current = cutAtWordBoundary(&segments, current)
		}
	}

	flushSegment(&segments, current, pending)

	// Nothing should follow the last word — a trailing pause is dead air.
	if len(segments) > 0 {
		segments[len(segments)-1].TrailingPauseMs = 0
	}

	return segments
}

// endsSentence reports whether the terminator at i really ends a sentence.
//
// A period between digits is a decimal ("3.5"), and one followed directly by a
// letter is usually an abbreviation or a URL — splitting there would cut a word
// in half and insert a pause inside it.
func endsSentence(units []uint16, i int) bool {
	// Absorb any run of closing punctuation ("...", "?!", ".").
	j := i + 1
	for j < len(units) && (terminators[rune(units[j])] || closers[rune(units[j])]) {
		j++
	}

	if j >= len(units) {
		return true // end of input
	}

	// Only SOME terminators can appear inside a token — '.' in 3.5 and co.za,
	// ':' in 12:30. For those, a following space is what separates a sentence end
	// from a decimal point. The rest cannot occur mid-token in any script, and
	// demanding a space after them would never split Chinese, Japanese, Khmer,
	// Thai or Burmese at all: those scripts write without spaces between words,
	// so their full stop is followed by the next letter.
	if !mayOccurInsideAToken[rune(units[i])] {
		return true
	}

	if !unicode.IsSpace(rune(units[j])) {
		return false // 3.5, e.g., co.za
	}

	if units[i] == '.' && i > 0 && unicode.IsDigit(rune(units[i-1])) &&
		j+1 < len(units) && unicode.IsDigit(rune(units[j+1])) {
		return false
	}

	return true
}

func flushSegment(segments *[]SpeechSegment, current []uint16, pauseMs int) []uint16 {
	s := strings.TrimSpace(utf16String(current))
	if s == "" {
		return nil
	}

	// The terminator STAYS in the segment text, deliberately. It is tempting to
	// strip it — this file has already turned it into a pause, and the MMS voices
	// have no token for it. But the SA-11 voice's vocabulary DOES carry '?' and
	// '.', so it can render a real question rise that no inserted silence could
	// imitate. Stripping would have discarded that from all eleven South African
	// languages to tidy up a log line.

	// A segment of nothing but punctuation has no sound to make, and the voice
	// has no token for it either.
	hasSpeech := false
	for _, ch := range s {
		if unicode.IsLetter(ch) || unicode.IsDigit(ch) {
			hasSpeech = true
			break
		}
	}
	if !hasSpeech {
		return nil
	}

	*segments = append(*segments, SpeechSegment{Text: s, TrailingPauseMs: pauseMs})
	return nil
}

// cutAtWordBoundary cuts an over-long run at the last space, so the break lands
// between words rather than inside one. With no space to use the run is left
// intact — a mid-word cut would be audibly worse than a long segment.
func cutAtWordBoundary(segments *[]SpeechSegment, current []uint16) []uint16 {
	cut := -1
	for i := len(current) - 1; i >= 0; i-- {
		if current[i] == ' ' {
			cut = i
			break
		}
	}
	if cut <= 0 {
		return current
	}

	head := strings.TrimSpace(utf16String(current[:cut]))
	if head != "" {
		*segments = append(*segments, SpeechSegment{Text: head, TrailingPauseMs: forcedPauseMs})
	}

	return append([]uint16{}, current[cut+1:]...)
}

// utf16Units converts a Go string to the UTF-16 code units a C# string is made
// of, so index arithmetic lines up with the reference.
func utf16Units(s string) []uint16 {
	units := make([]uint16, 0, len(s))
	for _, r := range s {
		if r <= 0xFFFF {
			units = append(units, uint16(r))
			continue
		}
		r -= 0x10000
		units = append(units, uint16(0xD800+(r>>10)), uint16(0xDC00+(r&0x3FF)))
	}
	return units
}

func utf16String(units []uint16) string {
	var sb strings.Builder
	for i := 0; i < len(units); i++ {
		u := units[i]
		if u >= 0xD800 && u <= 0xDBFF && i+1 < len(units) &&
			units[i+1] >= 0xDC00 && units[i+1] <= 0xDFFF {
			sb.WriteRune(rune(0x10000 + (uint32(u-0xD800) << 10) + uint32(units[i+1]-0xDC00)))
			i++
			continue
		}
		sb.WriteRune(rune(u))
	}
	return sb.String()
}

// ── LanguageSpanSplitter ────────────────────────────────────────────────────
//
// People do not speak one language per sentence. "Igama lami ngu-CircleAI" is
// isiZulu with an English name inside it, and read wholly in isiZulu the name
// comes out mangled — the listener hears the machine fail at a word they know
// perfectly well. A multi-lingual model takes ONE language id per utterance, so
// the fix is to cut the text where the language changes and synthesise each run
// under its own id.

// LanguageSpan is a run of text to be spoken in one language.
type LanguageSpan struct {
	// Text of the run, with its spacing preserved.
	Text string
	// IsForeign is true when this run is the embedded language (English), false
	// for the surrounding one.
	IsForeign bool
}

// IsForeignWord reports whether a token is unmistakably foreign (English) inside
// African-language text.
//
// Two signals only, both chosen because native orthographies do not produce
// them:
//
//	internal capitals     — CircleAI, WhatsApp, MTN's brand spellings
//	all-caps, 2-5 letters — GPS, SMS, ATM, PIN
//
// isiZulu, isiXhosa, Sesotho and the rest capitalise the first letter of a
// sentence or a proper noun and nothing else, so neither pattern arises
// naturally. A sentence-initial capital is therefore NOT a signal, which is why
// only capitals after position zero count.
//
// It does NOT try to spot ordinary lowercase English words like "computer" —
// that needs a lexicon per language pair, and guessing wrong is worse than not
// guessing: mispronouncing a native word to "fix" a foreign one insults the
// speaker in their own language.
func IsForeignWord(word string) bool {
	units := utf16Units(word)
	if len(units) < 2 {
		return false
	}

	upper, lower := 0, 0
	hasInternalCapital := false

	for i, u := range units {
		c := rune(u)
		if !unicode.IsLetter(c) {
			continue
		}
		if unicode.IsUpper(c) {
			upper++
			if i > 0 {
				hasInternalCapital = true
			}
		} else {
			lower++
		}
	}

	if hasInternalCapital && lower > 0 {
		return true // CircleAI, WhatsApp
	}
	if upper >= 2 && lower == 0 && len(units) <= 5 {
		return true // GPS, SMS, ATM
	}
	return false
}

// SplitLanguageSpans splits text into spans. Returns a single span when the text
// is all one language, which is the overwhelmingly common case — callers can
// check len == 1 and take their existing single-language path.
func SplitLanguageSpans(text string) []LanguageSpan {
	if strings.TrimSpace(text) == "" {
		return []LanguageSpan{}
	}

	units := utf16Units(text)
	spans := []LanguageSpan{}
	var current []uint16
	currentIsForeign := -1 // -1 = unset, 0 = native, 1 = foreign

	i := 0
	for i < len(units) {
		// Separators (spaces, punctuation, the hyphen in "ngu-CircleAI") ride
		// along with whatever run they FOLLOW, so a language change never strands
		// a comma on its own or splits mid-punctuation.
		if !isUnicodeLetterOrDigit(rune(units[i])) {
			sepStart := i
			for i < len(units) && !isUnicodeLetterOrDigit(rune(units[i])) {
				i++
			}
			current = append(current, units[sepStart:i]...)
			continue
		}

		wordStart := i
		for i < len(units) && isUnicodeLetterOrDigit(rune(units[i])) {
			i++
		}
		word := utf16String(units[wordStart:i])
		foreign := 0
		if IsForeignWord(word) {
			foreign = 1
		}

		if currentIsForeign != -1 && currentIsForeign != foreign {
			// The run ends at the last word, not at the separators that follow it
			// — those have already been appended and belong to the join.
			spans = append(spans, LanguageSpan{
				Text: utf16String(current), IsForeign: currentIsForeign == 1,
			})
			current = nil
		}

		currentIsForeign = foreign
		current = append(current, units[wordStart:i]...)
	}

	if len(current) > 0 && currentIsForeign != -1 {
		spans = append(spans, LanguageSpan{
			Text: utf16String(current), IsForeign: currentIsForeign == 1,
		})
	}

	return spans
}

// isUnicodeLetterOrDigit is Unicode-aware, unlike the ASCII-only
// isLetterOrDigit in model_download_service.go. Reusing that one would treat
// every Ethiopic, Devanagari and CJK letter as a SEPARATOR, so mixed-language
// splitting would cut between every character of a non-Latin word.
func isUnicodeLetterOrDigit(c rune) bool {
	return unicode.IsLetter(c) || unicode.IsDigit(c)
}

// ToSpokenForm rewrites a run into the form a voice can actually pronounce,
// without changing what is displayed.
//
// A compound like "CircleAI" is one token to a synthesiser and it has no idea
// where the words are, so it produces a mumble. Written "Circle AI" it is two
// things the voice already knows how to say. This is why the name came out
// garbled even after it was correctly switched to English — the language was
// right and the word was still unreadable.
func ToSpokenForm(text string) string {
	if text == "" {
		return text
	}

	units := utf16Units(text)

	// 1. Break the compound into words at case boundaries, which is where the
	//    word boundaries genuinely are in this naming style.
	var spaced []uint16
	for i, u := range units {
		c := rune(u)
		if i > 0 && unicode.IsUpper(c) {
			prev := rune(units[i-1])
			var next rune
			if i+1 < len(units) {
				next = rune(units[i+1])
			}

			// lower->Upper is a word boundary (Circle|AI, You|Tube).
			afterLower := unicode.IsLower(prev)
			// Upper->Upper->lower ends a run of capitals (API|Key).
			endOfAcronym := unicode.IsUpper(prev) && next != 0 && unicode.IsLower(next)

			if afterLower || endOfAcronym {
				spaced = append(spaced, ' ')
			}
		}
		spaced = append(spaced, u)
	}

	// 2. Punctuate the acronyms. "AI" as a bare token gets read as a word — "ay"
	//    — where "A.I." is read as the letters, which is what it is. The full
	//    stops are for the voice, not the reader.
	var out []uint16
	for i := 0; i < len(spaced); {
		if !unicode.IsUpper(rune(spaced[i])) {
			out = append(out, spaced[i])
			i++
			continue
		}

		start := i
		for i < len(spaced) && unicode.IsUpper(rune(spaced[i])) {
			i++
		}
		run := spaced[start:i]

		// A lone capital is an ordinary word opening ("Sawubona"), not an
		// acronym, and a run followed by lowercase was already split above.
		if len(run) < 2 {
			out = append(out, run...)
			continue
		}

		for _, ch := range run {
			out = append(out, ch, '.')
		}
	}
	return utf16String(out)
}

// ── GeezRomanizer ───────────────────────────────────────────────────────────
//
// Ethiopic (Ge'ez) script -> Latin, because the Amharic and Tigrinya voices do
// not read Ethiopic at all. Meta ships those two MMS models with
// is_uroman:true: their vocabularies are 28 and 27 LATIN letters and they expect
// text already transliterated. Measured on the P30, Amharic lost 43 distinct
// characters and produced 3.2 s of noise for a 15 s paragraph.
//
// The transliteration is computed, not tabulated, because Unicode lays the
// syllabary out exactly as the script is taught: each consecutive block of EIGHT
// codepoints is one consonant across its vowel orders.

const (
	geezBase               = 0x1200
	geezOrdersPerConsonant = 8
	// geezLastSyllable is the last codepoint that follows the
	// eight-orders-per-consonant layout. The syllabary ends here; everything
	// above is lone syllables, marks and numerals, and treating any of it as a
	// row invents a pronunciation.
	geezLastSyllable = 0x1357
)

// Consonant per 8-codepoint row, in Unicode order. ASCII only: these voices hold
// 27-28 plain Latin letters, so a transliteration carrying the Ethiopist
// diacritics would be dropped as surely as the Ethiopic was.
//
// Six rows are LABIALISED — the consonant carries a built-in /w/. Writing them
// plain turns "kwa" into "ka", which silently changes the word.
var geezConsonants = []string{
	"h", "l", "h", "m", "s", "r", "s", "sh",
	"q", "qw", "q", "qw", "b", "v", "t", "ch",
	"h", "hw", "n", "ny", "", "k", "kw", "k",
	"kw", "w", "", "z", "zh", "y", "d", "d",
	"j", "g", "gw", "ng", "t", "ch", "p", "ts",
	"ts", "f", "p",
}

// Vowel per order. The sixth is SILENT — it marks a bare consonant, which is why
// the greeting romanises with no trailing vowel.
var geezVowels = []string{"e", "u", "i", "a", "e", "", "o", "wa"}

// The three syllables Unicode assigns singly rather than as a row of eight. They
// are already in the -a order, so the vowel is part of the value.
var geezLoneSyllables = map[rune]string{
	'ፘ': "rya",
	'ፙ': "mya",
	'ፚ': "fya",
}

// Combining marks. They modify the syllable before them and have no sound of
// their own, so they are dropped rather than passed through — a bare mark
// reaching a Latin-only vocabulary is one more unmapped symbol.
var geezMarks = map[rune]bool{'፝': true, '፞': true, '፟': true}

// Ethiopic punctuation, mapped so sentence splitting still works.
var geezPunctuation = map[rune]string{
	'፠': " ", // section
	'፡': " ", // word separator
	'።': ".", // full stop
	'፣': ",", // comma
	'፤': ";", // semicolon
	'፥': ":", // colon
	'፦': ":", // preface colon
	'፧': "?", // question mark
	'፨': " ", // paragraph separator
}

// IsEthiopic reports whether text contains any Ethiopic character.
func IsEthiopic(text string) bool {
	for _, c := range text {
		if c >= 0x1200 && c <= 0x139F {
			return true
		}
	}
	return false
}

// Romanize converts Ethiopic to Latin. Characters outside the script pass
// through untouched, so mixed text (numerals, Latin names, punctuation) survives
// intact.
func Romanize(text string) string {
	if text == "" {
		return text
	}

	var sb strings.Builder
	for _, c := range text {
		if p, ok := geezPunctuation[c]; ok {
			sb.WriteString(p)
			continue
		}

		// THE EIGHT-PER-CONSONANT LAYOUT STOPS AT U+1357, and the range check has
		// to stop with it. Beyond that the block is no longer a syllabary:
		// U+1358..U+135A are three LONE syllables already in their -a order,
		// U+135D..U+135F are combining marks, and U+1369 onward are the numerals.
		// Sizing the check off the consonant table instead swept seven of those
		// numerals back into the syllabary — and they came out as sound, so
		// nothing failed.
		if geezMarks[c] {
			continue
		}
		if lone, ok := geezLoneSyllables[c]; ok {
			sb.WriteString(lone)
			continue
		}

		i := int(c) - geezBase
		if i < 0 || i > geezLastSyllable-geezBase {
			// Numerals and the rarely-used supplement blocks have no sound we can
			// render; anything else is not Ethiopic and is left alone.
			if c >= 0x1369 && c <= 0x137C {
				continue
			}
			sb.WriteRune(c)
			continue
		}

		row := i / geezOrdersPerConsonant
		order := i % geezOrdersPerConsonant

		consonant := geezConsonants[row]
		vowel := geezVowels[order]

		if consonant == "" {
			// The glottal and pharyngeal rows write no consonant in Latin, so the
			// vowel IS the character. First order is heard as "a", and the sixth —
			// silent after a real consonant — must still sound here, or the
			// word-initial one disappears entirely.
			if order == 0 {
				vowel = "a"
			} else if vowel == "" {
				vowel = "e"
			}
		}

		sb.WriteString(consonant)
		sb.WriteString(vowel)
	}
	return sb.String()
}

// ── ToneShaper ──────────────────────────────────────────────────────────────
//
// Warmth, after the model has finished.
//
// THE VOICE WAS REPORTED AS TINNY, AND THE SPEAKER COULD NOT FIX IT. Choosing a
// speaker by how well the recogniser understands it has a bias nobody costed:
// word error rate rewards crisp consonants and a bright top end, which is what
// "tinny" describes. Measured across all 130 speakers in the bundle, warmth and
// intelligibility are inversely related. So the speaker is not the lever. The
// waveform is, and it is entirely ours once the model hands it over.
//
// WHY A DIP AND NOT JUST A BOOST. A phone speaker cannot move enough air to
// reproduce a low-shelf boost; on a P30 the bass simply is not there to lift.
// Cutting 2-5 kHz, where harshness lives, works on hardware that cannot do bass,
// which is most of the hardware this ships to. The boost is for headphones. Both
// are applied because the product is used on both.

// ToneShaperSettings holds the two filters' parameters.
type ToneShaperSettings struct {
	LowShelfHz float64 // where the low shelf starts lifting
	LowShelfDb float64 // how much to lift the bottom
	PresenceHz float64 // centre of the harshness dip
	PresenceDb float64 // how much to cut there; negative cuts
	PresenceQ  float64 // width of the dip; lower is wider
}

// WarmToneShaper is the measured setting: warmer, with no cost to
// intelligibility.
var WarmToneShaper = ToneShaperSettings{
	LowShelfHz: 320, LowShelfDb: 4.0,
	PresenceHz: 3200, PresenceDb: -4.0, PresenceQ: 0.8,
}

const lowShelfSlope = 0.9

// BiquadCoefficients are already normalised by a0.
type BiquadCoefficients struct {
	B [3]float64
	A [3]float64
}

func normaliseBiquad(b, a [3]float64) BiquadCoefficients {
	a0 := a[0]
	for i := 0; i < 3; i++ {
		b[i] /= a0
		a[i] /= a0
	}
	return BiquadCoefficients{B: b, A: a}
}

// LowShelfCoefficients returns the RBJ audio-cookbook low shelf, normalised.
func LowShelfCoefficients(s ToneShaperSettings, rate int) BiquadCoefficients {
	amp := math.Pow(10, s.LowShelfDb/40)
	w0 := 2 * math.Pi * s.LowShelfHz / float64(rate)
	alpha := math.Sin(w0) / 2 * math.Sqrt((amp+1/amp)*(1/lowShelfSlope-1)+2)
	c := math.Cos(w0)
	s2 := 2 * math.Sqrt(amp) * alpha

	return normaliseBiquad(
		[3]float64{
			amp * ((amp + 1) - (amp-1)*c + s2),
			2 * amp * ((amp - 1) - (amp+1)*c),
			amp * ((amp + 1) - (amp-1)*c - s2),
		},
		[3]float64{
			(amp + 1) + (amp-1)*c + s2,
			-2 * ((amp - 1) + (amp+1)*c),
			(amp + 1) + (amp-1)*c - s2,
		},
	)
}

// PeakingCoefficients returns the RBJ audio-cookbook peaking EQ, normalised.
func PeakingCoefficients(s ToneShaperSettings, rate int) BiquadCoefficients {
	amp := math.Pow(10, s.PresenceDb/40)
	w0 := 2 * math.Pi * s.PresenceHz / float64(rate)
	alpha := math.Sin(w0) / (2 * s.PresenceQ)
	c := math.Cos(w0)

	return normaliseBiquad(
		[3]float64{1 + alpha*amp, -2 * c, 1 - alpha*amp},
		[3]float64{1 + alpha/amp, -2 * c, 1 - alpha/amp},
	)
}

// Biquad runs a direct-form-I biquad over x in place.
//
// THE STATE IS DOUBLE AND THE STORED SAMPLE IS FLOAT, and both halves matter.
// The filter memory never sees the float rounding — y1 keeps the full-precision
// result — so the recursion is identical everywhere. Only what lands in the
// buffer is narrowed, which is what the next stage then reads.
func Biquad(x []float32, c BiquadCoefficients) {
	var x1, x2, y1, y2 float64
	for i := range x {
		xn := float64(x[i])
		yn := c.B[0]*xn + c.B[1]*x1 + c.B[2]*x2 - c.A[1]*y1 - c.A[2]*y2
		x2, x1 = x1, xn
		y2, y1 = y1, yn
		x[i] = float32(yn)
	}
}

func peakOf(x []float32) float32 {
	var p float32
	for _, v := range x {
		a := v
		if a < 0 {
			a = -a
		}
		if a > p {
			p = a
		}
	}
	return p
}

// ApplyToneShaper filters waveform in place with a low shelf and a presence dip
// in series.
//
// PEAK IS RESTORED AFTERWARDS. Lifting the low shelf adds energy, and a waveform
// that already peaked near full scale would clip — which is heard as crackle and
// would be blamed on the quantised model rather than on this. Scaling back to
// the original peak keeps the tone change audible and the level unchanged.
func ApplyToneShaper(waveform []float32, sampleRate int, s ToneShaperSettings) {
	if len(waveform) == 0 || sampleRate <= 0 {
		return
	}

	before := peakOf(waveform)
	if before <= 0 {
		return // a silent buffer, and dividing by that peak is NaN
	}

	Biquad(waveform, LowShelfCoefficients(s, sampleRate))
	Biquad(waveform, PeakingCoefficients(s, sampleRate))

	after := peakOf(waveform)
	if after > 0 && after > before {
		// float32 division, because the reference divides two FLOATS here.
		// Widening to double makes the gain a few ULP different and the whole
		// tail of the waveform drifts with it.
		g := before / after
		for i := range waveform {
			waveform[i] *= g
		}
	}
}

// ── NchltPhonemizer ─────────────────────────────────────────────────────────
//
// A fully sovereign, permissive-licence grapheme-to-phoneme front-end for the
// South African languages. NOT espeak-ng (GPLv3 taints the app), NOT phonemeza
// (unlicensed, weights unpublished), and not neural. A faithful port of the
// NCHLT pronunciation predictor (Marelie Davel, pron_predict.pl) driven by the
// NCHLT-inlang resources, © DAC / CSIR / NWU under CC BY 3.0.
//
// Because the rule set covers any word there is no "OOV gap": a word is either
// in the dictionary (exact) or synthesised by the rules, which is what makes
// agglutinative isiZulu tractable.

// nchltRule is one context rule: grapheme g in left/right context -> code.
type nchltRule struct {
	Order int
	Left  string
	Right string
	Code  string
}

// NchltPhonemizer is grapheme-to-phoneme for the NCHLT languages. Pure Go — no
// espeak, no native library.
type NchltPhonemizer struct {
	dict     map[string][]string
	rules    map[rune][]nchltRule
	phoneMap map[rune]string
	graphMap map[rune]rune
	gnulls   [][2]string

	// LastRulePredictedWords counts words in the last Phonemize call that were
	// synthesised by the rule engine rather than found in the dictionary. A
	// coverage diagnostic, never a failure — the rules always produce output.
	LastRulePredictedWords int

	// LastUnknownGraphemes lists graphemes in the last call that no rule covered.
	// Skipped, never guessed.
	LastUnknownGraphemes []string
}

// NewNchltPhonemizerFromText builds from the file CONTENTS rather than paths, so
// a caller can load from an embedded resource or a downloaded bundle with no
// filesystem in reach.
func NewNchltPhonemizerFromText(dictText, rulesText, phoneMapText, graphMapText, gnullsText string) *NchltPhonemizer {
	p := &NchltPhonemizer{
		dict:                 parseNchltDict(dictText),
		rules:                parseNchltRules(rulesText),
		phoneMap:             parseNchltPhoneMap(phoneMapText),
		graphMap:             map[rune]rune{},
		gnulls:               nil,
		LastUnknownGraphemes: []string{},
	}
	if graphMapText != "" {
		p.graphMap = parseNchltGraphMap(graphMapText)
	}
	if gnullsText != "" {
		p.gnulls = parseNchltGnulls(gnullsText)
	}
	return p
}

// Phonemize turns text into the model's X-SAMPA phones.
func (p *NchltPhonemizer) Phonemize(text string) []string {
	p.LastRulePredictedWords = 0
	p.LastUnknownGraphemes = []string{}
	if strings.TrimSpace(text) == "" {
		return []string{}
	}

	phones := []string{}
	for _, word := range nchltTokenize(text) {
		if known, ok := p.dict[word]; ok {
			phones = append(phones, known...)
		} else {
			phones = append(phones, p.PredictWord(word)...)
			p.LastRulePredictedWords++
		}
	}
	return phones
}

// PredictWord predicts a single word's X-SAMPA phones from the context rules —
// the exact algorithm of g2p_word_olist: for each grapheme take the
// highest-order rule whose left/right context matches, emit its code, drop
// nulls, then remap codes to X-SAMPA.
func (p *NchltPhonemizer) PredictWord(word string) []string {
	if word == "" {
		return []string{}
	}

	// Grapheme remap (usually identity) then grapheme-null insertion.
	w := []rune(p.applyGnulls(p.mapGraphemes(word)))

	codes := []rune{}
	for i, g := range w {
		gRules, ok := p.rules[g]
		if !ok {
			// Skip an unknown grapheme rather than fabricate a phone for it.
			s := string(g)
			seen := false
			for _, u := range p.LastUnknownGraphemes {
				if u == s {
					seen = true
					break
				}
			}
			if !seen {
				p.LastUnknownGraphemes = append(p.LastUnknownGraphemes, s)
			}
			continue
		}

		// pat = " " + left-context + "-" + g + "-" + right-context + " "
		pat := " " + string(w[:i]) + "-" + string(g) + "-" + string(w[i+1:]) + " "

		// Rules are pre-sorted most-specific-first; the first match wins.
		code := '0'
		for _, r := range gRules {
			if strings.Contains(pat, r.Left+"-"+string(g)+"-"+r.Right) {
				if len(r.Code) > 0 {
					code = []rune(r.Code)[0]
				} else {
					code = '0'
				}
				break
			}
		}
		if code != '0' {
			codes = append(codes, code)
		}
	}

	phones := make([]string, 0, len(codes))
	for _, c := range codes {
		if xs, ok := p.phoneMap[c]; ok {
			phones = append(phones, xs)
		} else {
			phones = append(phones, string(c))
		}
	}
	return phones
}

func (p *NchltPhonemizer) mapGraphemes(word string) string {
	if len(p.graphMap) == 0 {
		return word
	}
	var sb strings.Builder
	for _, c := range word {
		if m, ok := p.graphMap[c]; ok {
			sb.WriteRune(m)
		} else {
			sb.WriteRune(c)
		}
	}
	return sb.String()
}

func (p *NchltPhonemizer) applyGnulls(word string) string {
	for _, g := range p.gnulls {
		word = strings.ReplaceAll(word, g[0], g[1])
	}
	return word
}

// nchltTokenize lower-cases and splits into word tokens on anything that is not
// a letter. Diacritics are preserved (Afrikaans ê/ë/ô are real graphemes);
// digits and punctuation become separators. Number and abbreviation expansion is
// out of scope and belongs to a text-normalisation pass upstream.
func nchltTokenize(text string) []string {
	words := []string{}
	var sb strings.Builder
	for _, ch := range strings.TrimSpace(text) {
		if unicode.IsLetter(ch) {
			sb.WriteRune(unicode.ToLower(ch))
		} else if sb.Len() > 0 {
			words = append(words, sb.String())
			sb.Reset()
		}
	}
	if sb.Len() > 0 {
		words = append(words, sb.String())
	}
	return words
}

// nchltLines splits the way a StreamReader does, so a CRLF file parses
// identically.
func nchltLines(text string) []string {
	lines := strings.Split(text, "\n")
	for i, l := range lines {
		lines[i] = strings.TrimSuffix(l, "\r")
	}
	return lines
}

func parseNchltDict(text string) map[string][]string {
	dict := map[string][]string{}
	for _, line := range nchltLines(text) {
		if line == "" {
			continue
		}
		tab := strings.Index(line, "\t")
		if tab <= 0 {
			continue
		}
		word := line[:tab]
		pron := strings.TrimSpace(line[tab+1:])
		if pron == "" {
			continue
		}
		if _, exists := dict[word]; exists {
			continue // keep the FIRST variant
		}
		dict[word] = strings.Fields(pron)
	}
	return dict
}

func parseNchltRules(text string) map[rune][]nchltRule {
	byGrapheme := map[rune][]nchltRule{}
	for _, line := range nchltLines(text) {
		if line == "" {
			continue
		}
		// grapheme ; left ; right ; code ; order [ ; count ]
		f := strings.Split(line, ";")
		if len(f) < 5 || f[0] == "" {
			continue
		}
		order, err := strconv.Atoi(strings.TrimSpace(f[4]))
		if err != nil {
			continue
		}
		g := []rune(f[0])[0]
		byGrapheme[g] = append(byGrapheme[g], nchltRule{Order: order, Left: f[1], Right: f[2], Code: f[3]})
	}

	// STABLE sort, descending by order. Two rules of equal order must stay in
	// file order — the reference uses LINQ's OrderByDescending, which is stable,
	// and sort.Slice is NOT stable, so this must be sort.SliceStable or dense
	// rule sets disagree on exactly the ties that are most common.
	for _, list := range byGrapheme {
		l := list
		sort.SliceStable(l, func(i, j int) bool { return l[i].Order > l[j].Order })
	}
	return byGrapheme
}

func parseNchltPhoneMap(text string) map[rune]string {
	// Line: "<code>\t<xsampa>"  (code is a single char).
	m := map[rune]string{}
	for _, line := range nchltLines(text) {
		if line == "" {
			continue
		}
		tab := strings.Index(line, "\t")
		if tab <= 0 {
			continue
		}
		code := []rune(line[:tab])
		if len(code) == 1 {
			m[code[0]] = line[tab+1:]
		}
	}
	return m
}

func parseNchltGraphMap(text string) map[rune]rune {
	// File line: "<funny>\t<std>" — we map std->funny (per remap_dict's gmap).
	m := map[rune]rune{}
	for _, line := range nchltLines(text) {
		if line == "" {
			continue
		}
		f := strings.Split(line, "\t")
		if len(f) != 2 {
			continue
		}
		a, b := []rune(f[0]), []rune(f[1])
		if len(a) == 1 && len(b) == 1 && a[0] != b[0] {
			m[b[0]] = a[0]
		}
	}
	return m
}

func parseNchltGnulls(text string) [][2]string {
	// File line: "<from>;<to>" — insert grapheme-nulls (empty for Nguni).
	list := [][2]string{}
	for _, line := range nchltLines(text) {
		if line == "" {
			continue
		}
		f := strings.Split(line, ";")
		if len(f) == 2 {
			list = append(list, [2]string{f[0], f[1]})
		}
	}
	return list
}
