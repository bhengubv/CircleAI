<div dir="rtl">

# Circle AI — کتابخانه SDK ده زبانه

هسته قابل‌حمل پشته همراه Circle AI. به‌صورت بومی در کنار هر گره
[Aether Protocol](https://github.com/bhengubv/aether-protocol) اجرا می‌شود — پوشیدنی،
تلفن، اینترنت اشیاء، HarmonyOS — بدون سربار FFI و بدون پل اتصال runtime.

---

## هسته قابل‌حمل (۸ ماژول)

| ماژول | انواع اصلی |
|--------|-----------|
| **models** | `ChatMessage`, `DownloadProgress` |
| **memory** | `AffectState`, `EpisodicMemoryEntry`, `PersonaState`, `Goal` |
| **identity** | `CircleIdentity`, `RegisteredDevice`, `IdentityTier` |
| **languages** | `LanguageTag`, `KnownLanguages` (۲۰ برچسب BCP-47)، `WritingSystem` |
| **companion** | `CompanionContext`, `CompanionTurn`, `ICompanionSession` |
| **inference** | `GenerationOptions`, `IChatGenerator` |
| **tools** | `ToolDefinition`, `ToolInvocation`, `ToolResult`, `IToolBridge` |
| **sync** | `SyncDelta`, `SyncDeliveryMode`, `ISyncChannel` |

---

## شروع سریع به تفکیک زبان

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

## AffectState — محاسبات یکسان در همه زبان‌ها

تمام ۱۰ پیاده‌سازی نتایج float یکسانی تولید می‌کنند (ε ≤ 1e-5).

| عملیات | تأثیر |
|-----------|--------|
| `applyPositiveSignal()` | engagement ‎+0.02،‏ rapport ‎+0.01،‏ uncertainty ‎−0.02 (محدود به [0, 1]) |
| `applyNegativeSignal()` | engagement ‎−0.03،‏ uncertainty ‎+0.03 (محدود) |
| `applyIdleDecay(hours)` | decay = min(0.3, hours × 0.02)؛ engagement و energy به سمت 0.5 میل می‌کنند |

بردارهای آزمون در [`fixtures/affect_state.json`](fixtures/affect_state.json) (۱۲ بردار). توسط CI در تمام ۱۰ زبان تأیید می‌شود.

---

## ثبت زبان‌ها (۲۰ برچسب BCP-47)

`zu` · `st` · `af` · `sw` · `ha` · `am` · `yo` · `ig` · `xh` · `nso` · `tn` · `so` · `om` · `ar` · `en` · `pt` · `fr` · `es` · `zh` · `hi`

---

## ساختار مخزن

```
CircleAI/
├── src/            پیاده‌سازی مرجع C# (CircleAI.*)
├── tests/          مجموعه آزمون C#
├── fixtures/       بردارهای آزمون بین‌زبانی (JSON)
├── docs/           CONTRACTS.md · MEMORY_SPEC.md · COMPANION_SPEC.md
├── android/        کتابخانه Kotlin/Android
├── c/              C99 خالص، CMake
├── go/             ماژول Go
├── harmonyos/      ArkTS، OpenHarmony
├── kotlin/         Kotlin/JVM
├── python/         Python 3.12+
├── rust/           Rust، Cargo
├── swift/          Swift 5.9+، Swift Package Manager
└── typescript/     TypeScript، npm
```

---

## CI

| گردش‌کار | راه‌انداز |
|----------|---------|
| [Fixture Validation](.github/workflows/fixture-validation.yml) | push/PR به master — همه ۱۰ مجموعه آزمون را اجرا می‌کند |
| [Publish](.github/workflows/publish.yml) | git tag ‎`v*.*.*`‎ — به NuGet، crates.io، PyPI، npm، GitHub Packages منتشر می‌کند |

---

## مجوز

MIT

</div>
