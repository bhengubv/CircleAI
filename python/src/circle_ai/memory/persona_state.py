from __future__ import annotations

from dataclasses import dataclass, field
from datetime import datetime, timezone
from typing import Optional


def _utc_now() -> datetime:
    return datetime.now(timezone.utc)


@dataclass
class PersonaState:
    """B!'s dynamic persona state for a specific user.

    Persisted between sessions and injected into the system prompt to shape
    tone, vocabulary, and topical depth.
    """

    user_id: str = "default"
    last_updated_utc: datetime = field(default_factory=_utc_now)

    verbosity: str = "balanced"        # "brief" | "balanced" | "detailed"
    formality: str = "neutral"         # "casual" | "neutral" | "formal"
    preferred_locale: Optional[str] = None  # IETF BCP-47; None = match device

    topic_weights: dict[str, float] = field(default_factory=dict)
    disfavoured_topics: set[str] = field(default_factory=set)

    total_interactions: int = 0
    positive_signals: int = 0
    negative_signals: int = 0

    @property
    def satisfaction_score(self) -> Optional[float]:
        """Derived satisfaction 0.0–1.0; None when fewer than 10 signals."""
        total = self.positive_signals + self.negative_signals
        if total < 10:
            return None
        return self.positive_signals / total

    def to_system_prompt_hint(self) -> str:
        """Compact persona instruction block for the B! system prompt."""
        hints: list[str] = []

        if self.verbosity != "balanced":
            hints.append(f"Keep responses {self.verbosity}.")

        if self.formality == "casual":
            hints.append("Use a casual, friendly tone.")
        elif self.formality == "formal":
            hints.append("Maintain a formal, professional tone.")

        if self.preferred_locale:
            hints.append(
                f"Respond in the language appropriate for locale {self.preferred_locale}."
            )

        if not hints:
            return ""
        return "[User preferences]\n" + "\n".join(hints) + "\n"
