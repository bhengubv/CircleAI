# CircleAI.Simulation

Offline GraphRAG / MiroFish-style simulation layer. Extracts a knowledge
graph from episodic memory, runs deterministic diffusion to forecast
network-health impact of a scenario (e.g. threat propagation across the
peer mesh).

```bash
dotnet add package CircleAI.Simulation
```

```csharp
using CircleAI.Simulation;

var extractor = new EpisodicGraphExtractor();        // CIRCLEAI_SIM_001
var simulator = new NetworkHealthSimulator(extractor, new MiroFishAdapter());
var scenario  = ThreatPropagationScenario.From(anomalySignal);
var result    = await simulator.RunAsync(entries, scenario, ct);
```

Gated as `CIRCLEAI_SIM_001` — diffusion math is deterministic and
unit-tested but the heuristic constants are not yet calibrated against
live mesh propagation curves. See
[docs/experimental.md](https://github.com/bhengubv/CircleAI/blob/master/docs/experimental.md).
