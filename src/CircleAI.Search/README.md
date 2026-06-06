# CircleAI.Search

Vector-search math (cosine similarity, top-K retrieval) used by
`CircleAI.Memory.RagContextBuilder` and `CircleAI.Knowledge.FileSystemKnowledgeStore`.

```bash
dotnet add package CircleAI.Search
```

```csharp
using CircleAI.Search;

float similarity = VectorMath.CosineSimilarity(queryVec, candidateVec);
var topK = VectorMath.TopKByCosine(queryVec, candidates, k: 5);
```

Pure functions, no allocations on hot path. See
[docs/ARCHITECTURE.md](https://github.com/bhengubv/CircleAI/blob/master/docs/ARCHITECTURE.md).
