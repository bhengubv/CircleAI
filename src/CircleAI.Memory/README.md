# CircleAI.Memory

On-device episodic memory, affect state, persona store, and a
`RagContextBuilder` that retrieves the most relevant recent memories for
the current turn.

```bash
dotnet add package CircleAI.Memory
```

```csharp
using CircleAI.Memory;

var rag = new RagPipelineBuilder()
    .WithInMemoryStore()       // CIRCLEAI_MEM_CAP_001 — see docs/experimental.md
    .WithEmbedder(embedder)
    .Build();
```

For production deployments choose a persistent store
(`SqliteVecEpisodicStore`) — the in-memory default is gated as
`CIRCLEAI_MEM_CAP_001` to force an explicit choice.

See [docs/ARCHITECTURE.md](https://github.com/bhengubv/CircleAI/blob/master/docs/ARCHITECTURE.md).
