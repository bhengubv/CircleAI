"""voice_piper.py

Ports of src/CircleAI.Voice/PiperVoiceConfig.cs, LexiconTokeniser.cs and
AudioFormat.cs.

Parity is asserted against fixtures/voice_piper_config.json,
fixtures/voice_lexicon_tokeniser.json and fixtures/voice_audio_format.json.
"""
from __future__ import annotations

import unicodedata
from dataclasses import dataclass, field

# Piper's special phoneme symbols (piper-phonemize defaults).
_PAD = "_"
_BOS = "^"
_EOS = "$"


@dataclass(frozen=True)
class AudioFormat:
    """A PCM audio format expected or produced by voice components."""

    sample_rate: int
    channels: int
    bits_per_sample: int


#: Canonical input format: PCM signed 16-bit, mono, 16 kHz. Most open-source ASR
#: engines (sherpa-onnx, Vosk) accept this directly.
PCM16_MONO_16K = AudioFormat(sample_rate=16000, channels=1, bits_per_sample=16)


@dataclass(frozen=True)
class PhonemeMapping:
    """What a :meth:`PiperVoiceConfig.phonemes_to_ids` call did, beyond the ids."""

    ids: list[int]
    #: How many symbols the vocabulary had no entry for.
    skipped: int
    #: WHICH symbols were dropped. A dropped symbol is inaudible, so this list is
    #: the only evidence a front-end is broken.
    skipped_symbols: list[str] = field(default_factory=list)
    #: Symbols APPROXIMATED rather than spoken exactly — a diacritic the voice
    #: lacks, folded to its base letter. A compromise, not a success.
    approximated_symbols: list[str] = field(default_factory=list)


