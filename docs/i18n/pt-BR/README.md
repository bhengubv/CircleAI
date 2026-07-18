# Circle AI — SDK para 10 Linguagens

O núcleo portátil da pilha de IA companion do Circle. Roda nativamente junto a cada
nó do [Aether Protocol](https://github.com/bhengubv/aether-protocol) — wearable,
smartphone, IoT, HarmonyOS — sem overhead de FFI e sem pontes de runtime.

---

## Núcleo Portátil (8 módulos)

| Módulo | Tipos principais |
|--------|-----------------|
| **models** | `ChatMessage`, `DownloadProgress` |
| **memory** | `AffectState`, `EpisodicMemoryEntry`, `PersonaState`, `Goal` |
| **identity** | `CircleIdentity`, `RegisteredDevice`, `IdentityTier` |
| **languages** | `LanguageTag`, `KnownLanguages` (20 tags BCP-47), `WritingSystem` |
| **companion** | `CompanionContext`, `CompanionTurn`, `ICompanionSession` |
| **inference** | `GenerationOptions`, `IChatGenerator` |
| **tools** | `ToolDefinition`, `ToolInvocation`, `ToolResult`, `IToolBridge` |
| **sync** | `SyncDelta`, `SyncDeliveryMode`, `ISyncChannel` |

---

## O Neurônio — um pequeno cérebro de IA no seu dispositivo

Um **Neurônio** é um pequeno cérebro de IA que roda no seu próprio dispositivo. Ele pensa, lembra e conversa — ali mesmo, no seu celular ou notebook, sem enviar nada para um servidor. Um ajudante rápido para o dia a dia responde à maioria das perguntas; para uma tarefa mais difícil (ler uma imagem, um documento longo ou raciocinar com cuidado, passo a passo) ele carrega discretamente um especialista, responde e depois o deixa de lado. Ele mantém apenas um especialista por vez, então nunca precisa de mais memória do que o dispositivo tem, e lembra da conversa para que um bate-papo continue de onde parou.

**Um Neurônio, ou muitos — um cérebro feito de cérebros.** Um Neurônio funciona bem sozinho. Mas os Neurônios também podem se unir, como as células de um cérebro — e é aí que está o verdadeiro poder. (Esta parte ainda não foi construída; é o que acreditamos que se torna possível, e é a razão pela qual o nó é chamado de Neurônio.) Em grupo, eles dividem o trabalho, ajudam uns aos outros quando um não consegue acomodar um especialista, respondem como iguais sem nenhum nó no comando e mantêm seus dados privados no seu próprio dispositivo — apenas a pergunta trafega entre eles. Cada Neurônio já é uma mente completa, então um grupo de Neurônios é muitas mentes completas ajudando umas às outras a fazer mais do que qualquer uma delas conseguiria sozinha.

O Neurônio vem na implementação de referência em C# e em todos os sete ports irmãos (Python, TypeScript, Go, Kotlin, Swift, Rust, C). HarmonyOS/ArkTS ainda está por vir.

---

## Início Rápido por Linguagem

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

## AffectState — Matemática Multilinguagem

Todas as 10 implementações produzem resultados em ponto flutuante idênticos (ε ≤ 1e-5).

| Operação | Efeito |
|----------|--------|
| `applyPositiveSignal()` | engagement +0.02, rapport +0.01, uncertainty −0.02 (clamped [0, 1]) |
| `applyNegativeSignal()` | engagement −0.03, uncertainty +0.03 (clamped) |
| `applyIdleDecay(hours)` | decay = min(0.3, hours × 0.02); engagement e energy fazem lerp em direção a 0.5 |

Vetores de teste em [`fixtures/affect_state.json`](fixtures/affect_state.json) (12 vetores). Validados pelo CI em todas as 10 linguagens.

---

## Registro de Idiomas (20 tags BCP-47)

`zu` · `st` · `af` · `sw` · `ha` · `am` · `yo` · `ig` · `xh` · `nso` · `tn` · `so` · `om` · `ar` · `en` · `pt` · `fr` · `es` · `zh` · `hi`

---

## Estrutura do Repositório

```
CircleAI/
├── src/            Implementação de referência em C# (CircleAI.*)
├── tests/          Suite de testes em C#
├── fixtures/       Vetores de teste multilinguagem (JSON)
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

| Workflow | Gatilho |
|----------|---------|
| [Fixture Validation](.github/workflows/fixture-validation.yml) | push/PR para master — executa todas as 10 suites de teste |
| [Publish](.github/workflows/publish.yml) | git tag `v*.*.*` — publica no NuGet, crates.io, PyPI, npm, GitHub Packages |

---

## Licença

MIT
