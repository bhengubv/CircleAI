# voice/energy_vad_detector.py
#
# Port of CircleAI.Voice/EnergyVadDetector.cs (C# — the EXACT spec).
#
# Energy-based IVoiceActivityDetector that uses RMS (Root Mean Square) energy to
# distinguish speech from silence. Pure managed code, no external dependencies.
#
# The detector processes incoming audio in fixed-size frames. When a frame's RMS
# energy exceeds EnergyThreshold it is speech. Speech frames are buffered until a
# configurable number of consecutive below-threshold frames (SilenceFrameCount)
# are observed, at which point the buffered speech segment is yielded.

from __future__ import annotations

import math
import struct
from typing import AsyncIterator

from .contracts import IVoiceActivityDetector, VadSegment

_SHORT_SCALE = 32768.0


def _compute_rms_energy(frame_bytes: bytes) -> float:
    """RMS energy of a PCM 16-bit frame, normalised to [0, 1]. Mirrors the C#
    ``ComputeRmsEnergy``."""
    n = len(frame_bytes) // 2
    if n == 0:
        return 0.0
    samples = struct.unpack_from("<" + "h" * n, frame_bytes, 0)
    sum_squares = 0.0
    for s in samples:
        normalised = s / _SHORT_SCALE
        sum_squares += normalised * normalised
    return math.sqrt(sum_squares / n)


class EnergyVadDetector(IVoiceActivityDetector):
    """Energy-based :class:`IVoiceActivityDetector` using RMS energy + silence-run
    end-of-speech detection. Mirrors ``CircleAI.Voice.EnergyVadDetector``.

    :param energy_threshold: RMS energy threshold in [0, 1]. Frames above this are
        classified as speech. Default 0.02 works for close-talking mics.
    :param silence_frames: Consecutive below-threshold frames that constitute
        end-of-speech. Default 15 frames = 300 ms at 20 ms/frame.
    :param frame_size_bytes: Size of each analysis frame in bytes. Default 640 =
        20 ms at 16 kHz mono 16-bit.
    """

    __slots__ = ("_energy_threshold", "_silence_frame_count", "_frame_size_bytes")

    def __init__(
        self,
        energy_threshold: float = 0.02,
        silence_frames: int = 15,
        frame_size_bytes: int = 640,
    ) -> None:
        if silence_frames <= 0:
            raise ValueError("silence_frames must be positive")
        if frame_size_bytes <= 0:
            raise ValueError("frame_size_bytes must be positive")
        if energy_threshold < 0:
            raise ValueError("energy_threshold must be non-negative")
        self._energy_threshold = energy_threshold
        self._silence_frame_count = silence_frames
        self._frame_size_bytes = frame_size_bytes

    @property
    def energy_threshold(self) -> float:
        return self._energy_threshold

    @property
    def silence_frame_count(self) -> int:
        return self._silence_frame_count

    @property
    def frame_size_bytes(self) -> int:
        return self._frame_size_bytes

    async def detect_async(
        self, audio_stream: AsyncIterator[bytes], cancellation_token: object = None
    ) -> AsyncIterator[VadSegment]:
        if audio_stream is None:
            raise ValueError("audio_stream")

        frame_size = self._frame_size_bytes
        # Carry-over buffer for bytes that don't fill a complete frame.
        residual = bytearray()
        # Accumulator for the current speech segment.
        speech_buffer = bytearray()

        in_speech = False
        consecutive_silence_frames = 0

        async for chunk in audio_stream:
            if len(chunk) == 0:
                continue

            residual.extend(chunk)

            offset = 0
            available = len(residual)
            while available - offset >= frame_size:
                frame = bytes(residual[offset : offset + frame_size])
                rms = _compute_rms_energy(frame)
                is_speech_frame = rms >= self._energy_threshold

                if is_speech_frame:
                    if not in_speech:
                        in_speech = True
                        consecutive_silence_frames = 0
                        speech_buffer.clear()
                    else:
                        consecutive_silence_frames = 0
                    speech_buffer.extend(frame)
                elif in_speech:
                    # Still in speech region; buffer silence frames in case speech
                    # resumes (avoids cutting off mid-word).
                    speech_buffer.extend(frame)
                    consecutive_silence_frames += 1
                    if consecutive_silence_frames >= self._silence_frame_count:
                        # End of speech — emit the buffered segment.
                        in_speech = False
                        consecutive_silence_frames = 0
                        audio = bytes(speech_buffer)
                        speech_buffer.clear()
                        yield VadSegment(audio, True)
                # else: silence while not in speech — discard.

                offset += frame_size

            # Drop the consumed prefix; keep unconsumed residual bytes.
            if offset > 0:
                del residual[:offset]

        # Stream ended — if we were mid-speech, emit what we have.
        if in_speech and len(speech_buffer) > 0:
            yield VadSegment(bytes(speech_buffer), True)


__all__ = ["EnergyVadDetector"]
