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

    def to_vad(self) -> "AffectVad":
        """Project this AffectState into the derived 3-dimensional VAD view."""
        return AffectVad.from_state(self)


def _lerp(a: float, b: float, t: float) -> float:
    """Linear interpolation. t is clamped to [0, 1]."""
    t = max(0.0, min(1.0, t))
    return a + (b - a) * t


@dataclass(frozen=True)
class AffectVad:
    """Derived Russell-PAD (Valence / Arousal / Dominance) view of an AffectState.

    The Circle AI SDK uses a 5-dimensional affect model (curiosity, engagement,
    uncertainty, rapport, energy). Some downstream systems — including external
    affective-computing research tooling and HR/health analytics pipelines —
    expect Russell's PAD/VAD model. AffectVad is the DERIVED 3-dimensional view
    of the same underlying state; it does not replace AffectState.

    Derivation (all results clamped to [0.0, 1.0]):
        valence   = (engagement + rapport + (1 - uncertainty)) / 3
        arousal   = (energy * 2 + curiosity + uncertainty) / 4
        dominance = (engagement + (1 - uncertainty)) / 2

    These formulas are the cross-language fixture contract — see
    fixtures/affect_vad_derivation.json. Any change to the math must update
    every port and every fixture vector.
    """

    # Pleasure ↔ displeasure axis. 1.0 = maximally pleasant, 0.0 = maximally unpleasant.
    valence: float

    # Activation ↔ deactivation axis. 1.0 = maximally aroused/alert,
    # 0.0 = maximally calm/dormant.
    arousal: float

    # In-control ↔ submissive axis. 1.0 = maximally in control,
    # 0.0 = maximally submissive/overwhelmed.
    dominance: float

    @classmethod
    def from_state(cls, state: "AffectState") -> "AffectVad":
        """Compute the VAD projection of an :class:`AffectState`.

        Output components are clamped to ``[0.0, 1.0]``.
        """
        v = (state.engagement + state.rapport + (1.0 - state.uncertainty)) / 3.0
        a = (state.energy * 2.0 + state.curiosity + state.uncertainty) / 4.0
        d = (state.engagement + (1.0 - state.uncertainty)) / 2.0
        return cls(
            valence=max(0.0, min(1.0, v)),
            arousal=max(0.0, min(1.0, a)),
            dominance=max(0.0, min(1.0, d)),
        )
