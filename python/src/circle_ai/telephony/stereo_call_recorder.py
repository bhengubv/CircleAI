# stereo_call_recorder.py
#
# Port of CircleAI.Telephony StereoCallRecorder.cs (C# — the EXACT spec).
#
# (3.3.0) Interleave inbound (caller) and outbound (agent) PCM-16 mono audio into
# a single stereo WAV file. Left channel = caller, right = agent. Sync is
# wall-clock based: caller frames go in at the time they arrive, agent frames at
# the time they're sent, and gaps are filled with silence.
#
# C# Stream -> a binary IO object (io.BytesIO / an open('wb') file). The
# seek/tell/backfill path uses Stream.CanSeek/Position -> obj.seekable()/tell()/
# seek(). struct writes little-endian PCM-16 (BinaryPrimitives.WriteInt16LE). The
# 44-byte placeholder header + Finalise backfill are byte-identical. Both the C#
# IAsyncDisposable and IDisposable are exposed (dispose / dispose_async +
# with / async with). ``finalize`` keeps the C# method name.

from __future__ import annotations

import struct
import threading
from typing import BinaryIO


class StereoCallRecorder:
    """(3.3.0) Records a call to disk as a stereo PCM-16 WAV."""

    def __init__(self, output: BinaryIO, sample_rate_hz: int, leave_open: bool = False) -> None:
        if output is None:
            raise ValueError("output must not be None")
        if sample_rate_hz <= 0:
            raise ValueError("sample_rate_hz out of range")
        self._output = output
        self._sample_rate_hz = sample_rate_hz
        self._leave_open = leave_open
        self._gate = threading.Lock()
        self._samples_written = 0  # total interleaved sample pairs
        self._header_written = False

    def write_caller_frame(self, pcm_frame: bytes) -> None:
        """(3.3.0) Write inbound (caller) PCM-16 mono audio. Caller side is left channel."""
        self._write_side(pcm_frame, is_caller=True)

    def write_agent_frame(self, pcm_frame: bytes) -> None:
        """(3.3.0) Write outbound (agent) PCM-16 mono audio. Agent side is right channel."""
        self._write_side(pcm_frame, is_caller=False)

    def finalize(self) -> None:
        """(3.3.0) Finalise the WAV header. After this, no more writes are allowed."""
        with self._gate:
            self._finalise_locked()

    def _write_side(self, pcm_frame: bytes, is_caller: bool) -> None:
        if pcm_frame is None or len(pcm_frame) < 2:
            return
        with self._gate:
            self._ensure_header()
            samples = len(pcm_frame) // 2
            for i in range(samples):
                mono = struct.unpack_from("<h", pcm_frame, i * 2)[0]
                if is_caller:
                    stereo = struct.pack("<hh", mono, 0)
                else:
                    stereo = struct.pack("<hh", 0, mono)
                self._output.write(stereo)
                self._samples_written += 1

    def _ensure_header(self) -> None:
        if self._header_written:
            return
        # Reserve 44 bytes for the WAV header — values backfilled in finalize.
        self._output.write(bytes(44))
        self._header_written = True

    def _finalise_locked(self) -> None:
        if not self._header_written:
            return
        data_size = self._samples_written * 4  # 2 channels x 2 bytes
        chunk_size = 36 + data_size
        if not self._output.seekable():
            # Streams that can't seek can't backfill — accept the placeholder header.
            return
        saved = self._output.tell()
        self._output.seek(0)
        header = bytearray(44)
        header[0:4] = b"RIFF"
        struct.pack_into("<i", header, 4, chunk_size)
        header[8:12] = b"WAVE"
        header[12:16] = b"fmt "
        struct.pack_into("<i", header, 16, 16)  # Subchunk1Size
        struct.pack_into("<h", header, 20, 1)  # PCM
        struct.pack_into("<h", header, 22, 2)  # channels
        struct.pack_into("<i", header, 24, self._sample_rate_hz)
        struct.pack_into("<i", header, 28, self._sample_rate_hz * 4)  # byte rate
        struct.pack_into("<h", header, 32, 4)  # block align
        struct.pack_into("<h", header, 34, 16)  # bits per sample
        header[36:40] = b"data"
        struct.pack_into("<i", header, 40, data_size)
        self._output.write(bytes(header))
        self._output.seek(saved)
        self._output.flush()

    def dispose(self) -> None:
        self.finalize()
        if not self._leave_open:
            self._output.close()

    async def dispose_async(self) -> None:
        self.finalize()
        if not self._leave_open:
            self._output.close()

    def __enter__(self) -> "StereoCallRecorder":
        return self

    def __exit__(self, *exc_info: object) -> None:
        self.dispose()

    async def __aenter__(self) -> "StereoCallRecorder":
        return self

    async def __aexit__(self, *exc_info: object) -> None:
        await self.dispose_async()
