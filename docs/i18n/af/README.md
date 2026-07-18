# Circle AI — SDK vir 10 tale

Die draagbare kern van die Circle AI-metgesel-stapel. Dit loop natief langs elke
[Aether Protocol](https://github.com/bhengubv/aether-protocol)-node —
draagbare toestel, foon, IoT, HarmonyOS — sonder FFI-bokoste en sonder
looptydbrûe.

---

## Draagbare kern (8 modules)

| Module | Hooftipes |
|--------|-----------|
| **models** | `ChatMessage`, `DownloadProgress` |
| **memory** | `AffectState`, `EpisodicMemoryEntry`, `PersonaState`, `Goal` |
| **identity** | `CircleIdentity`, `RegisteredDevice`, `IdentityTier` |
| **languages** | `LanguageTag`, `KnownLanguages` (20 BCP-47-etikette), `WritingSystem` |
| **companion** | `CompanionContext`, `CompanionTurn`, `ICompanionSession` |
| **inference** | `GenerationOptions`, `IChatGenerator` |
| **tools** | `ToolDefinition`, `ToolInvocation`, `ToolResult`, `IToolBridge` |
| **sync** | `SyncDelta`, `SyncDeliveryMode`, `ISyncChannel` |

---

## Die Neuron — 'n klein KI-brein op jou toestel

'n **Neuron** is 'n klein KI-brein wat op jou eie toestel loop. Dit dink,
onthou en gesels — reg daar op jou foon of skootrekenaar, met niks wat na 'n
bediener gestuur word nie. 'n Vinnige alledaagse helper beantwoord die meeste
vrae; vir 'n moeiliker taak (om 'n prent te lees, 'n lang dokument, of
noukeurige stap-vir-stap-denke) laai dit stilweg 'n spesialis, antwoord, en sit
dit dan opsy. Dit hou net een spesialis op 'n slag, sodat dit nooit meer geheue
nodig het as wat die toestel het nie, en dit onthou die gesprek sodat 'n
geselsie voortgaan waar dit opgehou het.

**Een Neuron, of baie — 'n brein gemaak van breine.** Een Neuron werk goed op
sy eie. Maar Neurons kan ook saamsluit, soos breinselle in 'n brein — en dít is
waar die ware krag lê. (Hierdie deel is nog nie gebou nie; dit is wat ons glo
moontlik word, en dit is die rede waarom die node 'n Neuron genoem word.) In 'n
groep deel hulle die werk, help hulle mekaar wanneer een nie 'n spesialis kan
inpas nie, antwoord hulle as gelykes met geen node in beheer nie, en hou hulle
jou private data op jou eie toestel — net die vraag beweeg ooit tussen hulle.
Elke Neuron is reeds 'n hele verstand, so 'n groep Neurons is baie hele
verstande wat mekaar help om meer te doen as wat enigeen van hulle alleen kon.

Die Neuron word saam met die C#-verwysing en al sewe susterpoorte (Python,
TypeScript, Go, Kotlin, Swift, Rust, C) gelewer. HarmonyOS/ArkTS kom nog.

---

## Vinnige begin per taal

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

## AffectState — wiskunde oor tale heen

Al 10 implementasies lewer identiese drywende-punt-resultate (ε ≤ 1e-5).

| Bewerking | Uitwerking |
|-----------|--------|
| `applyPositiveSignal()` | engagement +0.02, rapport +0.01, uncertainty −0.02 (vasgeklem tot [0, 1]) |
| `applyNegativeSignal()` | engagement −0.03, uncertainty +0.03 (vasgeklem) |
| `applyIdleDecay(hours)` | decay = min(0.3, hours × 0.02); engagement en energy dryf na 0.5 deur lineêre interpolasie |

Toetsvektore in [`fixtures/affect_state.json`](fixtures/affect_state.json) (12 vektore). Deur CI oor al 10 tale heen bekragtig.

---

## Taalregister (20 BCP-47-etikette)

`zu` · `st` · `af` · `sw` · `ha` · `am` · `yo` · `ig` · `xh` · `nso` · `tn` · `so` · `om` · `ar` · `en` · `pt` · `fr` · `es` · `zh` · `hi`

---

## Bewaarplekstruktuur

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

| Werkvloei | Sneller |
|----------|---------|
| [Fixture-validering](.github/workflows/fixture-validation.yml) | push/PR na master — laat al 10 toetsstelle loop |
| [Publiseer](.github/workflows/publish.yml) | git-etiket `v*.*.*` — publiseer na NuGet, crates.io, PyPI, npm, GitHub Packages |

---

## Lisensie

MIT
