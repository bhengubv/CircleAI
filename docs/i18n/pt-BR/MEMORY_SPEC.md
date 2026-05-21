# Circle AI — Especificação de Memória

Este documento define a **matemática exata** para as mutações de `AffectState` e
a geração de prompt de sistema do `PersonaState`. Toda implementação de linguagem deve
produzir **resultados idênticos ao nível de bit** (dentro do épsilon de float32) para essas operações.

Verificado por `fixtures/affect_state.json` (12 vetores de teste, CI entre linguagens).

---

## 1. AffectState — Campos e Padrões

| Campo | Tipo | Padrão | Semântica |
|-------|------|---------|-----------|
| `Curiosity` | float32 | 0.5 | 0 = entediado, 1 = fascinado. Impulsiona o acompanhamento proativo. |
| `Engagement` | float32 | 0.5 | 0 = desengajado, 1 = totalmente engajado. |
| `Uncertainty` | float32 | 0.2 | 0 = confiante, 1 = confuso. Alto → fazer perguntas de esclarecimento. |
| `Rapport` | float32 | 0.0 | 0 = desconhecido, 1 = rapport profundo. Cresce lentamente ao longo das sessões. |
| `Energy` | float32 | 0.5 | 0 = contido, 1 = energético. Espelha o ritmo de interação. |

Todos os campos são **fixados em [0.0, 1.0]** após cada operação.

---

## 2. Operações de Sinal e Decaimento

### 2.1 `ApplyPositiveSignal()`

Aplicado após uma interação positiva (curtida do usuário, engajamento contínuo etc.).

```
Engagement  ← clamp(Engagement  + 0.02, 0, 1)
Rapport     ← clamp(Rapport     + 0.01, 0, 1)
Uncertainty ← clamp(Uncertainty − 0.02, 0, 1)
```

`Curiosity` e `Energy` **não são modificados**.

### 2.2 `ApplyNegativeSignal()`

Aplicado após uma interação negativa (não-gostei do usuário, encerramento abrupto da sessão etc.).

```
Engagement  ← clamp(Engagement  − 0.03, 0, 1)
Uncertainty ← clamp(Uncertainty + 0.03, 0, 1)
```

`Rapport`, `Curiosity` e `Energy` **não são modificados**.

### 2.3 `ApplyIdleDecay(idle: duration)`

Aplicado quando o usuário está inativo. Faz `Engagement` e `Energy` derivarem de volta em
direção ao ponto médio neutro (0,5). Todas as outras dimensões **não são modificadas**.

```
hours ← idle.TotalHours   // as float32 (or float64 → cast to float32)
decay ← min(0.3, hours × 0.02)

Engagement ← Lerp(Engagement, 0.5, decay)
Energy     ← Lerp(Energy,     0.5, decay)
```

#### Definição de Lerp

```
Lerp(a, b, t) = a + (b − a) × clamp(t, 0, 1)
```

`clamp(t, 0, 1)` limita `t` a [0.0, 1.0] antes da multiplicação. Como `decay` já é
limitado por `min(0.3, ...)`, o `clamp` dentro de `Lerp` é apenas uma guarda de segurança.

#### Limite de decaimento

`min(0.3, ...)` significa que, independentemente de quanto tempo o usuário fique inativo, `Engagement` e `Energy`
só podem se mover **no máximo 30% em direção a 0,5** em uma única chamada. Isso impede que
uma lacuna de 48 horas colapse completamente o estado.

---

## 3. `ToSystemPromptHint()` — AffectState

Retorna um bloco de dica compacto (ou string vazia) para injeção no prompt de sistema do B!.
Emite apenas linhas que se desviam significativamente da banda neutra.

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

Retorna um bloco compacto de instrução de persona (ou string vazia) baseado nos desvios
em relação ao estilo padrão.

```
hints = []

if Verbosity ≠ "balanced"          → append "Keep responses {Verbosity}."
if Formality == "casual"           → append "Use a casual, friendly tone."
if Formality == "formal"           → append "Maintain a formal, professional tone."
if PreferredLocale is not empty    → append "Respond in the language appropriate for locale {PreferredLocale}."

if hints.isEmpty → return ""
return "[User preferences]\n" + hints.join("\n") + "\n"
```

Veja `fixtures/persona_state.json` para 6 vetores exatos de entrada/saída de teste.

---

## 5. Notas de Precisão entre Linguagens

1. Use **float IEEE 754 de precisão simples** (32 bits) para todos os cinco campos de AffectState.
   Linguagens cujo padrão é 64 bits (`float` do Python, `number` do TypeScript, `float64` do Go,
   `Double` do Kotlin) devem **converter o resultado para float32** antes de armazená-lo, ou
   acumular em float32 durante todo o processo.

2. Os vetores de teste em `fixtures/affect_state.json` são fornecidos como strings decimais. Compare
   com um épsilon de **1×10⁻⁶** (ou seja, `abs(resultado − esperado) < 0.000001`).

3. **Não** aplique arredondamento bancário, SIMD acelerado por hardware ou otimizações de FMA
   (multiplicação-adição fundida) que alterem a mantissa. Compute sequencialmente como
   descrito acima.

4. O campo de timestamp `LastUpdatedUtc` / `LastUpdatedAt` é **excluído** dos vetores de teste
   porque é definido como "agora" no momento da chamada e não pode ser pré-computado.

---

## 6. Verificação

Execute `fixtures/affect_state.json` em relação à sua implementação. Cada entrada possui:

- `id` — nome do teste
- `description` — o que o teste exercita
- `input` — o `AffectState` de entrada
- `operation` — `"positive_signal"`, `"negative_signal"` ou `"idle_decay"`
- `operationParam` — para decaimento: `{ "hours": N }`; para operações de sinal: `{}`
- `expected` — o `AffectState` resultante (excluindo campos de timestamp)
