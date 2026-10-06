# CircleAI.Federation

Federated-learning round bookkeeping for the CircleAI mesh — accepts
deltas from peers, aggregates per round, exposes the aggregated update
back to participants. In-memory by default
(`InMemoryFederationAggregator`); pluggable for persistent backends.

```bash
dotnet add package CircleAI.Federation
```

```csharp
using CircleAI.Federation;

IFederationAggregator agg = new InMemoryFederationAggregator(); // CIRCLEAI_FED_001
var round = await agg.OpenRoundAsync("model-x", ct);
await agg.SubmitDeltaAsync(round.Id, peerId: "uhid-1", delta, ct);
var aggregated = await agg.CloseRoundAsync(round.Id, ct);
```

Safe-by-default composer: `DefaultFederationDeltaDispatcher` wraps
verify + dedup + accept-or-reject so production consumers can't
accidentally accept a duplicate or signature-bad delta.

The in-memory aggregator is gated as `CIRCLEAI_FED_001` — single-process
correct, not multi-replica safe. See
[docs/experimental.md](https://github.com/bhengubv/CircleAI/blob/master/docs/experimental.md).
