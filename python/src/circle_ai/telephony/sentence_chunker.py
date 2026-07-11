# sentence_chunker.py
#
# Port of CircleAI.Telephony SentenceChunker.cs (C# — the EXACT spec).
#
# (3.3.0) Stream-friendly sentence chunker. Accepts streamed LLM tokens and
# emits whole sentences as soon as they're complete, so TTS can speak them out
# before the full response finishes — cuts time-to-first-audio dramatically.
#
# C# StringBuilder + monitor lock -> a list-of-str buffer (joined lazily) under
# a threading.Lock. C# IEnumerable<string> from PushToken -> a plain list return
# (eager); callers iterate the list identically. The terminal-punctuation set
# includes the CJK full-width marks the C# uses verbatim.

from __future__ import annotations

import threading
from typing import List, Optional, Tuple

_TERMINAL_PUNCTUATION = (".", "!", "?", "。", "！", "？")
_TRAILERS = (' ', '\t', '\n', '\r', '\f', '\v', '"', "'", ")")


class SentenceChunker:
    """(3.3.0) Streaming sentence chunker."""

    def __init__(self, min_sentence_length: int = 4) -> None:
        """``min_sentence_length``: sentences below this character count are
        buffered with the next one (avoids "1." / "Mr." splits)."""
        self._buffer = ""
        self._lock = threading.Lock()
        self._min_sentence_length = min_sentence_length

    def push_token(self, token: str) -> List[str]:
        """(3.3.0) Push a token; receive any complete sentences ready to emit."""
        if not token:
            return []
        ready: List[str] = []
        with self._lock:
            self._buffer += token
            while True:
                chunk, kept = self._extract_next(self._buffer)
                if chunk is None:
                    break
                self._buffer = kept
                ready.append(chunk)
        return ready

    def flush(self) -> str:
        """(3.3.0) Flush whatever's buffered as a final fragment, regardless of
        punctuation."""
        with self._lock:
            s = self._buffer
            self._buffer = ""
            return s

    def _extract_next(self, buffer: str) -> Tuple[Optional[str], str]:
        search_from = 0
        length = len(buffer)
        while search_from < length:
            idx = _index_of_any(buffer, _TERMINAL_PUNCTUATION, search_from)
            if idx < 0:
                return (None, buffer)

            # Consume any trailing whitespace + closing quotes after the punctuation.
            end = idx + 1
            while end < length and buffer[end] in _TRAILERS:
                end += 1

            candidate = buffer[:end].strip()
            if len(candidate) >= self._min_sentence_length:
                return (candidate, buffer[end:])
            # Too short — keep extending past this punctuation.
            search_from = end
        return (None, buffer)


def _index_of_any(text: str, chars: Tuple[str, ...], start: int) -> int:
    """C# ``string.IndexOfAny(char[], int)`` — first index at/after ``start`` of
    any char in ``chars``, or -1."""
    best = -1
    for ch in chars:
        pos = text.find(ch, start)
        if pos >= 0 and (best < 0 or pos < best):
            best = pos
    return best
