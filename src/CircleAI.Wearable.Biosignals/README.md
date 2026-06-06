# CircleAI.Wearable.Biosignals

Biosignal ingestion (HR, HRV, SpO2, accelerometer, sleep stage, …) and
deterministic projection of those signals into `CircleAI.Memory.AffectState`
mutations.

```bash
dotnet add package CircleAI.Wearable.Biosignals
```

```csharp
using CircleAI.Wearable.Biosignals;

IBiosignalSource source = new NullBiosignalSource();     // CIRCLEAI_BIO_001
var aggregator = new BiosignalAggregator(source);
var snapshot   = await aggregator.AggregateAsync(window: TimeSpan.FromMinutes(5), ct);

var affect = new AffectState();
BiosignalAffectMapper.Apply(sample, affect);
```

Gated as `CIRCLEAI_BIO_001` — the affect-mapping rule sheet is fixture-
tested but the thresholds are NOT clinically validated. Affect tinting
only; not a medical signal. See
[docs/experimental.md](https://github.com/bhengubv/CircleAI/blob/master/docs/experimental.md).
