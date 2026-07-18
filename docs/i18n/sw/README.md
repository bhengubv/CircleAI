# Circle AI — SDK ya Lugha 10

Kiini kinachobebeka cha mrundikano wa msindikizaji wa Circle AI. Hukimbia kwa
asili kando ya kila node ya
[Aether Protocol](https://github.com/bhengubv/aether-protocol) —
kifaa cha kuvaliwa, simu, IoT, HarmonyOS — bila gharama ya ziada ya FFI wala
madaraja ya wakati wa utekelezaji.

---

## Kiini Kinachobebeka (moduli 8)

| Moduli | Aina kuu |
|--------|-----------|
| **models** | `ChatMessage`, `DownloadProgress` |
| **memory** | `AffectState`, `EpisodicMemoryEntry`, `PersonaState`, `Goal` |
| **identity** | `CircleIdentity`, `RegisteredDevice`, `IdentityTier` |
| **languages** | `LanguageTag`, `KnownLanguages` (vitambulisho 20 vya BCP-47), `WritingSystem` |
| **companion** | `CompanionContext`, `CompanionTurn`, `ICompanionSession` |
| **inference** | `GenerationOptions`, `IChatGenerator` |
| **tools** | `ToolDefinition`, `ToolInvocation`, `ToolResult`, `IToolBridge` |
| **sync** | `SyncDelta`, `SyncDeliveryMode`, `ISyncChannel` |

---

## Nyuroni — ubongo mdogo wa AI kwenye kifaa chako

**Nyuroni** ni ubongo mdogo wa AI unaokimbia kwenye kifaa chako mwenyewe.
Hufikiri, hukumbuka, na huzungumza — hapo hapo kwenye simu au kompyuta yako ya
mkononi, bila chochote kutumwa kwa seva. Msaidizi wa kila siku mwenye kasi
hujibu maswali mengi; kwa kazi ngumu zaidi (kusoma picha, hati ndefu, au
kufikiri kwa makini hatua kwa hatua) hupakia mtaalamu kimya kimya, hujibu, kisha
humweka kando. Huhifadhi mtaalamu mmoja tu kwa wakati, hivyo haihitaji kamwe
kumbukumbu zaidi ya iliyopo kwenye kifaa, na hukumbuka mazungumzo ili gumzo
liendelee pale lilipoishia.

**Nyuroni Moja, au Nyingi — ubongo uliotengenezwa kwa bongo.** Nyuroni moja
hufanya kazi vizuri peke yake. Lakini Nyuroni pia zinaweza kuungana, kama seli
za ubongo ndani ya ubongo — na hapo ndipo nguvu halisi zilipo. (Sehemu hii bado
haijajengwa; ni kile tunachoamini kuwa kitawezekana, na ndiyo sababu node
huitwa Nyuroni.) Katika kikundi hushirikiana kazi, husaidiana wakati Nyuroni
moja inaposhindwa kubeba mtaalamu, hujibu kwa usawa bila node yoyote kuwa
kiongozi, na huhifadhi data yako ya faragha kwenye kifaa chako mwenyewe — swali
pekee ndilo husafiri kati yao. Kila Nyuroni tayari ni akili kamili, hivyo
kikundi cha Nyuroni ni akili kamili nyingi zinazosaidiana kufanya zaidi ya vile
yoyote kati yazo ingeweza peke yake.

Nyuroni inapatikana katika marejeo ya C# na matoleo dada yote saba (Python,
TypeScript, Go, Kotlin, Swift, Rust, C). HarmonyOS/ArkTS bado inakuja.

---

## Anza Haraka kwa Kila Lugha

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

## AffectState — Hesabu Baina ya Lugha

Utekelezaji wote 10 huzalisha matokeo ya nukta-elea yanayofanana (ε ≤ 1e-5).

| Operesheni | Athari |
|-----------|--------|
| `applyPositiveSignal()` | engagement +0.02, rapport +0.01, uncertainty −0.02 (imebanwa hadi [0, 1]) |
| `applyNegativeSignal()` | engagement −0.03, uncertainty +0.03 (imebanwa) |
| `applyIdleDecay(hours)` | decay = min(0.3, hours × 0.02); engagement na energy huelekea 0.5 kwa interpolation ya mstari |

Vekta za majaribio katika [`fixtures/affect_state.json`](fixtures/affect_state.json) (vekta 12). Zimehakikiwa na CI katika lugha zote 10.

---

## Rejista ya Lugha (vitambulisho 20 vya BCP-47)

`zu` · `st` · `af` · `sw` · `ha` · `am` · `yo` · `ig` · `xh` · `nso` · `tn` · `so` · `om` · `ar` · `en` · `pt` · `fr` · `es` · `zh` · `hi`

---

## Muundo wa Hifadhi

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

| Mtiririko wa Kazi | Kichochezi |
|----------|---------|
| [Uthibitishaji wa Fixture](.github/workflows/fixture-validation.yml) | push/PR kwenda master — huendesha seti zote 10 za majaribio |
| [Chapisha](.github/workflows/publish.yml) | git tag `v*.*.*` — huchapisha kwa NuGet, crates.io, PyPI, npm, GitHub Packages |

---

## Leseni

MIT
