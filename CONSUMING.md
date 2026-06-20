# Consuming Circle AI

How to wire Circle AI into a real consumer — phone app, desktop client,
server, headless service. Pick your binding level, supply your secrets,
let the SDK do the rest.

---

## The trinity

Three types. One you usually bind to; the other two you replace when
you need to.

| Type | Lives in | When you bind to it |
|---|---|---|
| `IAIService` | `CircleAI.Hosting` | **Default.** You want the full companion stack — RAG, persona, observers, tool calls, the lot. |
| `IInferenceBridge` | `CircleAI.Hosting.InferenceBridge` | You're building a server / multi-model host and want lifecycle wrapping around a single generator with a `ModelDescriptor`. |
| `IChatGenerator` | `CircleAI.Inference` | You want messages-in, tokens-out and nothing else. Most plumbing-level callers. |

Everything else in `CircleAI.*` is either an adapter that produces one
of these, or a consumer that calls into one.

---

## The three injection points

Every Circle AI consumer customises behaviour by injecting these. Each
defaults to a safe-null or auto-probe implementation; you swap in your
real one when you need it.

### 1. `IDeviceContext` — what the device knows

```csharp
new AIOptions
{
    DeviceContext = new MyPlatformDeviceContext(),  // your MAUI / Android / iOS adapter
};
```

What it tells the SDK: RAM, storage, CPU cores, GPU kind, thermal class,
connectivity, locale, battery, GPS, foreground app. The
[`IDeviceContext` interface](src/CircleAI.Core/IDeviceContext.cs)
enumerates every signal.

What the SDK does with it: picks the model, sizes the context window,
sets concurrency, picks KV-compression mode, decides whether to defer
heavy work on low battery, injects situational context into the system
prompt. **Without it, you're telling the SDK to guess.**

If you don't supply one, `DefaultDeviceContext` probes RAM / storage /
CPU / connectivity from the runtime — enough for the model selector,
but it can't see GPS, battery, foreground app, or thermal sensors. Wire
your real adapter on mobile / wearable hosts.

### 2. `IToolBridge` — what your tools do

```csharp
new AIOptions
{
    ToolBridge = new HttpToolBridge(myApiBaseUri),   // built-in REST adapter
    // or your own — e.g. an in-process function dispatcher
};
```

The bridge maps tool names to invocations. The built-in
`HttpToolBridge` (in `CircleAI.Tools`) walks an OpenAPI surface and
exposes each endpoint as a tool. When the model emits
`<tool_call>{"name":"…"}</tool_call>`, the bridge dispatches it.

Without a bridge, `AIService.InvokeToolAsync` returns a failure result
and the agentic loop short-circuits.

### 3. `IAIObserver` — what your telemetry / economics layer sees

```csharp
new AIOptions
{
    Observer = new MyAnalyticsObserver(),
};
```

Receives lifecycle events (`OnStartedAsync`, `OnStoppedAsync`),
generation events (`OnInferenceStarted`, `OnInferenceCompleted`,
`OnTokenStreamed`), tool events, fetch events, and (in P0) catalog
refresh events. Wire whatever you need: analytics, ledger entries,
Qi/Karma accrual, audit trail, sleeve telemetry.

The default is null — every event is a no-op.

---

## Zero-knob consumer

This is the whole program. The SDK figures out everything else.

```csharp
using CircleAI.Hosting;

var ai = new AIService(new AIOptions
{
    // ── What only you can answer ──
    SystemPrompt         = "You are B!, the helpful one.",
    RequiredCapabilities = ChatCapability.Default | ChatCapability.Tools,

    // ── Optional but recommended on mobile / wearable ──
    DeviceContext        = MyPlatformDeviceContext.Current,

    // ── Optional ──
    ToolBridge           = new HttpToolBridge(new Uri("https://api.example.com")),
    Observer             = new ConsoleObserver(),
});

await ai.StartAsync();

// Single-turn
var answer = await ai.AskAsync("What's the capital of Morocco?");

// Multi-turn with structured history
var chat = await ai.ChatAsync(new[]
{
    new ChatMessage("user", "What's good in Casablanca?"),
    new ChatMessage("assistant", answer),
    new ChatMessage("user", "Anything specifically for kids?"),
});

// Streaming
await foreach (var token in ai.StreamAsync(messages, ct))
    Console.Write(token);

// Agentic — model decides when to call tools and re-enter
var result = await ai.AgenticChatAsync(messages, ct);

await ai.StopAsync();
```

**No `ModelId`. No `ContextSize`. No `MaxConcurrency`. No
`KvCompressionMode`. No `Backend`.** All of those default to
"derive from device" — the SDK probes, queries the (eventually
ModelScope-live) catalog, picks the best fit, and keeps that decision
live as the catalog refreshes. You can pin any of them explicitly
when you need to (tests, regulated deployments, dev pinning) — that's
the back-compat hatch — but defaults are smart.

---

## When you need lower-level access

### Replace the inference bridge factory

Bring your own runtime — vLLM, llama.cpp (you do you), a custom MNN
build, a cloud-fallback router. Register an `IBridgeFactory` before the
default kicks in:

```csharp
services.AddSingleton<IBridgeFactory, MyCustomBridgeFactory>();
services.AddCircleAIInferenceServer(config);   // picks yours instead of MnnInferenceBridgeFactory
```

The factory's contract is `CreateAsync(modelId, backend, tier, ct) →
IInferenceBridge`. Anything from "pin to llama.cpp" to "round-robin
across three remote endpoints" fits.

### Talk to a generator directly

Skip `IAIService` entirely if you don't need the companion stack:

```csharp
using CircleAI.Inference;

