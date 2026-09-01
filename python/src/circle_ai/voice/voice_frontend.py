"""The voice front end: sound into features, wake detection, text into phonemes.

Everything here is arithmetic and text. The parts that need a model behind them
— whisper, the ONNX engines, espeak's native library — are seams, and the
DECISIONS around them are ported even where the binding is not: which engine a
bundle is, how a wake candidate is confirmed, what a blank pad token means.

THE PAD RULE, because it has cost more time than anything else in this module:
a blank pad token means the MODEL's blank, not the literal "_". MMS pads with
id 0 and Piper with id 3, and getting it wrong produces audio that is silent or
a burst of noise — never an error, and never anything a log mentions.
"""

from __future__ import annotations

import cmath
import math
import re
import threading
import unicodedata
from abc import ABC, abstractmethod
from dataclasses import dataclass, field
from datetime import datetime, timezone
from enum import Enum
from typing import Callable, Iterable, Sequence

# ─────────────────────────────────────────────────────────────────────────────
# Kaldi filterbank


@dataclass(frozen=True)
class KaldiFbankOptions:
    """Frame and mel settings.

    These match Kaldi's defaults exactly, and the exactness is the point: the
    models consuming these features were trained on Kaldi's output, and a
    filterbank that is close but not identical produces features the model has
    never seen. It does not error — it just recognises nothing.
    """

    sample_rate_hz: int = 16000
    frame_length_ms: float = 25.0
    frame_shift_ms: float = 10.0
    num_mel_bins: int = 80
    low_freq: float = 20.0
    #: -400 means NYQUIST MINUS 400, not 400 Hz and not an error. Kaldi treats a
    #: negative high_freq as an offset from nyquist, and a reader that clamps it
    #: to zero silently builds a filterbank over the wrong band.
    high_freq: float = -400.0
    dither: float = 0.0
    pre_emphasis: float = 0.97
    remove_dc_offset: bool = True
    #: When False, frames are CENTRED and the signal is MIRRORED at the edges.
    #: Kaldi's snip_edges=True drops the partial frames instead, which shifts
    #: every frame index by half a window — enough to move a wake word out of
    #: its detection span.
    snip_edges: bool = False


#: The floor applied before taking a log.
#:
#: float32 epsilon (about 1.19e-7), NOT the smallest denormal. Kaldi uses
#: epsilon, and a floor several orders of magnitude lower produces large
#: negative values in silent bins that a model reads as structure.
LOG_FLOOR = 1.1920928955078125e-07


def _povey_window(n: int) -> list[float]:
    """(0.5 - 0.5*cos)^0.85.

    Not Hamming and not Hann. The 0.85 exponent is Kaldi's and it is the
    difference between features a model recognises and features it does not.
    """
    return [(0.5 - 0.5 * math.cos(2 * math.pi * i / (n - 1))) ** 0.85 for i in range(n)]


def _mel(hz: float) -> float:
    return 1127.0 * math.log(1.0 + hz / 700.0)


def _inv_mel(m: float) -> float:
    return 700.0 * (math.exp(m / 1127.0) - 1.0)


