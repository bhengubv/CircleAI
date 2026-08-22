"""voice_xsampa.py

Port of src/CircleAI.Voice/XsampaToIpa.cs and SentencePieceUnigram.cs.

Parity is asserted against fixtures/voice_xsampa_to_ipa.json and
fixtures/voice_sentencepiece_unigram.json, which the C# reference generates. If
this module and those files disagree, one of them is wrong and the test names
the case.
"""
from __future__ import annotations

import json
import unicodedata
from pathlib import Path

# Every phone in the NCHLT Afrikaans dictionary, mapped to IPA.
#
# Derived from the corpus, not from memory: exactly the distinct phones in
# nchlt_afr.dict, with every IPA character checked against the target voice's
# own token table before the table was written.
_XSAMPA_TO_IPA: dict[str, str] = {
    # Vowels
    "a": "a", "A:": "ɑː", "A:r": "ɑːr",
    "E": "ɛ", "O": "ɔ", "@": "ə",
    "i": "i", "u": "u", "y": "y",
    "9": "œ", "2:": "øː", "{": "æ",

    # Diphthongs — NCHLT gives one token, the voice wants both elements.
    "9y": "œy", "@i": "əi", "@u": "əu",
    "i@": "iə", "u@": "uə",

    # Consonants
    "b": "b", "d": "d", "f": "f",
    # U+0261 LATIN SMALL LETTER SCRIPT G — the IPA letter, NOT ASCII 'g'. The
    # voice's vocabulary carries ɡ; a plain 'g' would miss and be dropped.
    "g": "ɡ",
    "j": "j", "k": "k", "l": "l",
    "m": "m", "n": "n", "N": "ŋ",
    "p": "p", "r": "r", "s": "s",
    "S": "ʃ", "t": "t", "v": "v",
    "w": "w", "x": "x", "z": "z",
    "Z": "ʒ",

    # APPROXIMATION, DELIBERATE AND THE ONLY ONE. X-SAMPA h\\ is ɦ, the voiced
    # glottal fricative Afrikaans uses in "hond". This voice's vocabulary has no
    # ɦ, only h. Voicing is lost; place and manner are right, so the word stays
    # recognisable.
    "h\\": "h",
}


def xsampa_to_ipa(xsampa: list[str]) -> tuple[list[str], list[str]]:
    """Convert X-SAMPA phone tokens to a flat IPA symbol list.

    Returns ``(ipa, unmapped)``. The misses are returned rather than stashed
    away because an unmapped phone produces NO SOUND and the audio is merely
    shorter — every acoustic measure still passes. A caller that cannot see the
    misses cannot refuse.

    LONGEST MATCH ON WHOLE TOKENS. Several entries are multi-character (``A:r``,
    ``@i``, ``9y``) and NCHLT emits them as single tokens; matching on the token
    — never character by character — is what keeps ``A:r`` from becoming
    ``A`` + ``:`` + ``r``.
    """
    ipa: list[str] = []
    unmapped: list[str] = []

    for phone in xsampa:
        if not phone.strip():
            continue
        mapped = _XSAMPA_TO_IPA.get(phone)
        if mapped is not None:
            # Per-character: the voice tokenises ɑ, ː and r separately, so "ɑːr"
            # must arrive as three symbols, not one.
            ipa.extend(mapped)
            continue
        if phone not in unmapped:
            unmapped.append(phone)

    return ipa, unmapped


def xsampa_can_say_all(xsampa: list[str]) -> bool:
    """True when every phone in *xsampa* has a mapping."""
    return all(p in _XSAMPA_TO_IPA for p in xsampa if p.strip())


def xsampa_known_phones() -> list[str]:
    """The X-SAMPA phones this table knows — for tests and diagnostics."""
    return list(_XSAMPA_TO_IPA)


# ---------------------------------------------------------------------------
# SentencePiece unigram
# ---------------------------------------------------------------------------

# Cost charged for falling back to raw bytes.
#
# Any finite penalty works, because fallback only ever competes with "no path at
# all". It must be worse than a real piece so the lattice never prefers it where
# a piece exists, and finite so a path always exists.
_FALLBACK_PENALTY = 10.0


class SentencePieceUnigram:
    """SentencePiece unigram tokeniser — Viterbi over the piece lattice."""

    def __init__(self, ids: dict[str, int], scores: dict[str, float]) -> None:
        self._ids = ids
        self._scores = scores
        self._max_piece_length = max((len(k) for k in ids), default=1)

    @classmethod
    def load(cls, vocab_path: str | Path, scores_path: str | Path) -> "SentencePieceUnigram":
        """Load from a bundle's ``vocab.json`` and ``token_scores.json``."""
        ids = json.loads(Path(vocab_path).read_text(encoding="utf-8"))
        scores = json.loads(Path(scores_path).read_text(encoding="utf-8"))
        if not ids:
            raise ValueError(f"{vocab_path} is empty")
        return cls(ids, scores)

    @property
    def count(self) -> int:
        return len(self._ids)

    def encode(self, text: str) -> list[int]:
        """Encode text to token ids.

        VITERBI, NOT GREEDY LONGEST-MATCH. Unigram scores are not monotone in
        piece length — a long piece can score worse than the two short pieces
        covering the same span — so greedy silently produces
        plausible-but-wrong segmentations.
        """
        if not text:
            return []

        # SentencePiece's own normalisation: NFKC, then spaces become U+2581,
        # with one prepended so the first word is marked word-initial too.
        normalised = "▁" + unicodedata.normalize("NFKC", text).replace(" ", "▁")

        # Python strings index by code point already, so a piece boundary cannot
        # land inside a surrogate pair the way it can in UTF-16 languages.
        chars = list(normalised)
        n = len(chars)

        unreachable = -1e18
        best = [unreachable] * (n + 1)
        from_index = [0] * (n + 1)
        piece: list[str | None] = [None] * (n + 1)
        has_piece = [False] * (n + 1)
        best[0] = 0.0

        for i in range(n):
            if best[i] <= unreachable / 2:
                continue

            limit = min(self._max_piece_length, n - i)
            for length in range(1, limit + 1):
                candidate = "".join(chars[i:i + length])
                if candidate not in self._ids:
                    continue
                score = best[i] + self._scores.get(candidate, 0.0)
                if score > best[i + length]:
                    best[i + length] = score
                    from_index[i + length] = i
                    piece[i + length] = candidate
                    has_piece[i + length] = True

            # Byte fallback for this ONE character, so no input is ever silent.
            end = i + 1
            fallback = best[i] - _FALLBACK_PENALTY
            if fallback > best[end]:
                best[end] = fallback
                from_index[end] = i
                has_piece[end] = False

        reversed_ids: list[int] = []
        i = n
        while i > 0:
            start = from_index[i]
            if has_piece[i] and piece[i] is not None:
                reversed_ids.append(self._ids[piece[i]])
            else:
                # BACKWARDS, because this whole list is built backwards. The
                # lattice is walked from the end and reversed once at the
                # bottom, so a multi-byte character appended in forward order
                # comes out byte-reversed: é is UTF-8 C3 A9 and would be emitted
                # A9 C3. Nothing raises — those are real pieces with real ids —
                # so the model simply says a different character.
                raw = "".join(chars[start:i]).encode("utf-8")
                for b in reversed(raw):
                    key = f"<0x{b:02X}>"
                    if key in self._ids:
                        reversed_ids.append(self._ids[key])
            i = start

        reversed_ids.reverse()
        return reversed_ids
