"""Audio: WAV in both directions, a music bed synthesised on the device, and
the wake-word seam.

WHY SYNTHESISE MUSIC AT ALL. A bed under a voice note or a clip has to come
from somewhere, and every other route is either a licence somebody has to read
or a file somebody has to download. Sine tones from a scale cost nothing, ship
in the binary, and are unambiguously ours to use.

THE TWO THINGS THAT SOUND LIKE BROKEN CODE AND ARE ARITHMETIC:

  * Summing voices at full amplitude CLIPS. Four notes each at 0.8 sum to 3.2,
    wrap round in 16-bit, and come out as a buzz that sounds like a crashed
    decoder. The mix is scaled by the voice count, not hoped about.

  * A note that starts or stops at a non-zero sample CLICKS. The discontinuity
    is a step, a step is broadband, and the ear hears it as a tick on every
    note boundary. Every note here gets an attack and a release.
"""

from __future__ import annotations

import math
import struct
from abc import ABC, abstractmethod
from dataclasses import dataclass, field
from datetime import timedelta
from enum import Enum, IntEnum
from typing import Callable, Sequence


# ─────────────────────────────────────────────────────────────────────────────
# PCM and WAV


@dataclass(frozen=True)
class AudioPcmFormat:
    """The shape of a block of PCM."""

    sample_rate_hz: int = 22050
    channels: int = 1
    bits_per_sample: int = 16

    def __post_init__(self) -> None:
        if self.sample_rate_hz <= 0 or self.channels <= 0:
            raise ValueError("a PCM format needs a positive rate and channel count")
        if self.bits_per_sample not in (8, 16, 24, 32):
            raise ValueError(f"{self.bits_per_sample}-bit PCM is not supported")

    @property
    def block_align(self) -> int:
        return self.channels * self.bits_per_sample // 8

    @property
    def byte_rate(self) -> int:
        return self.sample_rate_hz * self.block_align

    def duration_of(self, byte_count: int) -> timedelta:
        return timedelta(seconds=byte_count / self.byte_rate if self.byte_rate else 0)

    def frame_count(self, byte_count: int) -> int:
        return byte_count // self.block_align if self.block_align else 0

    #: 16 kHz mono is what every speech model on this device wants. Named rather
    #: than repeated, because a transcriber fed 22050 silently transcribes
    #: nonsense - it does not fail, it just hears the wrong speed.
    @staticmethod
    def speech() -> "AudioPcmFormat":
        return AudioPcmFormat(16000, 1, 16)

    @staticmethod
    def music() -> "AudioPcmFormat":
        return AudioPcmFormat(22050, 1, 16)


class WavWriter:
    """Writes a RIFF/WAVE file.

    THE TWO SIZE FIELDS ARE DIFFERENT and getting either wrong produces a file
    that plays in one program and not another - the worst kind of wrong, because
    the first program you test in is usually the forgiving one.

      RIFF size = whole file MINUS 8 (the "RIFF" tag and this field itself)
      data size = the PCM bytes only

    Everything in a WAV header is LITTLE-endian, unlike PNG, which is why this
    is a separate writer rather than a shared one.
    """

    @staticmethod
    def header(format: AudioPcmFormat, data_bytes: int) -> bytes:
        return b"".join((
            b"RIFF",
            struct.pack("<I", 36 + data_bytes),
            b"WAVEfmt ",
            struct.pack("<I", 16),
            struct.pack("<H", 1),  # PCM, uncompressed
            struct.pack("<H", format.channels),
            struct.pack("<I", format.sample_rate_hz),
            struct.pack("<I", format.byte_rate),
            struct.pack("<H", format.block_align),
            struct.pack("<H", format.bits_per_sample),
            b"data",
            struct.pack("<I", data_bytes),
        ))

    @staticmethod
    def write(format: AudioPcmFormat, pcm: bytes) -> bytes:
        return WavWriter.header(format, len(pcm)) + pcm

    @staticmethod
    def from_samples(
        format: AudioPcmFormat, samples: Sequence[float]
    ) -> bytes:
        """Floats in -1..1 to 16-bit.

        CLAMPED, not wrapped. A sample of 1.2 that wraps becomes a large
        negative number - a click at full scale, which is louder than anything
        else in the file and sounds like the file is broken.

        Scaled by 32767 rather than 32768 so that +1.0 is representable and
        does not become the one value that wraps.
        """
        if format.bits_per_sample != 16:
            raise ValueError("only 16-bit output is supported here")
        clamped = bytearray()
        for s in samples:
            v = int(round(max(-1.0, min(1.0, s)) * 32767))
            clamped += struct.pack("<h", v)
        return WavWriter.write(format, bytes(clamped))


