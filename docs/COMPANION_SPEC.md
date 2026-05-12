# Circle AI — Companion Session Specification

This document defines the **ICompanionSession lifecycle contract** that all language
ports must implement. `ICompanionSession` is the primary API surface that host
applications (MAUI, Android, iOS, Web, HarmonyOS) interact with.

---

## 1. Concepts

### 1.1 Session

An `ICompanionSession` represents a **single continuous conversation** between one
user and B!. It spans from creation (first message) to disposal (user closes the app
or session is explicitly ended).

Sessions are **not persisted themselves** — only the `CompanionTurn` history and the
underlying `AffectState`/`PersonaState` are stored. A new session created the next day
picks up the same affect and persona state from the stores.

### 1.2 Context

`CompanionContext` carries everything B! needs to stay grounded:

| Field | Purpose |
|-------|---------|
| `UserId` | Which user this session belongs to |
| `AppContext` | The calling app (e.g. `"tgn.bidbaas"`, `"tgn.tagme"`) |
| `Interface` | How the response will be rendered (voice, watch, text…) |
| `Locale` | Override language for responses |
| `Affect` | B!'s current emotional state for this user |
| `Persona` | B!'s learned style for this user |
| `ActiveGoals` | Goals B! should proactively assist with |

### 1.3 Interface-driven response shaping

The `InterfaceKind` enum drives output length and style:

| Value | Implied constraint |
|-------|--------------------|
| `Text` | Default — no special constraints |
| `Voice` | Short sentences, no markdown, no lists |
| `Watch` | Maximum ~40 words; single sentence preferred |
| `Car` | Very short; no lists; eyes-free safe |
| `Tv` | Conversational; brief; no code blocks |
| `Ar` | Ultra-short overlays (≤ 15 words) |
| `Iot` | Single action phrase |

Implementations are encouraged to inject interface-appropriate instructions into
the system prompt.

---

## 2. Session Lifecycle

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

## 3. Interface contract

### 3.1 `SendAsync(userMessage)`

Blocking (awaitable) send-receive. Appends the turn to `History`.

**Preconditions:**
- `userMessage` must be non-null, non-empty.

**Postconditions:**
- `History.Count` increases by 1.
- `AffectState` is updated inside the session (exact timing is implementation-defined,
  but must happen before the next `GetContext()` call returns).
- The returned `CompanionTurn.UsedTools` is `true` if any `IToolBridge` invocations
  occurred during generation.

### 3.2 `StreamAsync(userMessage)`

Token-by-token streaming. **History is updated** after the full response is assembled
(i.e. after the stream completes), not during.

The returned async stream emits **partial tokens** — callers must concatenate them.

### 3.3 `AgentAsync(task, tools?)`

Runs a multi-step agentic loop: the model calls tools and reasons until it produces
a final answer. Returns the final text response.

If `tools` is null or empty, the method falls back to a single `GenerateAsync` call
(no tool loop).

### 3.4 `GetContext()`

Returns the **current** `CompanionContext`, including the latest `AffectState` and
`PersonaState`. May be called at any time; is not affected by in-flight async calls.

### 3.5 `SignalFeedbackAsync(polarity, correction?)`

Records user feedback for the **most recent turn** in `History`.

- `FeedbackPolarity.Positive` → calls `AffectState.ApplyPositiveSignal()` and persists
- `FeedbackPolarity.Negative` → calls `AffectState.ApplyNegativeSignal()` and persists
- `FeedbackPolarity.Correction` → records the correction; no affect mutation

If `History` is empty (no turns yet), this method is a no-op.

### 3.6 `ProactiveMessageReady` event

Fires when B! has a message to deliver proactively (reminder, goal nudge, etc.).
The event **does not** append to `History` automatically — the host must call
`SendAsync` or otherwise surface the message.

---

## 4. `CompanionTurn` fields

```
CompanionTurn {
  UserText:      string         // the user's input (verbatim)
  AssistantText: string         // B!'s complete response
  CreatedAt:     datetime (UTC) // timestamp of the assistant's response
  UsedTools:     bool           // true if any tool invocations occurred
}
```

---

## 5. Error handling

| Condition | Expected behaviour |
|-----------|--------------------|
| `IChatGenerator` unavailable | Throw `GeneratorUnavailableException` (or language-equivalent) |
| Tool invocation fails | `ToolResult.Success = false`; include error in context; continue loop |
| Embedding unavailable | Store `EpisodicMemoryEntry.Embedding = null`; do not fail |
| `AffectStore` write fails | Log and continue; do not surface to caller |

---

## 6. Minimum viable implementation (testing)

For unit tests and language ports that do not yet have a real LLM backend:

```
MockChatGenerator:
  GenerateAsync(messages) → "Mock response from B!"
  StreamAsync(messages) → async stream of ["Mock", " ", "response", " ", "from", " ", "B!"]
```

The companion session tests in `tests/` use a `MockChatGenerator` to verify session
lifecycle, history management, feedback routing, and affect mutations without
requiring a real model.

---

## 7. System prompt assembly order

The reference C# implementation assembles the system prompt in this order:

1. Base system prompt (hardcoded persona: "You are B!, the on-device assistant…")
2. `AffectState.ToSystemPromptHint()` — appended if non-empty
3. `PersonaState.ToSystemPromptHint()` — appended if non-empty
4. `InterfaceKind` constraints — appended as appropriate
5. `AppContext` instructions — optional, injected by the host app

Implementations are free to order these differently as long as the fixture tests pass.
