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

### PiperVoiceConfig

A voice's phoneme→id vocabulary, and the token layout the model expects.

| Operation | Contract |
|---|---|
| `padId` | The id THIS voice uses for blank — read from the vocabulary, never assumed |
| `hasPhonemeMap` | False when the voice ships no vocabulary at all |
| `phonemesToIds(phonemes)` | `[BOS, PAD, id, PAD, …, id, PAD, EOS]`, plus what was skipped and what was approximated |
| `splitPhonemeString(s)` | Grapheme clusters, not codepoints |

- **THE PAD RULE.** `_` resolves to *that model's* blank: 0 in sherpa/MMS
  exports, 3 in Piper-family ones. Pointing it at an ordinary vocabulary entry
  is what made 42 MMS voices speak fluent nonsense. The fixture carries TWO
  configs, one of each convention, so a port that hard-codes either fails.
- BOS and EOS are emitted **only when the vocabulary has them** — the
  MMS-family exports do not.
- Lookup order is exact → lower-cased → split the grapheme cluster →
  approximate. Splitting comes BEFORE approximating because it keeps every mark.
- An unknown symbol is **skipped and reported**, never fatal. A dropped symbol
  is inaudible, so those lists are the only evidence a front-end is broken.
- Approximation folds Latin diacritics only. Thai, Burmese, Devanagari, Arabic
  and Vietnamese marks ARE the vowels and tones — dropping them does not
  approximate the word, it deletes it. Thai measured 4.3 s instead of ~15 s
  when every vowel sign was folded off its consonant and filed as harmless.

### LexiconTokeniser

Word-keyed pronunciation for voices that ship `lexicon.txt` + `tokens.txt`.

| Operation | Contract |
|---|---|
| `fromText(tokens, lexicon, blank)` | Null when either file is unusable — absence is normal |
| `encode(text, interleaveBlank)` | Longest match first; `add_blank` opens with blank and follows every token |
| `lastUnmapped` | Symbols with no entry, whitespace excluded |

- **LONGEST MATCH FIRST**, because these lexicons are word-keyed and the words
  overlap: あい, あいさつ and あいかわらず all start the same way, and taking the
  shortest pronounces a different word.
- `tokens.txt` splits on the LAST space, because the symbol may itself be a space.
- A lexicon phoneme absent from `tokens.txt` drops out of the ids rather than
  failing the whole entry.
- Scanning indexes **characters**, not bytes — a byte index cuts a CJK character
  in half and matches nothing.

### SentenceSplitter

Cuts a passage into the units a VITS voice should synthesise one at a time.

| Operation | Contract |
|---|---|
| `split(text)` | Segments plus the silence that follows each; empty for blank input |
| `MAX_CHARS_PER_SEGMENT` | 220 UTF-16 units |

- Pauses: sentence 280 ms, clause (`:` `;`) 200 ms, paragraph 400 ms, forced
  cut 60 ms. The last segment is always 0 — trailing silence serves nothing.
- **The pause is the only sentence break these voices get.** They were trained
  on text with the punctuation stripped, so their vocabularies hold no `.`,
  `,`, `?` or `:` at all, and a paragraph fed in one pass comes back as one
  unbroken run of speech.
- **Splits at SENTENCE boundaries only, never at commas.** A VITS model ends
  every utterance with falling, sentence-final prosody, so cutting at a comma
  makes each clause land like a finished sentence — worse than the run-on.
- The terminator table covers the danda, the Arabic full stop, the CJK and
  fullwidth stops, the Ethiopic stop, the Khmer khan and the Myanmar marks. A
  Latin-only list under-splits for about a billion people and fails silently:
  Hindi, Bengali and Urdu produced THREE segments where eleven other languages
  produced six from the same text.
- `.` `:` `;` need a following space before they may end a sentence (3.5,
  co.za, 12:30). Every other terminator does not — demanding a space would
  never split Chinese, Japanese, Khmer, Thai or Burmese at all, because those
  scripts write without spaces between words.
- The terminator STAYS in the segment text. The SA-11 voice's vocabulary DOES
  carry `?` and `.`, so it renders a real question rise that no inserted
  silence could imitate; stripping would discard that from all eleven South
  African languages.
- A segment of nothing but punctuation is dropped — no sound to make, and no
  token for it either.
- Indexing is by **UTF-16 code unit**, matching the reference. Every terminator
  is in the BMP so the splits agree, but the length cut counts units.

### LanguageSpanSplitter

Cuts mixed-language text where the language changes.

