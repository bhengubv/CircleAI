# CircleAI.Companion

The "HER + JARVIS" persona contract — an `ICompanionSession` ties
identity, memory, affect, language, persona, and (optionally) tool-use
into one coherent conversation surface across every device for a single
person.

```bash
dotnet add package CircleAI.Companion
```

```csharp
using CircleAI.Companion;

await using var session = await factory.CreateAsync(identityId, InterfaceKind.Mobile);
var reply = await session.SendAsync("How am I doing today?");
// Or agentic: session.AgentAsync("...") — detects tool calls, executes them, re-prompts.
// Or streaming: await foreach (var chunk in session.StreamAsync(...)) { ... }
```

The companion's working state is exposed via `CompanionContext` — a
snapshot of identity, persona hints, affect summary, recent memories,
and active goals. Proactive messages flow through the
`ProactiveMessageReady` event.

See [docs/ARCHITECTURE.md](https://github.com/bhengubv/CircleAI/blob/master/docs/ARCHITECTURE.md).
