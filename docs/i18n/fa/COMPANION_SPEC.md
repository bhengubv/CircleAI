<div dir="rtl">

# Circle AI — مشخصات جلسه همراه

این سند **قرارداد چرخه حیات ICompanionSession** را تعریف می‌کند که تمام پیاده‌سازی‌های زبانی باید آن را پیاده‌سازی کنند. `ICompanionSession` سطح اصلی API است که برنامه‌های میزبان (MAUI، Android، iOS، Web، HarmonyOS) با آن تعامل می‌کنند.

---

## ۱. مفاهیم

### ۱.۱ جلسه

یک `ICompanionSession` نمایانگر یک **مکالمه پیوسته منفرد** بین یک کاربر و B! است. از زمان ایجاد (اولین پیام) تا زمان از بین رفتن (کاربر برنامه را می‌بندد یا جلسه به صراحت پایان می‌یابد) ادامه دارد.

جلسات **خودشان ذخیره نمی‌شوند** — تنها تاریخچه `CompanionTurn` و `AffectState`/`PersonaState` زیرین ذخیره می‌شوند. جلسه جدیدی که روز بعد ایجاد می‌شود، وضعیت affect و persona یکسانی را از ذخیره‌سازها دریافت می‌کند.

### ۱.۲ زمینه

`CompanionContext` همه چیزهایی که B! برای زمینه‌مند ماندن نیاز دارد را حمل می‌کند:

| فیلد | هدف |
|-------|---------|
| `UserId` | این جلسه متعلق به کدام کاربر است |
| `AppContext` | برنامه فراخواننده (مثلاً `"tgn.bidbaas"`، `"tgn.tagme"`) |
| `Interface` | نحوه نمایش پاسخ (صدا، ساعت، متن…) |
| `Locale` | زبان اضافی برای پاسخ‌ها |
| `Affect` | وضعیت احساسی فعلی B! برای این کاربر |
| `Persona` | سبک یادگرفته‌شده B! برای این کاربر |
| `ActiveGoals` | اهدافی که B! باید به صورت فعالانه در آن‌ها کمک کند |

### ۱.۳ شکل‌دهی پاسخ مبتنی بر رابط

enum `InterfaceKind` طول خروجی و سبک را تعیین می‌کند:

| مقدار | محدودیت ضمنی |
|-------|--------------------|
| `Text` | پیش‌فرض — بدون محدودیت خاص |
| `Voice` | جملات کوتاه، بدون markdown، بدون فهرست |
| `Watch` | حداکثر ~۴۰ کلمه؛ ترجیحاً یک جمله |
| `Car` | بسیار کوتاه؛ بدون فهرست؛ ایمن برای رانندگی |
| `Tv` | مکالمه‌ای؛ مختصر؛ بدون بلوک کد |
| `Ar` | پوشش‌های بسیار کوتاه (≤ ۱۵ کلمه) |
| `Iot` | یک عبارت عملیاتی منفرد |

پیاده‌سازی‌ها تشویق می‌شوند دستورالعمل‌های مناسب رابط را به system prompt تزریق کنند.

---

## ۲. چرخه حیات جلسه

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

## ۳. قرارداد رابط

### ۳.۱ `SendAsync(userMessage)`

ارسال-دریافت مسدودکننده (قابل انتظار). نوبت را به `History` اضافه می‌کند.

**پیش‌شرط‌ها:**
- `userMessage` باید غیر null و غیر خالی باشد.

**پس‌شرط‌ها:**
- `History.Count` یک واحد افزایش می‌یابد.
- `AffectState` درون جلسه به‌روز می‌شود (زمان‌بندی دقیق به پیاده‌سازی بستگی دارد، اما باید قبل از بازگشت فراخوانی بعدی `GetContext()` اتفاق بیفتد).
- `CompanionTurn.UsedTools` برگشتی `true` است اگر فراخوانی `IToolBridge` در طول تولید رخ داده باشد.

### ۳.۲ `StreamAsync(userMessage)`

استریمینگ توکن به توکن. **History** پس از اینکه پاسخ کامل مونتاژ شد (یعنی پس از اتمام استریم) به‌روز می‌شود، نه در حین آن.

استریم async برگشتی **توکن‌های جزئی** منتشر می‌کند — فراخوانندگان باید آن‌ها را به هم متصل کنند.

### ۳.۳ `AgentAsync(task, tools?)`

