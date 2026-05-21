# Circle AI — Спецификация памяти

Данный документ определяет **точную математику** мутаций `AffectState` и
генерации системного промпта `PersonaState`. Каждый языковой порт должен
давать **побитово идентичные результаты** (в пределах эпсилон float32)
для этих операций.

Верифицируется через `fixtures/affect_state.json` (12 тестовых векторов, кросс-языковой CI).

---

## 1. AffectState — Поля и значения по умолчанию

| Поле | Тип | По умолчанию | Семантика |
|-------|------|---------|-----------|
| `Curiosity` | float32 | 0.5 | 0 = скучно, 1 = захвачен. Стимулирует проактивные уточняющие вопросы. |
| `Engagement` | float32 | 0.5 | 0 = не вовлечён, 1 = полностью вовлечён. |
| `Uncertainty` | float32 | 0.2 | 0 = уверен, 1 = растерян. Высокое значение → задавать уточняющие вопросы. |
| `Rapport` | float32 | 0.0 | 0 = незнакомец, 1 = глубокий контакт. Медленно растёт от сессии к сессии. |
| `Energy` | float32 | 0.5 | 0 = сдержанный, 1 = энергичный. Отражает темп взаимодействия. |

Все поля **зажимаются в [0.0, 1.0]** после каждой операции.

---

## 2. Операции сигналов и затухания

### 2.1 `ApplyPositiveSignal()`

Применяется после положительного взаимодействия (лайк пользователя, продолжение беседы и т. д.).

```
Engagement  ← clamp(Engagement  + 0.02, 0, 1)
Rapport     ← clamp(Rapport     + 0.01, 0, 1)
Uncertainty ← clamp(Uncertainty − 0.02, 0, 1)
```

`Curiosity` и `Energy` **не изменяются**.

### 2.2 `ApplyNegativeSignal()`

Применяется после отрицательного взаимодействия (дизлайк пользователя, резкое завершение сессии и т. д.).

```
Engagement  ← clamp(Engagement  − 0.03, 0, 1)
Uncertainty ← clamp(Uncertainty + 0.03, 0, 1)
```

`Rapport`, `Curiosity` и `Energy` **не изменяются**.

### 2.3 `ApplyIdleDecay(idle: duration)`

Применяется при неактивности пользователя. Дрейф `Engagement` и `Energy` назад
к нейтральной средней точке (0.5). Все остальные измерения **не изменяются**.

```
hours ← idle.TotalHours   // as float32 (or float64 → cast to float32)
decay ← min(0.3, hours × 0.02)

Engagement ← Lerp(Engagement, 0.5, decay)
Energy     ← Lerp(Energy,     0.5, decay)
```

#### Определение Lerp

```
Lerp(a, b, t) = a + (b − a) × clamp(t, 0, 1)
```

`clamp(t, 0, 1)` ограничивает `t` значением [0.0, 1.0] перед умножением. Поскольку `decay`
уже ограничен через `min(0.3, ...)`, `clamp` внутри `Lerp` — только защитная мера.

#### Ограничение затухания

`min(0.3, ...)` означает, что независимо от длительности простоя `Engagement` и `Energy`
за один вызов могут сдвинуться к 0.5 **не более чем на 30 %**. Это предотвращает
полный сброс состояния после перерыва в 48 часов.

---

## 3. `ToSystemPromptHint()` — AffectState

Возвращает компактный блок подсказок (или пустую строку) для внедрения в системный промпт B!.
Выводит только строки, значимо отклоняющиеся от нейтрального диапазона.

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

Возвращает компактный блок инструкций персоны (или пустую строку) на основе отклонений
от стиля по умолчанию.

```
hints = []

if Verbosity ≠ "balanced"          → append "Keep responses {Verbosity}."
if Formality == "casual"           → append "Use a casual, friendly tone."
if Formality == "formal"           → append "Maintain a formal, professional tone."
if PreferredLocale is not empty    → append "Respond in the language appropriate for locale {PreferredLocale}."

if hints.isEmpty → return ""
return "[User preferences]\n" + hints.join("\n") + "\n"
```

Точные входные/выходные тестовые векторы (6 штук) — в `fixtures/persona_state.json`.

---

## 5. Замечания о точности при кросс-языковом переносе

1. Используйте **одинарную точность IEEE 754** (32 бита) для всех пяти полей AffectState.
   Языки, по умолчанию работающие с 64-битными числами (Python `float`, TypeScript `number`,
   Go `float64`, Kotlin `Double`), должны **приводить результат к float32** перед сохранением
   или накапливать значения в float32 на протяжении всех вычислений.

2. Тестовые векторы в `fixtures/affect_state.json` заданы в виде десятичных строк. Сравнивайте
   с эпсилоном **1×10⁻⁶** (то есть `abs(result − expected) < 0.000001`).

3. **Не применяйте** банковское округление, аппаратно-ускоренные SIMD-операции или FMA
   (fused multiply-add), изменяющие мантиссу. Вычисляйте последовательно, как написано выше.

4. Поле временной метки `LastUpdatedUtc` / `LastUpdatedAt` **исключено** из тестовых
   векторов, поскольку устанавливается в «текущее время» во время вызова и не может
   быть предвычислено.

---

## 6. Проверка

Запустите `fixtures/affect_state.json` против своей реализации. Каждая запись содержит:

- `id` — имя теста
- `description` — что проверяет тест
- `input` — входной `AffectState`
- `operation` — `"positive_signal"`, `"negative_signal"` или `"idle_decay"`
- `operationParam` — для затухания: `{ "hours": N }`; для сигнальных операций: `{}`
- `expected` — результирующий `AffectState` (без полей временной метки)