| Operation | Contract |
|---|---|
| `split(text)` | Runs, each flagged native or foreign; 1 span for single-language text |
| `isForeignWord(word)` | Internal capitals, or 2-5 all-caps letters |
| `toSpokenForm(text)` | Split at case boundaries, then punctuate acronyms |

- A multi-lingual model takes ONE language id per utterance, so an English name
  inside an isiZulu sentence has to be cut out and synthesised separately —
  read wholly in isiZulu it comes out mangled, and the listener hears the
  machine fail at a word they know perfectly well.
- Detection is deliberately **CONSERVATIVE**: internal capitals (CircleAI,
  WhatsApp) and short all-caps runs (GPS, SMS, ATM) only. It does NOT guess at
  ordinary lowercase English words — that needs a lexicon per language pair,
  and mispronouncing a native word to "fix" a foreign one insults the speaker
  in their own language.
- A sentence-initial capital is NOT a signal. isiZulu, isiXhosa and Sesotho
  capitalise sentence openings and proper nouns and nothing else, so only
  capitals after position zero count.
- Separators ride along with the run they **FOLLOW**, so a language change
  never strands a comma on its own.
- `toSpokenForm` exists because a compound is one token to a synthesiser and it
  has no idea where the words are: `CircleAI` → `Circle A.I.`, `YouTube` →
  `You Tube`, `OpenAPIKey` → `Open A.P.I. Key`. The full stops are for the
  voice, not the reader.

### GeezRomanizer

