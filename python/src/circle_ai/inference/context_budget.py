"""Context-window token budget manager.

Port of ``CircleAI.Inference.ContextWindowBudgetManager`` — tracks token usage
against a fixed context window and signals when the KV cache should be
partially evicted to keep inference latency manageable.
"""
from __future__ import annotations

__all__ = ["ContextWindowBudgetManager"]


class ContextWindowBudgetManager:
    """Tracks token usage against a fixed context window.

    Mirrors ``CircleAI.Inference.ContextWindowBudgetManager``. ``context_size``
    and ``eviction_threshold`` are read-only after construction; ``used_tokens``
    accumulates via :meth:`record_exchange` and resets via :meth:`reset`.
    """

    __slots__ = ("_context_size", "_eviction_threshold", "_used_tokens")

    def __init__(self, context_size: int, eviction_threshold: float = 0.85) -> None:
        if context_size <= 0:
            raise ValueError("Context size must be greater than zero.")
        if eviction_threshold < 0.0 or eviction_threshold > 1.0:
            raise ValueError("Eviction threshold must be in the range [0, 1].")
        self._context_size = context_size
        self._eviction_threshold = eviction_threshold
        self._used_tokens = 0

    @property
    def context_size(self) -> int:
        """Maximum number of tokens the model's context window can hold."""
        return self._context_size

    @property
    def used_tokens(self) -> int:
        """Cumulative tokens consumed so far (prompt + completion)."""
        return self._used_tokens

    @property
    def remaining_tokens(self) -> int:
        """Tokens still available before the context window is full."""
        return self._context_size - self._used_tokens

    @property
    def fill_ratio(self) -> float:
        """Proportion of the context window currently occupied (0-1)."""
        return self._used_tokens / self._context_size

    @property
    def eviction_threshold(self) -> float:
        """Fill ratio at or above which :attr:`should_evict` becomes ``True``."""
        return self._eviction_threshold

    @property
    def should_evict(self) -> bool:
        """``True`` when the fill ratio has reached or exceeded the threshold."""
        return self.fill_ratio >= self._eviction_threshold

    def record_exchange(self, prompt_tokens: int, completion_tokens: int) -> None:
        """Record the token cost of one exchange (a prompt + its completion)."""
        if prompt_tokens < 0:
            raise ValueError("Token counts must not be negative.")
        if completion_tokens < 0:
            raise ValueError("Token counts must not be negative.")
        self._used_tokens += prompt_tokens + completion_tokens

    def calculate_eviction_count(self, target_fill_ratio: float = 0.50) -> int:
        """How many of the oldest tokens should be dropped so that
        :attr:`fill_ratio` returns to ``target_fill_ratio``.

        Returns 0 when the fill ratio is already at or below the target. The
        truncation of ``context_size * target_fill_ratio`` matches the C#
        ``(int)`` cast exactly.
        """
        if target_fill_ratio < 0.0 or target_fill_ratio > 1.0:
            raise ValueError("Target fill ratio must be in the range [0, 1].")
        target_used = int(self._context_size * target_fill_ratio)
        evict = self._used_tokens - target_used
        return evict if evict > 0 else 0

    def reset(self) -> None:
        """Reset the used-token counter to zero. Call after clearing the KV cache."""
        self._used_tokens = 0
