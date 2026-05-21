# Circle AI — Spécification mémoire

Ce document définit les **mathématiques exactes** pour les mutations de `AffectState`
et la génération du prompt système par `PersonaState`. Chaque portage linguistique doit
produire des **résultats bit à bit identiques** (dans l'epsilon du float32) pour ces
opérations.

Vérifié par `fixtures/affect_state.json` (12 vecteurs de test, CI multilangage).

---

## 1. AffectState — Champs et valeurs par défaut

| Champ | Type | Défaut | Sémantique |
|-------|------|--------|------------|
| `Curiosity` | float32 | 0.5 | 0 = ennuyé, 1 = fasciné. Entraîne des relances proactives. |
| `Engagement` | float32 | 0.5 | 0 = désengagé, 1 = pleinement engagé. |
| `Uncertainty` | float32 | 0.2 | 0 = confiant, 1 = confus. Élevé → poser des questions de clarification. |
| `Rapport` | float32 | 0.0 | 0 = étranger, 1 = rapport profond. Croît lentement au fil des sessions. |
| `Energy` | float32 | 0.5 | 0 = posé, 1 = énergique. Reflète le rythme des interactions. |

Tous les champs sont **clampés à [0.0, 1.0]** après chaque opération.

---

## 2. Opérations de signal et de décroissance

### 2.1 `ApplyPositiveSignal()`

Appliqué après une interaction positive (pouce levé de l'utilisateur, engagement
soutenu, etc.).

```
Engagement  ← clamp(Engagement  + 0.02, 0, 1)
Rapport     ← clamp(Rapport     + 0.01, 0, 1)
Uncertainty ← clamp(Uncertainty − 0.02, 0, 1)
```

`Curiosity` et `Energy` **ne sont pas modifiés**.

### 2.2 `ApplyNegativeSignal()`

Appliqué après une interaction négative (pouce baissé de l'utilisateur, fin de session
abrupte, etc.).

```
Engagement  ← clamp(Engagement  − 0.03, 0, 1)
Uncertainty ← clamp(Uncertainty + 0.03, 0, 1)
```

`Rapport`, `Curiosity` et `Energy` **ne sont pas modifiés**.

### 2.3 `ApplyIdleDecay(idle: duration)`

Appliqué quand l'utilisateur est inactif. Ramène `Engagement` et `Energy` vers
le point neutre (0.5). Toutes les autres dimensions **ne sont pas modifiées**.

```
hours ← idle.TotalHours   // as float32 (or float64 → cast to float32)
decay ← min(0.3, hours × 0.02)

Engagement ← Lerp(Engagement, 0.5, decay)
Energy     ← Lerp(Energy,     0.5, decay)
```

#### Définition de Lerp

```
Lerp(a, b, t) = a + (b − a) × clamp(t, 0, 1)
```

`clamp(t, 0, 1)` limite `t` à [0.0, 1.0] avant la multiplication. Comme `decay` est
déjà borné par `min(0.3, ...)`, le `clamp` dans `Lerp` n'est qu'un garde-fou de
sécurité.

#### Plafond de décroissance

`min(0.3, ...)` signifie que quelle que soit la durée d'inactivité de l'utilisateur,
`Engagement` et `Energy` ne peuvent se déplacer **d'au plus 30 % vers 0.5** en un seul
appel. Cela empêche une pause de 48 heures d'effondrer complètement l'état.

---

## 3. `ToSystemPromptHint()` — AffectState

Retourne un bloc d'indication compact (ou une chaîne vide) à injecter dans le prompt
système de B!. N'émet des lignes que pour les valeurs qui s'écartent significativement
de la bande neutre.

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

Retourne un bloc d'instructions de persona compact (ou une chaîne vide) basé sur les
écarts par rapport au style par défaut.

```
hints = []

if Verbosity ≠ "balanced"          → append "Keep responses {Verbosity}."
if Formality == "casual"           → append "Use a casual, friendly tone."
if Formality == "formal"           → append "Maintain a formal, professional tone."
if PreferredLocale is not empty    → append "Respond in the language appropriate for locale {PreferredLocale}."

if hints.isEmpty → return ""
return "[User preferences]\n" + hints.join("\n") + "\n"
```

Voir `fixtures/persona_state.json` pour 6 vecteurs de test entrée/sortie exacts.

---

## 5. Notes de précision multilangage

1. Utiliser le **flottant simple précision IEEE 754** (32 bits) pour les cinq champs de
   `AffectState`. Les langages qui utilisent 64 bits par défaut (Python `float`,
   TypeScript `number`, Go `float64`, Kotlin `Double`) doivent **caster le résultat en
   float32** avant de le stocker, ou accumuler en float32 tout au long du calcul.

2. Les vecteurs de test dans `fixtures/affect_state.json` sont fournis sous forme de
   chaînes décimales. Comparer avec un epsilon de **1×10⁻⁶** (c'est-à-dire
   `abs(result − expected) < 0.000001`).

3. **Ne pas** appliquer l'arrondi bancaire, les optimisations SIMD accélérées par le
   matériel, ou les optimisations FMA (fused multiply-add) qui modifient la mantisse.
   Calculer séquentiellement comme écrit ci-dessus.

4. Le champ d'horodatage `LastUpdatedUtc` / `LastUpdatedAt` est **exclu** des vecteurs
   de test car il est défini à « maintenant » au moment de l'appel et ne peut pas être
   précalculé.

---

## 6. Vérification

Exécuter `fixtures/affect_state.json` contre votre implémentation. Chaque entrée
contient :

- `id` — nom du test
- `description` — ce que le test vérifie
- `input` — le `AffectState` en entrée
- `operation` — `"positive_signal"`, `"negative_signal"`, ou `"idle_decay"`
- `operationParam` — pour la décroissance : `{ "hours": N }` ; pour les opérations de
  signal : `{}`
- `expected` — le `AffectState` résultant (sans les champs d'horodatage)
