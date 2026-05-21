# Configuration de BhenguAI

Cette bibliothèque nécessite des binaires natifs llama.cpp à l'exécution. Ils ne sont pas inclus dans le paquet (raisons de taille et de licence).

## Obtenir les binaires natifs

Le plus simple : télécharger les binaires llama.cpp précompilés depuis https://github.com/ggerganov/llama.cpp/releases

Ou compiler depuis les sources : `git clone https://github.com/ggerganov/llama.cpp && cd llama.cpp && cmake -B build && cmake --build build --config Release`.

## Emplacements de dépôt par plateforme

| Plateforme | Nom de fichier | Emplacement |
|---|---|---|
| Windows x64 | `llama.dll` | à côté de votre `.exe` |
| Linux x64 | `libllama.so` | à côté de votre binaire ou dans `LD_LIBRARY_PATH` |
| macOS arm64 | `libllama.dylib` | à côté de votre binaire |
| Android arm64 | `libllama.so` | dans l'APK sous `lib/arm64-v8a/` |
| iOS arm64 | `libllama.dylib` | intégré dans le bundle de l'application |

## Obtenir le modèle

Le téléchargeur récupère Qwen 3 14B Q4_K_M depuis ModelScope (source principale) ou HuggingFace (source de secours). Environ 8 Go.

```csharp
var loader = new LocalModelLoader();
var modelPath = await loader.DownloadModelAsync("Qwen3-14B-Q4");
```

## Vérification
Exécutez `samples/ConsoleTest`. Résultat attendu : une complétion de chat depuis Qwen.
