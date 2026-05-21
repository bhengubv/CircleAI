<div dir="rtl">

# Circle AI — مواصفات جلسة المرافق

يحدد هذا المستند **عقد دورة حياة ICompanionSession** الذي يجب على جميع المنافذ اللغوية تنفيذه. `ICompanionSession` هو سطح واجهة برمجة التطبيقات الرئيسي الذي تتفاعل معه تطبيقات المضيف (MAUI و Android و iOS و Web و HarmonyOS).

---

## 1. المفاهيم

### 1.1 الجلسة

يمثل `ICompanionSession` **محادثة مستمرة واحدة** بين مستخدم واحد وB!. تمتد من الإنشاء (الرسالة الأولى) حتى التخلص منها (يغلق المستخدم التطبيق أو تنتهي الجلسة صراحةً).

الجلسات **لا تُحفظ بذاتها** — يُخزَّن فقط سجل `CompanionTurn` والحالتان الأساسيتان `AffectState`/`PersonaState`. تلتقط الجلسة الجديدة المُنشأة في اليوم التالي نفس حالتَي التأثير والشخصية من المخازن.

### 1.2 السياق

يحمل `CompanionContext` كل ما يحتاجه B! للبقاء على الأرض:

| الحقل | الغرض |
|-------|---------|
| `UserId` | المستخدم الذي تنتمي إليه هذه الجلسة |
| `AppContext` | التطبيق المُستدعي (مثلاً `"tgn.bidbaas"`, `"tgn.tagme"`) |
| `Interface` | كيفية عرض الاستجابة (صوت، ساعة، نص…) |
| `Locale` | تجاوز اللغة في الاستجابات |
| `Affect` | الحالة العاطفية الحالية لـ B! مع هذا المستخدم |
| `Persona` | الأسلوب الذي تعلمه B! مع هذا المستخدم |
| `ActiveGoals` | الأهداف التي يجب على B! المساعدة فيها بشكل استباقي |

### 1.3 تشكيل الاستجابة المدفوعة بالواجهة

تُوجِّه قيمة `InterfaceKind` طول الإخراج وأسلوبه:

| القيمة | القيد الضمني |
|-------|--------------------|
| `Text` | الافتراضي — لا قيود خاصة |
| `Voice` | جمل قصيرة، بلا markdown، بلا قوائم |
| `Watch` | حد أقصى ~40 كلمة؛ يُفضَّل جملة واحدة |
| `Car` | قصير جداً؛ بلا قوائم؛ آمن لحالة القيادة |
| `Tv` | محادثاتي؛ موجز؛ بلا كتل أكواد |
| `Ar` | تراكبات فائقة القِصَر (≤ 15 كلمة) |
| `Iot` | عبارة إجراء واحدة |

يُشجَّع على حقن تعليمات مناسبة للواجهة في موجّه النظام.

---

## 2. دورة حياة الجلسة

```
┌──────────────────────────────────────────────┐
│                                              │
│  1. Create session with CompanionContext      │
│                                              │
│  2. User sends a message                     │
│     a. SendAsync(text)     → CompanionTurn   │
│     b. StreamAsync(text)   → token stream    │
│                                              │
│  3. Optionally: user sends feedback          │
│     SignalFeedbackAsync(Positive|Negative)   │
│                                              │
│  4. B! may raise ProactiveMessageReady event │
│     at any time (background thread is fine)  │
│                                              │
│  5. Dispose the session when done            │
│                                              │
└──────────────────────────────────────────────┘
```

---

## 3. عقد الواجهة

### 3.1 `SendAsync(userMessage)`

إرسال-استقبال حاجب (قابل للانتظار). يُلحق الدور بـ `History`.

**الشروط المسبقة:**
- يجب أن يكون `userMessage` غير فارغ.

**الشروط اللاحقة:**
- يزيد `History.Count` بمقدار 1.
- يُحدَّث `AffectState` داخل الجلسة (التوقيت الدقيق محدد بالتنفيذ، لكن يجب أن يحدث قبل إرجاع استدعاء `GetContext()` التالي).
- `CompanionTurn.UsedTools` المُعاد يكون `true` إذا حدثت أي استدعاءات لـ `IToolBridge` أثناء التوليد.

### 3.2 `StreamAsync(userMessage)`