یک حلقه agentic چندمرحله‌ای اجرا می‌کند: مدل تا زمانی که پاسخ نهایی تولید کند ابزارها را فراخوانی می‌کند و استدلال می‌کند. پاسخ متنی نهایی را برمی‌گرداند.

اگر `tools` null یا خالی باشد، متد به یک فراخوانی `GenerateAsync` منفرد (بدون حلقه ابزار) باز می‌گردد.

### ۳.۴ `GetContext()`

`CompanionContext` **فعلی** را برمی‌گرداند، شامل آخرین `AffectState` و `PersonaState`. می‌توان در هر زمانی فراخوانی کرد؛ تحت تأثیر فراخوانی‌های async در حین اجرا نیست.

### ۳.۵ `SignalFeedbackAsync(polarity, correction?)`

بازخورد کاربر را برای **آخرین نوبت** در `History` ثبت می‌کند.

- `FeedbackPolarity.Positive` ← `AffectState.ApplyPositiveSignal()` را فراخوانی و ذخیره می‌کند
- `FeedbackPolarity.Negative` ← `AffectState.ApplyNegativeSignal()` را فراخوانی و ذخیره می‌کند
- `FeedbackPolarity.Correction` ← تصحیح را ثبت می‌کند؛ affect جهش نمی‌یابد

اگر `History` خالی باشد (هنوز هیچ نوبتی وجود ندارد)، این متد عملیاتی انجام نمی‌دهد.

### ۳.۶ رویداد `ProactiveMessageReady`

زمانی فعال می‌شود که B! پیامی برای تحویل فعالانه دارد (یادآوری، انگیزش هدف، و غیره). رویداد **به طور خودکار** به `History` اضافه نمی‌شود — میزبان باید `SendAsync` را فراخوانی کند یا به نحو دیگری پیام را نمایش دهد.

---

## ۴. فیلدهای `CompanionTurn`

```
CompanionTurn {
  UserText:      string         // the user's input (verbatim)
  AssistantText: string         // B!'s complete response
  CreatedAt:     datetime (UTC) // timestamp of the assistant's response
  UsedTools:     bool           // true if any tool invocations occurred
}
```

---

## ۵. مدیریت خطا

| شرایط | رفتار مورد انتظار |
|-----------|--------------------|
| `IChatGenerator` در دسترس نیست | پرتاب `GeneratorUnavailableException` (یا معادل زبانی) |
| فراخوانی ابزار شکست می‌خورد | `ToolResult.Success = false`؛ خطا را در زمینه قرار دهید؛ حلقه را ادامه دهید |
| Embedding در دسترس نیست | `EpisodicMemoryEntry.Embedding = null` ذخیره کنید؛ شکست نخورید |
| نوشتن `AffectStore` شکست می‌خورد | ثبت و ادامه دهید؛ به فراخواننده نمایش ندهید |

---

## ۶. پیاده‌سازی حداقل قابل اجرا (آزمایش)

برای آزمون‌های واحد و پورت‌های زبانی که هنوز backend LLM واقعی ندارند:

```
MockChatGenerator:
  GenerateAsync(messages) → "Mock response from B!"
  StreamAsync(messages) → async stream of ["Mock", " ", "response", " ", "from", " ", "B!"]
```

آزمون‌های companion session در `tests/` از `MockChatGenerator` استفاده می‌کنند تا چرخه حیات جلسه، مدیریت تاریخچه، مسیریابی بازخورد، و جهش‌های affect را بدون نیاز به مدل واقعی تأیید کنند.

---

## ۷. ترتیب مونتاژ system prompt

پیاده‌سازی مرجع C# system prompt را به این ترتیب مونتاژ می‌کند:

۱. System prompt پایه (persona کدگذاری‌شده: "You are B!, the on-device assistant…")
۲. `AffectState.ToSystemPromptHint()` — در صورت غیر خالی بودن اضافه می‌شود
۳. `PersonaState.ToSystemPromptHint()` — در صورت غیر خالی بودن اضافه می‌شود
۴. محدودیت‌های `InterfaceKind` — در صورت مناسب بودن اضافه می‌شوند
۵. دستورالعمل‌های `AppContext` — اختیاری، توسط برنامه میزبان تزریق می‌شوند

پیاده‌سازی‌ها می‌توانند این ترتیب را آزادانه تغییر دهند مشروط بر اینکه آزمون‌های fixture قبول شوند.

</div>