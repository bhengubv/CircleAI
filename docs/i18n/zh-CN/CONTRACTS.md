# Circle AI — 可移植核心契约

本文档定义了 Circle AI 可移植核心的**完整、语言无关的接口契约**。每个语言移植版本必须精确实现这些契约。在移植到新语言时，请以本文档作为唯一权威来源。

---

## 目录

1. [模块概览](#module-overview)
2. [models](#module-models)
3. [memory](#module-memory)
4. [identity](#module-identity)
5. [languages](#module-languages)
6. [companion](#module-companion)
7. [inference](#module-inference)
8. [tools](#module-tools)
9. [sync](#module-sync)
10. [类型映射](#type-mapping)

---

## 模块概览

| 模块 | 关键类型 | 关键接口 |
|--------|-----------|----------------|
| **models** | `ChatMessage`, `DownloadProgress` | — |
| **memory** | `AffectState`, `EpisodicMemoryEntry`, `FeedbackSignal`, `PersonaState`, `Goal` | `IEpisodicMemoryStore`, `IAffectStore`, `IFeedbackStore`, `IPersonaStore`, `IGoalStore` |
| **identity** | `CircleIdentity`, `RegisteredDevice` | `IIdentityStore`, `IIdentityProvider` |
| **languages** | `LanguageTag`, `DetectionResult`, `WritingSystem`, `KnownLanguages` | `ILanguageDetector`, `ILanguageRegistry` |
| **companion** | `CompanionContext`, `CompanionTurn`, `CompanionProactiveEvent`, `InterfaceKind` | `ICompanionSession` |
| **inference** | `ChatMessage`, `GenerationOptions` | `IChatGenerator` |
| **tools** | `ToolDefinition`, `ToolParameter`, `ToolInvocation`, `ToolResult` | `IToolBridge` |
| **sync** | `SyncDelta`, `SyncDeliveryMode`, `SyncDomainKeys` | `ISyncChannel` |

---

## 模块：models

### ChatMessage

对话线程中的单条消息。

```
ChatMessage {
  Role:    string   // "system" | "user" | "assistant" | "tool"
  Content: string
}
```

### DownloadProgress

模型下载过程中发出的进度事件。

```
DownloadProgress {
  BytesReceived: int64
  TotalBytes:    int64?   // null if Content-Length unavailable
  Fraction:      float    // 0.0–1.0; NaN when TotalBytes is unknown
}
```

---

## 模块：memory

### AffectState

B! 的情感/参与状态。五个 float 维度，均被夹缩到 `[0.0, 1.0]`。
**数学计算在所有实现中必须按字节相同。** 参见 `docs/MEMORY_SPEC.md`。

```
AffectState {
  UserId:         string          // opaque identifier, never PII
  LastUpdatedUtc: datetime (UTC)
  Curiosity:      float32 = 0.5  // 0=bored, 1=fascinated
  Engagement:     float32 = 0.5  // 0=disengaged, 1=fully engaged
  Uncertainty:    float32 = 0.2  // 0=confident, 1=confused
  Rapport:        float32 = 0.0  // 0=stranger, 1=deep rapport
  Energy:         float32 = 0.5  // 0=subdued, 1=energetic
}

methods:
  ApplyPositiveSignal()             // see MEMORY_SPEC.md §2.1
  ApplyNegativeSignal()             // see MEMORY_SPEC.md §2.2
  ApplyIdleDecay(idle: duration)    // see MEMORY_SPEC.md §2.3
  ToSystemPromptHint() → string     // see MEMORY_SPEC.md §3
```

### EpisodicMemoryEntry

一次记录的对话交换。

```
EpisodicMemoryEntry {
  Id:             uuid
  RecordedAtUtc:  datetime (UTC)
  UserText:       string
  AssistantText:  string
  AppContext:     string?                      // e.g. "tgn.bidbaas"
  Embedding:      float32[]?                   // L2-normalised, null if unavailable
  Tags:           map<string, string>?         // e.g. { "locale": "zu", "sentiment": "positive" }
}
```

### FeedbackPolarity

```
FeedbackPolarity enum {
  Positive   =  1
  Negative   = -1
  Correction =  0
}
```

### FeedbackSignal

用户对某条 B! 响应的反馈。

```
FeedbackSignal {
  Id:            uuid
  RecordedAtUtc: datetime (UTC)
  EpisodeId:     uuid?          // links to EpisodicMemoryEntry.Id
  UserText:      string
  AssistantText: string
  Polarity:      FeedbackPolarity
  CorrectedText: string?        // present when Polarity = Correction
  Comment:       string?
}
```

### PersonaState

B! 针对特定用户不断演化的沟通风格。

```
PersonaState {
  UserId:            string
  LastUpdatedUtc:    datetime (UTC)
  Verbosity:         string = "balanced"   // "brief" | "balanced" | "detailed"
  Formality:         string = "neutral"    // "casual" | "neutral" | "formal"
  PreferredLocale:   string?               // BCP-47, null = use device locale
  TopicWeights:      map<string, float>    // topic → accumulated signal weight
  DisfavouredTopics: set<string>
  TotalInteractions: int32 = 0
  PositiveSignals:   int32 = 0
  NegativeSignals:   int32 = 0
}

derived:
  SatisfactionScore: float? // null when (Positive+Negative) < 10; else Positive/(Positive+Negative)

methods:
  ToSystemPromptHint() → string   // see fixtures/persona_state.json for vectors
```

### Goal

B! 追踪并主动协助的用户目标。

```
GoalStatus enum { Active, Completed, Abandoned }
GoalPriority enum { Low, Normal, High }

Goal {
  Id:           string        // stable unique identifier
  UserId:       string
  Title:        string
  Description:  string
  Status:       GoalStatus    = Active
  Priority:     GoalPriority  = Normal
  CreatedUtc:   datetime (UTC)
  DueUtc:       datetime?     (UTC)
  CompletedUtc: datetime?     (UTC)
  Notes:        string?
}
```

### IAffectStore

```
interface IAffectStore {
  LoadAsync(userId: string) → Task<AffectState>
  SaveAsync(state: AffectState) → Task
}
```

当 `userId` 未找到时，返回新建的默认 `AffectState`。
实现必须保证崩溃安全（写后交换或等效机制）。

### IEpisodicMemoryStore

```
interface IEpisodicMemoryStore {
  AddAsync(entry: EpisodicMemoryEntry) → Task
  SearchAsync(queryEmbedding: float32[]?, topK: int = 5) → Task<List<EpisodicMemoryEntry>>
  GetRecentAsync(count: int = 10) → Task<List<EpisodicMemoryEntry>>
  CountAsync() → Task<int>
  PruneOlderThanAsync(cutoff: datetime) → Task<int>
}
```

`SearchAsync`：当 `queryEmbedding` 为 null 时，回退到按时间排序（最近的 `topK` 条）。

### IPersonaStore

```
interface IPersonaStore {
  LoadAsync(userId: string) → Task<PersonaState>
  SaveAsync(persona: PersonaState) → Task
}
```

### IFeedbackStore

```
interface IFeedbackStore {
  AddAsync(signal: FeedbackSignal) → Task
  GetRecentAsync(count: int = 50) → Task<List<FeedbackSignal>>
  CountAsync() → Task<int>
  PositiveRatioAsync() → Task<float?>   // null when no signals stored
}
```

### IGoalStore

```
interface IGoalStore {
  ListAsync(userId: string) → Task<List<Goal>>
  GetAsync(id: string) → Task<Goal?>
  UpsertAsync(goal: Goal) → Task<Goal>
  DeleteAsync(id: string) → Task
  GetActiveAsync(userId: string) → Task<List<Goal>>
}
```

---

## 模块：identity

### IdentityTier

```
IdentityTier enum { Anonymous, Pseudonymous, Verified }
```

### CircleIdentity

跨所有设备随人移动的统一身份标识。

```
CircleIdentity {
  IdentityId:        string          // stable UUID, never changes
  DisplayName:       string
  PreferredLanguage: string?         // BCP-47
  Tier:              IdentityTier
  DeviceIds:         List<string>
  CreatedAt:         datetime (UTC)
  LastSeenAt:        datetime (UTC)
}
```

### RegisteredDevice

```
RegisteredDevice {
  DeviceId:      string     // stable UUID
  IdentityId:    string
  Platform:      string     // "android"|"ios"|"windows"|"macos"|"linux"|"web"|"watch"|"iot"
  DeviceName:    string?
  RegisteredAt:  datetime (UTC)
  LastActiveAt:  datetime (UTC)
}
```

### IIdentityStore

```
interface IIdentityStore {
  GetAsync(identityId: string) → Task<CircleIdentity?>
  SaveAsync(identity: CircleIdentity) → Task
  GetDevicesAsync(identityId: string) → Task<List<RegisteredDevice>>
  RegisterDeviceAsync(device: RegisteredDevice) → Task
  GetByDeviceAsync(deviceId: string) → Task<CircleIdentity?>
}
```

### IIdentityProvider

```
interface IIdentityProvider {
  GetCurrentIdentityAsync() → Task<CircleIdentity?>
  IsAuthenticatedAsync() → Task<bool>
  CreateIdentityAsync(displayName: string, preferredLanguage: string?) → Task<CircleIdentity>
}
```

---

## 模块：languages

### WritingSystem

```
WritingSystem enum {
  Latin, Arabic, Ethiopic, Han, Devanagari
}
```

### LanguageTag

```
LanguageTag {
  BcpTag:        string         // IETF BCP-47 (e.g. "zu", "ar", "zh")
  EnglishName:   string         // e.g. "Zulu"
  NativeName:    string         // e.g. "isiZulu"
  WritingSystem: WritingSystem
  IsRtl:         bool           // true only for Arabic
  PrimaryRegion: string         // ISO 3166-1 alpha-2 country code
}
```

静态哨兵值：`LanguageTag.Unknown = LanguageTag("", "Unknown", "Unknown", Latin, false, "")`。

### DetectionResult

```
DetectionResult {
  Language:   LanguageTag
  Confidence: float32    // 0.0–1.0
}
```

### KnownLanguages

静态注册表——20 种语言。完整列表请参见 `fixtures/language_tags.json`。
每个实现必须按声明顺序暴露全部 20 个标签及 `All` 列表。

### ILanguageDetector

```
interface ILanguageDetector {
  DetectAsync(text: string) → Task<DetectionResult>
  DetectMultipleAsync(text: string, maxResults: int = 3) → Task<List<DetectionResult>>
}
```

检测失败时，`DetectAsync` 返回 `LanguageTag.Unknown` 且 `Confidence = 0`。

### ILanguageRegistry

```
interface ILanguageRegistry {
  GetByBcpTag(bcpTag: string) → LanguageTag?
  GetAll() → List<LanguageTag>                  // 20 entries, declaration order
  GetForRegion(isoRegion: string) → List<LanguageTag>
  IsSupported(bcpTag: string) → bool
}
```

---

## 模块：companion

### InterfaceKind

```
InterfaceKind enum { Text, Voice, Watch, Car, Tv, Ar, Iot }
```

### CompanionContext

```
CompanionContext {
  UserId:      string
  AppContext:  string?       // e.g. "tgn.bidbaas"
  Interface:   InterfaceKind
  Locale:      string?       // BCP-47
  Affect:      AffectState?
  Persona:     PersonaState?
  ActiveGoals: List<Goal>?
}
```

### CompanionTurn

对话历史中一次已完成的交换。

```
CompanionTurn {
  UserText:      string
  AssistantText: string
  CreatedAt:     datetime (UTC)
  UsedTools:     bool
}
```

### CompanionProactiveEvent

```
CompanionProactiveEvent {
  Message:     string
  Reason:      string?
  ScheduledAt: datetime (UTC)
}
```

### ICompanionSession

```
interface ICompanionSession : Disposable {
  History: List<CompanionTurn>      // read-only, newest-last

  SendAsync(userMessage: string) → Task<CompanionTurn>
  StreamAsync(userMessage: string) → AsyncStream<string>   // token-by-token
  AgentAsync(task: string, tools: List<ToolDefinition>?) → Task<string>
  GetContext() → CompanionContext
  SignalFeedbackAsync(polarity: FeedbackPolarity, correction: string?) → Task

  event ProactiveMessageReady: (CompanionProactiveEvent) → void
}
```

---

## 模块：inference

### GenerationOptions

```
GenerationOptions {
  MaxTokens:      int?
  Temperature:    float32?   // 0.0–2.0; null = model default
  TopP:           float32?   // 0.0–1.0; null = model default
  StopSequences:  string[]?
}
```

### IChatGenerator

```
interface IChatGenerator : Disposable {
  GenerateAsync(messages: List<ChatMessage>, options: GenerationOptions?) → Task<string>
  StreamAsync(messages: List<ChatMessage>, options: GenerationOptions?) → AsyncStream<string>
}
```

---

## 模块：tools

### ToolDefinition

```
ToolDefinition {
  Name:               string
  Description:        string
  Parameters:         map<string, ToolParameter>
  RequiredParameters: List<string>
}
```

### ToolParameter

```
ToolParameter {
  Type:        string    // "string"|"number"|"boolean"|"object"|"array"
  Description: string
  Enum:        string[]? // null unless the value is restricted to a set
}
```

### ToolInvocation

```
ToolInvocation {
  ToolName:  string
  Arguments: map<string, any>
}
```

### ToolResult

```
ToolResult {
  ToolName: string
  Success:  bool
  Result:   any?     // present on success
  Error:    string?  // present on failure
}
```

### IToolBridge

```
interface IToolBridge {
  AvailableTools: List<ToolDefinition>     // synchronous property

  InvokeAsync(invocation: ToolInvocation) → Task<ToolResult>
  GetAvailableToolsAsync() → Task<List<ToolDefinition>>  // default = wrap AvailableTools
}
```

---

## 模块：sync

### SyncDeliveryMode

```
SyncDeliveryMode enum { BestEffort, Reliable, Guaranteed }
```

### SyncDomainKeys

```
SyncDomainKeys constants {
  AffectState    = "affect.state"
  EpisodicMemory = "memory.episodic"
  PersonaState   = "persona"
  Goals          = "goals"
}
```

### SyncDelta

```
SyncDelta {
  OwnerId:      string           // identity whose state this belongs to
  SourceDevice: string           // originating device ID
  TargetDevice: string           // "" = broadcast to all owned devices
  DomainKey:    string           // see SyncDomainKeys
  Payload:      bytes            // opaque serialised state fragment
  Sequence:     int64            // monotonic per owner+domain
  DeliveryMode: SyncDeliveryMode
  Ttl:          duration?        // null = no expiry
  CreatedAt:    datetime (UTC)
}
```

### ISyncChannel

```
interface ISyncChannel {
  PushDeltaAsync(delta: SyncDelta) → Task
  ReceiveDeltasAsync(ownerId: string, afterSequence: int64 = 0) → AsyncStream<SyncDelta>
  GetLastSequenceAsync(ownerId: string, domainKey: string) → Task<int64>
}
```

---

## 类型映射

| 契约类型 | C# | Rust | Go | Python | TypeScript | Kotlin | Swift | C | ArkTS |
|---|---|---|---|---|---|---|---|---|---|
| `string` | `string` | `String` | `string` | `str` | `string` | `String` | `String` | `const char*` | `string` |
| `bool` | `bool` | `bool` | `bool` | `bool` | `boolean` | `Boolean` | `Bool` | `int` (0/1) | `boolean` |
| `int32` | `int` | `i32` | `int32` | `int` | `number` | `Int` | `Int32` | `int32_t` | `number` |
| `int64` | `long` | `i64` | `int64` | `int` | `bigint` | `Long` | `Int64` | `int64_t` | `bigint` |
| `float32` | `float` | `f32` | `float32` | `float` | `number` | `Float` | `Float` | `float` | `number` |
| `uuid` | `Guid` | `Uuid` | `uuid.UUID` | `uuid.UUID` | `string` (v4) | `UUID` | `UUID` | `char[37]` | `string` |
| `datetime (UTC)` | `DateTimeOffset` | `DateTime<Utc>` | `time.Time` | `datetime` | `Date` | `Instant` | `Date` | `int64` (ms) | `number` (ms) |
| `duration` | `TimeSpan` | `Duration` | `time.Duration` | `timedelta` | `number` (ms) | `Duration` | `TimeInterval` | `int64` (ms) | `number` (ms) |
| `bytes` | `ReadOnlyMemory<byte>` | `Vec<u8>` | `[]byte` | `bytes` | `Uint8Array` | `ByteArray` | `Data` | `uint8_t*` + len | `Uint8Array` |
| `List<T>` | `IReadOnlyList<T>` | `Vec<T>` | `[]T` | `list[T]` | `T[]` | `List<T>` | `[T]` | `T*` + len | `T[]` |
| `map<K,V>` | `IReadOnlyDictionary<K,V>` | `HashMap<K,V>` | `map[K]V` | `dict[K,V]` | `Record<K,V>` | `Map<K,V>` | `[K:V]` | struct | `Record<K,V>` |
| `set<T>` | `IReadOnlySet<T>` | `HashSet<T>` | `map[T]struct{}` | `set[T]` | `Set<T>` | `Set<T>` | `Set<T>` | sorted array | `Set<T>` |
| `T?` (nullable) | `T?` | `Option<T>` | `*T` or nil | `T \| None` | `T \| null` | `T?` | `T?` | null check | `T \| null` |
| `Task<T>` | `Task<T>` | `Future<T>` | `(T, error)` | `async def → T` | `Promise<T>` | `suspend → T` | `async → T` | callback | `Promise<T>` |
| `AsyncStream<T>` | `IAsyncEnumerable<T>` | `Stream<T>` | channel | `AsyncGenerator[T]` | `AsyncGenerator<T>` | `Flow<T>` | `AsyncStream<T>` | callback chain | `AsyncGenerator<T>` |
| `event Handler<T>` | `event EventHandler<T>` | channel | channel | callback | `EventEmitter` | `Flow<T>` | `AsyncStream<T>` | callback | `EventEmitter` |