Ethiopic (Ge'ez) → Latin, for the two `is_uroman: true` MMS voices.

| Operation | Contract |
|---|---|
| `isEthiopic(text)` | Any codepoint in U+1200–U+139F |
| `romanize(text)` | Latin; non-Ethiopic passes through untouched |

- The Amharic and Tigrinya models hold 28 and 27 **plain Latin letters** and
  have never seen an Ethiopic codepoint. Measured on the P30, Amharic lost 43
  distinct characters and produced 3.2 s of noise for a 15 s paragraph.
- **Computed, not tabulated.** Unicode lays the syllabary out as consecutive
  blocks of EIGHT codepoints, one consonant across its vowel orders, so
  consonant = `(cp - 0x1200) / 8` and vowel = `(cp - 0x1200) % 8`. Two small
  tables replace three hundred entries.
- **The layout stops at U+1357, and the range check must stop with it.** Above
  that: U+1358–U+135A are three LONE syllables already in their -a order,
  U+135D–U+135F are combining marks, U+1360–U+1368 is punctuation, and
  U+1369 onward are the numerals. Sizing the check off the consonant table
  instead swept seven numerals back into the syllabary and made ፩፪፫ read as
  "fyufyifya" — as *sound*, so nothing failed. The numerals past the table's
  end were dropped correctly, which is exactly why it looked handled.
- Six rows are **LABIALISED** — the consonant carries a built-in /w/. Writing
  them plain turns "enkwan" into "enkan" and silently changes the word.
- The sixth vowel order is SILENT; the glottal and pharyngeal rows write no
  consonant, so their vowel IS the character (first order reads as "a").
- Ethiopic punctuation maps to Latin so sentence splitting still works.

### ToneShaper

Two RBJ biquads over the float waveform, before it becomes PCM.

| Operation | Contract |
|---|---|
| `WARM` | shelf 320 Hz +4 dB, dip 3200 Hz −4 dB Q 0.8, shelf slope 0.9 |
| `lowShelf` / `peaking` | RBJ audio-cookbook coefficients, normalised by a0 |
| `biquad(x, coeffs)` | Direct-form-I, double state, float store |
| `apply(waveform, rate)` | Both filters in series, then peak restored |

- **The speaker was not the lever.** Measured across all 130 speakers in the
  bundle, warmth and intelligibility are inversely related: word error rate
  rewards the bright top end that "tinny" describes. So the waveform is
  corrected instead, and it is entirely ours once the model hands it over.
- The dip matters more than the boost on a phone: a P30 speaker cannot move
  enough air to reproduce a low shelf, but cutting 2–5 kHz works on hardware
  that cannot do bass. The boost is for headphones; both ship because the
  product is used on both.
- **PEAK IS RESTORED AFTERWARDS.** Lifting the shelf adds energy, and a
  waveform already near full scale would clip — heard as crackle and blamed on
  the quantised model rather than on this.
- The filter memory is **double**; only the stored sample is narrowed to float.
  The recursion therefore never sees the rounding, which is why the biquad is
  bit-reproducible across ports.
- The gain restore divides two **floats**. Widening it to double shifts the
  gain a few ULP and the whole tail of the waveform drifts with it.
- A silent buffer returns untouched — dividing by a zero peak is NaN.
- **The fixture carries the coefficients, and the two halves are asserted
  separately.** Ports filter the fixture's own coefficients and must match to
  1e-6; their own *derived* coefficients are compared at 1e-9 relative, because
  `pow`, `sin` and `cos` are not bit-identical across languages and pretending
  otherwise buys a flaky test rather than a strict one.

### NchltPhonemizer

Grapheme-to-phoneme for the South African languages, over the CC-BY NCHLT data.

| Operation | Contract |
|---|---|
| `fromText(dict, rules, phoneMap, graphMap?, gnulls?)` | Build from file CONTENTS, not paths |
| `phonemize(text)` | Dictionary first, rules otherwise |
| `predictWord(word)` | Rules only, bypassing the dictionary |
| `lastRulePredictedWords` / `lastUnknownGraphemes` | Coverage diagnostics |

- NOT espeak-ng (GPLv3 would taint the app), NOT phonemeza (unlicensed, weights
  unpublished), and not neural — no GPU to build, no runtime to infer.
- **There is no OOV gap.** A word is either catalogued exactly or synthesised by
  the rules, which is what makes agglutinative isiZulu tractable. isiZulu needs
  only ~74 rules because its orthography is near-phonemic — the same reason the
  approach is sound.
- Rule format is `grapheme;left;right;code;order[;count]`, matched as
  `pat.contains(left + "-" + g + "-" + right)` where
  `pat = " " + left-context + "-" + g + "-" + right-context + " "`.
- Rules sort **most-specific-first, and the sort MUST BE STABLE** — two rules of
  equal order have to stay in file order, or ports disagree on exactly the ties
  that dense rule sets produce most. Go needs `sort.SliceStable`, Swift sorts on
  `(order, index)`, and the C port hand-rolls an insertion sort because `qsort`
  is not stable.
- Code `0` is a NULL and is dropped, not emitted.
- The dictionary keeps the **FIRST** variant of a repeated word.
- `graphMap` lines are `funny<TAB>std` and map **std → funny**.
- An unknown grapheme is **skipped and reported**, never guessed at.
- Tokenising lower-cases and splits on non-letters. Diacritics survive
  (Afrikaans ê/ë/ô are real graphemes); number and abbreviation expansion is out
  of scope and belongs to a normalisation pass upstream.

### Known port gaps

Recorded rather than hidden. All are honest divergences on input the fixtures do
not exercise:

| Port | Gap |
|---|---|
| Rust | No NFKC — no stdlib normaliser, and `unicode-normalization` was not pulled in for a step no fixture covers. Byte-identical on already-normalised input. |
| C | No NFKC, same reason. Its Unicode character classes are range tables covering Latin, Greek, Cyrillic, the Indic and Arabic blocks, CJK, Ethiopic, Khmer and Myanmar rather than a full database — NARROWER than the reference, which is the safe direction: text splits more, never less, and no word is silently merged with its neighbour. |
| C | The ONLY port whose test transcribes the fixture values as literals instead of reading the JSON, because it has no JSON reader. `c/tests/voice_text_expected.h` is GENERATED — regenerate it in the same commit as any fixture change: `python tools/gen_c_voice_expected.py fixtures c/tests/voice_text_expected.h` |
| All | `SentenceSplitter` leaves a closing quote at the START of the next segment (`He said "go.` / `" Then left.`). `endsSentence` absorbs closers when deciding, but the cut lands at the terminator. Inaudible — no voice has a token for `"` — so it is documented rather than changed, because changing it moves all nine ports. |
| All | An ellipsis is consumed rather than kept: `Wait... Then go.` yields `Wait.` and `Then go.`. The trailing dots flush as punctuation-only segments and are dropped. |

### Verified

All ten, from the same fixtures:

| Port | Result |
|---|---|
| C# (reference) | full suite green; Companion 178/178 |
| Swift | 16 tests |
| Go | full package suite green |
| Rust | 15 tests |
| TypeScript | 19 tests |
| HarmonyOS (ArkTS) | 19 tests |
| Python | 19 tests |
| Kotlin | 16 tests |
| Android (Kotlin) | 16 tests |
| C | 90 + 361 checks |

Counts are for the voice parity tests only; each port's wider suite runs
alongside them.

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
