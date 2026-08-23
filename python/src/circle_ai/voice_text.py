"""voice_text.py

Ports of the five text-side voice modules:

    src/CircleAI.Voice/SentenceSplitter.cs
    src/CircleAI.Voice/LanguageSpanSplitter.cs
    src/CircleAI.Voice/GeezRomanizer.cs
    src/CircleAI.Voice/ToneShaper.cs
    src/CircleAI.Voice/NchltPhonemizer.cs

Parity is asserted against fixtures/voice_sentence_splitter.json,
voice_language_spans.json, voice_geez_romanizer.json, voice_tone_shaper.json and
voice_nchlt_phonemizer.json, which the C# reference generates. If this file and
those disagree, one of them is wrong and the test names the case.

Bare standard library on purpose — this module has to import on a phone with
nothing else installed.
"""
from __future__ import annotations

import math
import re
import struct
from dataclasses import dataclass
from typing import Iterable, Iterator, Sequence

__all__ = [
    "SpeechSegment",
    "MAX_CHARS_PER_SEGMENT",
    "split_sentences",
    "LanguageSpan",
    "split_language_spans",
    "to_spoken_form",
    "is_foreign_word",
    "is_ethiopic",
    "romanize",
    "ToneShaperSettings",
    "WARM",
    "low_shelf_coefficients",
    "peaking_coefficients",
    "biquad",
    "apply_tone_shaper",
    "NchltPhonemizer",
]


# ── SentenceSplitter ────────────────────────────────────────────────────────
#
# Why this has to exist: the voices in use here were trained on text with the
# punctuation stripped out, so their vocabularies contain no '.', ',', '?' or
# ':' at all. Feeding a paragraph in one pass produces one unbroken run of
# speech — no pause between sentences, because there is no token that could
# encode one. The pause has to come from outside the model.
#
# It splits at SENTENCE boundaries only, never at commas. Each synthesis is an
# independent utterance and a VITS model ends every utterance with falling,
# sentence-final prosody, so cutting at a comma would make each clause land like
# a finished sentence — worse prosody than the run-on it was meant to fix.


@dataclass(frozen=True)
class SpeechSegment:
    """One unit of speech, plus the silence that should follow it."""

    text: str
    """The text to synthesise. Never empty or whitespace."""

    trailing_pause_ms: int
    """Silence to append after this segment, in milliseconds. 0 for the final
    segment — trailing silence at the end of a passage serves nothing."""


# Pause lengths are the perceptual point of this module, so they are named
# rather than buried. A full stop reads longer than a colon; a paragraph break
# longer than either.
_SENTENCE_PAUSE_MS = 280
_CLAUSE_PAUSE_MS = 200  # ':' and ';' — a lighter break
_PARAGRAPH_PAUSE_MS = 400
_FORCED_PAUSE_MS = 60  # an over-long run cut for latency

MAX_CHARS_PER_SEGMENT = 220
"""Beyond this many characters a segment is cut even without punctuation. A
single unbroken clause of this size is already several seconds of audio, and on
a phone the whole segment must render before ANY of it can play. The cut is
taken at a word boundary and given only a token pause."""

_TERMINATORS = frozenset(
    ".!?:;"          # Latin / Cyrillic / Greek
    "।॥"   # danda, double danda — Devanagari, Bengali, Gurmukhi
    "۔؟؛"   # Arabic script — Urdu, Arabic, Persian, Pashto
    "。！？"   # CJK ideographic + fullwidth
    "．：；"   # fullwidth
    "።"         # Ethiopic — Amharic, Tigrinya
    "។"         # Khmer khan
    "၊။"   # Myanmar little/section
)
"""Characters that end a sentence, across the scripts we speak.

A Latin-only list silently under-splits every language that punctuates
differently. Measured on the P30: Hindi, Bengali and Urdu produced THREE segments
from the same five-sentence text that gave six in eleven other languages, because
Devanagari and Bengali end sentences with the danda and Urdu with its own full
stop — none of which were listed. The paragraph ran together exactly as it did
before the splitter existed, for about a billion people, and nothing failed
loudly enough to notice."""

