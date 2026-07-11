# answering_machine_detector.py
#
# Port of CircleAI.Telephony AnsweringMachineDetector.cs (C# — the EXACT spec).
#
# (3.3.0) Heuristic AMD: classify whether the answering side of an outbound call
# is a human or an answering machine, based on the length of the first
# contiguous speech burst and the timing of any follow-up audio. Cheaper than
# carrier-side AMD; runs on the audio frames we already have, no extra cost.
#
# C# ReadOnlySpan<byte> pcm + MemoryMarshal.Cast<byte,short> -> Python bytes with
# array('h') for the little-endian PCM-16 view (the tree targets little-endian
# hosts; array is native-endian, matching the C# little-endian assumption on
# x86/ARM). RMS energy math and the decision ladder mirror the C# exactly. All
# mutable state is lock-guarded (C# monitor). AmdOptions int? fields keep the
# C# ``...OrDefault`` accessors.

from __future__ import annotations

import array
import math
import threading
from dataclasses import dataclass
from datetime import timedelta
from enum import IntEnum
from typing import Optional


class AmdVerdict(IntEnum):
    """(3.3.0) Verdict from the answering-machine detector."""

    UNKNOWN = 0
    HUMAN = 1
    ANSWERING_MACHINE = 2


@dataclass(frozen=True, slots=True)
class AmdOptions:
    """(3.3.0) Heuristic AMD configuration.

    ``human_max_first_utterance_ms``: above this length it's likely a machine (default 1800 ms).
    ``human_min_first_utterance_ms``: below this it's too short to decide (default 300 ms).
    ``max_observation_window``: stop accumulating once this elapses (default 3500 ms).
    ``silence_frame_threshold_ms``: frames silent for this long end the current utterance (default 250 ms).
    """

    human_max_first_utterance_ms: Optional[int] = None
    human_min_first_utterance_ms: Optional[int] = None
    max_observation_window: Optional[int] = None
    silence_frame_threshold_ms: Optional[int] = None

    @property
    def human_max_first_utterance_ms_or_default(self) -> int:
        return self.human_max_first_utterance_ms if self.human_max_first_utterance_ms is not None else 1800

    @property
    def human_min_first_utterance_ms_or_default(self) -> int:
        return self.human_min_first_utterance_ms if self.human_min_first_utterance_ms is not None else 300

    @property
    def max_observation_window_or_default(self) -> int:
        return self.max_observation_window if self.max_observation_window is not None else 3500

    @property
    def silence_frame_threshold_ms_or_default(self) -> int:
        return self.silence_frame_threshold_ms if self.silence_frame_threshold_ms is not None else 250


class AnsweringMachineDetector:
    """(3.3.0) Frame-by-frame AMD. Feed PCM-16 frames in until
    :attr:`current_verdict` stabilises."""

    def __init__(self, options: Optional[AmdOptions] = None) -> None:
        self._options = options if options is not None else AmdOptions()
        self._gate = threading.Lock()
        self._first_utterance_length = timedelta(0)
        self._accumulated_audio = timedelta(0)
        self._utterance_in_progress = False
        self._trailing_silence = timedelta(0)
        self._verdict = AmdVerdict.UNKNOWN

    @property
    def current_verdict(self) -> AmdVerdict:
        with self._gate:
            return self._verdict

    def observe(self, pcm_frame: bytes, sample_rate_hz: int) -> AmdVerdict:
        """(3.3.0) Feed one frame of PCM-16 mono. Returns the (possibly updated) verdict."""
        if sample_rate_hz <= 0:
            raise ValueError("sample_rate_hz out of range")
        if len(pcm_frame) < 2:
            return self.current_verdict

        frame_duration = timedelta(milliseconds=1000.0 * (len(pcm_frame) // 2) / sample_rate_hz)
        is_speech = _frame_has_speech(pcm_frame)

        with self._gate:
            if self._verdict != AmdVerdict.UNKNOWN:
                return self._verdict

            self._accumulated_audio += frame_duration

            if is_speech:
                if not self._utterance_in_progress:
                    self._utterance_in_progress = True
                self._first_utterance_length += frame_duration
                self._trailing_silence = timedelta(0)
            elif self._utterance_in_progress:
                self._trailing_silence += frame_duration
                if self._trailing_silence.total_seconds() * 1000 >= self._options.silence_frame_threshold_ms_or_default:
                    self._utterance_in_progress = False

            # Decide.
            first_ms = self._first_utterance_length.total_seconds() * 1000
            if first_ms >= self._options.human_max_first_utterance_ms_or_default:
                self._verdict = AmdVerdict.ANSWERING_MACHINE
            elif (
                not self._utterance_in_progress
                and first_ms >= self._options.human_min_first_utterance_ms_or_default
                and first_ms < self._options.human_max_first_utterance_ms_or_default
            ):
                self._verdict = AmdVerdict.HUMAN
            elif self._accumulated_audio.total_seconds() * 1000 >= self._options.max_observation_window_or_default:
                self._verdict = (
                    AmdVerdict.UNKNOWN
                    if first_ms < self._options.human_min_first_utterance_ms_or_default
                    else AmdVerdict.ANSWERING_MACHINE
                )
            return self._verdict

    def reset(self) -> None:
        with self._gate:
            self._first_utterance_length = timedelta(0)
            self._accumulated_audio = timedelta(0)
            self._utterance_in_progress = False
            self._trailing_silence = timedelta(0)
            self._verdict = AmdVerdict.UNKNOWN


def _frame_has_speech(pcm: bytes) -> bool:
    energy_threshold = 0.012
    # PCM-16 little-endian view. Drop a trailing odd byte so array('h') gets an
    # even length (mirrors the C# Cast which yields floor(len/2) shorts).
    usable = len(pcm) - (len(pcm) % 2)
    samples = array.array("h")
    samples.frombytes(pcm[:usable])
    if len(samples) == 0:
        return False
    sum_squares = 0.0
    for s in samples:
        sum_squares += s * s
    rms = math.sqrt(sum_squares / len(samples)) / 32767
    return rms >= energy_threshold
