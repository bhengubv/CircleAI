# Circle AI — SDK для 10 языков программирования

Портативное ядро компаньон-стека Circle AI. Работает нативно рядом с каждым
узлом [Aether Protocol](https://github.com/bhengubv/aether-protocol) — носимые устройства,
телефоны, IoT, HarmonyOS — без накладных расходов FFI и без прослоек runtime.

---

## Портативное ядро (8 модулей)

| Модуль | Основные типы |
|--------|-----------|
| **models** | `ChatMessage`, `DownloadProgress` |
| **memory** | `AffectState`, `EpisodicMemoryEntry`, `PersonaState`, `Goal` |
| **identity** | `CircleIdentity`, `RegisteredDevice`, `IdentityTier` |
| **languages** | `LanguageTag`, `KnownLanguages` (20 тегов BCP-47), `WritingSystem` |
| **companion** | `CompanionContext`, `CompanionTurn`, `ICompanionSession` |
| **inference** | `GenerationOptions`, `IChatGenerator` |
| **tools** | `ToolDefinition`, `ToolInvocation`, `ToolResult`, `IToolBridge` |
| **sync** | `SyncDelta`, `SyncDeliveryMode`, `ISyncChannel` |

---

## Нейрон — маленький ИИ-мозг на вашем устройстве

**Нейрон** — это маленький ИИ-мозг, работающий на вашем собственном устройстве.
Он думает, помнит и разговаривает — прямо на вашем телефоне или ноутбуке,
ничего не отправляя на сервер. Быстрый повседневный помощник отвечает на
большинство вопросов; для более трудной задачи (распознать картинку, длинный
документ или аккуратно порассуждать шаг за шагом) он тихо загружает
специалиста, отвечает, а затем откладывает его в сторону. Он держит лишь
одного специалиста одновременно, поэтому ему никогда не требуется больше
памяти, чем есть у устройства, и он помнит разговор, так что беседа
продолжается с того места, где прервалась.

**Один Нейрон или много — мозг, состоящий из мозгов.** Один Нейрон прекрасно
работает сам по себе. Но Нейроны могут и объединяться, как клетки мозга в
мозге — и именно в этом настоящая сила. (Эта часть ещё не создана; это то, что,
по нашему мнению, становится возможным, и именно поэтому узел называется
Нейроном.) В группе они делят работу между собой, помогают друг другу, когда
один не может вместить специалиста, отвечают на равных, без узла, который всем
командует, и хранят ваши личные данные на вашем собственном устройстве — между
ними путешествует только вопрос. Каждый Нейрон уже является целым разумом,
поэтому группа Нейронов — это множество целых разумов, помогающих друг другу
сделать больше, чем мог бы любой из них в одиночку.

Нейрон поставляется в эталонной реализации на C# и во всех семи родственных
портах (Python, TypeScript, Go, Kotlin, Swift, Rust, C). HarmonyOS/ArkTS ещё
впереди.

---

## Быстрый старт по языкам

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

## AffectState — математика для всех языков

Все 10 реализаций дают идентичные результаты с плавающей точкой (ε ≤ 1e-5).

| Операция | Эффект |
|-----------|--------|
| `applyPositiveSignal()` | engagement +0.02, rapport +0.01, uncertainty −0.02 (ограничено [0, 1]) |
| `applyNegativeSignal()` | engagement −0.03, uncertainty +0.03 (ограничено) |
| `applyIdleDecay(hours)` | decay = min(0.3, hours × 0.02); engagement и energy стремятся к 0.5 через линейную интерполяцию |

Тестовые векторы в [`fixtures/affect_state.json`](fixtures/affect_state.json) (12 векторов). Проверяются CI на всех 10 языках.

---

## Реестр языков (20 тегов BCP-47)

`zu` · `st` · `af` · `sw` · `ha` · `am` · `yo` · `ig` · `xh` · `nso` · `tn` · `so` · `om` · `ar` · `en` · `pt` · `fr` · `es` · `zh` · `hi`

---

## Структура репозитория

```
CircleAI/
├── src/            Эталонная реализация на C# (CircleAI.*)
├── tests/          Набор тестов на C#
├── fixtures/       Кросс-языковые тестовые векторы (JSON)
├── docs/           CONTRACTS.md · MEMORY_SPEC.md · COMPANION_SPEC.md
├── android/        Библиотека Kotlin/Android
├── c/              Чистый C99, CMake
├── go/             Go-модуль
├── harmonyos/      ArkTS, OpenHarmony
├── kotlin/         Kotlin/JVM
├── python/         Python 3.12+
├── rust/           Rust, Cargo
├── swift/          Swift 5.9+, Swift Package Manager
└── typescript/     TypeScript, npm
```

---

## CI

| Рабочий процесс | Триггер |
|----------|---------|
| [Fixture Validation](.github/workflows/fixture-validation.yml) | push/PR в master — запускает все 10 наборов тестов |
| [Publish](.github/workflows/publish.yml) | git tag `v*.*.*` — публикует на NuGet, crates.io, PyPI, npm, GitHub Packages |

---

## Лицензия

MIT
