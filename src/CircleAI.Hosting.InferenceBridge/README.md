# CircleAI.Hosting.InferenceBridge

Cross-OS LLM inference daemon contract. `IInferenceBridge` wraps any
`IChatGenerator` and adds descriptors, outcome classification, and
device-capability reporting. `LocalProcessInferenceBridge` is the
in-process reference impl; cross-process bridges (Binder / XPC / named
pipes) wrap it inside a daemon for the "one model loaded once per
device, shared by every app" deployment.

```bash
dotnet add package CircleAI.Hosting.InferenceBridge
```

```csharp
using CircleAI.Hosting.InferenceBridge;

var bridge = new LocalProcessInferenceBridge(generator, descriptor);
var response = await bridge.CompleteAsync(request, ct);
```

`GetDeviceCapabilitiesAsync` now returns REAL values via
`CircleAI.Runtime.ICapabilityProbe` (the synthetic stub was removed in
1.2.0). See [docs/ARCHITECTURE.md](https://github.com/bhengubv/CircleAI/blob/master/docs/ARCHITECTURE.md)
§ 2.
