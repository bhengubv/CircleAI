<div dir="rtl">

# Circle AI — حزمة SDK لعشر لغات

النواة المحمولة لمكدس مرافق Circle AI. تعمل بشكل أصلي بجانب كل عقدة من عقد
[Aether Protocol](https://github.com/bhengubv/aether-protocol) — الأجهزة القابلة للارتداء،
والهواتف، وإنترنت الأشياء، وHarmonyOS — دون أي تكاليف FFI ودون جسور وقت التشغيل.

---

## النواة المحمولة (8 وحدات)

| الوحدة | الأنواع الرئيسية |
|--------|-----------|
| **models** | `ChatMessage`, `DownloadProgress` |
| **memory** | `AffectState`, `EpisodicMemoryEntry`, `PersonaState`, `Goal` |
| **identity** | `CircleIdentity`, `RegisteredDevice`, `IdentityTier` |
| **languages** | `LanguageTag`, `KnownLanguages` (20 وسم BCP-47), `WritingSystem` |
| **companion** | `CompanionContext`, `CompanionTurn`, `ICompanionSession` |
| **inference** | `GenerationOptions`, `IChatGenerator` |
| **tools** | `ToolDefinition`, `ToolInvocation`, `ToolResult`, `IToolBridge` |
| **sync** | `SyncDelta`, `SyncDeliveryMode`, `ISyncChannel` |

---

## النيورون — دماغ ذكاء اصطناعي صغير على جهازك

**النيورون** هو دماغ ذكاء اصطناعي صغير يعمل على جهازك الخاص. إنه يفكّر ويتذكّر
ويتحدّث — هناك مباشرةً على هاتفك أو حاسوبك المحمول، دون إرسال أي شيء إلى خادم.
يجيب مساعد يومي سريع عن معظم الأسئلة؛ أما بالنسبة إلى مهمة أصعب (قراءة صورة، أو
مستند طويل، أو التفكير المتأنّي خطوةً بخطوة) فإنه يُحمّل بهدوء متخصصاً، ثم يجيب،
ثم يضعه جانباً. وهو لا يحتفظ إلا بمتخصص واحد في كل مرة، لذا فإنه لا يحتاج أبداً
إلى ذاكرة أكبر مما لدى الجهاز، ويتذكّر المحادثة بحيث تُستأنف الدردشة من حيث
توقّفت.

**نيورون واحد أو كثير — دماغ مكوَّن من أدمغة.** يعمل النيورون الواحد جيداً
بمفرده. لكن النيورونات يمكنها أيضاً أن تتّحد، مثل الخلايا العصبية في الدماغ —
وهنا تكمن القوة الحقيقية. (هذا الجزء لم يُبنَ بعد؛ إنه ما نؤمن بأنه سيصبح
ممكناً، وهو السبب في تسمية العقدة نيوروناً.) في المجموعة يتقاسمون العمل، ويساعد
بعضهم بعضاً عندما يعجز أحدهم عن استيعاب متخصص، ويجيبون كأنداد دون وجود عقدة
مسؤولة، ويُبقون بياناتك الخاصة على جهازك — ولا ينتقل بينهم سوى السؤال. كل نيورون
هو بالفعل عقل كامل، لذا فإن مجموعة من النيورونات هي عقول كاملة كثيرة يساعد بعضها
بعضاً على إنجاز ما لا يستطيع أيٌّ منها إنجازه بمفرده.

يأتي النيورون في التطبيق المرجعي بلغة C# وفي جميع المنافذ الشقيقة السبعة
(Python, TypeScript, Go, Kotlin, Swift, Rust, C). أما HarmonyOS/ArkTS فلا يزال
قادماً.

---

## دليل البدء السريع بحسب اللغة

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

## AffectState — الحسابات المشتركة بين اللغات

تُنتج جميع تطبيقات اللغات العشر نتائج عشرية متطابقة (ε ≤ 1e-5).

| العملية | التأثير |
|-----------|--------|
| `applyPositiveSignal()` | الانخراط +0.02، التقارب +0.01، عدم اليقين −0.02 (محصورة في [0, 1]) |
| `applyNegativeSignal()` | الانخراط −0.03، عدم اليقين +0.03 (محصورة) |
| `applyIdleDecay(hours)` | الاضمحلال = min(0.3, hours × 0.02)؛ الانخراط والطاقة يتجهان نحو 0.5 بالاستيفاء الخطي |

متجهات الاختبار في [`fixtures/affect_state.json`](fixtures/affect_state.json) (12 متجهاً). يتم التحقق منها بواسطة CI عبر جميع اللغات العشر.

---

## سجل اللغات (20 وسم BCP-47)

`zu` · `st` · `af` · `sw` · `ha` · `am` · `yo` · `ig` · `xh` · `nso` · `tn` · `so` · `om` · `ar` · `en` · `pt` · `fr` · `es` · `zh` · `hi`

---

## هيكل المستودع

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

## التكامل المستمر (CI)

| سير العمل | المحفّز |
|----------|---------|
| [Fixture Validation](.github/workflows/fixture-validation.yml) | الدفع/طلب السحب إلى master — تشغيل جميع مجموعات الاختبارات العشر |
| [Publish](.github/workflows/publish.yml) | وسم git `v*.*.*` — النشر إلى NuGet وcrates.io وPyPI وnpm وGitHub Packages |

---

## الرخصة

MIT

</div>