بث رمز بعد رمز. يُحدَّث **History** بعد تجميع الاستجابة الكاملة (أي بعد اكتمال التدفق)، ليس أثناءه.

يُصدر التدفق غير المتزامن المُعاد **رموزاً جزئية** — يجب على المستدعين تسلسلها.

### 3.3 `AgentAsync(task, tools?)`

يُشغِّل حلقة وكيل متعددة الخطوات: يستدعي النموذج الأدوات ويستدل حتى يُنتج إجابة نهائية. يُعيد نص الاستجابة النهائي.

إذا كان `tools` فارغاً أو null، تعود الطريقة إلى استدعاء `GenerateAsync` واحد (بلا حلقة أدوات).

### 3.4 `GetContext()`

يُعيد `CompanionContext` **الحالي**، بما يشمل أحدث `AffectState` و`PersonaState`. يمكن استدعاؤه في أي وقت؛ لا تؤثر عليه الاستدعاءات غير المتزامنة الجارية.

### 3.5 `SignalFeedbackAsync(polarity, correction?)`

يسجل تعليقات المستخدم على **أحدث دور** في `History`.

- `FeedbackPolarity.Positive` ← يستدعي `AffectState.ApplyPositiveSignal()` ويحفظ
- `FeedbackPolarity.Negative` ← يستدعي `AffectState.ApplyNegativeSignal()` ويحفظ
- `FeedbackPolarity.Correction` ← يسجل التصحيح؛ لا تعديل على التأثير

إذا كان `History` فارغاً (لا أدوار بعد)، فهذه الطريقة لا عملية.

### 3.6 حدث `ProactiveMessageReady`

يُطلَق عندما يكون لدى B! رسالة لتسليمها بشكل استباقي (تذكير، حثّ هدف، إلخ). الحدث **لا** يُلحق بـ `History` تلقائياً — يجب على المضيف استدعاء `SendAsync` أو عرض الرسالة بأي طريقة.

---

## 4. حقول `CompanionTurn`

```
CompanionTurn {
  UserText:      string         // the user's input (verbatim)
  AssistantText: string         // B!'s complete response
  CreatedAt:     datetime (UTC) // timestamp of the assistant's response
  UsedTools:     bool           // true if any tool invocations occurred
}
```

---

## 5. معالجة الأخطاء

| الحالة | السلوك المتوقع |
|-----------|--------------------|
| `IChatGenerator` غير متاح | ارمِ `GeneratorUnavailableException` (أو ما يعادلها في اللغة) |
| فشل استدعاء الأداة | `ToolResult.Success = false`؛ أدرج الخطأ في السياق؛ أكمل الحلقة |
| التضمين غير متاح | خزِّن `EpisodicMemoryEntry.Embedding = null`؛ لا تفشل |
| فشل كتابة `AffectStore` | سجِّل وأكمل؛ لا تكشف للمستدعي |

---

## 6. الحد الأدنى من التنفيذ القابل للتطبيق (للاختبار)

لاختبارات الوحدة والمنافذ اللغوية التي لا تمتلك بعد واجهة خلفية حقيقية لنموذج اللغة:

```
MockChatGenerator:
  GenerateAsync(messages) → "Mock response from B!"
  StreamAsync(messages) → async stream of ["Mock", " ", "response", " ", "from", " ", "B!"]
```

تستخدم اختبارات جلسة المرافق في `tests/` مولِّد `MockChatGenerator` للتحقق من دورة حياة الجلسة وإدارة السجل وتوجيه التعليقات وطفرات التأثير دون الحاجة إلى نموذج حقيقي.

---

## 7. ترتيب تجميع موجّه النظام

يجمع التنفيذ المرجعي بلغة C# موجّه النظام بهذا الترتيب:

1. موجّه النظام الأساسي (شخصية مُضمَّنة: "You are B!, the on-device assistant…")
2. `AffectState.ToSystemPromptHint()` — يُلحَق إذا لم يكن فارغاً
3. `PersonaState.ToSystemPromptHint()` — يُلحَق إذا لم يكن فارغاً
4. قيود `InterfaceKind` — تُلحَق حسب الاقتضاء
5. تعليمات `AppContext` — اختيارية، يحقنها تطبيق المضيف

يمكن للتنفيذات ترتيب هذه العناصر بشكل مختلف طالما تجتاز اختبارات التثبيت.

</div>