class PiperVoiceConfig:
    """A Piper-layout voice's phoneme→id vocabulary and inference settings."""

    def __init__(
        self,
        phoneme_id_map: dict[str, list[int]],
        sample_rate: int = 22050,
        noise_scale: float = 0.667,
        length_scale: float = 1.0,
        noise_w: float = 0.8,
        phoneme_type: str = "espeak",
    ) -> None:
        self._map = phoneme_id_map
        self.sample_rate = sample_rate
        self.noise_scale = noise_scale
        self.length_scale = length_scale
        self.noise_w = noise_w
        #: e.g. ``espeak`` (needs a phonemizer) or ``text`` (graphemes are phonemes).
        self.phoneme_type = phoneme_type

    @classmethod
    def parse(cls, root: dict) -> "PiperVoiceConfig":
        """Parse a Piper ``.onnx.json`` sidecar."""
        inference = root.get("inference") or {}
        raw_map = root.get("phoneme_id_map") or {}
        return cls(
            {k: list(v) for k, v in raw_map.items() if isinstance(v, list)},
            sample_rate=(root.get("audio") or {}).get("sample_rate", 22050),
            noise_scale=inference.get("noise_scale", 0.667),
            length_scale=inference.get("length_scale", 1.0),
            noise_w=inference.get("noise_w", 0.8),
            phoneme_type=root.get("phoneme_type", "espeak"),
        )

    @property
    def has_phoneme_map(self) -> bool:
        """True when this config has a usable phoneme→id map."""
        return len(self._map) > 0

    @property
    def pad_id(self) -> int:
        """THE PAD RULE: the id THIS voice uses for blank.

        It is 0 in sherpa/MMS exports and 3 in Piper-family ones, and pointing
        it at an ordinary vocabulary entry is what made 42 MMS voices speak
        fluent nonsense. Never assume a constant — read it from the model. Falls
        back to 0 only when the vocabulary has no ``_`` at all.
        """
        p = self._map.get(_PAD)
        return p[0] if p else 0

    def phonemes_to_ids(self, phonemes: list[str]) -> PhonemeMapping:
        """Turn a phoneme sequence into model token ids.

        piper-phonemize's exact layout with interspersed pad::

            [BOS, PAD, id(p1), PAD, id(p2), PAD, ..., id(pN), PAD, EOS]

        BOS and EOS appear only when the vocabulary HAS them — the MMS-family
        exports do not. Unknown symbols are SKIPPED and REPORTED, never fatal: a
        single unknown symbol must not abort the whole utterance.
        """
        ids: list[int] = []
        dropped: list[str] = []
        approximated: list[str] = []
        skipped = 0

        bos = self._map.get(_BOS)
        if bos:
            ids.extend(bos)
        pad = self._map.get(_PAD)
        if pad:
            ids.extend(pad)

        for phoneme in phonemes:
            mapped = self._map_symbol(phoneme)
            if mapped is None:
                skipped += 1
                if phoneme not in dropped:
                    dropped.append(phoneme)
                continue
            seq, was_approx = mapped
            if was_approx and phoneme not in approximated:
                approximated.append(phoneme)
            ids.extend(seq)
            if pad:
                ids.extend(pad)

        eos = self._map.get(_EOS)
        if eos:
            ids.extend(eos)

        return PhonemeMapping(ids, skipped, dropped, approximated)

    def _map_symbol(self, symbol: str) -> tuple[list[int], bool] | None:
        exact = self._map.get(symbol)
        if exact is not None:
            return exact, False

        # A grapheme voice's vocabulary is built AFTER the training text has been
        # through the model's own cleaner, and every cleaner in use here
        # lower-cases. Such a vocab contains no capitals at all, so matching on
        # the raw character silently discarded every sentence-initial letter —
        # the model received "awubona" for "Sawubona".
        lower = symbol.lower()
        if lower != symbol:
            l = self._map.get(lower)
            if l is not None:
                return l, False

        # A GRAPHEME CLUSTER the vocabulary stores as separate codepoints.
        # Burmese "ကြို" arrives as ONE symbol while the vocabulary holds each
        # codepoint on its own. Splitting it back keeps every mark, so this must
        # be tried BEFORE any approximation.
        if len(symbol) > 1:
            parts: list[int] = []
            whole = True
            for ch in symbol:
                # Zero-width formatting characters shape how text is DRAWN and
                # say nothing about how it sounds. Persian writes them
                # constantly, as do most Indic scripts, and one invisible
                # character was failing the whole cluster.
                if unicodedata.category(ch) == "Cf":
                    continue
                part = self._map.get(ch) or self._map.get(ch.lower())
                if part is None:
                    whole = False
                    break
                parts.extend(part)
            if whole and parts:
                return parts, False  # exact — nothing was lost

        # A letter the voice never learned. Dropping it deletes a consonant from
        # the middle of a word, so an approximation is worth more than a hole —
        # so long as it is declared rather than passed off as correct.
        for candidate in _approximations(symbol):
            a = self._map.get(candidate) or self._map.get(candidate.lower())
            if a is not None:
                return a, True

        return None


def split_phoneme_string(s: str) -> list[str]:
    """Split into grapheme clusters: a base character plus any combining marks
    that follow it, so "bát" is three elements and not four."""
    out: list[str] = []
    cur = ""
    for ch in s:
        if cur and _is_combining_mark(ch):
            cur += ch
            continue
        if cur:
            out.append(cur)
        cur = ch
    if cur:
        out.append(cur)
    return out


def _approximations(symbol: str) -> list[str]:
    out: list[str] = []

    # Where the vocabulary carries the true phoneme under a different spelling,
    # use it — Tshivenda's ṅ IS /ŋ/, so that substitution loses nothing at all.
    if symbol in ("ṅ", "Ṅ"):
        out.append("ŋ")
    if symbol in ("š", "Š"):
        out.append("ʃ")

    # Folding a diacritic away is only defensible where the mark modifies a
    # letter that still carries most of the sound without it — Latin š→s, ṱ→t.
    # In Thai, Burmese, Devanagari, Arabic and Vietnamese the marks ARE the
    # vowels and tones; dropping them does not approximate the word, it deletes
    # it. Thai measured 4.3 s instead of ~15 s because every vowel sign was
    # folded off a consonant and filed as a harmless approximation.
    stripped = _strip_diacritics(symbol)
    if not stripped or stripped == symbol or not _is_latin_base(stripped):
        return out
    out.append(stripped)
    return out


