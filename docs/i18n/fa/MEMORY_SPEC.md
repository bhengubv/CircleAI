<div dir="rtl">

# Circle AI — مشخصات حافظه

این سند **ریاضیات دقیق** جهش‌های `AffectState` و تولید system-prompt از `PersonaState` را تعریف می‌کند. هر پورت زبانی باید **نتایج بیت-یکسانی** (در محدوده epsilon مربوط به float32) برای این عملیات تولید کند.

تأیید شده توسط `fixtures/affect_state.json` (۱۲ بردار آزمون، CI چندزبانه).

---

## ۱. AffectState — فیلدها و پیش‌فرض‌ها

| فیلد | نوع | پیش‌فرض | معناشناسی |
|-------|------|---------|-----------|
| `Curiosity` | float32 | 0.5 | ۰ = خسته، ۱ = مجذوب. محرک پیگیری فعالانه. |
| `Engagement` | float32 | 0.5 | ۰ = بی‌توجه، ۱ = کاملاً درگیر. |
| `Uncertainty` | float32 | 0.2 | ۰ = مطمئن، ۱ = سردرگم. بالا ← سؤال روشن‌سازی بپرسد. |
| `Rapport` | float32 | 0.0 | ۰ = غریبه، ۱ = رابطه عمیق. به آرامی در طول جلسات رشد می‌کند. |
| `Energy` | float32 | 0.5 | ۰ = آرام، ۱ = پرانرژی. بازتاب سرعت تعامل. |

تمام فیلدها پس از هر عملیات به **[0.0, 1.0] محدود** می‌شوند.

---

## ۲. عملیات سیگنال و افت

### ۲.۱ `ApplyPositiveSignal()`

پس از تعامل مثبت (پسندیدن کاربر، ادامه تعامل، و غیره) اعمال می‌شود.

```
Engagement  ← clamp(Engagement  + 0.02, 0, 1)
Rapport     ← clamp(Rapport     + 0.01, 0, 1)
Uncertainty ← clamp(Uncertainty − 0.02, 0, 1)
```

`Curiosity` و `Energy` **تغییر نمی‌کنند**.

### ۲.۲ `ApplyNegativeSignal()`

پس از تعامل منفی (نپسندیدن کاربر، پایان ناگهانی جلسه، و غیره) اعمال می‌شود.

```
Engagement  ← clamp(Engagement  − 0.03, 0, 1)
Uncertainty ← clamp(Uncertainty + 0.03, 0, 1)
```

`Rapport`، `Curiosity`، و `Energy` **تغییر نمی‌کنند**.

### ۲.۳ `ApplyIdleDecay(idle: duration)`

زمانی که کاربر غیرفعال بوده اعمال می‌شود. Engagement و Energy را به سمت نقطه میانی خنثی (0.5) می‌کشد. تمام ابعاد دیگر **تغییر نمی‌کنند**.

```
hours ← idle.TotalHours   // as float32 (or float64 → cast to float32)
decay ← min(0.3, hours × 0.02)

Engagement ← Lerp(Engagement, 0.5, decay)
Energy     ← Lerp(Energy,     0.5, decay)
```

#### تعریف Lerp

```
Lerp(a, b, t) = a + (b − a) × clamp(t, 0, 1)
```

`clamp(t, 0, 1)` مقدار `t` را قبل از ضرب به [0.0, 1.0] محدود می‌کند. از آنجایی که `decay` توسط `min(0.3, ...)` از قبل کران‌بندی شده است، `clamp` داخل `Lerp` تنها یک محافظ ایمنی است.

#### کران افت

`min(0.3, ...)` یعنی صرف نظر از مدت غیرفعالی کاربر، Engagement و Energy می‌توانند **حداکثر ۳۰٪ از مسیر به سمت 0.5** در یک فراخوانی حرکت کنند. این مانع از فروپاشی کامل وضعیت در یک توقف ۴۸ ساعته می‌شود.

---

## ۳. `ToSystemPromptHint()` — AffectState

یک بلوک hint فشرده (یا رشته خالی) برای تزریق به system prompt B! برمی‌گرداند.
تنها خطوطی را منتشر می‌کند که به طور معناداری از نوار خنثی منحرف شده‌اند.

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

## ۴. `ToSystemPromptHint()` — PersonaState

یک بلوک دستورالعمل persona فشرده (یا رشته خالی) بر اساس انحراف از سبک پیش‌فرض برمی‌گرداند.

```
hints = []

if Verbosity ≠ "balanced"          → append "Keep responses {Verbosity}."
if Formality == "casual"           → append "Use a casual, friendly tone."
if Formality == "formal"           → append "Maintain a formal, professional tone."
if PreferredLocale is not empty    → append "Respond in the language appropriate for locale {PreferredLocale}."

if hints.isEmpty → return ""
return "[User preferences]\n" + hints.join("\n") + "\n"
```

برای ۶ بردار آزمون ورودی/خروجی دقیق به `fixtures/persona_state.json` مراجعه کنید.

---

## ۵. نکات دقت چندزبانه

۱. از **float IEEE 754 با دقت مفرد** (۳۲ بیتی) برای تمام پنج فیلد AffectState استفاده کنید.
   زبان‌هایی که پیش‌فرض ۶۴ بیتی دارند (Python `float`، TypeScript `number`، Go `float64`،
   Kotlin `Double`) باید **نتیجه را قبل از ذخیره‌سازی به float32 تبدیل کنند**، یا در طول محاسبات در float32 باقی بمانند.

۲. بردارهای آزمون در `fixtures/affect_state.json` به صورت رشته‌های اعشاری داده شده‌اند. با epsilon **1×10⁻⁶** مقایسه کنید (یعنی `abs(result − expected) < 0.000001`).

۳. **از** گرد کردن بانکر، SIMD شتاب‌یافته سخت‌افزاری، یا بهینه‌سازی‌های FMA (ضرب-جمع ترکیبی)
   که mantissa را تغییر می‌دهند **استفاده نکنید**. به صورت متوالی طبق فرمول‌های بالا محاسبه کنید.

۴. فیلد timestamp مربوط به `LastUpdatedUtc` / `LastUpdatedAt` از بردارهای آزمون **حذف شده است** چون در زمان فراخوانی روی "now" تنظیم می‌شود و نمی‌توان آن را از پیش محاسبه کرد.

---

## ۶. تأیید

`fixtures/affect_state.json` را در مقابل پیاده‌سازی خود اجرا کنید. هر ورودی دارد:

- `id` — نام آزمون
- `description` — چه چیزی را آزمون می‌کند
- `input` — `AffectState` ورودی
- `operation` — `"positive_signal"`، `"negative_signal"`، یا `"idle_decay"`
- `operationParam` — برای decay: `{ "hours": N }`؛ برای عملیات سیگنال: `{}`
- `expected` — `AffectState` حاصل (بدون فیلدهای timestamp)

</div>