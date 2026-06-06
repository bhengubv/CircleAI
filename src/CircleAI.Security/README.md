# CircleAI.Security

The CircleAI runtime immune system. `AnomalySignal` + `ISecurityWatchdog`
+ `SecurityCheckpoint` give every CircleAI component a uniform way to
report and respond to runtime anomalies (memory anomaly, control-flow
drift, biometric spoof, mesh pivot, etc.).

```bash
dotnet add package CircleAI.Security
```

```csharp
using CircleAI.Security;

ISecurityWatchdog watchdog = new DefaultSecurityWatchdog();
var response = await watchdog.OnAnomalyDetectedAsync(
    AnomalySignal.Create(ThreatVector.MemoryAnomaly, 0.92, "MyComponent", "..."),
    checkpoint: lastTrustedState);
```

Safe-by-default composer: wrap the watchdog with
`DefaultAnomalyEventDispatcher` to get verify + dedup + invoke in one
call so a production consumer cannot accidentally accept an unverified
or replayed signal.

`AnomalySignal.Evidence` is serialised via
`RedactedEvidenceJsonConverter` — every value becomes the SHA-256 hex of
its UTF-8 bytes; raw evidence never leaves the process in clear text.

See [docs/ARCHITECTURE.md](https://github.com/bhengubv/CircleAI/blob/master/docs/ARCHITECTURE.md).
