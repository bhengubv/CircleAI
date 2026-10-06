# CircleAI.Skills

Skill catalogue + registry — declarative descriptions of agent
capabilities the Companion can advertise to peers and discover from them
via `CircleAI.Agents.Peer`.

```bash
dotnet add package CircleAI.Skills
```

```csharp
using CircleAI.Skills;

ISkillRegistry registry = new InMemorySkillRegistry();
registry.Register(new SkillDescriptor(
    id: "summarise-text",
    description: "Compress prose to a short summary",
    inputSchema: …, outputSchema: …));
```

See [docs/ARCHITECTURE.md](https://github.com/bhengubv/CircleAI/blob/master/docs/ARCHITECTURE.md).
