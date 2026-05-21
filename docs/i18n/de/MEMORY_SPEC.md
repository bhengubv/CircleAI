# Circle AI — Speicherspezifikation

Dieses Dokument definiert die **exakte Mathematik** für `AffectState`-Mutationen und
die Generierung von `PersonaState`-System-Prompts. Jeder Sprachport muss
**bit-identische Ergebnisse** (innerhalb des float32-Epsilons) für diese Operationen
erzeugen.

Verifiziert durch `fixtures/affect_state.json` (12 Testvektoren, sprachübergreifende CI).

---

## 1. AffectState — Felder und Standardwerte

| Feld | Typ | Standard | Semantik |
|------|-----|----------|----------|
| `Curiosity` | float32 | 0.5 | 0 = gelangweilt, 1 = fasziniert. Treibt proaktive Nachfragen an. |
| `Engagement` | float32 | 0.5 | 0 = desengagiert, 1 = vollständig engagiert. |
| `Uncertainty` | float32 | 0.2 | 0 = zuversichtlich, 1 = verwirrt. Hoch → klärende Fragen stellen. |
| `Rapport` | float32 | 0.0 | 0 = Fremder, 1 = tiefer Rapport. Wächst langsam über Sitzungen. |
| `Energy` | float32 | 0.5 | 0 = gedämpft, 1 = energetisch. Spiegelt das Interaktionstempo wider. |

Alle Felder werden nach jeder Operation **auf [0.0, 1.0] begrenzt**.

---

## 2. Signal- und Decay-Operationen

### 2.1 `ApplyPositiveSignal()`

Wird nach einer positiven Interaktion angewendet (Benutzer gibt Daumen hoch,
fortgesetztes Engagement usw.).

```
Engagement  ← clamp(Engagement  + 0.02, 0, 1)
Rapport     ← clamp(Rapport     + 0.01, 0, 1)
Uncertainty ← clamp(Uncertainty − 0.02, 0, 1)
```

`Curiosity` und `Energy` werden **nicht modifiziert**.

### 2.2 `ApplyNegativeSignal()`

Wird nach einer negativen Interaktion angewendet (Benutzer gibt Daumen runter,
abruptes Sitzungsende usw.).

```
Engagement  ← clamp(Engagement  − 0.03, 0, 1)
Uncertainty ← clamp(Uncertainty + 0.03, 0, 1)
```

`Rapport`, `Curiosity` und `Energy` werden **nicht modifiziert**.

### 2.3 `ApplyIdleDecay(idle: duration)`

Wird angewendet, wenn der Benutzer inaktiv war. Drift von Engagement und Energy
zurück zum neutralen Mittelpunkt (0.5). Alle anderen Dimensionen werden
**nicht modifiziert**.

```
hours ← idle.TotalHours   // as float32 (or float64 → cast to float32)
decay ← min(0.3, hours × 0.02)

Engagement ← Lerp(Engagement, 0.5, decay)
Energy     ← Lerp(Energy,     0.5, decay)
```

#### Lerp-Definition

```
Lerp(a, b, t) = a + (b − a) × clamp(t, 0, 1)
```

`clamp(t, 0, 1)` begrenzt `t` auf [0.0, 1.0] vor der Multiplikation. Da `decay`
bereits durch `min(0.3, ...)` begrenzt ist, dient das `clamp` innerhalb von `Lerp`
nur als Sicherheitsabsicherung.

#### Decay-Deckelung

`min(0.3, ...)` bedeutet, dass Engagement und Energy unabhängig von der Länge der
Inaktivität des Benutzers in einem einzelnen Aufruf nur **maximal 30 % des Weges
in Richtung 0.5** bewegt werden können. Dies verhindert, dass eine 48-stündige
Lücke den Zustand vollständig kollabiert.

---

## 3. `ToSystemPromptHint()` — AffectState

Gibt einen kompakten Hinweisblock (oder einen leeren String) zur Einspeisung in
den B!-System-Prompt zurück. Gibt nur Zeilen aus, die bedeutsam vom neutralen
Band abweichen.

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

Gibt einen kompakten Persona-Anweisungsblock (oder einen leeren String) zurück,
basierend auf Abweichungen vom Standardstil.

```
hints = []

if Verbosity ≠ "balanced"          → append "Keep responses {Verbosity}."
if Formality == "casual"           → append "Use a casual, friendly tone."
if Formality == "formal"           → append "Maintain a formal, professional tone."
if PreferredLocale is not empty    → append "Respond in the language appropriate for locale {PreferredLocale}."

if hints.isEmpty → return ""
return "[User preferences]\n" + hints.join("\n") + "\n"
```

Siehe `fixtures/persona_state.json` für 6 exakte Eingabe/Ausgabe-Testvektoren.

---

## 5. Sprachübergreifende Präzisionshinweise

1. Verwenden Sie **IEEE 754 single-precision float** (32-Bit) für alle fünf
   AffectState-Felder. Sprachen, die standardmäßig 64-Bit verwenden (Python `float`,
   TypeScript `number`, Go `float64`, Kotlin `Double`), müssen das Ergebnis
   **auf float32 casten**, bevor es gespeichert wird, oder durchgehend in float32
   akkumulieren.

2. Die Testvektoren in `fixtures/affect_state.json` werden als Dezimalstrings angegeben.
   Vergleichen Sie mit einem Epsilon von **1×10⁻⁶** (d.h. `abs(result − expected) < 0.000001`).

3. Wenden Sie **keine** Banker-Rundung, hardware-beschleunigte SIMD- oder FMA-Optimierungen
   (Fused Multiply-Add) an, die die Mantisse verändern. Berechnen Sie sequenziell
   wie oben angegeben.

4. Das Zeitstempelfeld `LastUpdatedUtc` / `LastUpdatedAt` ist **aus den Testvektoren
   ausgeschlossen**, da es beim Aufruf auf „jetzt" gesetzt wird und nicht vorausberechnet
   werden kann.

---

## 6. Verifizierung

Führen Sie `fixtures/affect_state.json` gegen Ihre Implementierung aus. Jeder Eintrag enthält:

- `id` — Testname
- `description` — was der Test überprüft
- `input` — der eingehende `AffectState`
- `operation` — `"positive_signal"`, `"negative_signal"` oder `"idle_decay"`
- `operationParam` — für Decay: `{ "hours": N }`; für Signaloperationen: `{}`
- `expected` — der resultierende `AffectState` (ohne Zeitstempelfelder)