class WavIo:
    """Reads a WAV back.

    CHUNKS ARE WALKED, not assumed. A WAV written by a recorder usually has a
    LIST or fact chunk between `fmt ` and `data`, and code that seeks to a fixed
    offset reads that metadata as audio - which plays as a burst of noise at the
    start of every file from that recorder.
    """

    @staticmethod
    def read(data: bytes) -> tuple[AudioPcmFormat, bytes]:
        if len(data) < 12 or data[:4] != b"RIFF" or data[8:12] != b"WAVE":
            raise ValueError("not a RIFF/WAVE file")
        pos = 12
        format: AudioPcmFormat | None = None
        pcm = b""
        while pos + 8 <= len(data):
            kind = data[pos:pos + 4]
            size = struct.unpack("<I", data[pos + 4:pos + 8])[0]
            payload = data[pos + 8:pos + 8 + size]
            if kind == b"fmt " and len(payload) >= 16:
                _, channels, rate, _, _, bits = struct.unpack("<HHIIHH", payload[:16])
                format = AudioPcmFormat(rate, channels, bits)
            elif kind == b"data":
                pcm = payload
            # Chunks are WORD-ALIGNED: an odd-sized chunk is followed by a pad
            # byte that is not counted in its size. Skipping it puts every
            # subsequent chunk one byte out.
            pos += 8 + size + (size & 1)
        if format is None:
            raise ValueError("this WAV has no fmt chunk")
        return format, pcm

    @staticmethod
    def to_samples(format: AudioPcmFormat, pcm: bytes) -> list[float]:
        if format.bits_per_sample != 16:
            raise ValueError("only 16-bit input is supported here")
        count = len(pcm) // 2
        return [v / 32768.0 for v in struct.unpack(f"<{count}h", pcm[:count * 2])]

    @staticmethod
    def resample_linear(
        samples: Sequence[float], from_hz: int, to_hz: int
    ) -> list[float]:
        """Linear resampling, and it is honest about being that.

        Good enough to feed a wake detector and NOT good enough to feed a
        transcriber that was trained on properly filtered audio - downsampling
        without a low-pass folds everything above the new Nyquist back into the
        band as aliasing, which a model hears as noise it was never trained on.
        Named `linear` so nobody reaches for it without deciding.
        """
        if from_hz == to_hz or not samples:
            return list(samples)
        ratio = from_hz / to_hz
        out_count = max(1, int(len(samples) / ratio))
        out: list[float] = []
        for i in range(out_count):
            position = i * ratio
            left = int(position)
            frac = position - left
            right = min(left + 1, len(samples) - 1)
            out.append(samples[left] * (1 - frac) + samples[right] * frac)
        return out


# ─────────────────────────────────────────────────────────────────────────────
# Pitch


class PitchClass(IntEnum):
    """The twelve, as semitones above C.

    Sharps only. A separate flat spelling would be musically correct and would
    also double every lookup table for no audible difference - E flat and D
    sharp are the same frequency.
    """

    C = 0
    C_SHARP = 1
    D = 2
    D_SHARP = 3
    E = 4
    F = 5
    F_SHARP = 6
    G = 7
    G_SHARP = 8
    A = 9
    A_SHARP = 10
    B = 11


