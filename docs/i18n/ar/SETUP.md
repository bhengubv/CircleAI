<div dir="rtl">

# إعداد BhenguAI

تحتاج هذه المكتبة إلى ثنائيات llama.cpp الأصلية عند وقت التشغيل. لا يتم تضمينها (بسبب الحجم والترخيص).

## الحصول على الثنائيات الأصلية

الطريقة الأسهل: تنزيل ثنائيات llama.cpp المُجمَّعة مسبقاً من https://github.com/ggerganov/llama.cpp/releases

أو البناء من المصدر: `git clone https://github.com/ggerganov/llama.cpp && cd llama.cpp && cmake -B build && cmake --build build --config Release`.

## مواقع الإسقاط بحسب المنصة

| المنصة | اسم الملف | المسار |
|---|---|---|
| Windows x64 | `llama.dll` | بجانب ملف `.exe` الخاص بك |
| Linux x64 | `libllama.so` | بجانب ملفك الثنائي أو داخل `LD_LIBRARY_PATH` |
| macOS arm64 | `libllama.dylib` | بجانب ملفك الثنائي |
| Android arm64 | `libllama.so` | داخل حزمة APK في المسار `lib/arm64-v8a/` |
| iOS arm64 | `libllama.dylib` | مُضمَّنة في حزمة التطبيق |

## الحصول على النموذج

يقوم المُنزِّل بسحب Qwen 3 14B Q4_K_M من ModelScope (المصدر الأساسي) أو HuggingFace (كبديل). الحجم تقريباً 8 جيجابايت.

```csharp
var loader = new LocalModelLoader();
var modelPath = await loader.DownloadModelAsync("Qwen3-14B-Q4");
```

## التحقق
شغّل `samples/ConsoleTest`. المخرجات المتوقعة: إكمال محادثة من Qwen.

</div>