_MAY_OCCUR_INSIDE_A_TOKEN = frozenset(".:;")
"""Terminators that can legitimately appear inside a token, and so need a
following space before they may be read as ending a sentence."""

_CLOSERS = frozenset("\"')]")


def _ends_sentence(text: str, i: int) -> bool:
    """True when the terminator at ``i`` really ends a sentence.

    A period between digits is a decimal ("3.5"), and one followed directly by a
    letter is usually an abbreviation or a URL — splitting there would cut a word
    in half and insert a pause inside it.
    """
    # Absorb any run of closing punctuation ("...", "?!", ".").
    j = i + 1
    while j < len(text) and (text[j] in _TERMINATORS or text[j] in _CLOSERS):
        j += 1

    if j >= len(text):
        return True  # end of input

    # Only SOME terminators can appear inside a token — '.' in 3.5 and co.za,
    # ':' in 12:30. For those, a following space is what separates a sentence end
    # from a decimal point. The rest cannot occur mid-token in any script, and
    # demanding a space after them would never split Chinese, Japanese, Khmer,
    # Thai or Burmese at all: those scripts write without spaces between words,
    # so their full stop is followed by the next letter.
    if text[i] not in _MAY_OCCUR_INSIDE_A_TOKEN:
        return True

    if not text[j].isspace():
        return False  # 3.5, e.g., co.za

    if (
        text[i] == "."
        and i > 0
        and text[i - 1].isdigit()
        and j + 1 < len(text)
        and text[j + 1].isdigit()
    ):
        return False

    return True


def _flush(segments: list[SpeechSegment], current: str, pause_ms: int) -> str:
    s = current.strip()
    if not s:
        return ""

    # The terminator STAYS in the segment text, deliberately. It is tempting to
    # strip it — this module has already turned it into a pause, and the MMS
    # voices have no token for it. But the SA-11 voice's vocabulary DOES carry
    # '?' and '.', so it can render a real question rise that no inserted silence
    # could imitate. Stripping would have discarded that from all eleven South
    # African languages to tidy up a log line.

    # A segment of nothing but punctuation has no sound to make, and the voice
    # has no token for it either.
    if not any(ch.isalpha() or ch.isdigit() for ch in s):
        return ""

    segments.append(SpeechSegment(s, pause_ms))
    return ""


def _cut_at_word_boundary(segments: list[SpeechSegment], current: str) -> str:
    """Cut an over-long run at the last space, so the break lands between words
    rather than inside one. With no space to use the run is left intact — a
    mid-word cut would be audibly worse than a long segment."""
    cut = current.rfind(" ")
    if cut <= 0:
        return current

    head = current[:cut].strip()
    if head:
        segments.append(SpeechSegment(head, _FORCED_PAUSE_MS))

    return current[cut + 1 :]


def split_sentences(text: str | None) -> list[SpeechSegment]:
    """Split ``text`` into segments. Returns a single segment when there is no
    sentence punctuation, and an empty list for blank input."""
    segments: list[SpeechSegment] = []
    if text is None or not text.strip():
        return segments

    current = ""
    pending = _SENTENCE_PAUSE_MS

    for i, c in enumerate(text):
        if c == "\r":
            continue
        if c == "\n":
            current = _flush(segments, current, _PARAGRAPH_PAUSE_MS)
            continue

        current += c

        if c in _TERMINATORS and _ends_sentence(text, i):
            current = _flush(
                segments, current,
                _CLAUSE_PAUSE_MS if c in (":", ";") else _SENTENCE_PAUSE_MS,
            )
            continue

        if len(current) >= MAX_CHARS_PER_SEGMENT:
            current = _cut_at_word_boundary(segments, current)

    _flush(segments, current, pending)

    # Nothing should follow the last word — a trailing pause is dead air.
    if segments:
        segments[-1] = SpeechSegment(segments[-1].text, 0)

    return segments


