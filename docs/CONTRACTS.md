# Circle AI — Portable Core Contracts

This document defines the **complete, language-agnostic interface contracts** for the
Circle AI portable core. Every language port must implement these contracts exactly.
Use this as the single source of truth when porting to a new language.

---

## Table of Contents

1. [Module overview](#module-overview)
2. [models](#module-models)
3. [memory](#module-memory)
4. [identity](#module-identity)
5. [languages](#module-languages)
6. [companion](#module-companion)
7. [inference](#module-inference)
8. [tools](#module-tools)
9. [sync](#module-sync)
10. [Type mapping](#type-mapping)

---

## Module overview

| Module | Key Types | Key Interfaces |
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

## Module: models

### ChatMessage

A single message in a conversation thread.

```
ChatMessage {
  Role:    string   // "system" | "user" | "assistant" | "tool"
  Content: string
}
```

### DownloadProgress

Progress event emitted during model download.

```
DownloadProgress {
  BytesReceived: int64
  TotalBytes:    int64?   // null if Content-Length unavailable
  Fraction:      float    // 0.0–1.0; NaN when TotalBytes is unknown
}
```

---

## Module: memory

### AffectState

B!'s emotional/engagement state. Five float dimensions, all clamped `[0.0, 1.0]`.
**Math must be byte-identical across all implementations.** See `docs/MEMORY_SPEC.md`.

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

One recorded conversational exchange.

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

A user's reaction to a specific B! response.

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

B!'s evolving communication style for a specific user.

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

A user goal B! tracks and proactively assists with.

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

Returns a fresh default `AffectState` when `userId` is not found.
Implementations must be crash-safe (write-then-swap or equivalent).

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

`SearchAsync`: when `queryEmbedding` is null, falls back to recency (most recent `topK`).

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

## Module: identity

### IdentityTier

```
IdentityTier enum { Anonymous, Pseudonymous, Verified }
```

### CircleIdentity

The unified persona key that travels with the person across all devices.

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

## Module: languages

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

Static sentinel: `LanguageTag.Unknown = LanguageTag("", "Unknown", "Unknown", Latin, false, "")`.

### DetectionResult

```
DetectionResult {
  Language:   LanguageTag
  Confidence: float32    // 0.0–1.0
}
```

### KnownLanguages

Static registry — 20 languages. See `fixtures/language_tags.json` for the full set.
Every implementation must expose all 20 tags and the `All` list in declaration order.

### ILanguageDetector

```
interface ILanguageDetector {
  DetectAsync(text: string) → Task<DetectionResult>
  DetectMultipleAsync(text: string, maxResults: int = 3) → Task<List<DetectionResult>>
}
```

`DetectAsync` returns `LanguageTag.Unknown` with `Confidence = 0` when detection fails.

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

## Module: companion

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

One completed exchange in the conversation history.

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

## Module: inference

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

## Module: tools

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

## Module: voice

The C# assembly `CircleAI.Voice` is 52 files; **this module is the portable
subset** — the parts with no ONNX Runtime binding and no native library behind
them. Everything else in that assembly (the six TTS engines, espeak, Open JTalk,
Whisper, the wake-word stack) is deliberately NOT in this contract, because it
cannot be honoured without per-platform native builds.

**Parity here is PROVEN, not asserted.** The C# reference emits the expected
answers into `fixtures/voice_*.json` via `dotnet run --project tools/voice-fixtures`,
and every port asserts against those files. A port that drifts fails; a port
that agrees is provably identical rather than merely plausible. **A changed
fixture is a contract change** — if regenerating produces a diff you did not
intend, the C# side moved and every port has to move with it.

### XsampaToIpa

X-SAMPA (as `NchltPhonemizer` emits) → IPA (as Mimic3-family voices expect).
38 phones, exactly the distinct set in `nchlt_afr.dict`.

| Operation | Contract |
|---|---|
| `convert(phones) -> (ipa, unmapped)` | Longest match on WHOLE tokens; emits ONE CODE POINT per element |
| `canSayAll(phones) -> bool` | False when any phone has no mapping |
| `knownPhones() -> [string]` | The 38 keys |

- **`g` maps to U+0261 ɡ, not ASCII `g`.** The voices' vocabularies carry the
  IPA letter; ASCII `g` misses and is dropped. Invisible in a diff.
- **Multi-character tokens match whole.** `A:r` is one token, not `A` + `:` + `r`.
- **Unmapped phones are RETURNED, never dropped silently.** An unmapped phone
  produces no sound and the audio is merely shorter — every acoustic measure
  still passes, so a caller that cannot see the misses cannot refuse.
- `h\` → `h` is the one deliberate approximation (X-SAMPA `h\` is ɦ; these
  voices have no ɦ). Voicing is lost, place and manner are right.

### SentencePieceUnigram

| Operation | Contract |
|---|---|
| `encode(text) -> [id]` | **VITERBI** over the piece lattice, with byte fallback |

- **VITERBI, NOT GREEDY LONGEST-MATCH.** Unigram scores are not monotone in
  piece length, so greedy silently produces plausible-but-wrong segmentations.
  The fixture vocabulary is built so the two disagree — `▁hello` scores worse
  than `▁hell` + `o` — and a greedy port fails on that case alone.
- **Normalisation:** NFKC, then `' '` → U+2581, with one U+2581 prepended.
- **Byte fallback is mandatory.** A character no piece covers is emitted as
  `<0xNN>` pieces and never dropped — dropping makes the audio shorter than the
  text, which no acoustic check catches.
- **Byte order is UTF-8 order.** The lattice is walked backwards, so a naive
  implementation emits multi-byte characters reversed (é is C3 A9 and comes out
  A9 C3). Nothing throws — those are real pieces with real ids — the model just
  says a different character, and only outside ASCII, which is exactly the
  African and Asian languages this catalogue serves. **This bug was live in the
  C# reference and the fixtures caught it.**
- **Index by CODE POINT, not by UTF-16 unit or byte.** A piece boundary inside a
  surrogate pair or a UTF-8 sequence produces pieces that match nothing.

### WavIo

Minimal RIFF/WAVE reading and PCM-16 packing, so a reference recording can
become the float samples a voice needs.

| Operation | Contract |
|---|---|
| `parse(bytes) -> (samples, rate, channels)` | Interleaved float in [-1,1] |
| `toMono24k(wav, maxSeconds)` | Downmix by averaging, resample to 24 kHz, cap |
| `toPcm16(samples)` | Little-endian signed 16-bit |

- **WALK THE CHUNKS.** Data does NOT always start at byte 44 — a `LIST` or
  `fact` chunk before it is normal, and assuming otherwise reads metadata as
  audio, which sounds like a burst of noise before the recording. The fixture
  carries a LIST-chunk case for exactly this.
- Chunks are word-aligned: advance by `size + (size & 1)`.
- Formats decoded: PCM 8/16/24/32-bit and IEEE float 32. `0xFFFE`
  (WAVE_FORMAT_EXTENSIBLE) is treated as PCM.
- Multi-channel is averaged, not left-channel-only.
- Resampling is linear — the target is a speaker embedding, not playback.

### Known port gaps

Recorded rather than hidden. Both are honest divergences on input the fixtures
do not exercise:

| Port | Gap |
|---|---|
| Rust | No NFKC — no stdlib normaliser, and `unicode-normalization` was not pulled in for a step no fixture covers. Byte-identical on already-normalised input. |
| C | No NFKC, same reason. Also the ONLY port whose test transcribes the fixture values as literals instead of reading the JSON — the C port has no JSON reader. Change a fixture, change those literals in the same commit. |

### Verified

All ten, from the same fixtures:

| Port | Result |
|---|---|
| C# (reference) | full suite green |
| Swift | 9 tests |
| Go | 9 tests |
| Rust | 9 tests |
| TypeScript | 9 tests |
| HarmonyOS (ArkTS) | 9 tests |
| Python | 9 tests |
| Kotlin | 9 tests |
| Android (Kotlin) | 9 tests |
| C | 23 + 4 checks |

---

## Module: sync

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

## Type mapping

| Contract type | C# | Rust | Go | Python | TypeScript | Kotlin | Swift | C | ArkTS |
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
