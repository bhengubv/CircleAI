# Circle AI — SDK für 10 Sprachen

Der portable Kern des Circle-AI-Companion-Stacks. Läuft nativ neben jedem
[Aether Protocol](https://github.com/bhengubv/aether-protocol)-Knoten — Wearable,
Mobiltelefon, IoT, HarmonyOS — ohne FFI-Overhead und ohne Laufzeit-Bridging.

---

## Portabler Kern (8 Module)

| Modul | Wichtige Typen |
|-------|----------------|
| **models** | `ChatMessage`, `DownloadProgress` |
| **memory** | `AffectState`, `EpisodicMemoryEntry`, `PersonaState`, `Goal` |
| **identity** | `CircleIdentity`, `RegisteredDevice`, `IdentityTier` |
| **languages** | `LanguageTag`, `KnownLanguages` (20 BCP-47-Tags), `WritingSystem` |
| **companion** | `CompanionContext`, `CompanionTurn`, `ICompanionSession` |
| **inference** | `GenerationOptions`, `IChatGenerator` |
| **tools** | `ToolDefinition`, `ToolInvocation`, `ToolResult`, `IToolBridge` |
| **sync** | `SyncDelta`, `SyncDeliveryMode`, `ISyncChannel` |

---

## Das Neuron — ein kleines KI-Gehirn auf deinem Gerät

Ein **Neuron** ist ein kleines KI-Gehirn, das auf deinem eigenen Gerät läuft.
Es denkt, erinnert sich und spricht — direkt auf deinem Telefon oder Laptop,
ohne dass etwas an einen Server gesendet wird. Ein schneller Alltagshelfer
beantwortet die meisten Fragen; für eine schwierigere Aufgabe (das Lesen eines
Bildes, eines langen Dokuments oder sorgfältiges schrittweises Nachdenken) lädt
es leise einen Spezialisten, antwortet und legt ihn dann wieder beiseite. Es
behält immer nur einen Spezialisten gleichzeitig, sodass es nie mehr Speicher
benötigt, als das Gerät hat, und es merkt sich das Gespräch, sodass ein Chat
dort weitergeht, wo er aufgehört hat.

**Ein Neuron oder viele — ein Gehirn aus Gehirnen.** Ein einzelnes Neuron
funktioniert für sich allein gut. Aber Neuronen können sich auch
zusammenschließen, wie Gehirnzellen in einem Gehirn — und genau darin liegt die
eigentliche Stärke. (Dieser Teil ist noch nicht gebaut; es ist das, was unserer
Überzeugung nach möglich wird, und es ist der Grund, warum der Knoten Neuron
heißt.) In einer Gruppe teilen sie sich die Arbeit, helfen einander, wenn eines
keinen Spezialisten unterbringen kann, antworten als Gleichberechtigte ohne
einen Knoten, der das Sagen hat, und behalten deine privaten Daten auf deinem
eigenen Gerät — nur die Frage wandert jemals zwischen ihnen hin und her. Jedes
Neuron ist bereits ein ganzer Verstand, sodass eine Gruppe von Neuronen viele
ganze Verstände sind, die einander helfen, mehr zu leisten, als es jedes von
ihnen allein könnte.

Das Neuron ist in der C#-Referenz und allen sieben Schwester-Ports (Python,
TypeScript, Go, Kotlin, Swift, Rust, C) enthalten. HarmonyOS/ArkTS steht noch
aus.

---

## Schnellstart nach Sprache

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

## AffectState — Sprachübergreifende Mathematik

Alle 10 Implementierungen liefern identische Fließkommaergebnisse (ε ≤ 1e-5).

| Operation | Auswirkung |
|-----------|------------|
| `applyPositiveSignal()` | engagement +0.02, rapport +0.01, uncertainty −0.02 (begrenzt auf [0, 1]) |
| `applyNegativeSignal()` | engagement −0.03, uncertainty +0.03 (begrenzt) |
| `applyIdleDecay(hours)` | decay = min(0.3, hours × 0.02); engagement und energy interpolieren linear in Richtung 0.5 |

Testvektoren in [`fixtures/affect_state.json`](fixtures/affect_state.json) (12 Vektoren). Durch CI in allen 10 Sprachen validiert.

---

## Sprachregister (20 BCP-47-Tags)

`zu` · `st` · `af` · `sw` · `ha` · `am` · `yo` · `ig` · `xh` · `nso` · `tn` · `so` · `om` · `ar` · `en` · `pt` · `fr` · `es` · `zh` · `hi`

---

## Repository-Struktur

```
CircleAI/
├── src/            C#-Referenzimplementierung (CircleAI.*)
├── tests/          C#-Testsuite
├── fixtures/       Sprachübergreifende Testvektoren (JSON)
├── docs/           CONTRACTS.md · MEMORY_SPEC.md · COMPANION_SPEC.md
├── android/        Kotlin/Android-Bibliothek
├── c/              Reines C99, CMake
├── go/             Go-Modul
├── harmonyos/      ArkTS, OpenHarmony
├── kotlin/         Kotlin/JVM
├── python/         Python 3.12+
├── rust/           Rust, Cargo
├── swift/          Swift 5.9+, Swift Package Manager
└── typescript/     TypeScript, npm
```

---

## CI

| Workflow | Auslöser |
|----------|---------|
| [Fixture Validation](.github/workflows/fixture-validation.yml) | Push/PR auf master — führt alle 10 Testsuites aus |
| [Publish](.github/workflows/publish.yml) | Git-Tag `v*.*.*` — veröffentlicht auf NuGet, crates.io, PyPI, npm, GitHub Packages |

---

## Lizenz

MIT