# ── LanguageSpanSplitter ────────────────────────────────────────────────────
#
# People do not speak one language per sentence. "Igama lami ngu-CircleAI" is
# isiZulu with an English name inside it, and read wholly in isiZulu the name
# comes out mangled — the listener hears the machine fail at a word they know
# perfectly well. A multi-lingual model takes ONE language id per utterance, so
# the fix is to cut the text where the language changes and synthesise each run
# under its own id.


@dataclass(frozen=True)
class LanguageSpan:
    """A run of text to be spoken in one language."""

    text: str
    """The words, with their spacing preserved."""

    is_foreign: bool
    """True when this run is the embedded language (English), false for the
    surrounding one. The caller maps that to whatever ids its model uses."""


def is_foreign_word(word: str) -> bool:
    """Is this token unmistakably foreign (English) inside African-language text?

    Two signals only, both chosen because native orthographies do not produce
    them:

        internal capitals     — CircleAI, WhatsApp, MTN's brand spellings
        all-caps, 2-5 letters — GPS, SMS, ATM, PIN

    isiZulu, isiXhosa, Sesotho and the rest capitalise the first letter of a
    sentence or a proper noun and nothing else, so neither pattern arises
    naturally. A sentence-initial capital is therefore NOT a signal, which is why
    only capitals after position zero count.

    It does NOT try to spot ordinary lowercase English words like "computer" —
    that needs a lexicon per language pair, and guessing wrong is worse than not
    guessing: mispronouncing a native word to "fix" a foreign one insults the
    speaker in their own language.
    """
    if len(word) < 2:
        return False

    upper = 0
    lower = 0
    has_internal_capital = False

    for i, c in enumerate(word):
        if not c.isalpha():
            continue
        if c.isupper():
            upper += 1
            if i > 0:
                has_internal_capital = True
        else:
            lower += 1

    if has_internal_capital and lower > 0:
        return True  # CircleAI, WhatsApp
    if upper >= 2 and lower == 0 and len(word) <= 5:
        return True  # GPS, SMS, ATM
    return False


def _is_letter_or_digit(c: str) -> bool:
    return c.isalpha() or c.isdigit()


def split_language_spans(text: str | None) -> list[LanguageSpan]:
    """Split ``text`` into spans. Returns a single span when the text is all one
    language, which is the overwhelmingly common case — callers can check
    ``len(...) == 1`` and take their existing single-language path."""
    if text is None or not text.strip():
        return []

    spans: list[LanguageSpan] = []
    current = ""
    current_is_foreign: bool | None = None

    i = 0
    while i < len(text):
        # Separators (spaces, punctuation, the hyphen in "ngu-CircleAI") ride
        # along with whatever run they FOLLOW, so a language change never strands
        # a comma on its own or splits mid-punctuation.
        if not _is_letter_or_digit(text[i]):
            sep_start = i
            while i < len(text) and not _is_letter_or_digit(text[i]):
                i += 1
            current += text[sep_start:i]
            continue

        word_start = i
        while i < len(text) and _is_letter_or_digit(text[i]):
            i += 1
        word = text[word_start:i]
        foreign = is_foreign_word(word)

        if current_is_foreign is not None and current_is_foreign != foreign:
            # The run ends at the last word, not at the separators that follow it
            # — those have already been appended and belong to the join.
            spans.append(LanguageSpan(current, current_is_foreign))
            current = ""

        current_is_foreign = foreign
        current += word

    if current and current_is_foreign is not None:
        spans.append(LanguageSpan(current, current_is_foreign))

    return spans


