# Circle AI — メモリ仕様

このドキュメントは、`AffectState` の変更と `PersonaState` のシステムプロンプト生成に関する **正確な数学的定義** を提供します。すべての言語ポートは、これらの演算に対して **ビット単位で同一の結果** を生成しなければなりません（float32 のイプシロン範囲内）。

`fixtures/affect_state.json`（12 のテストベクター、クロス言語 CI）によって検証されています。

---

## 1. AffectState — フィールドとデフォルト値

| フィールド | 型 | デフォルト | セマンティクス |
|-------|------|---------|-----------|
| `Curiosity` | float32 | 0.5 | 0 = 退屈、1 = 魅了。積極的なフォローアップを促進。 |
| `Engagement` | float32 | 0.5 | 0 = 無関心、1 = 完全に関与。 |
| `Uncertainty` | float32 | 0.2 | 0 = 自信あり、1 = 混乱。高い値 → 明確化の質問をする。 |
| `Rapport` | float32 | 0.0 | 0 = 見知らぬ人、1 = 深いラポール。セッションを経て徐々に成長。 |
| `Energy` | float32 | 0.5 | 0 = 抑制的、1 = 活発。インタラクションのペースを反映。 |

すべてのフィールドは、すべての演算後に **[0.0, 1.0] にクランプ** されます。

---

## 2. シグナル & 減衰演算

### 2.1 `ApplyPositiveSignal()`

ポジティブなインタラクション後に適用されます（ユーザーのいいね、継続的なエンゲージメントなど）。

```
Engagement  ← clamp(Engagement  + 0.02, 0, 1)
Rapport     ← clamp(Rapport     + 0.01, 0, 1)
Uncertainty ← clamp(Uncertainty − 0.02, 0, 1)
```

`Curiosity` と `Energy` は **変更されません**。

### 2.2 `ApplyNegativeSignal()`

ネガティブなインタラクション後に適用されます（ユーザーのいやだね、突然のセッション終了など）。

```
Engagement  ← clamp(Engagement  − 0.03, 0, 1)
Uncertainty ← clamp(Uncertainty + 0.03, 0, 1)
```

`Rapport`、`Curiosity`、`Energy` は **変更されません**。

### 2.3 `ApplyIdleDecay(idle: duration)`

ユーザーが非アクティブになったときに適用されます。`Engagement` と `Energy` をニュートラルな中間点（0.5）に向けてドリフトさせます。その他のすべての次元は **変更されません**。

```
hours ← idle.TotalHours   // as float32 (or float64 → cast to float32)
decay ← min(0.3, hours × 0.02)

Engagement ← Lerp(Engagement, 0.5, decay)
Energy     ← Lerp(Energy,     0.5, decay)
```

#### Lerp の定義

```
Lerp(a, b, t) = a + (b − a) × clamp(t, 0, 1)
```

`clamp(t, 0, 1)` は乗算の前に `t` を [0.0, 1.0] に制限します。`decay` はすでに `min(0.3, ...)` によって制限されているため、`Lerp` 内の `clamp` は安全ガードとしてのみ機能します。

#### 減衰上限

`min(0.3, ...)` は、ユーザーがどれほど長く非アクティブであっても、`Engagement` と `Energy` は 1 回の呼び出しで **最大 0.5 に向かって 30% しか移動できない** ことを意味します。これにより、48 時間のギャップが状態を完全に崩壊させることを防ぎます。

---

## 3. `ToSystemPromptHint()` — AffectState

B! システムプロンプトへの注入用に、コンパクトなヒントブロック（または空文字列）を返します。ニュートラルバンドから意味のある偏差がある行のみを出力します。

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

デフォルトスタイルからの偏差に基づいて、コンパクトなペルソナ指示ブロック（または空文字列）を返します。

```
hints = []

if Verbosity ≠ "balanced"          → append "Keep responses {Verbosity}."
if Formality == "casual"           → append "Use a casual, friendly tone."
if Formality == "formal"           → append "Maintain a formal, professional tone."
if PreferredLocale is not empty    → append "Respond in the language appropriate for locale {PreferredLocale}."

if hints.isEmpty → return ""
return "[User preferences]\n" + hints.join("\n") + "\n"
```

6 つの正確な入力/出力テストベクターについては `fixtures/persona_state.json` を参照してください。

---

## 5. クロス言語精度に関する注意事項

1. 5 つの AffectState フィールドすべてに **IEEE 754 単精度浮動小数点**（32 ビット）を使用してください。デフォルトで 64 ビットを使用する言語（Python の `float`、TypeScript の `number`、Go の `float64`、Kotlin の `Double`）は、保存前に結果を float32 に **キャスト** するか、全体を float32 で累積する必要があります。

2. `fixtures/affect_state.json` のテストベクターは十進数文字列として与えられています。イプシロン **1×10⁻⁶**（つまり `abs(result − expected) < 0.000001`）で比較してください。

3. 仮数を変更する銀行家の丸め、ハードウェアアクセラレートされた SIMD、または FMA（積和演算）最適化は **適用しないでください**。上記のとおり順次計算してください。

4. `LastUpdatedUtc` / `LastUpdatedAt` タイムスタンプフィールドは、呼び出し時の「現在」に設定され、事前計算できないため、テストベクターから **除外** されています。

---

## 6. 検証方法

`fixtures/affect_state.json` を実装に対して実行してください。各エントリには以下が含まれます:

- `id` — テスト名
- `description` — テストが検証する内容
- `input` — 入力される `AffectState`
- `operation` — `"positive_signal"`、`"negative_signal"`、または `"idle_decay"`
- `operationParam` — 減衰の場合: `{ "hours": N }`; シグナル演算の場合: `{}`
- `expected` — 結果の `AffectState`（タイムスタンプフィールドを除く）
