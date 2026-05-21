# Configuração do BhenguAI

Esta biblioteca precisa de binários nativos do llama.cpp em tempo de execução. Eles não vêm empacotados (por questões de tamanho e licenciamento).

## Obter os binários nativos

A maneira mais fácil é baixar os binários pré-compilados do llama.cpp em https://github.com/ggerganov/llama.cpp/releases

Ou compile a partir do código-fonte: `git clone https://github.com/ggerganov/llama.cpp && cd llama.cpp && cmake -B build && cmake --build build --config Release`.

## Locais de instalação por plataforma

| Plataforma | Nome do arquivo | Caminho |
|---|---|---|
| Windows x64 | `llama.dll` | ao lado do seu `.exe` |
| Linux x64 | `libllama.so` | ao lado do seu binário ou em `LD_LIBRARY_PATH` |
| macOS arm64 | `libllama.dylib` | ao lado do seu binário |
| Android arm64 | `libllama.so` | dentro do APK em `lib/arm64-v8a/` |
| iOS arm64 | `libllama.dylib` | embutido no app bundle |

## Obter o modelo

O downloader baixa o Qwen 3 14B Q4_K_M do ModelScope (primário) ou HuggingFace (fallback). Aproximadamente 8 GB.

```csharp
var loader = new LocalModelLoader();
var modelPath = await loader.DownloadModelAsync("Qwen3-14B-Q4");
```

## Verificação

Execute `samples/ConsoleTest`. Saída esperada: uma conclusão de chat do Qwen.