def to_spoken_form(text: str) -> str:
    """Rewrite a run into the form a voice can actually pronounce, without
    changing what is displayed.

    A compound like ``CircleAI`` is one token to a synthesiser and it has no idea
    where the words are, so it produces a mumble. Written ``Circle AI`` it is two
    things the voice already knows how to say. This is why the name came out
    garbled even after it was correctly switched to English — the language was
    right and the word was still unreadable.
    """
    if not text:
        return text

    # 1. Break the compound into words at case boundaries, which is where the
    #    word boundaries genuinely are in this naming style.
    spaced = ""
    for i, c in enumerate(text):
        if i > 0 and c.isupper():
            prev = text[i - 1]
            nxt = text[i + 1] if i + 1 < len(text) else ""

            # lower->Upper is a word boundary (Circle|AI, You|Tube).
            after_lower = prev.islower()
            # Upper->Upper->lower ends a run of capitals (API|Key).
            end_of_acronym = prev.isupper() and nxt != "" and nxt.islower()

            if after_lower or end_of_acronym:
                spaced += " "
        spaced += c

    # 2. Punctuate the acronyms. "AI" as a bare token gets read as a word — "ay"
    #    — where "A.I." is read as the letters, which is what it is. The full
    #    stops are for the voice, not the reader.
    out = ""
    i = 0
    while i < len(spaced):
        if not spaced[i].isupper():
            out += spaced[i]
            i += 1
            continue

        start = i
        while i < len(spaced) and spaced[i].isupper():
            i += 1
        run = spaced[start:i]

        # A lone capital is an ordinary word opening ("Sawubona"), not an
        # acronym, and a run followed by lowercase was already split above.
        if len(run) < 2:
            out += run
            continue

        for ch in run:
            out += ch + "."
    return out


# ── GeezRomanizer ───────────────────────────────────────────────────────────
#
# Ethiopic (Ge'ez) script -> Latin, because the Amharic and Tigrinya voices do
# not read Ethiopic at all. Meta ships those two MMS models with
# `is_uroman: true`: their vocabularies are 28 and 27 LATIN letters and they
# expect text already transliterated. Measured on the P30, Amharic lost 43
# distinct characters and produced 3.2 s of noise for a 15 s paragraph.
#
# The transliteration is computed, not tabulated, because Unicode lays the
# syllabary out exactly as the script is taught: each consecutive block of EIGHT
# codepoints is one consonant across its vowel orders.

_BASE = 0x1200
_ORDERS_PER_CONSONANT = 8

_LAST_SYLLABLE = 0x1357
"""Last codepoint that follows the eight-orders-per-consonant layout. The
syllabary ends here; everything above is lone syllables, marks and numerals, and
treating any of it as a row invents a pronunciation."""

_CONSONANTS = (
    "h", "l", "h", "m", "s", "r", "s", "sh",
    "q", "qw", "q", "qw", "b", "v", "t", "ch",
    "h", "hw", "n", "ny", "", "k", "kw", "k",
    "kw", "w", "", "z", "zh", "y", "d", "d",
    "j", "g", "gw", "ng", "t", "ch", "p", "ts",
    "ts", "f", "p",
)
"""Consonant per 8-codepoint row, in Unicode order. ASCII only: these voices hold
27-28 plain Latin letters, so a transliteration carrying the Ethiopist diacritics
would be dropped as surely as the Ethiopic was.

Six rows are LABIALISED — the consonant carries a built-in /w/. Writing them
plain turns "kwa" into "ka", which silently changes the word."""

_VOWELS = ("e", "u", "i", "a", "e", "", "o", "wa")
"""Vowel per order. The sixth is SILENT — it marks a bare consonant, which is why
the greeting romanises with no trailing vowel."""

_LONE_SYLLABLES = {
    "ፘ": "rya",
    "ፙ": "mya",
    "ፚ": "fya",
}
"""The three syllables Unicode assigns singly rather than as a row of eight. They
are already in the -a order, so the vowel is part of the value."""

_MARKS = frozenset("፝፞፟")
"""Combining marks. They modify the syllable before them and have no sound of
their own, so they are dropped rather than passed through — a bare mark reaching
a Latin-only vocabulary is one more unmapped symbol."""

