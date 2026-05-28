from __future__ import annotations

from dataclasses import dataclass, field
from datetime import datetime, timezone


def _utc_now() -> datetime:
    return datetime.now(timezone.utc)


@dataclass
class AffectState:
    """B!'s current emotional/engagement state — the HER affect layer.

    Five float dimensions, all 0.0–1.0. Persisted per-user and injected
    into the system prompt to shape response tone and initiative.

    CRITICAL: the math in apply_positive_signal, apply_negative_signal,
    and apply_idle_decay is byte-identical to the C# reference
    implementation. Do not change the constants.
    """

    user_id: str = "default"
    last_updated_utc: datetime = field(default_factory=_utc_now)

    curiosity: float = 0.5     # 0=bored, 1=fascinated
    engagement: float = 0.5   # 0=disengaged, 1=fully engaged
    uncertainty: float = 0.2  # 0=confident, 1=confused
    rapport: float = 0.0      # 0=stranger, 1=deep rapport
    energy: float = 0.5       # 0=subdued, 1=energetic

    def apply_positive_signal(self) -> None:
        """Apply a positive interaction: nudge Engagement and Rapport up."""
        self.engagement = min(1.0, self.engagement + 0.02)
        self.rapport = min(1.0, self.rapport + 0.01)
        self.uncertainty = max(0.0, self.uncertainty - 0.02)
        self.last_updated_utc = _utc_now()

    def apply_negative_signal(self) -> None:
        """Apply a negative interaction: nudge Engagement down."""
        self.engagement = max(0.0, self.engagement - 0.03)
        self.uncertainty = min(1.0, self.uncertainty + 0.03)
        self.last_updated_utc = _utc_now()

    def apply_idle_decay(self, idle_hours: float) -> None:
        """Apply idle-time decay: Engagement and Energy drift toward 0.5."""
        decay = min(0.3, idle_hours * 0.02)
        self.engagement = _lerp(self.engagement, 0.5, decay)
        self.energy = _lerp(self.energy, 0.5, decay)
        self.last_updated_utc = _utc_now()

    def to_system_prompt_hint(self) -> str:
        """Compact affect hint for injection into the system prompt."""
        hints: list[str] = []

        if self.curiosity > 0.7:
            hints.append("You are deeply curious about this topic — ask a follow-up question.")
        if self.engagement > 0.7:
            hints.append("You are fully engaged — be enthusiastic and thorough.")
        if self.engagement < 0.3:
            hints.append("Keep your response brief and to the point.")
        if self.uncertainty > 0.6:
            hints.append("You are uncertain — ask a clarifying question before answering.")
        if self.rapport > 0.7:
            hints.append("You know this user well — use a warm, familiar tone.")
        if self.energy < 0.3:
            hints.append("Keep your response calm and measured.")
        if self.energy > 0.8:
            hints.append("You are energetic — be upbeat and concise.")

        if not hints:
            return ""
        return "[Affect state]\n" + "\n".join(hints) + "\n"


def _lerp(a: float, b: float, t: float) -> float:
    """Linear interpolation. t is clamped to [0, 1]."""
    t = max(0.0, min(1.0, t))
    return a + (b - a) * t
