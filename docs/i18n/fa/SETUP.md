<div dir="rtl">

# راه‌اندازی BhenguAI

این کتابخانه در زمان اجرا به باینری‌های بومی llama.cpp نیاز دارد. این فایل‌ها به دلیل حجم و مجوز، همراه پکیج ارائه نمی‌شوند.

## دریافت باینری‌های بومی

ساده‌ترین روش: دانلود باینری‌های از پیش‌ساخته llama.cpp از https://github.com/ggerganov/llama.cpp/releases

یا ساخت از سورس: `git clone https://github.com/ggerganov/llama.cpp && cd llama.cpp && cmake -B build && cmake --build build --config Release`.

## محل قرار دادن فایل‌ها بر اساس پلتفرم

| پلتفرم | نام فایل | مسیر |
|---|---|---|
| Windows x64 | `llama.dll` | کنار فایل `.exe` شما |
| Linux x64 | `libllama.so` | کنار باینری یا در `LD_LIBRARY_PATH` |
| macOS arm64 | `libllama.dylib` | کنار باینری شما |
| Android arm64 | `libllama.so` | داخل APK در مسیر `lib/arm64-v8a/` |
| iOS arm64 | `libllama.dylib` | جاسازی‌شده در بسته برنامه |

## دریافت مدل

دانلودر مدل Qwen 3 14B Q4_K_M را از ModelScope (اصلی) یا HuggingFace (پشتیبان) دریافت می‌کند. حجم تقریباً ۸ گیگابایت است.

```csharp
var loader = new LocalModelLoader();
var modelPath = await loader.DownloadModelAsync("Qwen3-14B-Q4");
```

## تأیید صحت
برنامه `samples/ConsoleTest` را اجرا کنید. خروجی مورد انتظار: یک پاسخ chat completion از Qwen.

</div>