_PUNCTUATION = {
    "፠": " ",   # section
    "፡": " ",   # word separator
    "።": ".",   # full stop
    "፣": ",",   # comma
    "፤": ";",   # semicolon
    "፥": ":",   # colon
    "፦": ":",   # preface colon
    "፧": "?",   # question mark
    "፨": " ",   # paragraph separator
}
"""Ethiopic punctuation, mapped so sentence splitting still works."""


def is_ethiopic(text: str | None) -> bool:
    """True when ``text`` contains any Ethiopic character."""
    if not text:
        return False
    return any(0x1200 <= ord(c) <= 0x139F for c in text)


def romanize(text: str | None) -> str:
    """Ethiopic -> Latin. Characters outside the script pass through untouched,
    so mixed text (numerals, Latin names, punctuation) survives intact."""
    if not text:
        return text or ""

    out: list[str] = []
    for c in text:
        p = _PUNCTUATION.get(c)
        if p is not None:
            out.append(p)
            continue

        # THE EIGHT-PER-CONSONANT LAYOUT STOPS AT U+1357, and the range check has
        # to stop with it. Beyond that the block is no longer a syllabary:
        # U+1358..U+135A are three LONE syllables already in their -a order,
        # U+135D..U+135F are combining marks, and U+1369 onward are the numerals.
        # Sizing the check off the consonant table instead swept seven of those
        # numerals back into the syllabary — and they came out as sound, so
        # nothing failed.
        if c in _MARKS:
            continue
        lone = _LONE_SYLLABLES.get(c)
        if lone is not None:
            out.append(lone)
            continue

        cp = ord(c)
        i = cp - _BASE
        if i < 0 or i > _LAST_SYLLABLE - _BASE:
            # Numerals and the rarely-used supplement blocks have no sound we can
            # render; anything else is not Ethiopic and is left alone.
            if 0x1369 <= cp <= 0x137C:
                continue
            out.append(c)
            continue

        row, order = divmod(i, _ORDERS_PER_CONSONANT)
        consonant = _CONSONANTS[row]
        vowel = _VOWELS[order]

        if not consonant:
            # The glottal and pharyngeal rows write no consonant in Latin, so the
            # vowel IS the character. First order is heard as "a", and the sixth
            # — silent after a real consonant — must still sound here, or the
            # word-initial one disappears entirely.
            if order == 0:
                vowel = "a"
            elif not vowel:
                vowel = "e"

        out.append(consonant + vowel)
    return "".join(out)


# ── ToneShaper ──────────────────────────────────────────────────────────────
#
# Warmth, after the model has finished.
#
# THE VOICE WAS REPORTED AS TINNY, AND THE SPEAKER COULD NOT FIX IT. Choosing a
# speaker by how well the recogniser understands it has a bias nobody costed:
# word error rate rewards crisp consonants and a bright top end, which is what
# "tinny" describes. Measured across all 130 speakers in the bundle, warmth and
# intelligibility are inversely related. So the speaker is not the lever. The
# waveform is, and it is entirely ours once the model hands it over.
#
# WHY A DIP AND NOT JUST A BOOST. A phone speaker cannot move enough air to
# reproduce a low-shelf boost; on a P30 the bass simply is not there to lift.
# Cutting 2-5 kHz, where harshness lives, works on hardware that cannot do bass,
# which is most of the hardware this ships to. The boost is for headphones. Both
# are applied because the product is used on both.


@dataclass(frozen=True)
class ToneShaperSettings:
    low_shelf_hz: float = 320.0
    """Where the low shelf starts lifting, in Hz."""
    low_shelf_db: float = 4.0
    """How much to lift the bottom, in dB."""
    presence_hz: float = 3200.0
    """Centre of the harshness dip, in Hz."""
    presence_db: float = -4.0
    """How much to cut there, in dB. Negative cuts."""
    presence_q: float = 0.8
    """Width of the dip. Lower is wider."""


WARM = ToneShaperSettings()
"""The measured setting: warmer, with no cost to intelligibility."""

_LOW_SHELF_SLOPE = 0.9


