# Circle AI — SDK para 10 Lenguajes

El núcleo portable del stack de compañero Circle AI. Se ejecuta de forma nativa junto a cada
nodo de [Aether Protocol](https://github.com/bhengubv/aether-protocol) — dispositivo wearable,
teléfono, IoT, HarmonyOS — sin sobrecarga de FFI ni puentes en tiempo de ejecución.

---

## Núcleo Portable (8 módulos)

| Módulo | Tipos principales |
|--------|-----------|
| **models** | `ChatMessage`, `DownloadProgress` |
| **memory** | `AffectState`, `EpisodicMemoryEntry`, `PersonaState`, `Goal` |
| **identity** | `CircleIdentity`, `RegisteredDevice`, `IdentityTier` |
| **languages** | `LanguageTag`, `KnownLanguages` (20 etiquetas BCP-47), `WritingSystem` |
| **companion** | `CompanionContext`, `CompanionTurn`, `ICompanionSession` |
| **inference** | `GenerationOptions`, `IChatGenerator` |
| **tools** | `ToolDefinition`, `ToolInvocation`, `ToolResult`, `IToolBridge` |
| **sync** | `SyncDelta`, `SyncDeliveryMode`, `ISyncChannel` |

---

## La Neurona — un pequeño cerebro de IA en tu dispositivo

Una **Neurona** es un pequeño cerebro de IA que se ejecuta en tu propio dispositivo. Piensa, recuerda y habla — allí mismo, en tu teléfono o portátil, sin enviar nada a un servidor. Un ayudante rápido para el día a día responde la mayoría de las preguntas; para una tarea más difícil (leer una imagen, un documento largo o razonar con cuidado paso a paso) carga discretamente a un especialista, responde y luego lo deja a un lado. Mantiene solo un especialista a la vez, por lo que nunca necesita más memoria de la que tiene el dispositivo, y recuerda la conversación para que un chat continúe donde lo dejó.

**Una Neurona, o muchas — un cerebro hecho de cerebros.** Una sola Neurona funciona bien por sí misma. Pero las Neuronas también pueden unirse, como las células de un cerebro — y ahí es donde está el verdadero poder. (Esta parte aún no está construida; es lo que creemos que se vuelve posible, y es la razón por la que el nodo se llama Neurona.) En grupo reparten el trabajo, se ayudan entre sí cuando una no puede alojar a un especialista, responden como iguales sin ningún nodo al mando y mantienen tus datos privados en tu propio dispositivo — solo la pregunta viaja entre ellas. Cada Neurona ya es una mente completa, así que un grupo de Neuronas es muchas mentes completas que se ayudan entre sí para hacer más de lo que cualquiera de ellas podría sola.

La Neurona se incluye en la implementación de referencia en C# y en las siete portaciones hermanas (Python, TypeScript, Go, Kotlin, Swift, Rust, C). HarmonyOS/ArkTS aún está por llegar.

---

## Inicio Rápido por Lenguaje

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

## AffectState — Matemáticas Multilenguaje

Las 10 implementaciones producen resultados de coma flotante idénticos (ε ≤ 1e-5).

| Operación | Efecto |
|-----------|--------|
| `applyPositiveSignal()` | engagement +0.02, rapport +0.01, uncertainty −0.02 (limitado a [0, 1]) |
| `applyNegativeSignal()` | engagement −0.03, uncertainty +0.03 (limitado) |
| `applyIdleDecay(hours)` | decay = min(0.3, hours × 0.02); engagement y energy convergen hacia 0.5 mediante interpolación lineal |

Vectores de prueba en [`fixtures/affect_state.json`](fixtures/affect_state.json) (12 vectores). Validados por CI en los 10 lenguajes.

---

## Registro de Idiomas (20 etiquetas BCP-47)

`zu` · `st` · `af` · `sw` · `ha` · `am` · `yo` · `ig` · `xh` · `nso` · `tn` · `so` · `om` · `ar` · `en` · `pt` · `fr` · `es` · `zh` · `hi`

---

## Estructura del Repositorio

```
CircleAI/
├── src/            Implementación de referencia en C# (CircleAI.*)
├── tests/          Suite de pruebas en C#
├── fixtures/       Vectores de prueba multilenguaje (JSON)
├── docs/           CONTRACTS.md · MEMORY_SPEC.md · COMPANION_SPEC.md
├── android/        Biblioteca Kotlin/Android
├── c/              C99 puro, CMake
├── go/             Módulo Go
├── harmonyos/      ArkTS, OpenHarmony
├── kotlin/         Kotlin/JVM
├── python/         Python 3.12+
├── rust/           Rust, Cargo
├── swift/          Swift 5.9+, Swift Package Manager
└── typescript/     TypeScript, npm
```

---

## CI

| Flujo de trabajo | Disparador |
|----------|---------|
| [Validación de Fixtures](.github/workflows/fixture-validation.yml) | push/PR a master — ejecuta las 10 suites de prueba |
| [Publicación](.github/workflows/publish.yml) | etiqueta git `v*.*.*` — publica en NuGet, crates.io, PyPI, npm, GitHub Packages |

---

## Licencia

MIT
