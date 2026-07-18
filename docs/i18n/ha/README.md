# Circle AI — SDK na Harsuna 10

Jigo mai ɗaukuwa na tsarin abokin-tafiya na Circle AI. Yana aiki kai tsaye a
gefen kowane node na [Aether Protocol](https://github.com/bhengubv/aether-protocol) —
na'urar sawa, waya, IoT, HarmonyOS — ba tare da nauyin FFI ba kuma ba tare da
gadoji na lokacin aiki ba.

---

## Jigo Mai Ɗaukuwa (Sassa 8)

| Sashe | Manyan Nau'ikan |
|--------|-----------|
| **models** | `ChatMessage`, `DownloadProgress` |
| **memory** | `AffectState`, `EpisodicMemoryEntry`, `PersonaState`, `Goal` |
| **identity** | `CircleIdentity`, `RegisteredDevice`, `IdentityTier` |
| **languages** | `LanguageTag`, `KnownLanguages` (alamun BCP-47 guda 20), `WritingSystem` |
| **companion** | `CompanionContext`, `CompanionTurn`, `ICompanionSession` |
| **inference** | `GenerationOptions`, `IChatGenerator` |
| **tools** | `ToolDefinition`, `ToolInvocation`, `ToolResult`, `IToolBridge` |
| **sync** | `SyncDelta`, `SyncDeliveryMode`, `ISyncChannel` |

---

## Neuron — Ƙaramar Ƙwaƙwalwar AI a Na'urarka

**Neuron** ƙaramar ƙwaƙwalwar AI ce da take aiki a kan na'urarka. Tana tunani,
tana tunawa, kuma tana magana — nan take a wayarka ko laptop ɗinka, ba tare da
an aika komai zuwa sabar ba. Mataimaki mai sauri na yau da kullum yana amsa
yawancin tambayoyi; don aiki mai wuya (karanta hoto, doguwar takarda, ko tunani
a hankali mataki-mataki) sai ta ɗora ƙwararre a shiru, ta amsa, sannan ta ajiye
shi a gefe. Tana riƙe ƙwararre ɗaya kaɗai a lokaci guda, don haka ba ta taɓa
buƙatar ƙwaƙwalwar ajiya fiye da abin da na'urar take da shi ba, kuma tana tuna
tattaunawar don hira ta ci gaba daga inda ta tsaya.

**Neuron ɗaya, ko da yawa — ƙwaƙwalwa da aka yi da ƙwaƙwalwu.** Neuron ɗaya tana
aiki sosai ita kaɗai. Amma Neurons kuma za su iya haɗuwa, kamar ƙwayoyin
ƙwaƙwalwa a cikin ƙwaƙwalwa — kuma a nan ne ainihin ƙarfin yake. (Ba a gina
wannan ɓangaren ba tukuna; shi ne abin da muka yi imani zai yiwu, kuma shi ne
dalilin da ya sa ake kiran node ɗin Neuron.) A cikin ƙungiya suna raba aiki,
suna taimakon juna idan wata ba ta iya ɗaukar ƙwararre, suna amsawa daidai da
juna babu wani node da ke shugabanci, kuma suna ajiye bayananka na sirri a kan
na'urarka — tambaya kaɗai ce take tafiya tsakaninsu. Kowace Neuron riga ta
kasance cikakken hankali, don haka ƙungiyar Neurons ita ce cikakkun hankula da
yawa da suke taimakon juna don yin fiye da yadda kowanne zai iya shi kaɗai.

Neuron yana samuwa a cikin aiwatarwar C# ta tushe da dukkan sauran aiwatarwa
bakwai 'yan'uwa (Python, TypeScript, Go, Kotlin, Swift, Rust, C).
HarmonyOS/ArkTS na zuwa nan gaba.

---

## Farawa Cikin Sauri bisa Harshe

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

## AffectState — Lissafi Tsakanin Harsuna

Dukkan aiwatarwa 10 suna ba da sakamako iri ɗaya na floating-point (ε ≤ 1e-5).

| Aiki | Tasiri |
|-----------|--------|
| `applyPositiveSignal()` | engagement +0.02, rapport +0.01, uncertainty −0.02 (an ƙuntata zuwa [0, 1]) |
| `applyNegativeSignal()` | engagement −0.03, uncertainty +0.03 (an ƙuntata) |
| `applyIdleDecay(hours)` | raguwa = min(0.3, hours × 0.02); engagement da energy suna gangarowa zuwa 0.5 ta hanyar linear interpolation |

Vectocin gwaji suna cikin [`fixtures/affect_state.json`](fixtures/affect_state.json) (guda 12). CI ya tabbatar da su a dukkan harsuna 10.

---

## Rijistar Harsuna (alamun BCP-47 guda 20)

`zu` · `st` · `af` · `sw` · `ha` · `am` · `yo` · `ig` · `xh` · `nso` · `tn` · `so` · `om` · `ar` · `en` · `pt` · `fr` · `es` · `zh` · `hi`

---

## Tsarin Ma'ajiya

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

| Tsarin Aiki | Mai Kunnawa |
|----------|---------|
| [Tabbatar da Fixtures](.github/workflows/fixture-validation.yml) | push/PR zuwa master — yana gudanar da dukkan tsarukan gwaji 10 |
| [Bugawa](.github/workflows/publish.yml) | git tag `v*.*.*` — yana bugawa zuwa NuGet, crates.io, PyPI, npm, GitHub Packages |

---

## Lasisi

MIT
