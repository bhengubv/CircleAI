# Circle AI — 컴패니언 세션 명세

이 문서는 모든 언어 포트가 구현해야 하는 **ICompanionSession 수명 주기 계약**을 정의합니다.
`ICompanionSession`은 호스트 애플리케이션(MAUI, Android, iOS, Web, HarmonyOS)이 상호작용하는
주요 API 접점입니다.

---

## 1. 개념

### 1.1 세션

`ICompanionSession`은 한 명의 사용자와 B! 사이의 **단일 연속 대화**를 나타냅니다. 세션은
생성(첫 번째 메시지)부터 소멸(사용자가 앱을 닫거나 세션이 명시적으로 종료될 때)까지 지속됩니다.

세션 자체는 **영속화되지 않습니다** — `CompanionTurn` 기록과 기반이 되는
`AffectState`/`PersonaState`만 저장됩니다. 다음 날 생성된 새 세션은 저장소에서 동일한 감정
및 페르소나 상태를 불러옵니다.

### 1.2 컨텍스트

`CompanionContext`는 B!가 일관성을 유지하는 데 필요한 모든 정보를 담습니다:

| 필드 | 목적 |
|-------|---------|
| `UserId` | 이 세션이 속하는 사용자 |
| `AppContext` | 호출 앱 (예: `"tgn.bidbaas"`, `"tgn.tagme"`) |
| `Interface` | 응답이 렌더링되는 방식 (음성, 시계, 텍스트 등) |
| `Locale` | 응답을 위한 언어 재정의 |
| `Affect` | 이 사용자에 대한 B!의 현재 감정 상태 |
| `Persona` | 이 사용자를 위해 B!가 학습한 스타일 |
| `ActiveGoals` | B!가 능동적으로 지원해야 할 목표들 |

### 1.3 인터페이스 기반 응답 형성

`InterfaceKind` 열거형은 출력 길이와 스타일을 결정합니다:

| 값 | 암묵적 제약 |
|-------|--------------------|
| `Text` | 기본값 — 특별한 제약 없음 |
| `Voice` | 짧은 문장, 마크다운 금지, 목록 금지 |
| `Watch` | 최대 약 40단어; 단일 문장 권장 |
| `Car` | 매우 짧게; 목록 금지; 눈을 떼지 않아도 안전 |
| `Tv` | 대화체; 간결하게; 코드 블록 금지 |
| `Ar` | 극도로 짧은 오버레이 (≤ 15단어) |
| `Iot` | 단일 행동 구문 |

구현체는 인터페이스에 적합한 지시사항을 시스템 프롬프트에 주입하는 것을 권장합니다.

---

## 2. 세션 수명 주기

```
┌──────────────────────────────────────────────┐
│                                              │
│  1. Create session with CompanionContext      │
│                                              │
│  2. User sends a message                     │
│     a. SendAsync(text)     → CompanionTurn   │
│     b. StreamAsync(text)   → token stream    │
│                                              │
│  3. Optionally: user sends feedback          │
│     SignalFeedbackAsync(Positive|Negative)   │
│                                              │
│  4. B! may raise ProactiveMessageReady event │
│     at any time (background thread is fine)  │
│                                              │
│  5. Dispose the session when done            │
│                                              │
└──────────────────────────────────────────────┘
```

---

## 3. 인터페이스 계약

### 3.1 `SendAsync(userMessage)`

블로킹(대기 가능) 전송-수신. 해당 턴을 `History`에 추가합니다.

**사전 조건:**
- `userMessage`는 null이 아니고 비어 있지 않아야 합니다.

**사후 조건:**
- `History.Count`가 1 증가합니다.
- `AffectState`는 세션 내부에서 업데이트됩니다(정확한 타이밍은 구현에 따라 다르지만,
  다음 `GetContext()` 호출이 반환되기 전에 완료되어야 합니다).
- 반환된 `CompanionTurn.UsedTools`는 생성 중 `IToolBridge` 호출이 발생한 경우 `true`입니다.

### 3.2 `StreamAsync(userMessage)`

토큰 단위 스트리밍. **History는** 전체 응답이 조합된 후(즉, 스트림이 완료된 후)에 업데이트되며,
스트리밍 중에는 업데이트되지 않습니다.

