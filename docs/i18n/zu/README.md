# Circle AI — SDK Yezilimi Ezingu-10

Yingqikithi ephathekayo yohlelo lomngane we-Circle AI. Isebenza ngokwemvelo
eduze kwayo yonke inodi ye-[Aether Protocol](https://github.com/bhengubv/aether-protocol) —
okugqokwayo, ifoni, i-IoT, i-HarmonyOS — ngaphandle kwesindo esengeziwe se-FFI
nangaphandle kwamabhuloho e-runtime.

---

## Ingqikithi Ephathekayo (izimojula ezingu-8)

| Imojula | Izinhlobo eziyinhloko |
|--------|-----------|
| **models** | `ChatMessage`, `DownloadProgress` |
| **memory** | `AffectState`, `EpisodicMemoryEntry`, `PersonaState`, `Goal` |
| **identity** | `CircleIdentity`, `RegisteredDevice`, `IdentityTier` |
| **languages** | `LanguageTag`, `KnownLanguages` (amathegi angu-20 e-BCP-47), `WritingSystem` |
| **companion** | `CompanionContext`, `CompanionTurn`, `ICompanionSession` |
| **inference** | `GenerationOptions`, `IChatGenerator` |
| **tools** | `ToolDefinition`, `ToolInvocation`, `ToolResult`, `IToolBridge` |
| **sync** | `SyncDelta`, `SyncDeliveryMode`, `ISyncChannel` |

---

## INyuroni — ubuchopho obuncane be-AI edivayisini yakho

**INyuroni** ubuchopho obuncane be-AI obusebenza edivayisini yakho siqu. Iyacabanga,
iyakhumbula, futhi iyakhuluma — khona lapho efonini yakho noma kwilaptophu yakho, kungekho
lutho oluthunyelwa kuseva. Umsizi osheshayo wansuku zonke uphendula imibuzo eminingi;
kodwa emsebenzini onzima (ukufunda isithombe, idokhumenti elide, noma ukucabanga
okucophelele isinyathelo nesinyathelo) ilayisha ngokuthulile uchwepheshe, iphendule, bese
imbeka eceleni. Igcina uchwepheshe oyedwa kuphela ngesikhathi esisodwa, ngakho ayidingi
imemori engaphezu kwaleyo idivayisi enayo, futhi iyayikhumbula ingxoxo ukuze ingxoxo
iqhubeke kusukela lapho igcine khona.

**INyuroni eyodwa, noma eziningi — ubuchopho obakhiwe ngobuchopho.** INyuroni eyodwa
isebenza kahle iyodwa. Kodwa iziNyuroni zingahlangana futhi, njengamaseli obuchopho
asebuchosheni — futhi yilapho khona amandla angempela. (Le ngxenye ayikakhiwa okwamanje;
yilokho esikholwa ukuthi kungenzeka, futhi yiso isizathu sokuthi inodi ibizwe ngokuthi
iNyuroni.) Eqenjini zabelana ngomsebenzi, zisizane lapho enye ingakwazi ukufaka
uchwepheshe, ziphendule zilingana kungekho inodi ephethe, futhi zigcine idatha yakho
eyimfihlo edivayisini yakho siqu — umbuzo kuphela ohamba phakathi kwazo. INyuroni ngayinye
isivele iyingqondo ephelele, ngakho iqembu leziNyuroni liyizingqondo eziphelele eziningi
ezisizanayo ukwenza okungaphezu kwalokho noma iyiphi kuzo engakwenza iyodwa.

INyuroni itholakala kunguqulo yesisekelo ye-C# kanye nakuzo zonke izinguqulo ezingodadewabo
eziyisikhombisa (Python, TypeScript, Go, Kotlin, Swift, Rust, C). I-HarmonyOS/ArkTS
isazolandela.

---

## Ukuqala Okusheshayo Ngolimi Ngalunye

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

## AffectState — Izibalo Ezisebenza Kuzo Zonke Izilimi

Zonke izinguqulo ezingu-10 zikhiqiza imiphumela efanayo ye-floating-point (ε ≤ 1e-5).

| Umsebenzi | Umthelela |
|-----------|--------|
| `applyPositiveSignal()` | engagement +0.02, rapport +0.01, uncertainty −0.02 (kuvinjelwe ku-[0, 1]) |
| `applyNegativeSignal()` | engagement −0.03, uncertainty +0.03 (kuvinjelwe) |
| `applyIdleDecay(hours)` | decay = min(0.3, hours × 0.02); engagement ne-energy zishelela ziye ku-0.5 nge-linear interpolation |

Amavektha okuhlola aku-[`fixtures/affect_state.json`](fixtures/affect_state.json) (amavektha angu-12). Aqinisekiswa yi-CI kuzo zonke izilimi ezingu-10.

---

## Irejista Yezilimi (amathegi angu-20 e-BCP-47)

`zu` · `st` · `af` · `sw` · `ha` · `am` · `yo` · `ig` · `xh` · `nso` · `tn` · `so` · `om` · `ar` · `en` · `pt` · `fr` · `es` · `zh` · `hi`

---

## Isakhiwo Sendawo Yokugcina

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

| Uhlelo Lokusebenza | Isiqalisi |
|----------|---------|
| [Ukuqinisekiswa Kwama-Fixture](.github/workflows/fixture-validation.yml) | push/PR ku-master — iqhuba wonke amaqoqo okuhlola angu-10 |
| [Ukushicilela](.github/workflows/publish.yml) | git tag `v*.*.*` — ishicilela ku-NuGet, crates.io, PyPI, npm, GitHub Packages |

---

## Ilayisensi

MIT
