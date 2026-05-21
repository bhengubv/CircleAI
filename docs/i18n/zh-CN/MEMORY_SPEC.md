# Circle AI — 内存规范

本文档定义了 `AffectState` 变更运算和 `PersonaState` 系统提示生成的**精确数学公式**。每个语言移植版本对这些操作必须产生**按位相同的结果**（在 float32 精度范围内）。

通过 `fixtures/affect_state.json` 验证（12 个测试向量，跨语言 CI）。

---

## 1. AffectState — 字段与默认值

| 字段 | 类型 | 默认值 | 语义 |
|-------|------|---------|-----------|
| `Curiosity` | float32 | 0.5 | 0 = 无聊，1 = 着迷。驱动主动追问。 |
| `Engagement` | float32 | 0.5 | 0 = 漠然，1 = 全神贯注。 |
| `Uncertainty` | float32 | 0.2 | 0 = 自信，1 = 困惑。值高时触发澄清性提问。 |
| `Rapport` | float32 | 0.0 | 0 = 陌生人，1 = 深度融洽。随会话缓慢增长。 |
| `Energy` | float32 | 0.5 | 0 = 低沉，1 = 活跃。反映交互节奏。 |

每次操作后，所有字段均被**夹缩到 [0.0, 1.0]**。

---

## 2. 信号与衰减操作

### 2.1 `ApplyPositiveSignal()`

在正面交互后调用（用户点赞、持续互动等）。

```
Engagement  ← clamp(Engagement  + 0.02, 0, 1)
Rapport     ← clamp(Rapport     + 0.01, 0, 1)
Uncertainty ← clamp(Uncertainty − 0.02, 0, 1)
```

`Curiosity` 和 `Energy` **不被修改**。

### 2.2 `ApplyNegativeSignal()`

在负面交互后调用（用户点踩、骤然结束会话等）。

```
Engagement  ← clamp(Engagement  − 0.03, 0, 1)
Uncertainty ← clamp(Uncertainty + 0.03, 0, 1)
```

`Rapport`、`Curiosity` 和 `Energy` **不被修改**。

### 2.3 `ApplyIdleDecay(idle: duration)`

当用户不活跃时调用。将 Engagement 和 Energy 向中性中点（0.5）漂移。其余维度**不被修改**。

```
hours ← idle.TotalHours   // as float32 (or float64 → cast to float32)
decay ← min(0.3, hours × 0.02)

Engagement ← Lerp(Engagement, 0.5, decay)
Energy     ← Lerp(Energy,     0.5, decay)
```

#### Lerp 定义

```
Lerp(a, b, t) = a + (b − a) × clamp(t, 0, 1)
```

`clamp(t, 0, 1)` 在乘法前将 `t` 限制到 [0.0, 1.0]。由于 `decay` 已通过 `min(0.3, ...)` 限定，`Lerp` 内部的 `clamp` 仅作安全保障。

#### 衰减上限

`min(0.3, ...)` 意味着无论用户空闲多久，Engagement 和 Energy 在单次调用中最多只能向 0.5 **移动 30%**。这防止了 48 小时的间隔将状态完全归零。

---

## 3. `ToSystemPromptHint()` — AffectState

返回一个紧凑提示块（或空字符串），用于注入 B! 系统提示。仅输出偏离中性区间的行。

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

根据风格偏差返回一个紧凑的个性化指令块（或空字符串）。

```
hints = []

if Verbosity ≠ "balanced"          → append "Keep responses {Verbosity}."
if Formality == "casual"           → append "Use a casual, friendly tone."
if Formality == "formal"           → append "Maintain a formal, professional tone."
if PreferredLocale is not empty    → append "Respond in the language appropriate for locale {PreferredLocale}."

if hints.isEmpty → return ""
return "[User preferences]\n" + hints.join("\n") + "\n"
```

6 个精确输入/输出测试向量请参见 `fixtures/persona_state.json`。

---

## 5. 跨语言精度说明

1. 五个 AffectState 字段均使用 **IEEE 754 单精度浮点数**（32 位）。默认使用 64 位的语言（Python 的 `float`、TypeScript 的 `number`、Go 的 `float64`、Kotlin 的 `Double`）必须在存储前**将结果转换为 float32**，或在整个计算过程中始终使用 float32。

2. `fixtures/affect_state.json` 中的测试向量以十进制字符串给出。比较时使用 **1×10⁻⁶** 的精度（即 `abs(result − expected) < 0.000001`）。

3. **不得**使用银行家舍入法、硬件加速 SIMD 或会改变尾数的 FMA（融合乘加）优化。按上述顺序逐步计算。

4. `LastUpdatedUtc` / `LastUpdatedAt` 时间戳字段从测试向量中**排除**，因为它在调用时设为"当前时间"，无法预先计算。

---

## 6. 验证

针对您的实现运行 `fixtures/affect_state.json`。每个条目包含：

- `id` — 测试名称
- `description` — 该测试所验证的内容
- `input` — 输入的 `AffectState`
- `operation` — `"positive_signal"`、`"negative_signal"` 或 `"idle_decay"`
- `operationParam` — 对于衰减操作：`{ "hours": N }`；对于信号操作：`{}`
- `expected` — 操作结果的 `AffectState`（不含时间戳字段）
