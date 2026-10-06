# CircleAI.Knowledge

File-system knowledge store for the Companion's long-term facts +
documents. Pairs with `CircleAI.Memory.RagContextBuilder` to inject
retrieved facts into prompts.

```bash
dotnet add package CircleAI.Knowledge
```

```csharp
using CircleAI.Knowledge;

IKnowledgeStore store = new FileSystemKnowledgeStore("./knowledge");
await store.UpsertAsync(new KnowledgeEntry("kb-1", "title", "body…", tags: ["work"]), ct);
var hits = await store.SearchAsync("query", topK: 5, ct);
```

See [docs/ARCHITECTURE.md](https://github.com/bhengubv/CircleAI/blob/master/docs/ARCHITECTURE.md).
