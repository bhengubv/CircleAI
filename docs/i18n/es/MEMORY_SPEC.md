# Circle AI — Especificación de Memoria

Este documento define la **matemática exacta** para las mutaciones de `AffectState` y
la generación de prompt de sistema de `PersonaState`. Cada puerto de idioma debe producir
**resultados bit-idénticos** (dentro del epsilon de float32) para estas operaciones.

Verificado por `fixtures/affect_state.json` (12 vectores de prueba, CI entre idiomas).

---

## 1. AffectState — Campos y valores por defecto

| Campo | Tipo | Por defecto | Semántica |
|-------|------|---------|-----------|
| `Curiosity` | float32 | 0.5 | 0 = aburrido, 1 = fascinado. Impulsa el seguimiento proactivo. |
| `Engagement` | float32 | 0.5 | 0 = desvinculado, 1 = plenamente comprometido. |
| `Uncertainty` | float32 | 0.2 | 0 = seguro, 1 = confundido. Alto → hacer preguntas aclaratorias. |
| `Rapport` | float32 | 0.0 | 0 = desconocido, 1 = confianza profunda. Crece lentamente entre sesiones. |
| `Energy` | float32 | 0.5 | 0 = tranquilo, 1 = enérgico. Refleja el ritmo de interacción. |

Todos los campos están **restringidos a [0.0, 1.0]** después de cada operación.

---

## 2. Operaciones de señal y decaimiento

### 2.1 `ApplyPositiveSignal()`

Se aplica después de una interacción positiva (pulgar arriba del usuario, participación
continuada, etc.).

```
Engagement  ← clamp(Engagement  + 0.02, 0, 1)
Rapport     ← clamp(Rapport     + 0.01, 0, 1)
Uncertainty ← clamp(Uncertainty − 0.02, 0, 1)
```

`Curiosity` y `Energy` **no se modifican**.

### 2.2 `ApplyNegativeSignal()`

Se aplica después de una interacción negativa (pulgar abajo del usuario, fin abrupto de
sesión, etc.).

```
Engagement  ← clamp(Engagement  − 0.03, 0, 1)
Uncertainty ← clamp(Uncertainty + 0.03, 0, 1)
```

`Rapport`, `Curiosity` y `Energy` **no se modifican**.

### 2.3 `ApplyIdleDecay(idle: duration)`

Se aplica cuando el usuario ha estado inactivo. Deriva `Engagement` y `Energy` de vuelta
hacia el punto neutro (0.5). Todas las demás dimensiones **no se modifican**.

```
hours ← idle.TotalHours   // as float32 (or float64 → cast to float32)
decay ← min(0.3, hours × 0.02)

Engagement ← Lerp(Engagement, 0.5, decay)
Energy     ← Lerp(Energy,     0.5, decay)
```

#### Definición de Lerp

```
Lerp(a, b, t) = a + (b − a) × clamp(t, 0, 1)
```

`clamp(t, 0, 1)` limita `t` a [0.0, 1.0] antes de multiplicar. Dado que `decay` ya está
acotado por `min(0.3, ...)`, el `clamp` dentro de `Lerp` es solo una salvaguarda.

#### Límite de decaimiento

`min(0.3, ...)` significa que sin importar cuánto tiempo esté inactivo el usuario,
`Engagement` y `Energy` solo pueden moverse **como máximo un 30 % del camino hacia 0.5**
en una sola llamada. Esto evita que una brecha de 48 horas colapse el estado por completo.

---

## 3. `ToSystemPromptHint()` — AffectState

Retorna un bloque de sugerencia compacto (o cadena vacía) para inyectar en el prompt del
sistema de B!. Solo emite líneas que se desvíen significativamente de la banda neutral.

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

Retorna un bloque de instrucciones de persona compacto (o cadena vacía) basado en las
desviaciones del estilo por defecto.

```
hints = []

if Verbosity ≠ "balanced"          → append "Keep responses {Verbosity}."
if Formality == "casual"           → append "Use a casual, friendly tone."
if Formality == "formal"           → append "Maintain a formal, professional tone."
if PreferredLocale is not empty    → append "Respond in the language appropriate for locale {PreferredLocale}."

if hints.isEmpty → return ""
return "[User preferences]\n" + hints.join("\n") + "\n"
```

Ver `fixtures/persona_state.json` para 6 vectores exactos de prueba de entrada/salida.

---

## 5. Notas de precisión entre idiomas

1. Utilizar **float IEEE 754 de precisión simple** (32 bits) para los cinco campos de
   AffectState. Los idiomas que usan 64 bits por defecto (`float` de Python,
   `number` de TypeScript, `float64` de Go, `Double` de Kotlin) deben **convertir el
   resultado a float32** antes de almacenarlo, o acumular en float32 durante todo el
   proceso.

2. Los vectores de prueba en `fixtures/affect_state.json` se expresan como cadenas
   decimales. Comparar con un epsilon de **1×10⁻⁶** (es decir,
   `abs(result − expected) < 0.000001`).

3. **No** aplicar redondeo bancario, SIMD acelerado por hardware, ni optimizaciones FMA
   (fusión multiplicar-sumar) que cambien la mantisa. Calcular secuencialmente como se
   describe arriba.

4. El campo de marca de tiempo `LastUpdatedUtc` / `LastUpdatedAt` está **excluido** de
   los vectores de prueba porque se establece en "ahora" en el momento de la llamada y
   no puede precalcularse.

---

## 6. Verificar

Ejecutar `fixtures/affect_state.json` contra su implementación. Cada entrada contiene:

- `id` — nombre de la prueba
- `description` — qué ejercita la prueba
- `input` — el `AffectState` de entrada
- `operation` — `"positive_signal"`, `"negative_signal"` o `"idle_decay"`
- `operationParam` — para decaimiento: `{ "hours": N }`; para operaciones de señal: `{}`
- `expected` — el `AffectState` resultante (excluyendo campos de marca de tiempo)
