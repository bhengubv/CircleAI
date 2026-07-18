# Circle AI — SDK ya Dipuo tse 10

Ke motheo o nkehang wa tsamaiso ya motswalle wa Circle AI. E sebetsa ka tlhaho
haufi le node e nngwe le e nngwe ya [Aether Protocol](https://github.com/bhengubv/aether-protocol) —
se aparwang, mohala, IoT, HarmonyOS — ntle le boima bo eketsehileng ba FFI
le ntle le marokho a runtime.

---

## Motheo o Nkehang (limodule tse 8)

| Mojule | Mefuta e ka sehloohong |
|--------|-----------|
| **models** | `ChatMessage`, `DownloadProgress` |
| **memory** | `AffectState`, `EpisodicMemoryEntry`, `PersonaState`, `Goal` |
| **identity** | `CircleIdentity`, `RegisteredDevice`, `IdentityTier` |
| **languages** | `LanguageTag`, `KnownLanguages` (matshwao a 20 a BCP-47), `WritingSystem` |
| **companion** | `CompanionContext`, `CompanionTurn`, `ICompanionSession` |
| **inference** | `GenerationOptions`, `IChatGenerator` |
| **tools** | `ToolDefinition`, `ToolInvocation`, `ToolResult`, `IToolBridge` |
| **sync** | `SyncDelta`, `SyncDeliveryMode`, `ISyncChannel` |

---

## Nyurone — boko bo bonyenyane ba AI sesebedisweng sa hao

**Nyurone** ke boko bo bonyenyane ba AI bo sebetsang sesebedisweng sa hao ka bowena.
Ea nahana, ea hopola, mme ea bua — hona moo mohaleng wa hao kapa laptop ya hao, ho se
letho le rometsweng seveng. Mothusi ya potlakileng wa letsatsi le letsatsi o araba dipotso
tse ngata; empa mosebetsing o thata (ho bala setshwantsho, tokomane e telele, kapa ho
nahana ka hloko mohato ka mohato) e kenya setsebi ka khutso, e arabe, ebe e se beha ka
thoko. E boloka setsebi se le seng feela ka nako, kahoo ha e hloke memori e fetang eo
sesebediswa se nang le yona, mme e hopola moqoqo e le hore puisano e tswele pele moo e
emeng teng.

**Nyurone e le nngwe, kapa tse ngata — boko bo entsweng ka boko.** Nyurone e le nngwe e
sebetsa hantle e le nngwe. Empa di-Nyurone di ka boela tsa kopana, jwalo ka disele tsa boko
ka hare ho boko — mme ke moo matla a sebele a leng teng. (Karolo ena ha e so ka e ahwa; ke
seo re dumelang hore se ka kgoneha, mme ke lona lebaka leo ka lona node e bitswang Nyurone.)
Ka sehlopheng di arolelana mosebetsi, di thusana ha e nngwe e sitwa ho kenya setsebi, di
araba di lekana ntle le node e laolang, mme di boloka data ya hao ya lekunutu sesebedisweng
sa hao ka bowena — ke potso feela e tsamayang pakeng tsa tsona. Nyurone e nngwe le e nngwe e
se e le kelello e felletseng, kahoo sehlopha sa di-Nyurone ke dikelello tse ngata tse
felletseng tse thusanang ho etsa ho fetang seo le e le nngwe ya tsona e ka se etsang e le
nngwe.

Nyurone e fumaneha phetolelong ya motheo ya C# le diphetolelong tsohle tse supileng tse
tshwanang (Python, TypeScript, Go, Kotlin, Swift, Rust, C). HarmonyOS/ArkTS e sa ntse e tla.

---

## Qalo e Potlakileng ka Puo ka Nngwe

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

## AffectState — Dipalo tse Tshwanang Dipuong Tsohle

Diphetolelo tsohle tse 10 di hlahisa diphetho tse tshwanang tsa floating-point (ε ≤ 1e-5).

| Tshebetso | Phello |
|-----------|--------|
| `applyPositiveSignal()` | engagement +0.02, rapport +0.01, uncertainty −0.02 (e thibetswe ho [0, 1]) |
| `applyNegativeSignal()` | engagement −0.03, uncertainty +0.03 (e thibetswe) |
| `applyIdleDecay(hours)` | decay = min(0.3, hours × 0.02); engagement le energy di sutha ho ya ho 0.5 ka linear interpolation |

Di-vector tsa teko ho [`fixtures/affect_state.json`](fixtures/affect_state.json) (di-vector tse 12). Di netefaditswe ke CI dipuong tsohle tse 10.

---

## Lenane la Dipuo (matshwao a 20 a BCP-47)

`zu` · `st` · `af` · `sw` · `ha` · `am` · `yo` · `ig` · `xh` · `nso` · `tn` · `so` · `om` · `ar` · `en` · `pt` · `fr` · `es` · `zh` · `hi`

---

## Sebopeho sa Polokelo

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

| Workflow | Qaliso |
|----------|---------|
| [Netefatso ya Fixture](.github/workflows/fixture-validation.yml) | push/PR ho master — e matha dihlopha tsohle tse 10 tsa diteko |
| [Phatlalatso](.github/workflows/publish.yml) | git tag `v*.*.*` — e phatlalatsa ho NuGet, crates.io, PyPI, npm, GitHub Packages |

---

## Laesense

MIT
