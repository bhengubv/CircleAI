# Configuración de BhenguAI

Esta biblioteca requiere binarios nativos de llama.cpp en tiempo de ejecución. No están incluidos (por tamaño y licencia).

## Obtener los binarios nativos

La forma más sencilla: descargar los binarios precompilados de llama.cpp desde https://github.com/ggerganov/llama.cpp/releases

O compilar desde el código fuente: `git clone https://github.com/ggerganov/llama.cpp && cd llama.cpp && cmake -B build && cmake --build build --config Release`.

## Ubicaciones de instalación por plataforma

| Plataforma | Nombre de archivo | Ruta |
|---|---|---|
| Windows x64 | `llama.dll` | junto a tu `.exe` |
| Linux x64 | `libllama.so` | junto a tu binario o en `LD_LIBRARY_PATH` |
| macOS arm64 | `libllama.dylib` | junto a tu binario |
| Android arm64 | `libllama.so` | dentro del APK en `lib/arm64-v8a/` |
| iOS arm64 | `libllama.dylib` | integrado en el bundle de la aplicación |

## Obtener el modelo

El descargador obtiene Qwen 3 14B Q4_K_M desde ModelScope (primario) o HuggingFace (alternativo). Aproximadamente 8 GB.

```csharp
var loader = new LocalModelLoader();
var modelPath = await loader.DownloadModelAsync("Qwen3-14B-Q4");
```

## Verificar
Ejecuta `samples/ConsoleTest`. Salida esperada: una respuesta de completado de chat de Qwen.
