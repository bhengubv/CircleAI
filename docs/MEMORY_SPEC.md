# Circle AI — Memory Specification

This document defines the **exact math** for `AffectState` mutations and
`PersonaState` system-prompt generation. Every language port must produce
**bit-identical results** (within float32 epsilon) for these operations.

Verified by `fixtures/affect_state.json` (12 test vectors, cross-language CI).

---

## 1. AffectState — Fields & Defaults

| Field | Type | Default | Semantics |
|-------|------|---------|-----------|
| `Curiosity` | float32 | 0.5 | 0 = bored, 1 = fascinated. Drives proactive follow-up. |
| `Engagement` | float32 | 0.5 | 0 = disengaged, 1 = fully engaged. |
| `Uncertainty` | float32 | 0.2 | 0 = confident, 1 = confused. High → ask clarifying questions. |
| `Rapport` | float32 | 0.0 | 0 = stranger, 1 = deep rapport. Grows slowly over sessions. |
| `Energy` | float32 | 0.5 | 0 = subdued, 1 = energetic. Mirrors interaction pace. |

All fields are **clamped to [0.0, 1.0]** after every operation.

---

## 2. Signal & Decay Operations

### 2.1 `ApplyPositiveSignal()`

Applied after a positive interaction (user thumbs-up, continued engagement, etc.).

```
Engagement  ← clamp(Engagement  + 0.02, 0, 1)
Rapport     ← clamp(Rapport     + 0.01, 0, 1)
Uncertainty ← clamp(Uncertainty − 0.02, 0, 1)
```

`Curiosity` and `Energy` are **not modified**.

### 2.2 `ApplyNegativeSignal()`

Applied after a negative interaction (user thumbs-down, abrupt session end, etc.).

```
Engagement  ← clamp(Engagement  − 0.03, 0, 1)
Uncertainty ← clamp(Uncertainty + 0.03, 0, 1)
```

`Rapport`, `Curiosity`, and `Energy` are **not modified**.

### 2.3 `ApplyIdleDecay(idle: duration)`

Applied when the user has been inactive. Drift Engagement and Energy back toward
the neutral midpoint (0.5). All other dimensions are **not modified**.

```
hours ← idle.TotalHours   // as float32 (or float64 → cast to float32)
decay ← min(0.3, hours × 0.02)

Engagement ← Lerp(Engagement, 0.5, decay)
Energy     ← Lerp(Energy,     0.5, decay)
```

#### Lerp definition

```
Lerp(a, b, t) = a + (b − a) × clamp(t, 0, 1)
```

`clamp(t, 0, 1)` caps `t` to [0.0, 1.0] before the multiply. Because `decay` is already
bounded by `min(0.3, ...)`, the `clamp` inside `Lerp` is only a safety guard.

#### Decay cap

`min(0.3, ...)` means that no matter how long the user is idle, Engagement and Energy
can only move **at most 30 % of the way toward 0.5** in a single call. This prevents
a 48-hour gap from collapsing the state entirely.

---

## 3. `ToSystemPromptHint()` — AffectState

Returns a compact hint block (or empty string) for injection into the B! system prompt.
Only emits lines that deviate meaningfully from the neutral band.

```
hints = []

if Curiosity   > 0.7  → append "You are deeply curious about this topic — ask a follow-up question."
if Engagement  > 0.7  → append "You are fully engaged — be enthusiastic and thorough."
if Engagement  < 0.3  → append "Keep your response brief and to the point."
if Uncertainty > 0.6  → append "You are uncertain — ask a clarifying question before answering."
if Rapport     > 0.7  → append "You know this user well — use a warm, familiar tone."
if Energy      < 0.3  → append "Keep your response calm and measured."
if Energy      > 0.8  → append "You are energetic — be upbeat and concise."

if hints.isEmpty → return ""
return "[Affect state]\n" + hints.join("\n") + "\n"
```

---

## 4. `ToSystemPromptHint()` — PersonaState

Returns a compact persona instruction block (or empty string) based on deviations
from the default style.

```
hints = []

if Verbosity ≠ "balanced"          → append "Keep responses {Verbosity}."
if Formality == "casual"           → append "Use a casual, friendly tone."
if Formality == "formal"           → append "Maintain a formal, professional tone."
if PreferredLocale is not empty    → append "Respond in the language appropriate for locale {PreferredLocale}."

if hints.isEmpty → return ""
return "[User preferences]\n" + hints.join("\n") + "\n"
```

See `fixtures/persona_state.json` for 6 exact input/output test vectors.

---

## 5. Cross-Language Precision Notes

1. Use **IEEE 754 single-precision float** (32-bit) for all five AffectState fields.
   Languages that default to 64-bit (Python `float`, TypeScript `number`, Go `float64`,
   Kotlin `Double`) must **cast the result to float32** before storing it, or accumulate
   in float32 throughout.

2. The test vectors in `fixtures/affect_state.json` are given as decimal strings. Compare
   with an epsilon of **1×10⁻⁶** (i.e. `abs(result − expected) < 0.000001`).

3. **Do not** apply banker's rounding, hardware-accelerated SIMD, or FMA (fused
   multiply-add) optimisations that change the mantissa. Compute sequentially as
   written above.

4. The `LastUpdatedUtc` / `LastUpdatedAt` timestamp field is **excluded** from test
   vectors because it is set to "now" at call time and cannot be pre-computed.

---

## 6. Verify

Run `fixtures/affect_state.json` against your implementation. Each entry has:

- `id` — test name
- `description` — what the test exercises
- `input` — the `AffectState` going in
- `operation` — `"positive_signal"`, `"negative_signal"`, or `"idle_decay"`
- `operationParam` — for decay: `{ "hours": N }`; for signal operations: `{}`
- `expected` — the resulting `AffectState` (excluding timestamp fields)