def _fround(v: float) -> float:
    """Round a double to the nearest float32, the way storing into a float array
    does in the reference."""
    return struct.unpack("<f", struct.pack("<f", v))[0]


def _normalise(b: list[float], a: list[float]) -> tuple[list[float], list[float]]:
    a0 = a[0]
    return [x / a0 for x in b], [x / a0 for x in a]


def low_shelf_coefficients(
    s: ToneShaperSettings, rate: int
) -> tuple[list[float], list[float]]:
    """RBJ audio-cookbook low shelf, normalised by a0."""
    amp = math.pow(10, s.low_shelf_db / 40)
    w0 = 2 * math.pi * s.low_shelf_hz / rate
    alpha = math.sin(w0) / 2 * math.sqrt((amp + 1 / amp) * (1 / _LOW_SHELF_SLOPE - 1) + 2)
    c = math.cos(w0)
    s2 = 2 * math.sqrt(amp) * alpha

    return _normalise(
        [
            amp * ((amp + 1) - (amp - 1) * c + s2),
            2 * amp * ((amp - 1) - (amp + 1) * c),
            amp * ((amp + 1) - (amp - 1) * c - s2),
        ],
        [
            (amp + 1) + (amp - 1) * c + s2,
            -2 * ((amp - 1) + (amp + 1) * c),
            (amp + 1) + (amp - 1) * c - s2,
        ],
    )


def peaking_coefficients(
    s: ToneShaperSettings, rate: int
) -> tuple[list[float], list[float]]:
    """RBJ audio-cookbook peaking EQ, normalised by a0."""
    amp = math.pow(10, s.presence_db / 40)
    w0 = 2 * math.pi * s.presence_hz / rate
    alpha = math.sin(w0) / (2 * s.presence_q)
    c = math.cos(w0)

    return _normalise(
        [1 + alpha * amp, -2 * c, 1 - alpha * amp],
        [1 + alpha / amp, -2 * c, 1 - alpha / amp],
    )


def biquad(x: list[float], b: Sequence[float], a: Sequence[float]) -> None:
    """Direct-form-I biquad, in place.

    THE STATE IS DOUBLE AND THE STORED SAMPLE IS FLOAT, and both halves matter.
    The filter memory never sees the float rounding — y1 keeps the full-precision
    result — so the recursion is identical everywhere. Only what lands in the
    buffer is narrowed, which is what the next stage then reads.
    """
    x1 = x2 = y1 = y2 = 0.0
    for i in range(len(x)):
        xn = x[i]
        yn = b[0] * xn + b[1] * x1 + b[2] * x2 - a[1] * y1 - a[2] * y2
        x2, x1 = x1, xn
        y2, y1 = y1, yn
        x[i] = _fround(yn)


def _peak(x: Sequence[float]) -> float:
    p = 0.0
    for v in x:
        a = abs(v)
        if a > p:
            p = a
    return p


def apply_tone_shaper(
    waveform: list[float], sample_rate: int, settings: ToneShaperSettings = WARM
) -> None:
    """Filter ``waveform`` in place with a low shelf and a presence dip in series.

    PEAK IS RESTORED AFTERWARDS. Lifting the low shelf adds energy, and a
    waveform that already peaked near full scale would clip — which is heard as
    crackle and would be blamed on the quantised model rather than on this.
    Scaling back to the original peak keeps the tone change audible and the level
    unchanged.
    """
    if not waveform or sample_rate <= 0:
        return

    before = _peak(waveform)
    if before <= 0:
        return  # a silent buffer, and dividing by that peak is NaN

    b, a = low_shelf_coefficients(settings, sample_rate)
    biquad(waveform, b, a)

    b, a = peaking_coefficients(settings, sample_rate)
    biquad(waveform, b, a)

    after = _peak(waveform)
    if after > 0 and after > before:
        # _fround, because the reference divides two FLOATS here. Leaving it a
        # double makes the gain a few ULP different and the whole tail of the
        # waveform drifts with it.
        g = _fround(before / after)
        for i in range(len(waveform)):
            waveform[i] = _fround(waveform[i] * g)


