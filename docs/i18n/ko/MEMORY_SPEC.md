# Circle AI — 메모리 명세

이 문서는 `AffectState` 변이와 `PersonaState` 시스템 프롬프트 생성을 위한 **정확한 수학**을
정의합니다. 모든 언어 포트는 이 연산에 대해 **비트 단위로 동일한 결과**
(float32 엡실론 범위 내)를 생성해야 합니다.

`fixtures/affect_state.json` (12개 테스트 벡터, 크로스-언어 CI)으로 검증됩니다.

---

## 1. AffectState — 필드 및 기본값

| 필드 | 타입 | 기본값 | 의미 |
|-------|------|---------|-----------|
| `Curiosity` | float32 | 0.5 | 0 = 지루함, 1 = 매혹됨. 능동적인 후속 조치를 유도합니다. |
| `Engagement` | float32 | 0.5 | 0 = 참여하지 않음, 1 = 완전히 참여함. |
| `Uncertainty` | float32 | 0.2 | 0 = 확신함, 1 = 혼란스러움. 높으면 명확화 질문을 유발합니다. |
| `Rapport` | float32 | 0.0 | 0 = 낯선 사람, 1 = 깊은 친밀감. 세션에 걸쳐 천천히 증가합니다. |
| `Energy` | float32 | 0.5 | 0 = 차분함, 1 = 활기참. 상호작용 속도를 반영합니다. |

모든 필드는 모든 연산 후 **[0.0, 1.0]으로 고정**됩니다.

---

## 2. 신호 및 감쇠 연산

### 2.1 `ApplyPositiveSignal()`

긍정적인 상호작용 후 적용됩니다 (사용자 엄지 올리기, 지속적인 참여 등).

```
Engagement  ← clamp(Engagement  + 0.02, 0, 1)
Rapport     ← clamp(Rapport     + 0.01, 0, 1)
Uncertainty ← clamp(Uncertainty − 0.02, 0, 1)
```

`Curiosity`와 `Energy`는 **수정되지 않습니다**.

### 2.2 `ApplyNegativeSignal()`

부정적인 상호작용 후 적용됩니다 (사용자 엄지 내리기, 갑작스러운 세션 종료 등).

```
Engagement  ← clamp(Engagement  − 0.03, 0, 1)
Uncertainty ← clamp(Uncertainty + 0.03, 0, 1)
```

`Rapport`, `Curiosity`, `Energy`는 **수정되지 않습니다**.

### 2.3 `ApplyIdleDecay(idle: duration)`

사용자가 비활성 상태일 때 적용됩니다. `Engagement`와 `Energy`를 중립 중간점(0.5)으로
되돌립니다. 다른 모든 차원은 **수정되지 않습니다**.

```
hours ← idle.TotalHours   // as float32 (or float64 → cast to float32)
decay ← min(0.3, hours × 0.02)

Engagement ← Lerp(Engagement, 0.5, decay)
Energy     ← Lerp(Energy,     0.5, decay)
```

#### Lerp 정의

```
Lerp(a, b, t) = a + (b − a) × clamp(t, 0, 1)
```

`clamp(t, 0, 1)`은 곱셈 전에 `t`를 [0.0, 1.0]으로 제한합니다. `decay`는 이미
`min(0.3, ...)`으로 경계가 설정되어 있으므로, `Lerp` 내부의 `clamp`는 안전 장치에 불과합니다.

#### 감쇠 상한

`min(0.3, ...)`은 사용자가 아무리 오래 비활성 상태이더라도, 단일 호출에서 `Engagement`와
`Energy`가 **최대 0.5를 향해 30% 이상** 이동할 수 없음을 의미합니다. 이는 48시간 간격으로
인해 상태가 완전히 붕괴되는 것을 방지합니다.

---

## 3. `ToSystemPromptHint()` — AffectState

B! 시스템 프롬프트에 주입하기 위한 압축된 힌트 블록(또는 빈 문자열)을 반환합니다.
중립 범위에서 의미 있게 벗어난 줄만 방출합니다.

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

기본 스타일에서 벗어난 내용을 기반으로 압축된 페르소나 지시 블록(또는 빈 문자열)을 반환합니다.

```
hints = []

if Verbosity ≠ "balanced"          → append "Keep responses {Verbosity}."
if Formality == "casual"           → append "Use a casual, friendly tone."
if Formality == "formal"           → append "Maintain a formal, professional tone."
if PreferredLocale is not empty    → append "Respond in the language appropriate for locale {PreferredLocale}."

if hints.isEmpty → return ""
return "[User preferences]\n" + hints.join("\n") + "\n"
```

6개의 정확한 입력/출력 테스트 벡터는 `fixtures/persona_state.json`을 참조하십시오.

---

## 5. 크로스-언어 정밀도 참고사항

1. 다섯 개의 `AffectState` 필드 모두에 **IEEE 754 단정밀도 부동소수점** (32비트)을
   사용하십시오. 기본적으로 64비트를 사용하는 언어(Python `float`, TypeScript `number`,
   Go `float64`, Kotlin `Double`)는 저장 전에 **결과를 float32로 캐스팅**하거나,
   처음부터 float32로 누적해야 합니다.

2. `fixtures/affect_state.json`의 테스트 벡터는 십진수 문자열로 제공됩니다.
   **1×10⁻⁶** 엡실론으로 비교하십시오 (즉, `abs(result − expected) < 0.000001`).

3. 가수를 변경하는 뱅커 반올림, 하드웨어 가속 SIMD, 또는 FMA(융합 곱셈-덧셈) 최적화를
   **적용하지 마십시오**. 위에 작성된 대로 순차적으로 계산하십시오.

4. `LastUpdatedUtc` / `LastUpdatedAt` 타임스탬프 필드는 호출 시 "현재" 시간으로 설정되어
   사전 계산할 수 없으므로 테스트 벡터에서 **제외**됩니다.

---

## 6. 검증

구현체에 대해 `fixtures/affect_state.json`을 실행하십시오. 각 항목에는 다음이 포함됩니다:

- `id` — 테스트 이름
- `description` — 테스트가 검증하는 내용
- `input` — 입력되는 `AffectState`
- `operation` — `"positive_signal"`, `"negative_signal"`, 또는 `"idle_decay"`
- `operationParam` — 감쇠의 경우: `{ "hours": N }`; 신호 연산의 경우: `{}`
- `expected` — 결과 `AffectState` (타임스탬프 필드 제외)