var generator = new QwenTextGenerator(modelPath, contextSize: 4096);
var response = await generator.GenerateAsync(messages, options, ct);
```

Same generator type the bridge factory builds. No RAG, no persona, no
tool loop — just inference.

### Talk to the inference server over HTTP

If you want OpenAI compatibility instead of a C# binding, run
`CircleAI.Inference.Server` and point any OpenAI SDK at it:

```bash
curl http://localhost:5050/v1/chat/completions \
    -H "Authorization: Bearer $TOKEN" \
    -H "Content-Type: application/json" \
    -d '{"model":"auto","messages":[{"role":"user","content":"Hi"}]}'
```

`"model":"auto"` ⇒ `IModelSelector.BestFit`. The server picks.

---

## Customisation cookbook

| You want… | Set | Or implement |
|---|---|---|
| Pin a specific bundle | `AIOptions.ModelId = "Qwen3-4B-MNN"` | — |
| Pin a context window | `AIOptions.ContextSize = 8192` | — |
| Long-context model | `AIOptions.RequiredCapabilities = ChatCapability.LongContext` | — |
| Vision model | `AIOptions.RequiredCapabilities = ChatCapability.Vision` | — |
| Tool-use model | `AIOptions.RequiredCapabilities = ChatCapability.Tools` | — |
| Declarative energy ceiling per call | `GenerateAsync(..., budget: PowerBudget.Low \| Normal \| High)` | — |
| Survive OOM kill — auto-snapshot | `AIOptions.AutoSnapshotOnPause = true` | — |
| Manual snapshot + restore | `await generator.SaveSessionAsync(path)` / `LoadSessionAsync(path)` | — |
| Bring your own runtime | — | `IBridgeFactory` |
| Bring your own model storage | `AIOptions.ModelStorageDir` | `IModelLoader` |
| Bring your own tools | `AIOptions.ToolBridge` | `IToolBridge` |
| Watch lifecycle / inference / tool events | `AIOptions.Observer` | `IAIObserver` |
| Watch upgrade availability | — | `IAIObserver.OnUpgradeAvailableAsync` |
| Wire sensors (GPS, battery, thermal, …) | `AIOptions.DeviceContext` | `IDeviceContext` |
| Add persistent memory | `AIOptions.EpisodicMemory` | `IEpisodicMemoryStore` |
| Add HippoRAG-style multi-hop recall | — | `CircleAI.Domain.IHippoRagStore` |
| Add persistent persona | `AIOptions.PersonaStore` | `IPersonaStore` |
| Add user feedback signals | `AIOptions.FeedbackStore` | `IFeedbackStore` |
| Add affect / goal tracking | `AIOptions.AffectStore` / `GoalStore` | corresponding interfaces |
| Add voice (wake-word / STT / TTS) | `AIOptions.Voice` | `CircleAI.Speech` (`IWakeWordDetector`, `ISpeechRecognizer`, `ISpeechSynthesizer`) |
| Add face / liveness / document / plate detection | — | `CircleAI.Vision` (`IFaceDetector`, `IFaceLivenessDetector`, `IDocumentVerifier`, `IPlateRecognizer`) |
| Add tool-catalog with OAuth providers | — | `CircleAI.Tools.Catalog` (`IProviderCatalog`, `ICredentialStore`, `IOAuth2FlowDriver`, `IQuotaGuard`) |
| Add perceive-reason-act loop | — | `CircleAI.Observer` (`ISensor`, `IObservationToolbox`, `IObservationLoop`) |
| Add safety / refusal / prompt-injection detection | — | `CircleAI.ContentPolicy` (`IContentFilter`, `IRefusalPolicy`, `IPromptInjectionDetector`, `ISafetyAuditLog`) |
| Add observability (metrics / traces / dashboards) | — | `CircleAI.Observability` (`IMetricSink`, `ITraceSink`, `IDashboardPublisher`) |
| Cross-tier offload (phone borrows server brain) | — | `CircleAI.AetherNet` `ICrossTierOffload` (RT-12 v2) |
| Cloud fallback when local fails | `AIOptions.CloudFallbackEnabled = true` + `CloudFallbackEndpoint` | — |

The full `AIOptions` surface is documented in
[`src/CircleAI.Hosting/AIOptions.cs`](src/CircleAI.Hosting/AIOptions.cs).

### Beyond the trinity — the 3.0 pillar packages

The cookbook above mixes core hosting (`CircleAI.Hosting`) with the
3.0 pillar packages. Pillar packages each ship one focused contract
surface with fail-closed Null implementations — you install the
package, register a real backend, and your `IAIService` gains the
capability. Each pillar's `Contracts.cs` is the authoritative interface
list; the [README](README.md) groups them by purpose with one-line
descriptions. Most pillars (`CircleAI.Vision`, `CircleAI.Speech`,
`CircleAI.Spatial`, `CircleAI.Domain`, `CircleAI.Banking`, …) declare
contracts now; real backends land in dot releases.

---

## What's reserved for you, what's reserved for the SDK

**You decide:**
- Persona voice (system prompt).
- What capabilities the conversation needs.
- What tools the model can reach.
- Where to store models, episodic memory, persona state, feedback.
- API keys, auth tokens.
- Observer wiring.

**The SDK decides:**
- Which concrete model fits this device + those capabilities.
- Context window size.
- Concurrency cap.
- KV-cache compression mode.
- Native backend (CPU / Vulkan / Metal / CUDA / OpenCL / NPU).
- Which transport to use for any given outbound message.
- When to refresh the model catalog.

The line is drawn at "what only the consumer can know" versus "what the
device can answer." Everything in the second column has consumer-facing
override knobs — but the default is always "infer, don't ask."

See [ARCHITECTURE.md](ARCHITECTURE.md) for why.
