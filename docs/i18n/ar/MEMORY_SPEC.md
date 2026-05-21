<div dir="rtl">

# Circle AI — مواصفات الذاكرة

يحدد هذا المستند **الرياضيات الدقيقة** لطفرات `AffectState` وتوليد موجّه النظام الخاص بـ `PersonaState`. يجب على كل منفذ لغوي أن ينتج **نتائج متطابقة بالبت** (ضمن epsilon لنوع float32) لهذه العمليات.

مُتحقَّق منه بواسطة `fixtures/affect_state.json` (12 متجه اختبار، CI متعدد اللغات).

---

## 1. AffectState — الحقول والقيم الافتراضية

| الحقل | النوع | الافتراضي | الدلالات |
|-------|------|---------|-----------|
| `Curiosity` | float32 | 0.5 | 0 = ممل، 1 = مفتون. يُحرِّك المتابعة الاستباقية. |
| `Engagement` | float32 | 0.5 | 0 = غير مُتفاعل، 1 = مُتفاعل بالكامل. |
| `Uncertainty` | float32 | 0.2 | 0 = واثق، 1 = مرتبك. عالٍ ← اطرح أسئلة توضيحية. |
| `Rapport` | float32 | 0.0 | 0 = غريب، 1 = ألفة عميقة. ينمو ببطء عبر الجلسات. |
| `Energy` | float32 | 0.5 | 0 = هادئ، 1 = نشيط. يعكس وتيرة التفاعل. |

جميع الحقول **مُقيَّدة بـ [0.0, 1.0]** بعد كل عملية.

---

## 2. عمليات الإشارة والاضمحلال

### 2.1 `ApplyPositiveSignal()`

تُطبَّق بعد تفاعل إيجابي (إعجاب المستخدم، استمرار التفاعل، إلخ).

```
Engagement  ← clamp(Engagement  + 0.02, 0, 1)
Rapport     ← clamp(Rapport     + 0.01, 0, 1)
Uncertainty ← clamp(Uncertainty − 0.02, 0, 1)
```

`Curiosity` و`Energy` **لا تُعدَّلان**.

### 2.2 `ApplyNegativeSignal()`

تُطبَّق بعد تفاعل سلبي (عدم إعجاب المستخدم، إنهاء الجلسة فجأة، إلخ).

```
Engagement  ← clamp(Engagement  − 0.03, 0, 1)
Uncertainty ← clamp(Uncertainty + 0.03, 0, 1)
```

`Rapport` و`Curiosity` و`Energy` **لا تُعدَّلان**.

### 2.3 `ApplyIdleDecay(idle: duration)`

تُطبَّق عندما يكون المستخدم غير نشط. تُعيد `Engagement` و`Energy` نحو النقطة المحايدة (0.5). جميع الأبعاد الأخرى **لا تُعدَّل**.

```
hours ← idle.TotalHours   // as float32 (or float64 → cast to float32)
decay ← min(0.3, hours × 0.02)

Engagement ← Lerp(Engagement, 0.5, decay)
Energy     ← Lerp(Energy,     0.5, decay)
```

#### تعريف Lerp

```
Lerp(a, b, t) = a + (b − a) × clamp(t, 0, 1)
```

يُحدِّد `clamp(t, 0, 1)` قيمة `t` بـ [0.0, 1.0] قبل الضرب. بما أن `decay` مُقيَّدة مسبقاً بـ `min(0.3, ...)`, فإن `clamp` داخل `Lerp` هي مجرد حارس أمان.

#### حد الاضمحلال

`min(0.3, ...)` يعني أنه بغض النظر عن مدة خمول المستخدم، يمكن لـ `Engagement` و`Energy` أن تتحركا **بحد أقصى 30٪ نحو 0.5** في استدعاء واحد. هذا يمنع فجوة 48 ساعة من انهيار الحالة كلياً.

---

## 3. `ToSystemPromptHint()` — AffectState

يُعيد كتلة تلميح مضغوطة (أو سلسلة فارغة) للحقن في موجّه نظام B!.
يُصدر فقط الأسطر التي تنحرف بشكل ذي معنى عن النطاق المحايد.

```
hints = []

if Curiosity   > 0.7  → append "You are deeply curious about this topic — ask a follow-up question."
if Engagement  > 0.7  → append "You are fully engaged — be enthusiastic and thorough."
if Engagement  < 0.3  → append "Keep your response brief and to the point."
if Uncertainty > 0.6  → append "You are uncertain — ask a clarifying question before answering."
if Rapport     > 0.7  → append "You know this user well — use a warm, familiar tone."
if Energy      < 0.3  → append "Keep your response calm and measured."
if Energy      > 0.8  → append "You are energetic — be upbeat and concise."

if hints.isEmpty → return ""
return "[Affect state]\n" + hints.join("\n") + "\n"
```

---

## 4. `ToSystemPromptHint()` — PersonaState

يُعيد كتلة تعليمات شخصية مضغوطة (أو سلسلة فارغة) استناداً إلى الانحرافات عن الأسلوب الافتراضي.

```
hints = []

if Verbosity ≠ "balanced"          → append "Keep responses {Verbosity}."
if Formality == "casual"           → append "Use a casual, friendly tone."
if Formality == "formal"           → append "Maintain a formal, professional tone."
if PreferredLocale is not empty    → append "Respond in the language appropriate for locale {PreferredLocale}."

if hints.isEmpty → return ""
return "[User preferences]\n" + hints.join("\n") + "\n"
```

راجع `fixtures/persona_state.json` لـ 6 متجهات اختبار دقيقة للمدخلات/المخرجات.

---

## 5. ملاحظات الدقة متعددة اللغات

1. استخدم **IEEE 754 single-precision float** (32-bit) لجميع حقول AffectState الخمسة.
   اللغات التي تستخدم 64-bit افتراضياً (Python `float`، TypeScript `number`، Go `float64`،
   Kotlin `Double`) يجب أن **تحوِّل النتيجة إلى float32** قبل تخزينها، أو تتراكم
   في float32 طوال العملية.

2. متجهات الاختبار في `fixtures/affect_state.json` تُعطى كسلاسل عشرية. قارن
   بـ epsilon قدره **1×10⁻⁶** (أي `abs(result − expected) < 0.000001`).

3. **لا** تطبق تقريب المصرفي، أو SIMD المُسرَّع بالعتاد، أو تحسينات FMA (ضرب-جمع مدمج)
   التي تغير المانتيسا. احسب بالتسلسل كما هو مكتوب أعلاه.

4. حقل الطابع الزمني `LastUpdatedUtc` / `LastUpdatedAt` **مستثنى** من متجهات الاختبار
   لأنه يُعيَّن بقيمة "الآن" وقت الاستدعاء ولا يمكن حسابه مسبقاً.

---

## 6. التحقق

شغِّل `fixtures/affect_state.json` مقابل تنفيذك. كل مدخل يحتوي على:

- `id` — اسم الاختبار
- `description` — ما يختبره الاختبار
- `input` — `AffectState` عند الدخول
- `operation` — `"positive_signal"` أو `"negative_signal"` أو `"idle_decay"`
- `operationParam` — للاضمحلال: `{ "hours": N }`؛ لعمليات الإشارة: `{}`
- `expected` — `AffectState` الناتجة (مستثنياً حقول الطابع الزمني)

</div>
