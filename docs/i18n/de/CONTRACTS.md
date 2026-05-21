# Circle AI — Portable Core-Verträge

Dieses Dokument definiert die **vollständigen, sprachunabhängigen Schnittstellenverträge**
für den portablen Circle-AI-Kern. Jeder Sprachport muss diese Verträge exakt implementieren.
Verwenden Sie dieses Dokument als einzige Quelle der Wahrheit bei der Portierung in eine
neue Sprache.

---

## Inhaltsverzeichnis

1. [Modulübersicht](#module-overview)
2. [models](#module-models)
3. [memory](#module-memory)
4. [identity](#module-identity)
5. [languages](#module-languages)
6. [companion](#module-companion)
7. [inference](#module-inference)
8. [tools](#module-tools)
9. [sync](#module-sync)
10. [Typzuordnung](#type-mapping)

---

## Modulübersicht

| Modul | Schlüsseltypen | Schlüsselschnittstellen |
|-------|----------------|-------------------------|
| **models** | `ChatMessage`, `DownloadProgress` | — |
| **memory** | `AffectState`, `EpisodicMemoryEntry`, `FeedbackSignal`, `PersonaState`, `Goal` | `IEpisodicMemoryStore`, `IAffectStore`, `IFeedbackStore`, `IPersonaStore`, `IGoalStore` |
| **identity** | `CircleIdentity`, `RegisteredDevice` | `IIdentityStore`, `IIdentityProvider` |
| **languages** | `LanguageTag`, `DetectionResult`, `WritingSystem`, `KnownLanguages` | `ILanguageDetector`, `ILanguageRegistry` |
| **companion** | `CompanionContext`, `CompanionTurn`, `CompanionProactiveEvent`, `InterfaceKind` | `ICompanionSession` |
| **inference** | `ChatMessage`, `GenerationOptions` | `IChatGenerator` |
| **tools** | `ToolDefinition`, `ToolParameter`, `ToolInvocation`, `ToolResult` | `IToolBridge` |
| **sync** | `SyncDelta`, `SyncDeliveryMode`, `SyncDomainKeys` | `ISyncChannel` |

---

## Modul: models

### ChatMessage

Eine einzelne Nachricht in einem Gesprächsstrang.

```
ChatMessage {
  Role:    string   // "system" | "user" | "assistant" | "tool"
  Content: string
}
```

### DownloadProgress

Fortschrittsereignis, das während des Modell-Downloads ausgelöst wird.

```
DownloadProgress {
  BytesReceived: int64
  TotalBytes:    int64?   // null if Content-Length unavailable
  Fraction:      float    // 0.0–1.0; NaN when TotalBytes is unknown
}
```

---

## Modul: memory

### AffectState

B!'s emotionaler/Engagement-Zustand. Fünf Float-Dimensionen, alle auf `[0.0, 1.0]` begrenzt.
**Die Mathematik muss byte-identisch in allen Implementierungen sein.** Siehe `docs/MEMORY_SPEC.md`.

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

Ein aufgezeichneter Gesprächsaustausch.

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

Die Reaktion eines Benutzers auf eine bestimmte B!-Antwort.

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

B!'s sich entwickelnder Kommunikationsstil für einen bestimmten Benutzer.

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

Ein Benutzerziel, das B! verfolgt und proaktiv dabei unterstützt.

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

Gibt einen neuen Standard-`AffectState` zurück, wenn `userId` nicht gefunden wird.
Implementierungen müssen absturzsicher sein (Write-then-Swap oder gleichwertig).

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

`SearchAsync`: Wenn `queryEmbedding` null ist, fällt die Methode auf Aktualität zurück
(die neuesten `topK` Einträge).

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

## Modul: identity

### IdentityTier

```
IdentityTier enum { Anonymous, Pseudonymous, Verified }
```

### CircleIdentity

Der einheitliche Persona-Schlüssel, der mit der Person über alle Geräte hinweg reist.

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

## Modul: languages

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

Statischer Sentinel: `LanguageTag.Unknown = LanguageTag("", "Unknown", "Unknown", Latin, false, "")`.

### DetectionResult

```
DetectionResult {
  Language:   LanguageTag
  Confidence: float32    // 0.0–1.0
}
```

### KnownLanguages

Statisches Verzeichnis — 20 Sprachen. Siehe `fixtures/language_tags.json` für den vollständigen
Satz. Jede Implementierung muss alle 20 Tags und die `All`-Liste in Deklarationsreihenfolge
bereitstellen.

### ILanguageDetector

```
interface ILanguageDetector {
  DetectAsync(text: string) → Task<DetectionResult>
  DetectMultipleAsync(text: string, maxResults: int = 3) → Task<List<DetectionResult>>
}
```

`DetectAsync` gibt `LanguageTag.Unknown` mit `Confidence = 0` zurück, wenn die Erkennung
fehlschlägt.

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

## Modul: companion

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

Ein abgeschlossener Austausch im Gesprächsverlauf.

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

## Modul: inference

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

## Modul: tools

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

## Modul: sync

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

## Typzuordnung

| Vertragstyp | C# | Rust | Go | Python | TypeScript | Kotlin | Swift | C | ArkTS |
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
