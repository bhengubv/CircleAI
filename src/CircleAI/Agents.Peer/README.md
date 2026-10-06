# CircleAI.Agents.Peer

Capability-routed peer-to-peer agent bus. Agents announce capabilities;
the `InMemoryAgentPeerProtocol` routes incoming work to the
highest-capability matching agent.

```bash
dotnet add package CircleAI.Agents.Peer
```

```csharp
using CircleAI.Agents.Peer;

IAgentPeerProtocol bus = new InMemoryAgentPeerProtocol(); // CIRCLEAI_PEER_001
await bus.RegisterAsync(agentA, ct);
await bus.RegisterAsync(agentB, ct);
var result = await bus.DispatchAsync(new AgentRequest("summarise", payload), ct);
```

The in-memory protocol is gated as `CIRCLEAI_PEER_001` — in-process
routing only, no cycle detection. Production deployments route through
the CircleAether mesh transport. See
[docs/experimental.md](https://github.com/bhengubv/CircleAI/blob/master/docs/experimental.md).