class Scale(Enum):
    """Which notes are in play."""

    MAJOR = "major"
    #: Natural minor. The one people mean by "sad".
    MINOR = "minor"
    #: Five notes, no semitone clashes. ANY two notes in it sound fine together,
    #: which is what makes it the safe default for a generated bed - a bad
    #: random choice is still consonant.
    PENTATONIC = "pentatonic"
    DORIAN = "dorian"
    #: Whole tones only. Deliberately unresolved; used for tension, never for a
    #: bed somebody has to listen to for four minutes.
    WHOLE_TONE = "whole-tone"

    @property
    def intervals(self) -> tuple[int, ...]:
        return {
            Scale.MAJOR: (0, 2, 4, 5, 7, 9, 11),
            Scale.MINOR: (0, 2, 3, 5, 7, 8, 10),
            Scale.PENTATONIC: (0, 2, 4, 7, 9),
            Scale.DORIAN: (0, 2, 3, 5, 7, 9, 10),
            Scale.WHOLE_TONE: (0, 2, 4, 6, 8, 10),
        }[self]


@dataclass(frozen=True)
class MusicalKey:
    """A tonic and a scale."""

    tonic: PitchClass = PitchClass.C
    scale: Scale = Scale.PENTATONIC

    #: A4 = 440 Hz is MIDI note 69. Equal temperament, so every semitone is the
    #: twelfth root of two - the formula rather than a table, because a table
    #: has to stop somewhere and this does not.
    A4_MIDI = 69
    A4_HZ = 440.0

    @classmethod
    def frequency_of(cls, midi_note: int) -> float:
        return cls.A4_HZ * (2.0 ** ((midi_note - cls.A4_MIDI) / 12.0))

    def degrees(self, octave: int = 4, count: int = 0) -> list[int]:
        """MIDI notes of the scale, ascending, wrapping into higher octaves.

        C4 is MIDI 60, so an octave number maps to (octave + 1) * 12. Getting
        that offset wrong transposes everything by an octave, which sounds fine
        and is wrong - the bed ends up under or over the voice it should sit
        with.
        """
        intervals = self.scale.intervals
        wanted = count or len(intervals)
        base = (octave + 1) * 12 + int(self.tonic)
        return [
            base + 12 * (i // len(intervals)) + intervals[i % len(intervals)]
            for i in range(wanted)
        ]

    def frequencies(self, octave: int = 4, count: int = 0) -> list[float]:
        return [self.frequency_of(n) for n in self.degrees(octave, count)]


# ─────────────────────────────────────────────────────────────────────────────
# The bed


class MusicBedBackend(Enum):
    """Where a bed comes from."""

    #: Sine tones from a scale. Always available, ours, free.
    PROCEDURAL = "procedural"
    #: A model. Only when one has been downloaded.
    NEURAL = "neural"
    #: A file the person supplied. Their licence, their decision.
    SAMPLE_LIBRARY = "sample-library"
    NONE = "none"


@dataclass(frozen=True)
class MusicSpec:
    """What kind of bed is wanted."""

    key: MusicalKey = field(default_factory=MusicalKey)
    #: Beats per minute. Under a voice, slower is better - a bed that competes
    #: for attention with the words is a bed that failed.
    tempo_bpm: int = 72
    duration: timedelta = timedelta(seconds=8)
    #: 0..1, and the default is deliberately low. A bed at conversational level
    #: is not a bed.
    level: float = 0.18
    voices: int = 3
    format: AudioPcmFormat = field(default_factory=AudioPcmFormat.music)
    seed: int = 0

    @property
    def frame_count(self) -> int:
        return int(self.duration.total_seconds() * self.format.sample_rate_hz)

    @property
    def seconds_per_beat(self) -> float:
        return 60.0 / max(1, self.tempo_bpm)


@dataclass(frozen=True)
class MusicBed:
    """The rendered result."""

    pcm: bytes = b""
    format: AudioPcmFormat = field(default_factory=AudioPcmFormat.music)
    backend: MusicBedBackend = MusicBedBackend.NONE
    duration: timedelta = timedelta()
    #: Set when nothing could be made. Empty PCM with no reason is a bug that
    #: reads as silence.
    error: str = ""

    @property
    def is_silent(self) -> bool:
        return not self.pcm

    def to_wav(self) -> bytes:
        return WavWriter.write(self.format, self.pcm)


class IMusicBedGenerator(ABC):
    """Makes a bed."""

    @property
    @abstractmethod
    def backend(self) -> MusicBedBackend: ...

    @property
    @abstractmethod
    def is_available(self) -> bool: ...

    @abstractmethod
    def generate(self, spec: MusicSpec) -> MusicBed: ...


class NullMusicBedGenerator(IMusicBedGenerator):
    """Makes silence, and says so.

    Returns a bed with an error rather than raising: a clip with no music is
    still a clip, and failing the whole render because the bed could not be made
    is the wrong trade.
    """

    @property
    def backend(self) -> MusicBedBackend:
        return MusicBedBackend.NONE

    @property
    def is_available(self) -> bool:
        return True

    def generate(self, spec: MusicSpec) -> MusicBed:
        return MusicBed(b"", spec.format, MusicBedBackend.NONE,
                        error="no music generator is configured on this device")


class ProceduralMusicBedGenerator(IMusicBedGenerator):
    """Sine tones from a scale, mixed and enveloped.

    DETERMINISTIC from the spec's seed, so the same spec makes the same bed -
    which matters because a person who liked yesterday's clip should be able to
    make it again.
    """

    #: Attack and release, in seconds. Short enough to be inaudible as a fade
    #: and long enough to remove the click: a step at 22050 Hz is broadband and
    #: the ear hears it as a tick, which is the single most common defect in
    #: generated audio.
    ENVELOPE_SECONDS = 0.02

    @property
    def backend(self) -> MusicBedBackend:
        return MusicBedBackend.PROCEDURAL

    @property
    def is_available(self) -> bool:
        return True

    @staticmethod
    def _next_random(state: int) -> tuple[int, float]:
        """A tiny LCG, so the bed does not depend on the platform's generator.

        `random` is seeded per process and shared; a bed that used it would
        change because something unrelated drew a number first.
        """
        state = (state * 1103515245 + 12345) & 0x7FFFFFFF
        return state, state / float(0x7FFFFFFF)

    def _envelope(self, index: int, total: int, rate: int) -> float:
        ramp = max(1, int(self.ENVELOPE_SECONDS * rate))
        if index < ramp:
            return index / ramp
        if index >= total - ramp:
            return max(0.0, (total - index) / ramp)
        return 1.0

    def generate(self, spec: MusicSpec) -> MusicBed:
        rate = spec.format.sample_rate_hz
        total = spec.frame_count
        if total <= 0:
            return MusicBed(b"", spec.format, self.backend,
                            error="a bed needs a duration")

        pool = spec.key.frequencies(octave=3, count=max(5, spec.voices * 2))
        state = spec.seed or 1
        samples = [0.0] * total
        note_frames = max(1, int(spec.seconds_per_beat * 2 * rate))

        for voice in range(max(1, spec.voices)):
            # Each voice starts at a different point so they do not all change
            # note together - simultaneous changes sound like a chord machine
            # rather than a bed.
            offset = voice * note_frames // max(1, spec.voices)
            position = -offset
            while position < total:
                state, r = self._next_random(state)
                frequency = pool[int(r * len(pool)) % len(pool)]
                length = min(note_frames, total - max(0, position))
                if length <= 0:
                    break
                for i in range(length):
                    n = position + i
                    if n < 0 or n >= total:
                        continue
                    envelope = self._envelope(i, length, rate)
                    samples[n] += math.sin(2 * math.pi * frequency * n / rate) * envelope
                position += note_frames

        # SCALED BY THE VOICE COUNT. Without this, three voices each reaching
        # 1.0 sum to 3.0, wrap in 16-bit and come out as a buzz that sounds
        # exactly like a broken decoder.
        scale = spec.level / max(1, spec.voices)
        pcm = WavWriter.from_samples(spec.format, [s * scale for s in samples])
        # `from_samples` returns a whole WAV; the bed holds raw PCM so a caller
        # can concatenate beds without splicing headers into the middle.
        return MusicBed(
            pcm[44:], spec.format, self.backend,
            spec.format.duration_of(len(pcm) - 44))


class MusicBedGeneratorResolver:
    """Picks a generator, preferring the one that is actually there.

    PROCEDURAL IS THE FLOOR and never absent. A resolver that could return
    nothing would make every caller handle a case that need not exist.
    """

    def __init__(self, generators: Sequence[IMusicBedGenerator] = ()) -> None:
        self._generators = tuple(generators)
        self._fallback = ProceduralMusicBedGenerator()

    def resolve(self, preferred: MusicBedBackend | None = None) -> IMusicBedGenerator:
        if preferred is not None:
            for g in self._generators:
                if g.backend is preferred and g.is_available:
                    return g
        for g in self._generators:
            if g.is_available and g.backend is not MusicBedBackend.NONE:
                return g
        return self._fallback

    def available_backends(self) -> tuple[MusicBedBackend, ...]:
        found = {g.backend for g in self._generators if g.is_available}
        found.add(MusicBedBackend.PROCEDURAL)
        return tuple(sorted(found, key=lambda b: b.value))


# ─────────────────────────────────────────────────────────────────────────────
# Playback across devices


@dataclass(frozen=True)
class MediaItem:
    """Something playable."""

    item_id: str
    title: str = ""
    #: A local path or a URL a peer can reach. Never a cloud identifier: a hub
    #: that only works when a service is up is not a hub.
    source: str = ""
    duration: timedelta = timedelta()
    media_type: str = ""


@dataclass(frozen=True)
class PlaybackPosition:
    """Where a stream is, and WHEN that was true.

    The timestamp is the whole point. A position sent between devices is stale
    the moment it is sent, so a receiver has to extrapolate - and it cannot
    without knowing how old the reading is.
    """

    item_id: str = ""
    position: timedelta = timedelta()
    is_playing: bool = False
    #: Monotonic seconds on the SENDING device. Monotonic rather than wall time
    #: because two phones' clocks disagree by seconds, which is an eternity for
    #: audio.
    at_monotonic: float = 0.0
    rate: float = 1.0

    def extrapolated(self, now_monotonic: float) -> timedelta:
        """Where it would be NOW, if it kept playing.

        Never runs backwards: a clock that reports an earlier reading than the
        one recorded would otherwise rewind the playhead, which is audible and
        alarming.
        """
        if not self.is_playing:
            return self.position
        elapsed = max(0.0, now_monotonic - self.at_monotonic)
        return self.position + timedelta(seconds=elapsed * self.rate)


class ISyncedPlayback(ABC):
    """Playback kept in step across devices."""

    @abstractmethod
    def play(self, item: MediaItem, at: timedelta = timedelta()) -> None: ...

    @abstractmethod
    def pause(self) -> None: ...

    @abstractmethod
    def seek(self, to: timedelta) -> None: ...

    @abstractmethod
    def position(self) -> PlaybackPosition: ...


class NullSyncedPlayback(ISyncedPlayback):
    """Plays nothing."""

    def play(self, item: MediaItem, at: timedelta = timedelta()) -> None:
        return None

    def pause(self) -> None:
        return None

    def seek(self, to: timedelta) -> None:
        return None

    def position(self) -> PlaybackPosition:
        return PlaybackPosition()


class NullMediaLibrary:
    """Knows about nothing."""

    def list(self) -> Sequence[MediaItem]:
        return ()

    def get(self, item_id: str) -> MediaItem | None:
        return None


class InMemorySyncedPlayback(ISyncedPlayback):
    """Keeps a position, for testing and for a device that is only following."""

    #: How far out of step before a follower jumps rather than drifts back.
    #: Below this, correcting by rate would be more noticeable than the error.
    RESYNC_THRESHOLD_SECONDS = 0.35

    def __init__(self, monotonic: Callable[[], float] | None = None) -> None:
        self._monotonic = monotonic or (lambda: 0.0)
        self._item: MediaItem | None = None
        self._position = timedelta()
        self._playing = False
        self._since = 0.0

    def play(self, item: MediaItem, at: timedelta = timedelta()) -> None:
        self._item = item
        self._position = at
        self._playing = True
        self._since = self._monotonic()

    def pause(self) -> None:
        # The elapsed time is FOLDED IN before the flag flips. Setting the flag
        # first loses everything played since the last event, and the playhead
        # jumps backwards on every pause.
        if self._playing:
            self._position += timedelta(seconds=self._monotonic() - self._since)
        self._playing = False

    def seek(self, to: timedelta) -> None:
        self._position = to
        self._since = self._monotonic()

    def position(self) -> PlaybackPosition:
        return PlaybackPosition(
            self._item.item_id if self._item else "", self._position,
            self._playing, self._since)

    def should_resync(self, remote: PlaybackPosition) -> bool:
        now = self._monotonic()
        mine = self.position().extrapolated(now)
        theirs = remote.extrapolated(now)
        return abs((mine - theirs).total_seconds()) > self.RESYNC_THRESHOLD_SECONDS


# ─────────────────────────────────────────────────────────────────────────────
# Wake word and transcription


@dataclass(frozen=True)
class ZipformerWakeConfig:
    """How the wake detector is tuned.

    THE THRESHOLD IS A TRADE, not a correctness setting. Too low and the phone
    wakes to the television; too high and a person says the phrase three times
    in front of somebody. The default leans towards missing, because a missed
    wake is an annoyance and a false wake is a microphone opening in a room
    where nobody asked it to.
    """

    threshold: float = 0.62
    #: Ignore anything for this long after a detection. Without it one utterance
    #: fires on several consecutive frames and the assistant answers itself.
    refractory: timedelta = timedelta(milliseconds=900)
    #: Frames the score must hold above the threshold. A single frame over is
    #: usually a door closing.
    consecutive_frames: int = 2
    sample_rate_hz: int = 16000
    frame_ms: int = 30


@dataclass(frozen=True)
class KwsDetection:
    """A wake phrase was heard."""

    phrase: str = ""
    score: float = 0.0
    at: timedelta = timedelta()
    #: How much audio before the phrase is worth keeping. A person usually
    #: starts the request in the same breath as the wake word, and discarding it
    #: makes them repeat themselves.
    lookback: timedelta = timedelta(milliseconds=500)


@dataclass(frozen=True)
class KwsProgress:
    """How close the current audio is, for a UI that shows listening.

    Shown so that a phrase which nearly fires is VISIBLE. A detector that only
    reports success leaves a person repeating a phrase with no idea whether
    anything is hearing them.
    """

    score: float = 0.0
    threshold: float = 0.0
    frames_held: int = 0

    @property
    def fraction(self) -> float:
        return 0.0 if self.threshold <= 0 else max(0.0, min(1.0, self.score / self.threshold))


class ZipformerKwsSpotter:
    """Streaming keyword spotting over a scoring callable.

    The model is not here; a `score` callable is supplied. What IS here is the
    part that goes wrong: the hold, the refractory period, and the fact that
    both are counted in AUDIO TIME rather than wall time, so a device that
    stalls does not silently change the tuning.
    """

    def __init__(
        self,
        config: ZipformerWakeConfig | None = None,
        score: Callable[[Sequence[float]], tuple[str, float]] | None = None,
    ) -> None:
        self._config = config or ZipformerWakeConfig()
        self._score = score
        self._held = 0
        self._elapsed = timedelta()
        self._muted_until = timedelta()
        self._last = KwsProgress(0.0, self._config.threshold, 0)

    @property
    def config(self) -> ZipformerWakeConfig:
        return self._config

    @property
    def progress(self) -> KwsProgress:
        return self._last

    def reset(self) -> None:
        self._held = 0
        self._muted_until = self._elapsed

    def push(self, frame: Sequence[float]) -> KwsDetection | None:
        """One frame in, a detection out or None.

        Time advances by the FRAME LENGTH, not by a clock. A phone that pauses
        for a garbage collection would otherwise appear to have been listening
        through it.
        """
        cfg = self._config
        self._elapsed += timedelta(milliseconds=cfg.frame_ms)
        if self._score is None:
            return None
        phrase, score = self._score(frame)

        if self._elapsed < self._muted_until:
            # Still counted and still reported, so a UI does not freeze during
            # the refractory period - it just cannot fire.
            self._last = KwsProgress(score, cfg.threshold, 0)
            return None

        if score >= cfg.threshold:
            self._held += 1
        else:
            self._held = 0
        self._last = KwsProgress(score, cfg.threshold, self._held)

        if self._held >= cfg.consecutive_frames:
            self._held = 0
            self._muted_until = self._elapsed + cfg.refractory
            return KwsDetection(phrase, score, self._elapsed)
        return None


class ZipformerWakeWordDetector:
    """A wake word over the spotter, with the phrase book.

    Matching is on a NORMALISED form - case folded, punctuation dropped - so
    "Hey B", "hey b" and "hey, B" are one phrase. A phrase book that treats them
    as three is a phrase book that fails for two thirds of the people who use
    it.
    """

    def __init__(
        self,
        phrases: Sequence[str] = (),
        config: ZipformerWakeConfig | None = None,
        score: Callable[[Sequence[float]], tuple[str, float]] | None = None,
    ) -> None:
        self._config = config or ZipformerWakeConfig()
        self._spotter = ZipformerKwsSpotter(self._config, score)
        self._phrases = {self.normalise(p) for p in phrases if p.strip()}

    @staticmethod
    def normalise(phrase: str) -> str:
        return " ".join(
            "".join(c for c in phrase.lower() if c.isalnum() or c.isspace()).split())

    @property
    def phrases(self) -> tuple[str, ...]:
        return tuple(sorted(self._phrases))

    @property
    def progress(self) -> KwsProgress:
        return self._spotter.progress

    def push(self, frame: Sequence[float]) -> KwsDetection | None:
        detection = self._spotter.push(frame)
        if detection is None:
            return None
        # An empty phrase book accepts ANY detection, so a build with no
        # configured phrase still wakes rather than being silently deaf.
        if self._phrases and self.normalise(detection.phrase) not in self._phrases:
            return None
        return detection


class WhisperTranscriber:
    """Transcription over a supplied engine.

    THE RATE CHECK IS THE POINT. Whisper wants 16 kHz mono, and feeding it
    22050 does not fail - it transcribes audio it believes is slower than it is,
    and produces confident nonsense. So the rate is checked and resampled rather
    than assumed.
    """

    #: What every Whisper variant expects. Not configurable, because it is a
    #: property of the model rather than a preference.
    REQUIRED_FORMAT = AudioPcmFormat(16000, 1, 16)

    def __init__(
        self,
        transcribe: Callable[[Sequence[float], str], str] | None = None,
        language: str = "",
    ) -> None:
        self._transcribe = transcribe
        self._language = language

    @property
    def is_available(self) -> bool:
        return self._transcribe is not None

    def prepare(self, format: AudioPcmFormat, samples: Sequence[float]) -> list[float]:
        """Downmixes and resamples to what the model needs."""
        mono = list(samples)
        if format.channels > 1:
            # AVERAGED, not left-channel-only. Taking one channel loses anything
            # panned away from it, and a phone's two microphones are not a
            # stereo image - they are the same voice with different noise.
            n = format.channels
            mono = [sum(mono[i:i + n]) / n for i in range(0, len(mono) - n + 1, n)]
        if format.sample_rate_hz != self.REQUIRED_FORMAT.sample_rate_hz:
            mono = WavIo.resample_linear(
                mono, format.sample_rate_hz, self.REQUIRED_FORMAT.sample_rate_hz)
        return mono

    def transcribe(
        self, format: AudioPcmFormat, samples: Sequence[float], language: str = ""
    ) -> str:
        if not self.is_available:
            raise RuntimeError("no transcription engine is loaded on this device")
        return self._transcribe(
            self.prepare(format, samples), language or self._language)


class WhisperNetTranscriber(WhisperTranscriber):
    """The managed binding.

    A separate type from the base because a host chooses ONE, and having a name
    for each makes which one is running visible in a diagnostics screen instead
    of a matter of inference.
    """

    def __init__(
        self,
        transcribe: Callable[[Sequence[float], str], str] | None = None,
        language: str = "",
        model_path: str = "",
    ) -> None:
        super().__init__(transcribe, language)
        self._model_path = model_path

    @property
    def model_path(self) -> str:
        return self._model_path

    @property
    def is_available(self) -> bool:
        """Needs BOTH a model file and a binding. Either alone is a transcriber
        that reports ready and then fails on the first call."""
        return super().is_available and bool(self._model_path)
