# Circle AI — የ10 ቋንቋ SDK

የCircle AI አጃቢ ስብስብ ተንቀሳቃሽ ዋና ክፍል። ከእያንዳንዱ
[Aether Protocol](https://github.com/bhengubv/aether-protocol) ኖድ ጎን ለጎን
በቀጥታ ይሠራል — ተለባሽ መሣሪያ፣ ስልክ፣ IoT፣ HarmonyOS — ያለ ምንም የFFI ጫና እና
ያለ ምንም የruntime ድልድዮች።

---

## ተንቀሳቃሽ ዋና ክፍል (8 ሞጁሎች)

| ሞጁል | ዋና ዓይነቶች |
|--------|-----------|
| **models** | `ChatMessage`, `DownloadProgress` |
| **memory** | `AffectState`, `EpisodicMemoryEntry`, `PersonaState`, `Goal` |
| **identity** | `CircleIdentity`, `RegisteredDevice`, `IdentityTier` |
| **languages** | `LanguageTag`, `KnownLanguages` (20 የBCP-47 መለያዎች), `WritingSystem` |
| **companion** | `CompanionContext`, `CompanionTurn`, `ICompanionSession` |
| **inference** | `GenerationOptions`, `IChatGenerator` |
| **tools** | `ToolDefinition`, `ToolInvocation`, `ToolResult`, `IToolBridge` |
| **sync** | `SyncDelta`, `SyncDeliveryMode`, `ISyncChannel` |

---

## ኒዩሮን — በመሣሪያህ ላይ ያለ ትንሽ የAI አእምሮ

**ኒዩሮን** በራስህ መሣሪያ ላይ የሚሠራ ትንሽ የAI አእምሮ ነው። ያስባል፣ ያስታውሳል፣ ይናገራልም —
እዚያው በስልክህ ወይም በላፕቶፕህ ላይ፣ ወደ አገልጋይ ምንም ሳይላክ። ፈጣን የዕለት ተዕለት ረዳት አብዛኞቹን
ጥያቄዎች ይመልሳል፤ ለከባድ ሥራ (ስዕል ማንበብ፣ ረጅም ሰነድ፣ ወይም በጥንቃቄ ደረጃ በደረጃ ማሰብ) በጸጥታ
አንድ ባለሙያ ይጭናል፣ ይመልሳል፣ ከዚያም ወደ ጎን ያስቀምጠዋል። በአንድ ጊዜ አንድ ባለሙያ ብቻ ነው የሚይዘው፣
ስለዚህ ከመሣሪያው አቅም በላይ ማህደረ ትውስታ ፈጽሞ አያስፈልገውም፣ እንዲሁም ውይይቱን ስለሚያስታውስ ጭውውት
ካቆመበት ይቀጥላል።

**አንድ ኒዩሮን፣ ወይም ብዙ — ከአእምሮዎች የተሠራ አእምሮ።** አንድ ኒዩሮን በራሱ በደንብ ይሠራል። ነገር ግን
ኒዩሮኖች እርስ በርስ መተባበርም ይችላሉ፣ በአእምሮ ውስጥ እንዳሉ የአእምሮ ሕዋሳት — እውነተኛው ኃይል ያለውም
እዚያ ነው። (ይህ ክፍል ገና አልተገነባም፤ ሊሆን ይችላል ብለን የምናምንበት ነው፣ እና ኖዱ ኒዩሮን ተብሎ
የተጠራበት ምክንያትም ይኸው ነው።) በቡድን ውስጥ ሥራውን ይካፈላሉ፣ አንዱ ባለሙያ መያዝ ሳይችል ሲቀር እርስ
በርስ ይረዳዳሉ፣ ማንም ኖድ አዛዥ ሳይኖር በእኩልነት ይመልሳሉ፣ እና የግል መረጃህን በራስህ መሣሪያ ላይ
ያስቀምጣሉ — በመካከላቸው የሚተላለፈው ጥያቄው ብቻ ነው። እያንዳንዱ ኒዩሮን ራሱ ሙሉ አእምሮ ነው፣ ስለዚህ
የኒዩሮኖች ቡድን ማለት ብዙ ሙሉ አእምሮዎች እርስ በርስ ተረዳድተው ከማንኛቸውም ብቻውን ከሚችለው በላይ
የሚያደርጉ ናቸው።

ኒዩሮን በC# ማመሳከሪያ ትግበራ እና በሁሉም ሰባት እህት ትግበራዎች (Python, TypeScript, Go,
Kotlin, Swift, Rust, C) ውስጥ ይገኛል። HarmonyOS/ArkTS ገና ወደፊት ይመጣል።

---

## ፈጣን መጀመሪያ በቋንቋ

### C# (.NET)

```bash
dotnet add package CircleAI.Core
```

```csharp
using CircleAI.Memory;
using CircleAI.Languages;

var state = new AffectState();
state.ApplyPositiveSignal();
Console.WriteLine(state.Engagement); // 0.52

var lang = KnownLanguages.Zulu;
Console.WriteLine(lang.BcpTag); // "zu"
```

---

### Python

```bash
pip install circle-ai-sdk
```

```python
from circle_ai.memory import AffectState
from circle_ai.languages import KnownLanguages

state = AffectState()
state.apply_positive_signal()
print(state.engagement)  # 0.52

reg = KnownLanguages()
print(reg.find_by_bcp_tag("zu").english_name)  # Zulu
```

---

### TypeScript / Node.js

```bash
npm install @bhengubv/circle-ai
```

```typescript
import { AffectState, KnownLanguages } from '@bhengubv/circle-ai';

const state = new AffectState();
state.applyPositiveSignal();
console.log(state.engagement); // 0.52

const reg = new KnownLanguages();
console.log(reg.findByBcpTag('zu')?.englishName); // Zulu
```

