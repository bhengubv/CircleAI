# CircleAI.Personality

Persistent persona store — communication-style hints, tone preferences,
and personality drift over time. Backed by a flat-file
`JsonPersonaProvider` by default; pluggable for cloud-sync backends.

```bash
dotnet add package CircleAI.Personality
```

```csharp
using CircleAI.Personality;

IPersonaProvider provider = new JsonPersonaProvider(rootDirectory: "./personas");
var persona = await provider.GetAsync(identityId, ct);
await provider.SaveAsync(persona with { ToneHint = "warmer" }, ct);
```

The provider participates in `CircleAIComponentBase` — every read/write
emits an audit entry + operation counter. See
[docs/ARCHITECTURE.md](https://github.com/bhengubv/CircleAI/blob/master/docs/ARCHITECTURE.md).
