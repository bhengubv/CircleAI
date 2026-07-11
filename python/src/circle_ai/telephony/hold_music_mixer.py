# hold_music_mixer.py
#
# Port of CircleAI.Telephony HoldMusicMixer.cs (C# — the EXACT spec).
#
# (3.3.0) Background-audio mixer for call-on-hold experiences. Loops a music
# track and mixes the AI's speech on top at adjustable gain. Ducks the
# background automatically when speech frames arrive.
#
# C# ``int MixFrame(ReadOnlySpan<byte> speech, Span<byte> destination)`` writes
# into a caller-supplied destination and returns the byte count. Python has no
# idiomatic caller-owned output span, so mix_frame takes the speech bytes plus an
# ``output_length`` (defaulting to len(speech_frame); required — and used — when
# rendering plain background with no speech) and RETURNS the mixed PCM-16 bytes.
# The loop-cursor advance + 16-bit-boundary realignment is preserved verbatim so
# the looped background stays sample-aligned. struct handles little-endian PCM-16.

from __future__ import annotations

import struct
from typing import Optional

_SHORT_MAX = 32767
_SHORT_MIN = -32768


def _clamp_int(value: int, lo: int, hi: int) -> int:
    if value < lo:
        return lo
    if value > hi:
        return hi
    return value


class HoldMusicMixer:
    """(3.3.0) Background audio mixer for hold music."""

    def __init__(self, background_loop: bytes, background_gain: float = 0.6, ducked_gain: float = 0.15) -> None:
        """``background_loop``: PCM-16 mono buffer that the mixer loops over.
        ``background_gain``: gain when no speech (0..1). Default 0.6.
        ``ducked_gain``: gain while speech is being mixed (0..1). Default 0.15.
        """
        if background_loop is None:
            raise ValueError("background_loop must not be None")
        if len(background_loop) < 2:
            raise ValueError("Background loop must contain at least one PCM-16 sample.")
        if background_gain < 0 or background_gain > 1:
            raise ValueError("background_gain out of range")
        if ducked_gain < 0 or ducked_gain > 1:
            raise ValueError("ducked_gain out of range")
        self._background_loop = background_loop
        self._background_gain = background_gain
        self._ducked_gain = ducked_gain
        self._loop_cursor = 0

    def reset(self) -> None:
        """Reset the loop cursor to the start."""
        self._loop_cursor = 0

    def mix_frame(self, speech_frame: bytes, output_length: Optional[int] = None) -> bytes:
        """(3.3.0) Mix ``speech_frame`` on top of looped background and return the
        result. Pass empty speech bytes (with ``output_length`` set) to render
        plain background."""
        has_speech = speech_frame is not None and len(speech_frame) >= 2
        if output_length is None:
            output_length = len(speech_frame) if speech_frame is not None else 0
        if output_length < 2:
            return b""
        frame_length = len(speech_frame) if has_speech else output_length
        if output_length < frame_length:
            raise ValueError("destination must be at least as long as the speech frame.")

        gain = self._ducked_gain if has_speech else self._background_gain

        dest = bytearray(frame_length)
        loop_len = len(self._background_loop)
        i = 0
        while i < frame_length:
            if i + 2 > frame_length:
                break
            speech_sample = struct.unpack_from("<h", speech_frame, i)[0] if has_speech else 0

            # Pull background sample from the loop, wrapping as needed.
            bg_sample = struct.unpack_from("<h", self._background_loop, self._loop_cursor)[0]
            self._loop_cursor = (self._loop_cursor + 2) % loop_len
            if self._loop_cursor % 2 != 0:
                self._loop_cursor -= 1  # align to 16-bit boundary

            mixed = speech_sample + int(bg_sample * gain)
            mixed = _clamp_int(mixed, _SHORT_MIN, _SHORT_MAX)
            struct.pack_into("<h", dest, i, mixed)
            i += 2
        return bytes(dest)