# ── NchltPhonemizer ─────────────────────────────────────────────────────────
#
# A fully sovereign, permissive-licence grapheme-to-phoneme front-end for the
# South African languages. NOT espeak-ng (GPLv3 taints the app), NOT phonemeza
# (unlicensed, weights unpublished), and not neural. A faithful port of the NCHLT
# pronunciation predictor (Marelie Davel, pron_predict.pl) driven by the
# NCHLT-inlang resources, © DAC / CSIR / NWU under CC BY 3.0.
#
# Because the rule set covers any word there is no "OOV gap": a word is either in
# the dictionary (exact) or synthesised by the rules, which is what makes
# agglutinative isiZulu tractable.


@dataclass(frozen=True)
class _Rule:
    """One context rule: grapheme ``g`` in left/right context -> code."""

    order: int
    left: str
    right: str
    code: str


_INT_RE = re.compile(r"^[+-]?\d+$")


class NchltPhonemizer:
    """Grapheme-to-phoneme for isiZulu, isiXhosa, Afrikaans and the other NCHLT
    languages. Pure Python — no espeak, no native library."""

    def __init__(
        self,
        dictionary: dict[str, list[str]],
        rules: dict[str, list[_Rule]],
        phone_map: dict[str, str],
        graph_map: dict[str, str],
        gnulls: list[tuple[str, str]],
    ) -> None:
        self._dict = dictionary
        self._rules = rules
        self._phone_map = phone_map
        self._graph_map = graph_map
        self._gnulls = gnulls

        self.last_rule_predicted_words = 0
        """Words in the last :meth:`phonemize` call that were synthesised by the
        rule engine rather than found in the dictionary. A coverage diagnostic,
        never a failure — the rules always produce output."""

        self.last_unknown_graphemes: list[str] = []
        """Graphemes in the last call that no rule covered. Skipped, never
        guessed."""

    @classmethod
    def from_text(
        cls,
        dict_text: str,
        rules_text: str,
        phone_map_text: str,
        graph_map_text: str | None = None,
        gnulls_text: str | None = None,
    ) -> "NchltPhonemizer":
        """Build from the file CONTENTS rather than paths, so a caller can load
        from an embedded resource or a downloaded bundle with no filesystem in
        reach."""
        return cls(
            _parse_dict(dict_text),
            _parse_rules(rules_text),
            _parse_phone_map(phone_map_text),
            _parse_graph_map(graph_map_text) if graph_map_text else {},
            _parse_gnulls(gnulls_text) if gnulls_text else [],
        )

    def phonemize(self, text: str) -> list[str]:
        self.last_rule_predicted_words = 0
        self.last_unknown_graphemes = []
        if not text or not text.strip():
            return []

        phones: list[str] = []
        for word in _tokenize(text):
            known = self._dict.get(word)
            if known is not None:
                phones.extend(known)
            else:
                phones.extend(self.predict_word(word))
                self.last_rule_predicted_words += 1
        return phones

    def predict_word(self, word: str) -> list[str]:
        """Predict a single word's X-SAMPA phones from the context rules — the
        exact algorithm of ``g2p_word_olist``: for each grapheme take the
        highest-order rule whose left/right context matches, emit its code, drop
        nulls, then remap codes to X-SAMPA."""
        if not word:
            return []

        # Grapheme remap (usually identity) then grapheme-null insertion.
        w = self._apply_gnulls(self._map_graphemes(word))

        codes: list[str] = []
        for i, g in enumerate(w):
            g_rules = self._rules.get(g)
            if g_rules is None:
                # Skip an unknown grapheme rather than fabricate a phone for it.
                if g not in self.last_unknown_graphemes:
                    self.last_unknown_graphemes.append(g)
                continue

            # pat = " " + left-context + "-" + g + "-" + right-context + " "
            pat = " " + w[:i] + "-" + g + "-" + w[i + 1 :] + " "

            # Rules are pre-sorted most-specific-first; the first match wins.
            code = "0"
            for r in g_rules:
                if (r.left + "-" + g + "-" + r.right) in pat:
                    code = r.code[0] if r.code else "0"
                    break
            if code != "0":
                codes.append(code)

        return [self._phone_map.get(c, c) for c in codes]

    def _map_graphemes(self, word: str) -> str:
        if not self._graph_map:
            return word
        return "".join(self._graph_map.get(c, c) for c in word)

    def _apply_gnulls(self, word: str) -> str:
        for frm, to in self._gnulls:
            word = word.replace(frm, to)
        return word


