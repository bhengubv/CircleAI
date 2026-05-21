# Настройка BhenguAI

Эта библиотека требует нативных бинарных файлов llama.cpp во время выполнения. Они не включены в поставку (из-за размера и лицензионных ограничений).

## Получение нативных бинарных файлов

Проще всего: загрузить готовые бинарные файлы llama.cpp с https://github.com/ggerganov/llama.cpp/releases

Или собрать из исходников: `git clone https://github.com/ggerganov/llama.cpp && cd llama.cpp && cmake -B build && cmake --build build --config Release`.

## Расположение файлов по платформам

| Платформа | Имя файла | Путь |
|---|---|---|
| Windows x64 | `llama.dll` | рядом с вашим `.exe` |
| Linux x64 | `libllama.so` | рядом с вашим бинарным файлом или в `LD_LIBRARY_PATH` |
| macOS arm64 | `libllama.dylib` | рядом с вашим бинарным файлом |
| Android arm64 | `libllama.so` | внутри APK по пути `lib/arm64-v8a/` |
| iOS arm64 | `libllama.dylib` | встроен в пакет приложения |

## Получение модели

Загрузчик скачивает Qwen 3 14B Q4_K_M с ModelScope (основной источник) или HuggingFace (резервный). Объём около 8 ГБ.

```csharp
var loader = new LocalModelLoader();
var modelPath = await loader.DownloadModelAsync("Qwen3-14B-Q4");
```

## Проверка
Запустите `samples/ConsoleTest`. Ожидаемый результат: завершение чата от Qwen.