def _fft(values: Sequence[complex]) -> list[complex]:
    """Radix-2 Cooley-Tukey. len(values) must be a power of two."""
    n = len(values)
    if n == 1:
        return list(values)
    even = _fft(values[0::2])
    odd = _fft(values[1::2])
    out = [0j] * n
    for k in range(n // 2):
        t = cmath.exp(-2j * math.pi * k / n) * odd[k]
        out[k] = even[k] + t
        out[k + n // 2] = even[k] - t
    return out


class KaldiFbank:
    """Log-mel filterbank features."""

    def __init__(self, options: KaldiFbankOptions | None = None) -> None:
        self.options = options or KaldiFbankOptions()
        frame_len = int(self.options.sample_rate_hz * self.options.frame_length_ms / 1000)
        self._fft_size = 1
        while self._fft_size < frame_len:
            self._fft_size *= 2
        self._window = _povey_window(frame_len)
        self._mel_bank = self._build_mel_bank()

    def _build_mel_bank(self) -> list[list[float]]:
        o = self.options
        nyquist = o.sample_rate_hz / 2
        # Negative is an OFFSET FROM NYQUIST. -400 at 16 kHz means 7600 Hz.
        high = nyquist + o.high_freq if o.high_freq <= 0 else o.high_freq
        bins = self._fft_size // 2 + 1
        mel_low, mel_high = _mel(o.low_freq), _mel(high)
        step = (mel_high - mel_low) / (o.num_mel_bins + 1)

        bank: list[list[float]] = []
        for m in range(o.num_mel_bins):
            left = _inv_mel(mel_low + m * step)
            centre = _inv_mel(mel_low + (m + 1) * step)
            right = _inv_mel(mel_low + (m + 2) * step)
            row = [0.0] * bins
            for k in range(bins):
                hz = k * o.sample_rate_hz / self._fft_size
                if left <= hz <= centre and centre > left:
                    row[k] = (hz - left) / (centre - left)
                elif centre < hz <= right and right > centre:
                    row[k] = (right - hz) / (right - centre)
            bank.append(row)
        return bank

    def compute(self, samples: Sequence[float]) -> list[list[float]]:
        """Log-mel features, one row per frame.

        Order matters and is Kaldi's: remove DC, then pre-emphasise, then
        window. Pre-emphasising before removing DC leaves an offset the
        high-pass then amplifies.
        """
        o = self.options
        frame_len = len(self._window)
        shift = int(o.sample_rate_hz * o.frame_shift_ms / 1000)
        if not samples or frame_len == 0 or shift == 0:
            return []

        src = list(samples)
        if not o.snip_edges:
            # Centre the frames: mirror half a window at each end so the first
            # frame is centred on sample zero rather than starting there.
            pad = frame_len // 2
            head = [samples[min(i, len(samples) - 1)] for i in range(pad, 0, -1)]
            tail = [samples[max(len(samples) - 1 - i, 0)] for i in range(1, pad + 1)]
            src = head + list(samples) + tail

        out: list[list[float]] = []
        for start in range(0, len(src) - frame_len + 1, shift):
            frame = list(src[start:start + frame_len])
            if o.remove_dc_offset:
                mean = sum(frame) / frame_len
                frame = [v - mean for v in frame]
            if o.pre_emphasis > 0:
                for i in range(frame_len - 1, 0, -1):
                    frame[i] -= o.pre_emphasis * frame[i - 1]
                frame[0] -= o.pre_emphasis * frame[0]
            frame = [v * w for v, w in zip(frame, self._window)]

            padded = frame + [0.0] * (self._fft_size - frame_len)
            spectrum = _fft([complex(v) for v in padded])
            power = [abs(spectrum[k]) ** 2 for k in range(self._fft_size // 2 + 1)]

            row = []
            for filt in self._mel_bank:
                e = sum(w * power[k] for k, w in enumerate(filt) if w and k < len(power))
                row.append(math.log(max(e, LOG_FLOOR)))
            out.append(row)
        return out


# ─────────────────────────────────────────────────────────────────────────────
# Wake confirmation


@dataclass(frozen=True)
class WakeCandidate:
    """A possible wake, before it has been confirmed."""

    keyword: str
    score: float
    at: datetime
    #: Audio around the candidate, so a second stage can look at it. Without
    #: this the confirmer would have to ask for the audio again, by which time
    #: the ring buffer has moved on.
    audio: bytes = b""
    sample_rate_hz: int = 16000


class IWakeConfirmer(ABC):
    """The second stage: something that decides whether a candidate was really
    the wake word.

    Two stages because the cheap detector has to run constantly and therefore
    has to be permissive. A single-stage detector tuned tight enough to avoid
    false wakes misses the real ones; tuned loose enough to catch them it fires
    at the television.
    """

    @abstractmethod
    def confirm(self, candidate: WakeCandidate) -> tuple[bool, str]: ...


class AlwaysConfirm(IWakeConfirmer):
    """Accepts every candidate.

    For a host that has decided the first stage is enough — a push-to-talk
    device, a test. NAMED so that choosing it is visible rather than looking
    like a missing confirmer.
    """

    def confirm(self, candidate: WakeCandidate) -> tuple[bool, str]:
        return True, "no second stage configured"


def _frame_has_speech(pcm: bytes, threshold: float = 0.015) -> bool:
    if len(pcm) < 2:
        return False
    total = 0.0
    n = len(pcm) // 2
    for i in range(n):
        v = int.from_bytes(pcm[i * 2:i * 2 + 2], "little", signed=True) / 32768
        total += v * v
    return math.sqrt(total / n) >= threshold


class UtteranceOnsetConfirmer(IWakeConfirmer):
    """Accepts only when the candidate sits at the START of an utterance.

    The single most effective filter there is, and it needs no model: people say
    a wake word first. A match in the middle of a sentence is almost always the
    television, a passing conversation, or the assistant's own audio.
    """

    def __init__(self, max_lead_in_ms: float = 320.0) -> None:
        self.max_lead_in_ms = max_lead_in_ms

    def confirm(self, candidate: WakeCandidate) -> tuple[bool, str]:
        if len(candidate.audio) < 2 or candidate.sample_rate_hz <= 0:
            return False, "no audio to inspect"
        lead = min(
            int(candidate.sample_rate_hz * self.max_lead_in_ms / 1000) * 2,
            len(candidate.audio),
        )
        if _frame_has_speech(candidate.audio[:lead]):
            return False, "speech before the keyword: not the start of an utterance"
        return True, "at the start of an utterance"


class TranscriptConfirmer(IWakeConfirmer):
    """Asks a transcriber what was actually said.

    More accurate and much slower, so it runs only on a candidate the first
    stage already liked. On a device with no transcriber it is UNAVAILABLE
    rather than permissive — the fallback for "cannot check" is not "assume
    yes".
    """

    def __init__(self, transcribe: Callable[[bytes, int], str] | None = None) -> None:
        self._transcribe = transcribe

    def confirm(self, candidate: WakeCandidate) -> tuple[bool, str]:
        if self._transcribe is None:
            return False, "no transcriber available"
        try:
            text = self._transcribe(candidate.audio, candidate.sample_rate_hz)
        except Exception:
            return False, "transcription failed"
        if candidate.keyword.lower() in text.lower():
            return True, "the transcript contains the keyword"
        return False, "the transcript does not contain the keyword"


class EitherConfirmer(IWakeConfirmer):
    """Accepts when EITHER of two confirmers does.

    Or, not and. The two stages catch different failures — onset catches the
    television, the transcript catches a similar-sounding word — and requiring
    both means a device with no transcriber can never wake at all.
    """

    def __init__(self, first: IWakeConfirmer, second: IWakeConfirmer) -> None:
        self.first, self.second = first, second

    def confirm(self, candidate: WakeCandidate) -> tuple[bool, str]:
        for stage in (self.first, self.second):
            if stage is None:
                continue
            ok, why = stage.confirm(candidate)
            if ok:
                return True, why
        return False, "neither stage confirmed"


class ConfirmedKeywordSpotter:
    """A first-stage spotter with a confirmer behind it."""

    def __init__(self, confirmer: IWakeConfirmer | None = None) -> None:
        self._confirmer = confirmer or AlwaysConfirm()
        self._lock = threading.Lock()
        self._accepted = 0
        self._rejected = 0

    def offer(self, candidate: WakeCandidate) -> tuple[bool, str]:
        ok, why = self._confirmer.confirm(candidate)
        with self._lock:
            if ok:
                self._accepted += 1
            else:
                self._rejected += 1
        return ok, why

    @property
    def counts(self) -> tuple[int, int]:
        """Accepted and rejected.

        The REJECTED count is the useful one: it is the only evidence that the
        second stage is doing anything.
        """
        with self._lock:
            return self._accepted, self._rejected


# ─────────────────────────────────────────────────────────────────────────────
# Wake engines and calibration


class WakeEngine(Enum):
    """Which kind of wake bundle this is."""

    #: Three graphs, keywords are TEXT, so a phrase can be changed without
    #: training anything.
    ZIPFORMER_TRANSDUCER = "zipformer-transducer"
    #: One trained phrase and no other.
    SINGLE_GRAPH_CLASSIFIER = "single-graph-classifier"


@dataclass(frozen=True)
class WakeHostCapabilities:
    """What the device running this can do.

    Both fields decide which engine and which confirmer are viable, and a wrong
    RAM figure here picks an engine the phone cannot load.
    """

    total_ram_bytes: int
    transcriber_available: bool


@dataclass(frozen=True)
class WakeCalibration:
    """Per-device wake tuning that survives a restart.

    The thresholds were compile-time constants, which is a claim that every
    phone, room and voice behaves like the ones they were measured on. They do
    not: the same phrase read 0.42 on one synthetic voice and 0.94 on another.
    Persisting per device lets a phone that consistently under-scores be nudged
    ONCE, instead of the default being loosened for everybody — which is how a
    wake word starts firing on the television.

    None means unset: use the phrase or engine default.
    """

    threshold: float | None = None
    max_lead_in_ms: float | None = None
    wakes: int = 0


@dataclass(frozen=True)
class WakeLanguageChoice:
    """The model to use for a language."""

    #: None means no model at all.
    model_name: str | None
    is_native: bool
    #: Plain language, and EMPTY when native. A note on every choice trains
    #: people to ignore notes.
    note: str = ""


class WakeLanguages:
    """Which wake model serves which language."""

    _NATIVE = frozenset({"en", "eng"})
    _CROSS_LINGUAL = frozenset({"zu", "zul", "xh", "xho", "st", "sot", "tn", "tsn"})

    @classmethod
    def for_language(cls, iso_language: str) -> WakeLanguageChoice:
        iso = iso_language.lower()
        if iso in cls._NATIVE:
            return WakeLanguageChoice("wake-en", True)
        if iso in cls._CROSS_LINGUAL:
            # A cross-lingual model rather than none. STATED, because somebody
            # choosing a phrase in isiZulu should know it is being matched by a
            # model that was not trained on it — the phrase will need to be
            # longer.
            return WakeLanguageChoice(
                "wake-multilingual", False,
                "no native wake model for this language yet; a cross-lingual one "
                "is used, so pick a longer phrase",
            )
        return WakeLanguageChoice(None, False, "no wake model for this language")


class WakeWordFactory:
    """Builds the right detector for a bundle and a device."""

    @staticmethod
    def engine_for(bundle_directory: str) -> WakeEngine:
        """Which engine a bundle on disk actually is.

        DETECTED rather than configured: a bundle and a setting that disagree
        fail at the first utterance, with a shape error nobody can read.
        """
        if "zipformer" in bundle_directory:
            return WakeEngine.ZIPFORMER_TRANSDUCER
        return WakeEngine.SINGLE_GRAPH_CLASSIFIER

    @staticmethod
    def confirmer_for(
        host: WakeHostCapabilities,
        transcribe: Callable[[bytes, int], str] | None = None,
    ) -> IWakeConfirmer:
        """Picks the second stage the device can actually run.

        A transcript confirmer on a device with no transcriber would be a
        confirmer that always says no, which is worse than the onset one: the
        device would never wake at all.
        """
        onset = UtteranceOnsetConfirmer()
        if not host.transcriber_available or transcribe is None:
            return onset
        return EitherConfirmer(onset, TranscriptConfirmer(transcribe))


# ─────────────────────────────────────────────────────────────────────────────
# Keyword spotting


class KwsInputKind(Enum):
    """What the spotter consumes."""

    #: Log-mel features, computed here.
    FBANK = "fbank"
    #: Raw samples; the model does its own front end.
    WAVEFORM = "waveform"


@dataclass(frozen=True)
class KwsKeyword:
    """One phrase the spotter looks for."""

    text: str
    token_ids: tuple[int, ...] = ()
    #: None for the spotter's default.
    threshold: float | None = None
    boost: float | None = None


@dataclass(frozen=True)
class KwsConfig:
    """How a spotter is set up."""

    bundle_directory: str
    input_kind: KwsInputKind = KwsInputKind.FBANK
    keywords: tuple[KwsKeyword, ...] = ()
    num_threads: int = 1
    provider: str = "cpu"
    fbank_options: KaldiFbankOptions = field(default_factory=KaldiFbankOptions)


class KwsContextState(Enum):
    """Where a context graph currently sits."""

    ROOT = "root"
    PARTIAL = "partial"
    MATCHED = "matched"


class KwsContextGraph:
    """Tracks partial matches across frames.

    A graph rather than a string compare because a keyword arrives one token at
    a time and can be abandoned half way. Without the graph, "hey circle" and
    "hey there" are indistinguishable until the last token, and the spotter has
    already committed.
    """

    def __init__(self, keywords: Sequence[KwsKeyword]) -> None:
        self._keywords = list(keywords)
        self._lock = threading.Lock()
        self._position: dict[str, int] = {}

    def advance(self, token_id: int) -> tuple[KwsContextState, str | None]:
        with self._lock:
            state = KwsContextState.ROOT
            for kw in self._keywords:
                pos = self._position.get(kw.text, 0)
                if pos < len(kw.token_ids) and kw.token_ids[pos] == token_id:
                    pos += 1
                    self._position[kw.text] = pos
                    if pos == len(kw.token_ids):
                        self._position[kw.text] = 0
                        return KwsContextState.MATCHED, kw.text
                    state = KwsContextState.PARTIAL
                    continue
                # A token that does not continue THIS keyword resets it — but
                # only it. Resetting every keyword on any mismatch loses a match
                # that started one token later.
                self._position[kw.text] = 0
            return state, None

    def reset(self) -> None:
        with self._lock:
            self._position.clear()


class KwsWakeWordDetector:
    """The single-graph classifier."""

    def __init__(self, config: KwsConfig) -> None:
        self.config = config
        self.engine = WakeEngine.SINGLE_GRAPH_CLASSIFIER
        self.graph = KwsContextGraph(config.keywords)


# ─────────────────────────────────────────────────────────────────────────────
# Phonemizers


class IPhonemizer(ABC):
    """Text into the phonemes a synthesiser wants."""

    @abstractmethod
    def phonemize(self, text: str, iso_language: str) -> str:
        """Returns "" when it cannot handle the language, rather than falling
        through to English.

        Wrong phonemes are not degraded output — they are a different language
        coming out of the speaker.
        """

    @abstractmethod
    def supports(self, iso_language: str) -> bool: ...


class PassthroughPhonemizer(IPhonemizer):
    """Returns the text unchanged.

    For models that take graphemes directly. NAMED so that using it is a
    decision, rather than looking like a phonemizer that failed.
    """

    def phonemize(self, text: str, iso_language: str) -> str:
        return text

    def supports(self, iso_language: str) -> bool:
        return True


def _strip_language_markers(text: str) -> str:
    """Removes the "(xx)" espeak emits when it switches language mid-string.

    Left in, they reach the model as phonemes and are synthesised as noise.
    """
    out: list[str] = []
    depth = 0
    for ch in text:
        if ch == "(":
            depth += 1
        elif ch == ")" and depth:
            depth -= 1
        elif depth == 0:
            out.append(ch)
    return "".join(out).strip()


class EspeakPhonemizer(IPhonemizer):
    """Drives espeak-ng OUT OF PROCESS.

    Out of process is not an implementation detail: espeak-ng is GPL, and
    linking it would put this codebase under the GPL. Running it as a program
    and reading its output does not.

    TWO THINGS THAT COST A DAY EACH. On Windows the executable eats non-Latin
    argv, so text goes in on STDIN. And stdin must be terminated with a NEWLINE
    or the last character is dropped — which shows up as a missing final
    phoneme and nothing else.
    """

    def __init__(self, run: Callable[[Sequence[str], str], str] | None = None) -> None:
        self._run = run

    def phonemize(self, text: str, iso_language: str) -> str:
        if self._run is None:
            raise RuntimeError("no espeak runner configured")
        return _strip_language_markers(
            self._run(["-q", "--ipa", "-v", iso_language], text + "\n")
        )

    def supports(self, iso_language: str) -> bool:
        return self._run is not None


class NativeEspeakPhonemizer(IPhonemizer):
    """The in-process binding, for a build that has accepted the licence
    position.

    Present as a seam and deliberately NOT wired by default.
    """

    def __init__(self, phonemize: Callable[[str, str], str] | None = None) -> None:
        self._phonemize = phonemize

    def phonemize(self, text: str, iso_language: str) -> str:
        if self._phonemize is None:
            raise RuntimeError("no native espeak bound")
        return self._phonemize(text, iso_language)

    def supports(self, iso_language: str) -> bool:
        return self._phonemize is not None


class IToneSource(ABC):
    """Tone marks for a tonal language."""

    @abstractmethod
    def tone_for(self, word: str) -> str | None: ...


class LexiconPhonemizer(IPhonemizer):
    """Looks words up in a pronunciation dictionary.

    Exact and unable to generalise, which is the trade: a lexicon gets the words
    it knows exactly right and has nothing at all for the rest. It is the
    correct front end for a language with a good dictionary and no G2P model.
    """

    def __init__(
        self,
        iso_language: str,
        entries: dict[str, str],
        tones: IToneSource | None = None,
    ) -> None:
        self._iso = iso_language
        self._entries = {k.lower(): v for k, v in entries.items()}
        self._tones = tones

    def phonemize(self, text: str, iso_language: str) -> str:
        if not self.supports(iso_language):
            return ""
        out: list[str] = []
        for word in text.split():
            key = "".join(c for c in word if c.isalpha()).lower()
            ipa = self._entries.get(key)
            if ipa is None:
                # A word NOT in the lexicon is skipped, not guessed. A guessed
                # pronunciation of somebody's name is worse than a gap.
                continue
            if self._tones is not None:
                tone = self._tones.tone_for(key)
                if tone:
                    ipa += tone
            out.append(ipa)
        return " ".join(out)

    def supports(self, iso_language: str) -> bool:
        return not self._iso or self._iso.lower() == iso_language.lower()


_GEEZ_CONSONANTS = [
    "h", "l", "ḥ", "m", "ś", "r", "s", "š", "q", "b", "t", "č", "ḫ", "n", "ñ",
    "ʾ", "k", "w", "ʿ", "z", "ž", "y", "d", "ǧ", "g", "ṭ", "č̣", "p̣", "ṣ", "ḍ", "f", "p",
]
_GEEZ_VOWELS = ["ä", "u", "i", "a", "e", "ə", "o"]


class GeezRomanizer:
    """Transliterates Ge'ez script into Latin.

    Ge'ez is a SYLLABARY: each character carries a consonant and a vowel
    together, so transliteration is per-character rather than per-letter, and a
    mapping built for an alphabet produces nonsense.
    """

    @staticmethod
    def is_ethiopic(ch: str) -> bool:
        code = ord(ch)
        return (0x1200 <= code <= 0x137F) or (0x1380 <= code <= 0x139F) or (0x2D80 <= code <= 0x2DDF)

    @classmethod
    def romanize(cls, text: str) -> str:
        out: list[str] = []
        for ch in text:
            if not cls.is_ethiopic(ch):
                out.append(ch)
                continue
            code = ord(ch)
            if not (0x1200 <= code <= 0x135A):
                out.append(ch)
                continue
            idx = code - 0x1200
            c, v = divmod(idx, 8)
            if c >= len(_GEEZ_CONSONANTS) or v >= len(_GEEZ_VOWELS):
                out.append(ch)
                continue
            out.append(_GEEZ_CONSONANTS[c] + _GEEZ_VOWELS[v])
        return "".join(out)


class GeezPhonemizer(IPhonemizer):
    """Phonemizes Ge'ez-script languages by romanising first."""

    _SUPPORTED = frozenset({"am", "amh", "ti", "tir", "gez"})

    def __init__(self, inner: IPhonemizer | None = None) -> None:
        self._inner = inner

    def phonemize(self, text: str, iso_language: str) -> str:
        roman = GeezRomanizer.romanize(text)
        if self._inner is None:
            return roman
        return self._inner.phonemize(roman, iso_language)

    def supports(self, iso_language: str) -> bool:
        return iso_language.lower() in self._SUPPORTED


class OpenJTalkPhonemizer(IPhonemizer):
    """The Japanese front end."""

    def __init__(self, tokenise: Callable[[str], str] | None = None) -> None:
        self._tokenise = tokenise

    def phonemize(self, text: str, iso_language: str) -> str:
        if self._tokenise is None:
            raise RuntimeError(
                "Open JTalk is not available: Japanese needs its dictionary, "
                "and there is no drop-in substitute"
            )
        return self._tokenise(text)

    def supports(self, iso_language: str) -> bool:
        return self._tokenise is not None and iso_language.lower() in {"ja", "jpn"}


class OpenJTalkProsodyTokeniser:
    """Open JTalk's prosody tokens.

    Japanese is a FOURTH FAMILY here. The others hand a phonemiser's output
    straight to the model; this one emits accent-phrase markers — ^ $ _ # [ ] —
    alongside the moras, and the model was trained expecting them. Feeding it
    bare phonemes produces speech that is intelligible and completely flat,
    which reads as a broken voice rather than a missing feature.

    Needs the 103 MB dictionary; without it there is no drop-in substitute.
    """

    def __init__(self, dictionary_directory: str, tokenise: Callable[[str], str] | None = None) -> None:
        self.dictionary_directory = dictionary_directory
        self._tokenise = tokenise

    def tokenise(self, japanese_text: str) -> str:
        if self._tokenise is None:
            raise RuntimeError("Open JTalk dictionary not available")
        return self._tokenise(japanese_text)


# ─────────────────────────────────────────────────────────────────────────────
# Respelling


class RespellingSource(Enum):
    """Where a respelling came from."""

    LEXICON = "lexicon"
    RULE = "rule"
    PERSON = "person"


class Respeller(ABC):
    """Rewrites a word so a synthesiser says it the way somebody expects."""

    @abstractmethod
    def respell(self, word: str) -> tuple[str, RespellingSource, bool]: ...


class LoanwordRespeller(Respeller):
    """Rewrites borrowed words into the host language's spelling.

    English words inside an isiZulu sentence are the common case, and a
    synthesiser handed the English spelling reads them with English phonology —
    which is intelligible to an English speaker and wrong to everybody else.
    """

    def __init__(self, rules: dict[str, str] | None = None) -> None:
        self._rules = {k.lower(): v for k, v in (rules or {}).items()}

    def respell(self, word: str) -> tuple[str, RespellingSource, bool]:
        out = self._rules.get(word.lower())
        if out is None:
            return word, RespellingSource.LEXICON, False
        return out, RespellingSource.LEXICON, True


class NguniRespeller(Respeller):
    """Applies Nguni orthographic rules.

    The CLICK LETTERS are the reason this exists: c, q and x are clicks in Nguni
    languages and consonants in English, and a synthesiser that does not know
    which language it is in gets every one of them wrong.
    """

    _RULES = {"ph": "pʰ", "th": "tʰ", "kh": "kʰ", "hl": "ɬ", "dl": "ɮ"}

    def respell(self, word: str) -> tuple[str, RespellingSource, bool]:
        out = word
        for frm, to in self._RULES.items():
            out = out.replace(frm, to)
        return out, RespellingSource.RULE, out != word


class LearningState(Enum):
    """How far a learned word has got."""

    #: Still listening. Nothing has changed how the word is spoken.
    LISTENING = "listening"
    #: Five hearings agreed; the new spelling is in use and awaiting its check.
    ADOPTED = "adopted"
    #: The check passed. This is how the word is said for this person.
    CONFIRMED = "confirmed"


@dataclass
class LearnedWord:
    """What has been learned about one word."""

    word: str
    spelling: str | None
    state: LearningState
    #: Each candidate and how many hearings agreed. KEPT after adoption: a word
    #: can be re-learned when somebody's pronunciation shifts, and throwing the
    #: tallies away makes that restart from nothing.
    candidates: dict[str, int] = field(default_factory=dict)


#: How many agreeing hearings adopt a spelling.
#:
#: One is a mis-hearing; five in agreement is a habit. Adopting on the first
#: would make the assistant mispronounce a word confidently on the strength of
#: one bad frame, and the person would have no idea why it changed.
ADOPTION_THRESHOLD = 5


class PersonalRespellings:
    """Learns how one person says borrowed words, from ordinary use."""

    def __init__(self) -> None:
        self._lock = threading.Lock()
        self._words: dict[str, LearnedWord] = {}

    def hear(self, word: str, heard_spelling: str) -> None:
        if not word or not heard_spelling:
            return
        key = word.lower()
        with self._lock:
            learned = self._words.get(key)
            if learned is None:
                learned = LearnedWord(word=word, spelling=None, state=LearningState.LISTENING)
                self._words[key] = learned
            learned.candidates[heard_spelling] = learned.candidates.get(heard_spelling, 0) + 1
            if (
                learned.state is LearningState.LISTENING
                and learned.candidates[heard_spelling] >= ADOPTION_THRESHOLD
            ):
                learned.spelling = heard_spelling
                learned.state = LearningState.ADOPTED

    def lookup(self, word: str) -> LearnedWord | None:
        with self._lock:
            return self._words.get(word.lower())

    def confirm(self, word: str) -> bool:
        with self._lock:
            learned = self._words.get(word.lower())
            if learned is None or learned.state is not LearningState.ADOPTED:
                return False
            learned.state = LearningState.CONFIRMED
            return True


# ─────────────────────────────────────────────────────────────────────────────
# Text into what gets spoken


@dataclass(frozen=True)
class LanguageSpan:
    """One run of text in one language."""

    text: str
    #: None when the splitter could not tell — which is not the same as the
    #: document's language.
    language: str | None
    start: int
    length: int


class LanguageSpanSplitter:
    """Splits mixed-language text into runs.

    Code-switching mid-sentence is normal here, and a synthesiser handed the
    whole sentence in one language reads half of it wrong.
    """

    @staticmethod
    def _script_of(ch: str) -> str | None:
        try:
            name = unicodedata.name(ch)
        except ValueError:
            return None
        for script in ("LATIN", "ETHIOPIC", "CJK", "CYRILLIC", "ARABIC", "HIRAGANA", "KATAKANA"):
            if name.startswith(script):
                return script.lower()
        return None

    @classmethod
    def split(cls, text: str) -> list[LanguageSpan]:
        if not text:
            return []
        spans: list[LanguageSpan] = []
        buf: list[str] = []
        current: str | None = None
        start = 0
        for i, ch in enumerate(text):
            script = cls._script_of(ch)
            if script is None:
                buf.append(ch)
                continue
            if current is not None and script != current:
                spans.append(LanguageSpan("".join(buf), current, start, len(buf)))
                buf, start = [], i
            current = script
            buf.append(ch)
        if buf:
            spans.append(LanguageSpan("".join(buf), current, start, len(buf)))
        return spans


class SentenceSplitter:
    """Splits text into sentences for synthesis.

    Different from a streaming chunker, which optimises for time-to-first-audio.
    This one sees the whole text and optimises for PROSODY: a synthesiser handed
    a sentence in two halves puts a full stop in the middle of it, and no amount
    of joining the audio afterwards takes that back.
    """

    #: Includes the FULLWIDTH forms: a Japanese or Chinese reply ends in U+3002,
    #: and a splitter that only knows "." never splits it at all.
    TERMINALS = ".!?。！？"

    @classmethod
    def split(cls, text: str) -> list[str]:
        out: list[str] = []
        buf: list[str] = []
        for ch in text:
            buf.append(ch)
            if ch in cls.TERMINALS:
                candidate = "".join(buf).strip()
                if candidate:
                    out.append(candidate)
                buf = []
        tail = "".join(buf).strip()
        if tail:
            out.append(tail)
        return out


_XSAMPA_TO_IPA = [
    ("tS", "tʃ"), ("dZ", "dʒ"), ("@`", "ɚ"), ("3`", "ɝ"),
    ("A", "ɑ"), ("E", "ɛ"), ("I", "ɪ"), ("O", "ɔ"), ("U", "ʊ"), ("V", "ʌ"),
    ("@", "ə"), ("S", "ʃ"), ("Z", "ʒ"), ("T", "θ"), ("D", "ð"), ("N", "ŋ"),
    ("R", "ʁ"), ("H", "ɥ"), ("J", "ɲ"), ("L", "ʎ"), ("Q", "ɒ"), ("Y", "ʏ"),
    ("{", "æ"), ("}", "ʉ"), ("1", "ɨ"), ("2", "ø"), ("3", "ɜ"), ("4", "ɾ"),
    ("5", "ɫ"), ("6", "ɐ"), ("7", "ɤ"), ("8", "ɵ"), ("9", "œ"), ("&", "ɶ"),
]


def xsampa_to_ipa(xsampa: str) -> str:
    """X-SAMPA to IPA.

    Needed because lexicons in this space are published in X-SAMPA — it is ASCII
    and survives a spreadsheet — while every model consumes IPA.

    Longest-first, because "tS" must be matched before "t" and "S".
    """
    out = xsampa
    for frm, to in _XSAMPA_TO_IPA:
        out = out.replace(frm, to)
    return out


@dataclass(frozen=True)
class ToneShaper:
    """Gentle tone correction applied to synthesised speech.

    Two RBJ biquads in series over the float waveform before it becomes PCM: a
    low shelf that lifts the bottom and a peaking dip that takes out the harsh
    band. The defaults are MEASURED, not chosen by ear on one machine, and the
    constraint was that intelligibility must not drop — a warmer voice nobody
    can make out is a worse voice.
    """

    low_shelf_hz: float = 320.0
    low_shelf_db: float = 4.0
    presence_hz: float = 3200.0
    presence_db: float = -4.0
    presence_q: float = 0.8

    @classmethod
    def warm(cls) -> "ToneShaper":
        return cls()

    def apply(self, waveform: list[float], sample_rate_hz: int) -> None:
        """Filters in place."""
        if sample_rate_hz <= 0 or not waveform:
            return
        _biquad(waveform, *_low_shelf(self.low_shelf_hz, self.low_shelf_db, sample_rate_hz))
        _biquad(waveform, *_peaking(self.presence_hz, self.presence_db, self.presence_q, sample_rate_hz))


def _biquad(x: list[float], b0: float, b1: float, b2: float, a1: float, a2: float) -> None:
    """One direct-form-I biquad, in place."""
    x1 = x2 = y1 = y2 = 0.0
    for i, v in enumerate(x):
        y = b0 * v + b1 * x1 + b2 * x2 - a1 * y1 - a2 * y2
        x2, x1 = x1, v
        y2, y1 = y1, y
        x[i] = y


def _low_shelf(f0: float, gain_db: float, rate: int) -> tuple[float, float, float, float, float]:
    a = 10 ** (gain_db / 40)
    w0 = 2 * math.pi * f0 / rate
    cw, sw = math.cos(w0), math.sin(w0)
    alpha = sw / 2 * math.sqrt((a + 1 / a) * (1 / 0.707 - 1) + 2)
    sq = 2 * math.sqrt(a) * alpha
    a0 = (a + 1) + (a - 1) * cw + sq
    return (
        a * ((a + 1) - (a - 1) * cw + sq) / a0,
        2 * a * ((a - 1) - (a + 1) * cw) / a0,
        a * ((a + 1) - (a - 1) * cw - sq) / a0,
        -2 * ((a - 1) + (a + 1) * cw) / a0,
        ((a + 1) + (a - 1) * cw - sq) / a0,
    )


def _peaking(f0: float, gain_db: float, q: float, rate: int) -> tuple[float, float, float, float, float]:
    a = 10 ** (gain_db / 40)
    w0 = 2 * math.pi * f0 / rate
    cw, sw = math.cos(w0), math.sin(w0)
    alpha = sw / (2 * q)
    a0 = 1 + alpha / a
    return (
        (1 + alpha * a) / a0,
        -2 * cw / a0,
        (1 - alpha * a) / a0,
        -2 * cw / a0,
        (1 - alpha / a) / a0,
    )


# ─────────────────────────────────────────────────────────────────────────────
# Sentencepiece


class SentencePieceKind(Enum):
    """How a piece is used, mirroring sentencepiece's own enum.

    The values are the on-disk ones: a vocabulary file names them by number.
    """

    NORMAL = 1
    UNKNOWN = 2
    CONTROL = 3
    USER_DEFINED = 4
    UNUSED = 5
    BYTE = 6


@dataclass(frozen=True)
class SentencePiece:
    """One entry of a vocabulary."""

    piece: str
    score: float
    kind: SentencePieceKind
    id: int


#: The word-boundary marker, U+2581 — NOT an underscore.
#:
#: It looks like one in a terminal and it is not one. A tokenizer that
#: substitutes "_" produces pieces absent from every real vocabulary, so every
#: word falls back to bytes — and the only symptom is a spotter that quietly
#: never matches anything.
WORD_BOUNDARY_MARKER = "▁"


class SentencePieceTokenizer:
    """Segments text into vocabulary pieces."""

    def __init__(self, pieces: Sequence[SentencePiece]) -> None:
        self._pieces = list(pieces)
        self._by_text = {p.piece: p for p in pieces}

    def __len__(self) -> int:
        return len(self._pieces)

    @staticmethod
    def normalise(text: str) -> str:
        """sentencepiece's normalisation: spaces become the marker AND one is
        prefixed.

        The prefix is not optional — without it the first word of a sentence
        tokenises differently from the same word anywhere else.
        """
        return WORD_BOUNDARY_MARKER + text.strip().replace(" ", WORD_BOUNDARY_MARKER)

    def encode(self, text: str) -> list[int]:
        """Best-scoring segmentation as piece ids.

        VITERBI over every segmentation, not greedy longest-match. Greedy is
        faster and gets ordinary words right, but it splits exactly the words
        that matter here — names, loanwords, anything the vocabulary only half
        covers — and it splits them differently depending on what preceded them.
        """
        s = self.normalise(text)
        n = len(s)
        if n == 0:
            return []
        best = [-math.inf] * (n + 1)
        best[0] = 0.0
        back: list[int] = [0] * (n + 1)
        back_id: list[int] = [0] * (n + 1)
        for i in range(n):
            if best[i] == -math.inf:
                continue
            for j in range(i + 1, n + 1):
                piece = self._by_text.get(s[i:j])
                if piece is None:
                    continue
                score = best[i] + piece.score
                if score > best[j]:
                    best[j], back[j], back_id[j] = score, i, piece.id
        if best[n] == -math.inf:
            return []
        ids: list[int] = []
        i = n
        while i > 0:
            ids.append(back_id[i])
            i = back[i]
        return list(reversed(ids))

    def covers(self, text: str) -> bool:
        """Whether every piece of the text is in the vocabulary.

        The question a phrase book asks before promising a keyword will ever be
        matched.
        """
        return bool(self.encode(text))


# ─────────────────────────────────────────────────────────────────────────────
# Judging a wake phrase


class WakePhraseVerdict(Enum):
    """What we think of a phrase."""

    #: Nothing to say against it.
    GOOD = "good"
    #: Usable, with a caveat the owner should hear.
    CAUTION = "caution"
    #: Cannot work at all; the advice says why.
    UNUSABLE = "unusable"


@dataclass(frozen=True)
class WakePhrase:
    """A phrase, its tokens, and the verdict."""

    text: str
    tokens: tuple[str, ...]
    verdict: WakePhraseVerdict
    #: Plain language, shown to the person choosing. Empty when good.
    advice: str = ""
    threshold: float | None = None
    boost: float | None = None


_COMMON_WORDS = frozenset({
    "hey", "hi", "hello", "the", "a", "an", "you", "me", "please", "now", "ok", "okay",
})


def _count_vowel_runs(word: str) -> int:
    n, in_run = 0, False
    for ch in word.lower():
        vowel = ch in "aeiouy"
        if vowel and not in_run:
            n += 1
        in_run = vowel
    return n


class WakePhraseBook:
    """Judges a phrase before somebody lives with it.

    A wake word is the only part of an assistant that runs constantly, and a bad
    one fails in the two worst ways at once: it misses when you want it and
    fires when you do not. Neither is fixable later by tuning.
    """

    def __init__(self, tokenizer: SentencePieceTokenizer | None = None) -> None:
        self._tokenizer = tokenizer

    def judge(self, text: str) -> WakePhrase:
        words = text.strip().lower().split()
        if not words:
            return WakePhrase(text, (), WakePhraseVerdict.UNUSABLE,
                              "a wake phrase cannot be empty")

        syllables = sum(_count_vowel_runs(w) for w in words)
        if syllables < 3:
            return WakePhrase(
                text, tuple(words), WakePhraseVerdict.UNUSABLE,
                "too short: under three syllables there is not enough signal, "
                "and it will fire on coughs",
            )

        if self._tokenizer is not None and not self._tokenizer.covers(text):
            # The one that looks like a broken microphone: the spotter matches
            # pieces, and a phrase whose pieces are absent can never match
            # anything.
            return WakePhrase(
                text, tuple(words), WakePhraseVerdict.UNUSABLE,
                "these words are not in the wake model's vocabulary, so it can "
                "never match them",
            )

        if all(w in _COMMON_WORDS for w in words):
            return WakePhrase(
                text, tuple(words), WakePhraseVerdict.CAUTION,
                "these are common words, so this will fire while you are talking "
                "to somebody else; add an unusual one",
            )

        return WakePhrase(text, tuple(words), WakePhraseVerdict.GOOD)

    @property
    def suggested(self) -> tuple[str, ...]:
        """Phrases known to work, for somebody who does not want to choose."""
        return ("hey circle", "okay butler", "hello indlu")


# ─────────────────────────────────────────────────────────────────────────────
# Engines and the loop


class OnnxSessionFactory:
    """Builds ONNX sessions.

    ONE factory so the thread count, the execution provider and the graph
    optimisation level are set in one place. Three engines each configuring
    their own is three different answers to "why is this slow on that phone".
    """

    def __init__(
        self,
        num_threads: int = 1,
        provider: str = "cpu",
        create: Callable[[str, int, str], object] | None = None,
    ) -> None:
        #: One thread by default. More threads on a phone contend with the UI
        #: thread and make the assistant feel slower while finishing sooner.
        self.num_threads = max(1, num_threads)
        self.provider = provider or "cpu"
        self._create = create

    @property
    def is_available(self) -> bool:
        return self._create is not None

    def create(self, model_path: str) -> object:
        if self._create is None:
            raise RuntimeError("no ONNX runtime is available in this build")
        return self._create(model_path, self.num_threads, self.provider)


class ITtsFrontEndDiagnostics(ABC):
    """What the front end did to a piece of text.

    So a wrong pronunciation can be traced to the STAGE that caused it rather
    than blamed on the model.
    """

    @property
    @abstractmethod
    def phonemes(self) -> str: ...

    @property
    @abstractmethod
    def respellings(self) -> Sequence[str]: ...

    @property
    @abstractmethod
    def language(self) -> str: ...

    @property
    @abstractmethod
    def front_end_name(self) -> str: ...


#: Pad token ids per voice family. -1 means unknown, which is NOT 0 — 0 is a
#: real answer, and confusing the two is the pad rule failing.
_PAD_IDS = {"mms": 0, "piper": 3}


class OnnxTtsEngine:
    """Synthesises with an ONNX model.

    THE PAD RULE lives here: a blank pad token means the MODEL's blank, not the
    literal "_".
    """

    def __init__(self, factory: OnnxSessionFactory, family: str = "mms") -> None:
        self.factory = factory
        self.family = family
        self.pad_id = _PAD_IDS.get(family.lower(), -1)

    @property
    def is_available(self) -> bool:
        return self.factory.is_available

    def synthesize(self, text: str, voice_id: str = "") -> tuple[bytes, int]:
        if not self.is_available:
            raise RuntimeError("no ONNX runtime is available in this build")
        if self.pad_id < 0:
            raise RuntimeError(
                "this voice family's pad token is unknown; synthesising would "
                "produce silence or noise"
            )
        raise RuntimeError("no ONNX session runner wired")


class ToucanOnnxTtsEngine(OnnxTtsEngine):
    """The Toucan family, whose models take a speaker embedding alongside the
    text."""

    def __init__(self, factory: OnnxSessionFactory) -> None:
        super().__init__(factory, "mms")


class KokoroTtsEngine(OnnxTtsEngine):
    """The Kokoro family."""

    def __init__(self, factory: OnnxSessionFactory) -> None:
        super().__init__(factory, "mms")


class PocketTtsEngine(OnnxTtsEngine):
    """The PocketTTS family.

    The voice rides the TEXT input rather than a separate speaker channel, NaN
    marks the beginning of the sequence, and the end token does not stop
    generation on its own. Measured on a P30: about seven times slower than
    realtime, which is why it is not the default for anything a person waits on.
    """

    def __init__(self, factory: OnnxSessionFactory) -> None:
        super().__init__(factory, "mms")


class PhrasedTtsEngine:
    """Splits into phrases and synthesises each.

    Long-form synthesis loses pitch and pace over tens of seconds; phrase-sized
    chunks re-anchor it without an audible seam.
    """

    def __init__(self, inner: object, max_phrase_chars: int = 220) -> None:
        self.inner = inner
        self.max_phrase_chars = max_phrase_chars

    def synthesize(self, text: str, voice_id: str = "") -> tuple[bytes, int]:
        audio = b""
        rate = 0
        for sentence in SentenceSplitter.split(text):
            pcm, r = self.inner.synthesize(sentence, voice_id)  # type: ignore[attr-defined]
            audio += pcm
            rate = r
        return audio, rate


class RespellingTtsEngine:
    """Puts words through the respellers before synthesis.

    Composed rather than built in, because whether respelling helps depends on
    the voice: a model trained on the same accent needs none of it.
    """

    def __init__(self, inner: object, respellers: Sequence[Respeller] = ()) -> None:
        self.inner = inner
        self.respellers = list(respellers)

    def synthesize(self, text: str, voice_id: str = "") -> tuple[bytes, int]:
        words = text.split()
        for i, w in enumerate(words):
            for r in self.respellers:
                out, _, changed = r.respell(w)
                if changed:
                    words[i] = out
                    break
        return self.inner.synthesize(" ".join(words), voice_id)  # type: ignore[attr-defined]


class IAudioPlayer(ABC):
    """Plays synthesised audio."""

    @abstractmethod
    def play(self, pcm: bytes, sample_rate_hz: int) -> None: ...

    @abstractmethod
    def stop(self) -> None: ...

    @property
    @abstractmethod
    def is_playing(self) -> bool: ...


class NullAudioPlayer(IAudioPlayer):
    """Plays nothing and reports success.

    The default: a host with no audio output gets a loop that completes rather
    than one that fails, and a test never opens a device.
    """

    def play(self, pcm: bytes, sample_rate_hz: int) -> None:
        return None

    def stop(self) -> None:
        return None

    @property
    def is_playing(self) -> bool:
        return False


@dataclass(frozen=True)
class VoiceExchangeEventArgs:
    """One completed turn.

    The C# carries this as event args; Python has no events, so it is the
    payload a callback receives.
    """

    heard: str
    said: str
    language: str
    started_at: datetime
    duration_ms: int
    #: Whether the person interrupted. Recorded because a turn that was CUT OFF
    #: and one that completed are different events, and a transcript that treats
    #: them alike reads as though the assistant finished.
    interrupted: bool = False


class VoiceTrace:
    """One turn's timeline.

    It exists because voice failures are not reproducible. By the time somebody
    says "it did not hear me", the audio is gone; without a trace the only
    evidence is a description of a sound.

    OFF BY DEFAULT and never written anywhere by itself — it holds what somebody
    said, and a diagnostic that quietly logs speech is a recorder.
    """

    def __init__(self) -> None:
        self._lock = threading.Lock()
        self._marks: list[tuple[str, datetime, str]] = []

    def mark(self, stage: str, at: datetime, detail: str = "") -> None:
        with self._lock:
            self._marks.append((stage, at, detail))

    @property
    def stages(self) -> list[str]:
        with self._lock:
            return [m[0] for m in self._marks]

    def __len__(self) -> int:
        with self._lock:
            return len(self._marks)


class VoiceLoop:
    """Ties the front end, the wake stage and the player together."""

    def __init__(
        self,
        player: IAudioPlayer | None = None,
        spotter: ConfirmedKeywordSpotter | None = None,
    ) -> None:
        self.player = player or NullAudioPlayer()
        self.spotter = spotter
        self.trace = VoiceTrace()
        self._lock = threading.Lock()
        self._running = False
        self._on_exchange: Callable[[VoiceExchangeEventArgs], None] | None = None

    def on_exchange(self, handler: Callable[[VoiceExchangeEventArgs], None]) -> None:
        with self._lock:
            self._on_exchange = handler

    def start(self) -> None:
        with self._lock:
            self._running = True

    def stop(self) -> None:
        """Stops the loop AND any playback.

        Stopping playback here rather than leaving it is the difference between
        an assistant that goes quiet when told and one that finishes its
        sentence first.
        """
        with self._lock:
            self._running = False
            player = self.player
        player.stop()

    @property
    def is_running(self) -> bool:
        with self._lock:
            return self._running

    def complete_exchange(self, args: VoiceExchangeEventArgs) -> None:
        with self._lock:
            handler = self._on_exchange
        if handler is not None:
            handler(args)