def _is_latin_base(stripped: str) -> bool:
    """Judge the BASE that remains, not the composed character.

    Tshivenda ṱ lives at U+1E71, far above the Latin block, yet strips to a plain
    't'. Thai วั strips to ว, which is not Latin at all — the case to refuse.
    """
    return bool(stripped) and all(ord(c) <= 0x024F for c in stripped)


def _strip_diacritics(s: str) -> str:
    """Decompose and remove combining marks: ṱ → t."""
    return "".join(c for c in unicodedata.normalize("NFD", s) if not _is_combining_mark(c))


def _is_combining_mark(ch: str) -> bool:
    return unicodedata.category(ch) in ("Mn", "Mc", "Me")


# ---------------------------------------------------------------------------
# LexiconTokeniser
# ---------------------------------------------------------------------------


class LexiconTokeniser:
    """Turns text into model tokens using a voice's own lexicon files.

    Pronunciation as a FILE, which is what makes these voices shippable: a
    word→phoneme table and a phoneme→id table beside the model. No phonemizer
    process, no second package, no licence wall.
    """

    def __init__(self, words: dict[str, list[int]], blank: int = 0) -> None:
        self._words = words
        self._longest = max((len(w) for w in words), default=1)
        #: Blank id, interleaved between tokens when the model expects it.
        self.blank = blank
        #: Symbols the lexicon had no entry for on the last call.
        self.last_unmapped: list[str] = []

    @classmethod
    def from_text(
        cls, tokens_text: str, lexicon_text: str, blank: int = 0
    ) -> "LexiconTokeniser | None":
        """Build from a voice's ``tokens.txt`` and ``lexicon.txt`` content."""
        # tokens.txt is "<symbol> <id>" per line. The symbol MAY BE A SPACE, so
        # split on the LAST space rather than the first.
        ids: dict[str, int] = {}
        for raw in tokens_text.split("\n"):
            line = raw.rstrip("\r")
            cut = line.rfind(" ")
            if cut <= 0:
                continue
            try:
                ids[line[:cut]] = int(line[cut + 1 :])
            except ValueError:
                continue
        if not ids:
            return None

        # lexicon.txt is "<word> <phoneme> <phoneme> ...".
        words: dict[str, list[int]] = {}
        for raw in lexicon_text.split("\n"):
            parts = raw.rstrip("\r").split()
            if len(parts) < 2:
                continue
            seq = [ids[p] for p in parts[1:] if p in ids]
            if not seq:
                continue
            words[parts[0]] = seq
        return cls(words, blank) if words else None

    def encode(self, text: str, interleave_blank: bool = True) -> list[int]:
        """Segment *text* and return the model's tokens.

        LONGEST MATCH FIRST, because these lexicons are word-keyed and the words
        overlap: あい, あいさつ and あいかわらず all start the same way, and taking
        the shortest would pronounce a different word. Falls back to the single
        character when no word matches.
        """
        out: list[int] = []
        unmapped: list[str] = []

        i = 0
        while i < len(text):
            taken = 0
            longest = min(self._longest, len(text) - i)
            for length in range(longest, 0, -1):
                seq = self._words.get(text[i : i + length])
                if seq is not None:
                    out.extend(seq)
                    taken = length
                    break
            if taken == 0:
                if not text[i].isspace():
                    unmapped.append(text[i])
                taken = 1
            i += taken

        self.last_unmapped = unmapped
        if not interleave_blank:
            return out

        # add_blank: a blank opens the utterance and follows every token.
        padded = [self.blank]
        for token in out:
            padded.append(token)
            padded.append(self.blank)
        return padded
