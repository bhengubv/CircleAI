# CircleAI.Inference.Server

OpenAI-compatible ASP.NET Core minimal-API hosted runtime over
`CircleAI.Hosting.InferenceBridge`.

Endpoints:

| Method | Path                          | Notes |
|--------|-------------------------------|-------|
| POST   | `/v1/chat/completions`        | non-stream + SSE stream, OpenAI shape |
| POST   | `/v1/embeddings`              | single-string or string-array input |
| POST   | `/v1/companion/turn`          | CircleAI-native Companion (Send / Agent / Stream) |
| GET    | `/v1/diagnostics`             | uptime, loaded models, host profile, backend, counters |
| GET    | `/v1/models`                  | OpenAI-shaped list of loaded models |
| GET    | `/v1/admin/lifecycle`         | total VRAM/RAM allocated + per-load state |
| POST   | `/v1/admin/models/load`       | runtime load via `MnnInferenceBridgeFactory` (default) |
| DELETE | `/v1/admin/models/{modelId}`  | runtime unload |
| GET    | `/v1/healthz`                 | liveness (no auth) |
| GET    | `/v1/readyz`                  | readiness (no auth) |

```bash
dotnet add package CircleAI.Inference.Server
```

```csharp
using CircleAI.Inference.Server.Hosting;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddCircleAIInferenceServer(builder.Configuration);

var app = builder.Build();
app.UseAuthentication();
app.UseAuthorization();
app.MapCircleAIEndpoints();
app.Run();
```

Default `IBridgeFactory` is `MnnInferenceBridgeFactory` — composes
ModelRegistryService + ModelDownloadService + NativeRuntimeFetcher +
QwenTextGenerator into a working `IInferenceBridge` for any
`(modelId, backend, tier)` the admin endpoint requests.

Deployment artefacts ship in-package:
- `Dockerfile` (multi-stage, non-root, healthcheck)
- `systemd/circleai-inference-server.service` (`Type=notify`, hardened)
- `windows/install-windows-service.ps1` (sc.exe install/uninstall/restart)

See [docs/DEPLOY.md](https://github.com/bhengubv/CircleAI/blob/master/docs/DEPLOY.md).