---

### Go

```bash
go get github.com/bhengubv/CircleAI/go
```

```go
import "github.com/bhengubv/CircleAI/go"

state := circleai.NewAffectState()
state.ApplyPositiveSignal()
fmt.Println(state.Engagement) // 0.52

lang := circleai.FindLanguage("zu")
fmt.Println(lang.EnglishName) // Zulu
```

---

### Kotlin (JVM)

```kotlin
// build.gradle.kts
implementation("com.bhengubv:circle-ai:0.1.0")
```

```kotlin
import com.bhengubv.circleai.AffectState
import com.bhengubv.circleai.KnownLanguages

val state = AffectState()
state.applyPositiveSignal()
println(state.engagement) // 0.52

println(KnownLanguages.findByBcpTag("zu")?.englishName) // Zulu
```

---

### Swift

```swift
// Package.swift
.package(url: "https://github.com/bhengubv/CircleAI.git", from: "0.1.0")
```

```swift
import CircleAI

let state = AffectState()
state.applyPositiveSignal()
print(state.engagement) // 0.52

let reg = KnownLanguages()
print(reg.findByBcpTag("zu")?.englishName ?? "") // Zulu
```

---

### Rust

```toml
# Cargo.toml
circle-ai = "0.1.0"
```

```rust
use circle_ai::memory::AffectState;
use circle_ai::languages::KnownLanguages;

let mut state = AffectState::default();
state.apply_positive_signal();
println!("{}", state.engagement); // 0.52

let lang = KnownLanguages::find_by_bcp_tag("zu").unwrap();
println!("{}", lang.english_name); // Zulu
```

---

### C (CMake)

```cmake
# CMakeLists.txt
FetchContent_Declare(circle_ai
    GIT_REPOSITORY https://github.com/bhengubv/CircleAI.git
    GIT_TAG        v0.1.0
    SOURCE_SUBDIR  c
)
FetchContent_MakeAvailable(circle_ai)
target_link_libraries(my_app circle_ai)
```

```c
#include "circle_ai/circle_ai.h"

ca_affect_state_t s = ca_affect_state_default();
ca_affect_state_positive_signal(&s);
printf("%.2f\n", s.engagement); /* 0.52 */

const ca_language_tag_t* zu = ca_find_language("zu");
printf("%s\n", zu->english_name); /* Zulu */
```

---

### Android (Kotlin)

```kotlin
// build.gradle.kts
implementation("com.bhengubv:circle-ai-android:0.1.0")
```

```kotlin
import com.bhengubv.circleai.AffectState
import com.bhengubv.circleai.KnownLanguages

val state = AffectState()
state.applyPositiveSignal()
Log.d("CircleAI", "Engagement: ${state.engagement}") // 0.52
```

---

### HarmonyOS / ArkTS

```json5
// oh-package.json5
"dependencies": { "@bhengubv/circle-ai": "^0.1.0" }
```

```typescript
import { AffectState, KnownLanguages } from '@bhengubv/circle-ai';

const state = new AffectState();
state.applyPositiveSignal();
console.log(state.engagement); // 0.52
```

---

## AffectState — በቋንቋዎች መካከል ያለ ሒሳብ

ሁሉም 10 ትግበራዎች ተመሳሳይ የfloating-point ውጤቶችን ያመነጫሉ (ε ≤ 1e-5)።

| ክንውን | ውጤት |
|-----------|--------|
| `applyPositiveSignal()` | engagement +0.02, rapport +0.01, uncertainty −0.02 (ወደ [0, 1] የተገደበ) |
| `applyNegativeSignal()` | engagement −0.03, uncertainty +0.03 (የተገደበ) |
| `applyIdleDecay(hours)` | መቀነስ = min(0.3, hours × 0.02); engagement እና energy በlinear interpolation ወደ 0.5 ይንሸራተታሉ |

የፈተና ቬክተሮች በ[`fixtures/affect_state.json`](fixtures/affect_state.json) ውስጥ ይገኛሉ (12 ቬክተሮች)። በCI በሁሉም 10 ቋንቋዎች ተረጋግጧል።

---

## የቋንቋ መዝገብ (20 የBCP-47 መለያዎች)

`zu` · `st` · `af` · `sw` · `ha` · `am` · `yo` · `ig` · `xh` · `nso` · `tn` · `so` · `om` · `ar` · `en` · `pt` · `fr` · `es` · `zh` · `hi`

---

## የማከማቻ አወቃቀር

```
CircleAI/
├── src/            C# reference implementation (CircleAI.*)
├── tests/          C# test suite
├── fixtures/       Cross-language test vectors (JSON)
├── docs/           CONTRACTS.md · MEMORY_SPEC.md · COMPANION_SPEC.md
├── android/        Kotlin/Android library
├── c/              Pure C99, CMake
├── go/             Go module
├── harmonyos/      ArkTS, OpenHarmony
├── kotlin/         Kotlin/JVM
├── python/         Python 3.12+
├── rust/           Rust, Cargo
├── swift/          Swift 5.9+, Swift Package Manager
└── typescript/     TypeScript, npm
```

---

## CI

| የሥራ ፍሰት | ቀስቃሽ |
|----------|---------|
| [የFixture ማረጋገጫ](.github/workflows/fixture-validation.yml) | push/PR ወደ master — ሁሉንም 10 የፈተና ስብስቦች ያስኬዳል |
| [ማሳተም](.github/workflows/publish.yml) | git tag `v*.*.*` — ወደ NuGet, crates.io, PyPI, npm, GitHub Packages ያሳትማል |

---

## ፈቃድ

MIT