def _tokenize(text: str) -> Iterator[str]:
    """Lower-case and split into word tokens on anything that is not a letter.
    Diacritics are preserved (Afrikaans ê/ë/ô are real graphemes); digits and
    punctuation become separators. Number and abbreviation expansion is out of
    scope and belongs to a text-normalisation pass upstream."""
    sb: list[str] = []
    for ch in text.strip():
        if ch.isalpha():
            sb.append(ch.lower())
        elif sb:
            yield "".join(sb)
            sb = []
    if sb:
        yield "".join(sb)


def _lines(text: str) -> Iterable[str]:
    # Split the way a StreamReader does, so a CRLF file parses identically.
    return (line[:-1] if line.endswith("\r") else line for line in text.split("\n"))


def _parse_dict(text: str) -> dict[str, list[str]]:
    out: dict[str, list[str]] = {}
    for line in _lines(text):
        if not line:
            continue
        tab = line.find("\t")
        if tab <= 0:
            continue
        word = line[:tab]
        pron = line[tab + 1 :].strip()
        if not pron or word in out:
            continue  # keep the FIRST variant
        out[word] = [p for p in pron.split(" ") if p]
    return out


def _parse_rules(text: str) -> dict[str, list[_Rule]]:
    by_grapheme: dict[str, list[_Rule]] = {}
    for line in _lines(text):
        if not line:
            continue
        # grapheme ; left ; right ; code ; order [ ; count ]
        f = line.split(";")
        if len(f) < 5 or not f[0]:
            continue
        if not _INT_RE.match(f[4].strip()):
            continue
        by_grapheme.setdefault(f[0][0], []).append(
            _Rule(int(f[4].strip()), f[1], f[2], f[3])
        )

    # STABLE sort, descending by order. Two rules of equal order must stay in
    # file order — the reference uses LINQ's OrderByDescending, which is stable,
    # and a port that reaches for an unstable sort will disagree on ties in
    # exactly the dense rule sets where ties are common. Python's sort is stable,
    # so sorting on the negated key preserves file order within an order.
    for lst in by_grapheme.values():
        lst.sort(key=lambda r: -r.order)
    return by_grapheme


def _parse_phone_map(text: str) -> dict[str, str]:
    # Line: "<code>\t<xsampa>"  (code is a single char).
    out: dict[str, str] = {}
    for line in _lines(text):
        if not line:
            continue
        tab = line.find("\t")
        if tab <= 0:
            continue
        code = line[:tab]
        if len(code) == 1:
            out[code] = line[tab + 1 :]
    return out


def _parse_graph_map(text: str) -> dict[str, str]:
    # File line: "<funny>\t<std>" — we map std->funny (per remap_dict's gmap).
    out: dict[str, str] = {}
    for line in _lines(text):
        if not line:
            continue
        f = line.split("\t")
        if len(f) == 2 and len(f[0]) == 1 and len(f[1]) == 1 and f[0] != f[1]:
            out[f[1]] = f[0]
    return out


def _parse_gnulls(text: str) -> list[tuple[str, str]]:
    # File line: "<from>;<to>" — insert grapheme-nulls (empty for Nguni).
    out: list[tuple[str, str]] = []
    for line in _lines(text):
        if not line:
            continue
        f = line.split(";")
        if len(f) == 2:
            out.append((f[0], f[1]))
    return out
