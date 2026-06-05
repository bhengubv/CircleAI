# CircleAI — Experimental gates

Surfaces that work but aren't yet "wire-proven in production at scale"
carry a `[System.Diagnostics.CodeAnalysis.Experimental("CIRCLEAI_*")]`
attribute. Calling them produces a compiler warning that consumers must
opt into via `<NoWarn>` in their csproj, with the diagnostic ID below
documenting WHY.

If a gated surface stops being experimental, we drop the attribute and
update this file.

---

## CIRCLEAI_BIO_001 — Wearable biosignals

Types affected:
- `CircleAI.Wearable.Biosignals.BiosignalAffectMapper`
- `CircleAI.Wearable.Biosignals.BiosignalAggregator`
- `CircleAI.Wearable.Biosignals.NullBiosignalSource`

Why gated: the affect-mapping rule sheet is internally consistent and
fixture-tested but the thresholds have not been clinically validated.
Output is suitable for affect-tinting a Companion's tone — NOT for
medical signal.

How to opt in: `<NoWarn>$(NoWarn);CIRCLEAI_BIO_001</NoWarn>`.

Exit criteria: clinical-grade calibration study + replacement of the
heuristic thresholds with study-derived values.

---

## CIRCLEAI_FED_001 — In-memory federation

Types affected:
- `CircleAI.Federation.InMemoryFederationAggregator`
- `CircleAI.Federation.FederationRound`

Why gated: aggregator is correct for in-process tests but is not
multi-replica safe (signals emitted on replica A do not reach stream
subscribers on replica B). Federation rounds count only what the local
process can see.

How to opt in: `<NoWarn>$(NoWarn);CIRCLEAI_FED_001</NoWarn>`.

Exit criteria: cross-replica delivery via the mesh transport
(`CircleAI.Networking.Aether`) + per-replica state persisted in
`opsupport_db`.

---

## CIRCLEAI_PEER_001 — Mesh agent peer protocol

Types affected:
- `CircleAI.Agents.Peer.InMemoryAgentPeerProtocol`
- `CircleAI.Agents.Peer.AgentBus`

Why gated: in-memory implementation routes capability-by-capability and
will deadlock on cyclic capability graphs. Production deployments need
a true mesh transport with cycle detection.

How to opt in: `<NoWarn>$(NoWarn);CIRCLEAI_PEER_001</NoWarn>`.

Exit criteria: cycle-detecting message router on top of the Aether
transport.

---

## CIRCLEAI_SIM_001 — Simulation / GraphRAG

Types affected:
- `CircleAI.Simulation.NetworkHealthSimulator`
- `CircleAI.Simulation.EpisodicGraphExtractor`
- `CircleAI.Simulation.MiroFishAdapter`
- `CircleAI.Simulation.ThreatPropagationScenario`

Why gated: diffusion math is deterministic and unit-tested in-process
but no end-to-end wire-proven run has been executed against a populated
peer graph in production. ThreatPropagationScenario's depth + spread
constants are heuristic and not yet calibrated against observed
propagation curves on a live mesh.

How to opt in: `<NoWarn>$(NoWarn);CIRCLEAI_SIM_001</NoWarn>`.

Exit criteria: simulation outputs validated against ≥ 1 real anomaly
playback corpus.

---

## CIRCLEAI_MEM_CAP_001 — In-memory episodic store

Types affected:
- `CircleAI.Memory.InMemoryEpisodicStore`

Why gated: the 1000-entry FIFO cap is a test default. Production
deployments MUST configure `maxEntries` explicitly based on observed
memory pressure and the desired retention horizon, or substitute a
persistent backend (`CircleAI.Memory.SqliteVecEpisodicStore`).

How to opt in: `<NoWarn>$(NoWarn);CIRCLEAI_MEM_CAP_001</NoWarn>`.

Internal SDK consumers (e.g. `RagPipelineBuilder.WithInMemoryStore`,
`AddCircleAI` hosting fallback) suppress the warning at the call site
with a `#pragma warning disable / restore` block + justifying comment;
external consumers should choose deliberately.

---

## Removed gates

- **CIRCLEAI_DEVCAPS_001** (removed 2026-06-05) — was on
  `LocalProcessInferenceBridge.GetDeviceCapabilitiesAsync`. The method now
  delegates to `CircleAI.Runtime.ICapabilityProbe`, returns real values,
  and is no longer experimental.

---

## Pattern

Every gate declaration looks like:

```csharp
[Experimental("CIRCLEAI_XXX_NNN",
    UrlFormat = "https://github.com/bhengubv/CircleAI/blob/master/docs/experimental.md#{0}")]
[CircleAIVerificationStatus(VerificationLevel.Reference)]
public sealed class Foo { ... }
```

The `UrlFormat` lands on this page. The `CircleAIVerificationStatus`
attribute carries a parallel field consumers can read at runtime — see
`CircleAI.Core.Validation.CircleAIVerificationStatusAttribute`.