반환된 비동기 스트림은 **부분 토큰**을 방출합니다 — 호출자가 이를 연결해야 합니다.

### 3.3 `AgentAsync(task, tools?)`

다단계 에이전트 루프를 실행합니다: 모델은 최종 답변을 생성할 때까지 도구를 호출하고 추론합니다.
최종 텍스트 응답을 반환합니다.

`tools`가 null이거나 비어 있으면, 메서드는 단일 `GenerateAsync` 호출로 폴백합니다
(도구 루프 없음).

### 3.4 `GetContext()`

최신 `AffectState` 및 `PersonaState`를 포함한 **현재** `CompanionContext`를 반환합니다.
언제든지 호출할 수 있으며, 진행 중인 비동기 호출의 영향을 받지 않습니다.

### 3.5 `SignalFeedbackAsync(polarity, correction?)`

`History`에서 **가장 최근 턴**에 대한 사용자 피드백을 기록합니다.

- `FeedbackPolarity.Positive` → `AffectState.ApplyPositiveSignal()`을 호출하고 영속화합니다
- `FeedbackPolarity.Negative` → `AffectState.ApplyNegativeSignal()`을 호출하고 영속화합니다
- `FeedbackPolarity.Correction` → 수정 내용을 기록하며; 감정 상태를 변경하지 않습니다

`History`가 비어 있는 경우(아직 턴이 없는 경우), 이 메서드는 아무 작업도 수행하지 않습니다.

### 3.6 `ProactiveMessageReady` 이벤트

B!가 능동적으로 전달할 메시지가 있을 때(알림, 목표 촉구 등) 발생합니다.
이 이벤트는 자동으로 `History`에 추가되지 **않습니다** — 호스트가 `SendAsync`를 호출하거나
메시지를 다른 방식으로 표시해야 합니다.

---

## 4. `CompanionTurn` 필드

```
CompanionTurn {
  UserText:      string         // the user's input (verbatim)
  AssistantText: string         // B!'s complete response
  CreatedAt:     datetime (UTC) // timestamp of the assistant's response
  UsedTools:     bool           // true if any tool invocations occurred
}
```

---

## 5. 오류 처리

| 조건 | 예상 동작 |
|-----------|--------------------|
| `IChatGenerator` 사용 불가 | `GeneratorUnavailableException` 발생 (또는 언어별 동등 예외) |
| 도구 호출 실패 | `ToolResult.Success = false`; 오류를 컨텍스트에 포함; 루프 계속 |
| 임베딩 사용 불가 | `EpisodicMemoryEntry.Embedding = null` 저장; 실패하지 않음 |
| `AffectStore` 쓰기 실패 | 로그 기록 후 계속 진행; 호출자에게 표시하지 않음 |

---

## 6. 최소 구현 가능 구현체 (테스트용)

단위 테스트 및 아직 실제 LLM 백엔드가 없는 언어 포트의 경우:

```
MockChatGenerator:
  GenerateAsync(messages) → "Mock response from B!"
  StreamAsync(messages) → async stream of ["Mock", " ", "response", " ", "from", " ", "B!"]
```

`tests/`의 컴패니언 세션 테스트는 `MockChatGenerator`를 사용하여 실제 모델 없이도
세션 수명 주기, 기록 관리, 피드백 라우팅, 및 감정 상태 변경을 검증합니다.

---

## 7. 시스템 프롬프트 조합 순서

참조 C# 구현은 다음 순서로 시스템 프롬프트를 조합합니다:

1. 기본 시스템 프롬프트 (하드코딩된 페르소나: "You are B!, the on-device assistant…")
2. `AffectState.ToSystemPromptHint()` — 비어 있지 않으면 추가
3. `PersonaState.ToSystemPromptHint()` — 비어 있지 않으면 추가
4. `InterfaceKind` 제약 조건 — 적절하게 추가
5. `AppContext` 지시사항 — 선택 사항, 호스트 앱이 주입

구현체는 픽스처 테스트를 통과하는 한 이 순서를 자유롭게 변경할 수 있습니다.
