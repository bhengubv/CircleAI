# CircleAI.Embeddings

Text embeddings via Alibaba MNN. Implements `ITextEmbedder` over a small
on-device Qwen embedding model. Used by `CircleAI.Memory.RagContextBuilder`
for semantic recall and by `CircleAI.Inference.Server`'s `/v1/embeddings`
endpoint.

```bash
dotnet add package CircleAI.Embeddings
```

```csharp
using CircleAI.Embeddings;

ITextEmbedder embedder = new TextEmbedder("./qwen-embed.gguf");
float[] vec = await embedder.GenerateAsync("hello world");
```

See [docs/ARCHITECTURE.md](https://github.com/bhengubv/CircleAI/blob/master/docs/ARCHITECTURE.md).
