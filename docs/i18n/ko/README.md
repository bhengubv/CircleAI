# Circle AI — 10개 언어 SDK

Circle AI 컴패니언 스택의 이식성 있는 핵심 구성 요소입니다. 모든
[Aether Protocol](https://github.com/bhengubv/aether-protocol) 노드(웨어러블,
스마트폰, IoT, HarmonyOS)에서 FFI 오버헤드 및 런타임 브리지 없이 네이티브로 동작합니다.

---

## 이식 가능한 핵심 (8개 모듈)

| 모듈 | 주요 타입 |
|--------|-----------|
| **models** | `ChatMessage`, `DownloadProgress` |
| **memory** | `AffectState`, `EpisodicMemoryEntry`, `PersonaState`, `Goal` |
| **identity** | `CircleIdentity`, `RegisteredDevice`, `IdentityTier` |
| **languages** | `LanguageTag`, `KnownLanguages` (20개 BCP-47 태그), `WritingSystem` |
| **companion** | `CompanionContext`, `CompanionTurn`, `ICompanionSession` |
| **inference** | `GenerationOptions`, `IChatGenerator` |
| **tools** | `ToolDefinition`, `ToolInvocation`, `ToolResult`, `IToolBridge` |
| **sync** | `SyncDelta`, `SyncDeliveryMode`, `ISyncChannel` |

---

## 뉴런 — 기기에서 동작하는 작은 AI 두뇌

**뉴런**은 여러분 자신의 기기에서 동작하는 작은 AI 두뇌입니다. 여러분의 스마트폰이나 노트북 바로 그 자리에서 생각하고, 기억하고, 대화합니다 — 서버로는 아무것도 보내지 않습니다. 빠른 일상용 도우미가 대부분의 질문에 답하며, 더 어려운 작업(그림 읽기, 긴 문서 읽기, 또는 신중한 단계별 사고)에는 조용히 전문가를 불러와 답한 뒤 옆으로 치워 둡니다. 한 번에 하나의 전문가만 유지하므로 기기가 가진 것보다 더 많은 메모리가 필요한 경우가 결코 없으며, 대화를 기억하기 때문에 채팅은 중단된 지점에서 계속 이어집니다.

**하나의 뉴런, 또는 여럿 — 두뇌들로 이루어진 두뇌.** 하나의 뉴런은 그 자체만으로도 잘 작동합니다. 하지만 뉴런은 뇌 속의 뇌세포처럼 서로 연결될 수도 있으며 — 바로 여기에 진정한 힘이 있습니다. (이 부분은 아직 구현되지 않았습니다. 이는 우리가 가능해질 것이라 믿는 것이며, 이 노드를 뉴런이라 부르는 이유입니다.) 그룹 안에서 뉴런들은 작업을 나누고, 어떤 뉴런이 전문가를 담지 못할 때 서로 도우며, 주도하는 노드 없이 대등하게 답하고, 여러분의 개인 데이터를 여러분 자신의 기기에 보관합니다 — 뉴런들 사이를 오가는 것은 오직 질문뿐입니다. 각 뉴런은 이미 그 자체로 하나의 온전한 지성이므로, 뉴런들의 그룹은 여러 온전한 지성이 서로 도와 어느 하나만으로는 해낼 수 없는 일을 함께 해내는 것입니다.

뉴런은 C# 참조 구현체와 7개의 모든 자매 이식판(Python, TypeScript, Go, Kotlin, Swift, Rust, C)에 포함되어 제공됩니다. HarmonyOS/ArkTS는 아직 준비 중입니다.

---

## 언어별 빠른 시작

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

## AffectState — 언어 간 수학적 일관성

10개 구현체 모두 동일한 부동소수점 결과를 생성합니다 (ε ≤ 1e-5).

| 연산 | 효과 |
|-----------|--------|
| `applyPositiveSignal()` | engagement +0.02, rapport +0.01, uncertainty −0.02 (범위 [0, 1]로 제한) |
| `applyNegativeSignal()` | engagement −0.03, uncertainty +0.03 (범위 제한) |
| `applyIdleDecay(hours)` | decay = min(0.3, hours × 0.02); engagement과 energy가 0.5 방향으로 선형 보간 |

테스트 벡터는 [`fixtures/affect_state.json`](fixtures/affect_state.json)에 있습니다 (12개 벡터). CI에서 10개 언어 모두 검증됩니다.

---

## 언어 레지스트리 (20개 BCP-47 태그)

`zu` · `st` · `af` · `sw` · `ha` · `am` · `yo` · `ig` · `xh` · `nso` · `tn` · `so` · `om` · `ar` · `en` · `pt` · `fr` · `es` · `zh` · `hi`

---

## 저장소 구조

```
CircleAI/
├── src/            C# 참조 구현체 (CircleAI.*)
├── tests/          C# 테스트 스위트
├── fixtures/       언어 간 테스트 벡터 (JSON)
├── docs/           CONTRACTS.md · MEMORY_SPEC.md · COMPANION_SPEC.md
├── android/        Kotlin/Android 라이브러리
├── c/              순수 C99, CMake
├── go/             Go 모듈
├── harmonyos/      ArkTS, OpenHarmony
├── kotlin/         Kotlin/JVM
├── python/         Python 3.12+
├── rust/           Rust, Cargo
├── swift/          Swift 5.9+, Swift Package Manager
└── typescript/     TypeScript, npm
```

---

## CI

| 워크플로 | 트리거 |
|----------|---------|
| [Fixture Validation](.github/workflows/fixture-validation.yml) | master에 push/PR 시 — 10개 테스트 스위트 전체 실행 |
| [Publish](.github/workflows/publish.yml) | git 태그 `v*.*.*` 시 — NuGet, crates.io, PyPI, npm, GitHub Packages에 게시 |

---

## 라이선스

MIT
