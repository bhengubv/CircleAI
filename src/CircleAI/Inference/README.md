# CircleAI.Inference

On-device chat-generation core — `IChatGenerator` over Alibaba MNN.
Ships `QwenTextGenerator`, the canonical `MnnInterop` P/Invoke surface,
the `ModelDownloadService` that auto-fetches Qwen / Kimi weights from
ModelScope, and the `ModelRegistryService` that turns a model id into a
download URL + checksum.

```bash
dotnet add package CircleAI.Inference
```

```csharp
using CircleAI.Inference;

NativeLibraryResolver.EnsureRegistered();           // wire P/Invoke search paths

var generator = new QwenTextGenerator("./qwen3-7b.gguf");
var reply = await generator.GenerateAsync(
    new[] { new ChatMessage("user", "Hello") }, ct: CancellationToken.None);
```

No Western inference runtimes in this package — `LlamaCppInterop` was
removed in 1.2.0. The native runtime (mnnbridge + MNN) is fetched on
demand by `CircleAI.Runtime.NativeRuntimeFetcher`; see the package README
for that wiring.

See [docs/ARCHITECTURE.md](https://github.com/bhengubv/CircleAI/blob/master/docs/ARCHITECTURE.md)
§ 2 (the ONE seam) and § 5 (native runtimes).
