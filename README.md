# Circle AI

On-device companion AI for the [Aether Protocol](https://github.com/bhengubv/aether-protocol)
ecosystem. ~100 C# modules covering inference, multi-agent orchestration,
memory + persona, voice, tooling, mesh transport, and 50+ domain adapters —
plus an OpenAI-compatible **self-hosted inference server** you can drop
behind a load balancer.

> **Built around one architectural promise:** the SDK has zero model
> knowledge. ModelScope's catalog is the source of truth, discovered at
> runtime. A new Qwen / Kimi / DeepSeek variant lands on ModelScope, the
> SDK picks it up on next refresh. **NuGet sleeps** — releases are for
> SDK bugs and new runtime backends, not "we support a new model."
>
> See [ARCHITECTURE.md](ARCHITECTURE.md) for why.

---

## What's in here

### 🧠 Inference

The trinity every consumer binds to:

| Type | Where | Purpose |
|---|---|---|
| `IChatGenerator` | `CircleAI.Inference` | The atomic seam: messages in, tokens out |
| `IInferenceBridge` | `CircleAI.Hosting.InferenceBridge` | Lifecycle + descriptor wrapper around a generator |
| `IAIService` | `CircleAI.Hosting` | The long-lived companion service — RAG, persona, tools, observers |

Runtime: **Alibaba MNN** (Apache-2.0) on every platform. Models:
**Qwen 3+, Kimi-VL, DeepSeek, GLM** — discovered through ModelScope, not
pinned in source. See [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) for
the full Chinese-sovereign rationale.

### 🖥️ `CircleAI.Inference.Server`

OpenAI-compatible HTTP server. Endpoints: `/v1/chat/completions` (SSE
streaming), `/v1/embeddings`, `/v1/companion/*`, `/v1/diagnostics`,
`/v1/healthz`, `/v1/readyz`. JWT and API-key auth out of the box. Ships
with a `Dockerfile`, a systemd unit, and a Windows-service install script.

Drop it behind a load balancer and any OpenAI-SDK consumer talks to it
unchanged.

### 👥 Companion + Memory

- **`CircleAI.Companion`** — long-running session loop with proactive turn
  generation and mesh-state sync.
- **`CircleAI.Memory`** — episodic memory + RAG context builder, persona
  evolution, affect state, goal tracking, feedback signals.
- **`CircleAI.Orchestration`** — multi-agent loop (`LokiOrchestrator`)
  with quality gates and concurrent dispatch.

### 🔧 Tools + Skills + Voice

- **`CircleAI.Tools`** — `IToolBridge` for function calling. Built-in
  `HttpToolBridge` maps REST APIs to tool definitions.
- **`CircleAI.Skills`** — pluggable capability surface the model can
  invoke contextually.
- **`CircleAI.Voice`** — ONNX TTS / STT pipeline (the only ONNX dependency
  in the codebase — text inference stays MNN).

### 🕸️ Networking + Security

- **`CircleAI.Networking.*`** — pluggable transports (HTTP / WebSocket /
  AetherNet are production; BLE / DTN / NearLink are scaffolded).
  `ITransportSelector` picks per-message based on connectivity, not by
  consumer choice.
- **`CircleAI.Security.*`** — mesh-directive-driven chat refusal,
  AetherMesh bindings.

### 🏛️ ≈50 domain adapters

`CircleAI.Beauty`, `CircleAI.Construction`, `CircleAI.Faith`,
`CircleAI.Family`, `CircleAI.Fitness`, `CircleAI.Food`,
`CircleAI.Healthcare`, `CircleAI.RealEstate`, `CircleAI.Tourism`,
`CircleAI.Tradesperson`, … — each provides domain-specific system-prompt
context, knowledge facets, and lifecycle hooks. The companion engine
loads only the adapters whose required sensors / UI fit the host device.

---

## Quick install — C# (.NET 9 / .NET 10)

```bash
dotnet add package CircleAI.Inference.Server   # if you want the HTTP server
dotnet add package CircleAI.Hosting             # if you want the in-process service
```

### Zero-knob consumer

```csharp
using CircleAI.Hosting;

// The SDK figures out: which model fits this device, what context window
// it can sustain, how many agents it can dispatch concurrently, which
// KV-compression mode to use. The consumer only states intent.
var ai = new AIService(new AIOptions
{
    SystemPrompt         = "You are B!, the helpful one.",
    RequiredCapabilities = ChatCapability.Default | ChatCapability.Tools,
});

await ai.StartAsync();

var answer = await ai.AskAsync("What's the capital of Morocco?");
Console.WriteLine(answer);
```

**That's it.** No `ModelId`. No `ContextSize`. No `MaxConcurrency`. The
SDK probes the device, queries the ModelScope catalog, picks the
highest-quality bundle that fits + satisfies the capability flags, and
keeps that decision live as the catalog refreshes.

For the full picture (overrides, injection points, observer events,
bring-your-own-runtime), see [CONSUMING.md](CONSUMING.md).

---

## Self-hosted inference server

```bash
docker run -d -p 5050:5050 \
  -v ~/.circleai/models:/var/lib/circleai/models \
  ghcr.io/bhengubv/circleai-inference-server:latest
```

```bash
# Drop-in OpenAI replacement
curl http://localhost:5050/v1/chat/completions \
  -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json" \
  -d '{"model":"auto","messages":[{"role":"user","content":"Hi"}]}'
```

`"model":"auto"` ⇒ `IModelSelector.BestFit(deviceProbe,
ChatCapability.Default)`. The server picks; you don't.

Full deployment guide in [docs/DEPLOY.md](docs/DEPLOY.md).

---

## Sister repository — the 10-language portable kernel

The portable Circle AI kernel (AffectState math, KnownLanguages registry,
ICompanionSession contracts) ships in **10 languages** so every Aether node
can host the companion stack natively — C#, Python, TypeScript, Go, Kotlin,
Swift, Rust, C, Android (Kotlin), HarmonyOS (ArkTS).

The 10-language SDK lives alongside the C# framework in this repository.
Per-language quickstarts: see [docs/quickstart/](docs/quickstart/).

---

## License

MIT. See [LICENSE](LICENSE).

---

## Pointers

| Doc | Purpose |
|---|---|
| [ARCHITECTURE.md](ARCHITECTURE.md) | Why ModelScope is the catalog and NuGet sleeps |
| [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) | The full Chinese-sovereign stack rationale |
| [CONSUMING.md](CONSUMING.md) | The trinity, the three injection points, worked example |
| [SETUP.md](SETUP.md) | MNN native-runtime setup per platform |
| [TODO.md](TODO.md) | Open work, current priorities |
| [docs/quickstart/](docs/quickstart/) | Per-language quickstart for the portable kernel |
