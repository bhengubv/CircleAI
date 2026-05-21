# BhenguAI-Einrichtung

Diese Bibliothek benötigt native llama.cpp-Binärdateien zur Laufzeit. Sie sind nicht im Paket enthalten (Größe + Lizenzierung).

## Native Binärdateien beschaffen

Am einfachsten: Vorgefertigte llama.cpp-Binärdateien von https://github.com/ggerganov/llama.cpp/releases herunterladen.

Oder aus dem Quellcode erstellen: `git clone https://github.com/ggerganov/llama.cpp && cd llama.cpp && cmake -B build && cmake --build build --config Release`.

## Ablagepfade je Plattform

| Plattform | Dateiname | Pfad |
|---|---|---|
| Windows x64 | `llama.dll` | Neben Ihrer `.exe`-Datei |
| Linux x64 | `libllama.so` | Neben Ihrer ausführbaren Datei oder in `LD_LIBRARY_PATH` |
| macOS arm64 | `libllama.dylib` | Neben Ihrer ausführbaren Datei |
| Android arm64 | `libllama.so` | Im APK unter `lib/arm64-v8a/` |
| iOS arm64 | `libllama.dylib` | Eingebettet im App-Bundle |

## Modell beschaffen

Das Download-Programm lädt Qwen 3 14B Q4_K_M von ModelScope (primär) oder HuggingFace (Fallback). Ungefähr 8 GB.

```csharp
var loader = new LocalModelLoader();
var modelPath = await loader.DownloadModelAsync("Qwen3-14B-Q4");
```

## Überprüfung
`samples/ConsoleTest` ausführen. Erwartete Ausgabe: eine Chat-Vervollständigung von Qwen.
