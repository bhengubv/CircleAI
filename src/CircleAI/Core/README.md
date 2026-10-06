# CircleAI.Core

Foundation primitives shared by every CircleAI package — diagnostics
(`ActivitySource` + `Meter`), audit-log abstraction, ambient tenant
context, the `CircleAIComponentBase` outcome-classification wrapper, and
the `[CircleAIVerificationStatus]` attribute that gates honest-but-not-
yet-wire-proven surface.

```bash
dotnet add package CircleAI.Core
```

```csharp
using CircleAI.Core.Diagnostics;
using CircleAI.Core.Components;

public sealed class MyComponent : CircleAIComponentBase
{
    public override string ComponentName => "MyComponent";

    public Task DoWorkAsync(CancellationToken ct) =>
        RunOperationAsync("DoWork", async () =>
        {
            // your work here — outcome, duration, audit emitted automatically
            return 42;
        }, ct);
}
```

OpenTelemetry exporters bind to `Meter "CircleAI"` and `ActivitySource
"CircleAI"` — Prometheus / Jaeger / Loki pick them up with no extra
wiring.

See [docs/ARCHITECTURE.md](https://github.com/bhengubv/CircleAI/blob/master/docs/ARCHITECTURE.md)
for the bigger picture and [docs/experimental.md](https://github.com/bhengubv/CircleAI/blob/master/docs/experimental.md)
for the verification-status story.
