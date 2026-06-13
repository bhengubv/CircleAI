# CircleAI 1.7.0 — "Cheap-phone tier"

**Released:** 2026-06-13
**Programme:** runtime-2.0 (first of seven planned releases)

This release starts the runtime-2.0 programme. CircleAI takes the same $100
Tecno or Itel your users are already carrying and squeezes 80% of the
flagship AI experience out of it. The pitch:

> **Drop CircleAI in. Your app runs on the bottom 60 % of the smartphone market.**

Three features ship today; nine more follow over the next two quarters.

---

## What you get today

### 🔋 Power-budget API (RT-11)

New `GenerationOptions.Budget` — a declarative knob that says how much device
energy a single call is worth.

```csharp
await generator.GenerateAsync(
    messages,
    new GenerationOptions { Budget = PowerBudget.Low }, // chat ack / quick reply
    ct);

await generator.GenerateAsync(
    messages,
    new GenerationOptions { Budget = PowerBudget.High }, // long planning reply
    ct);
```

| Budget | What it does |
|---|---|
| `None` | Opt out — honour `MaxTokens` literally |
| `Low` | Cap ~64 tokens, prefer TQ4 KV, pick smaller model in chain (when configured) |
| `Normal` *(default)* | Cap ~512 tokens. Auto-downgrades to `Low` when battery < 15% |
| `High` | Cap ~2048 tokens, full FP16 KV. Auto-throttles to `Normal` on thermal warnings |

The runtime decides the trade-off; you declare the budget. Reach for `Low` on
quick replies, `High` for long planning — and stop worrying about which
PowerBudget the user's phone can afford on its current battery.

### 💾 Live snapshot / restore (RT-02)

Android and iOS reap background processes aggressively. Until 1.7.0, that
meant the user lost their entire conversation when they switched apps for too
long.

```csharp
// Background notification handler
await generator.SaveSessionAsync("/data/user/0/com.app/files/last.session", ct);

// Foreground / next launch
await generator.LoadSessionAsync("/data/user/0/com.app/files/last.session", ct);
```

Round-trips the model's KV cache through the existing MNN session primitives.
Both `QwenTextGenerator` and `KimiVlGenerator` implement the new methods; the
generic `IChatGenerator` contract carries default `NotSupportedException`
implementations for other backends. Users come back tomorrow, the assistant
remembers everything.

### ⚡ Cross-session prefix cache (RT-06)

Every fresh conversation today re-pays the system-prompt prefill cost — on a
Tier-0 device, 2-3 seconds before the first token. CircleAI 1.7.0 caches the
prefill state, keyed by `(modelId, systemPrompt)`, so the second chat with the
same persona starts instantly.

```csharp
await foreach (var chunk in generator.StreamAsync(
    messages,
    new GenerationOptions { UsePrefixCache = true },
    ct))
{
    // First token in < 200 ms on the second and subsequent chats
    // with the same system prompt.
}
```

Zero infrastructure for the integrator. The cache lives at
`%LOCALAPPDATA%/CircleAI/prefix-cache/` (Windows) or
`~/.circleai/prefix-cache/` (Unix-like), is bounded at 500 MB with LRU
eviction, and uses MNN's existing session primitives — no native bridge
changes required.

---

## What's coming next

The runtime-2.0 programme has nine more features over the next ~32 weeks.

| Release | Features |
|---|---|
| **1.8.0** *(wk 4-8)* | Multi-tier model fallback chain · Adaptive brownout under RAM pressure · Embeddings-as-a-Service (RAG out of the box) |
| **1.9.0** *(wk 9-12)* | Predictive warmup · Compressed snapshot format · Mesh-offload capability discovery |
| **2.0.0** *(wk 13-16)* | **Tiered KV cache** — 4-8× longer context on the same RAM (breaking change: new `KvCompressionPolicy`) |
| **2.1.0** *(wk 17-24)* | mmap weight loading · Speculative decoding |
| **2.2.0** *(wk 25-32)* | On-device LoRA personalisation · Transparent mesh offload over AetherNet |

---

## Breaking changes

**None.** Every new API has a default implementation; existing callers keep
working byte-for-byte. The first breaking release is 2.0.0 (Q+3).

## Upgrade path

```xml
<PackageReference Include="CircleAI.Inference"                Version="1.7.0" />
<PackageReference Include="CircleAI.Hosting.InferenceBridge"  Version="1.4.0" />
<PackageReference Include="CircleAI.Inference.Server"         Version="1.5.0" />
```

Source: [https://nuget.pkg.github.com/bhengubv/index.json](https://github.com/bhengubv?tab=packages)

## Telemetry / privacy

**None of the new features call home.** The prefix cache is on-disk only. The
power-budget API reads platform-published battery + thermal signals; no usage
data leaves the device. The forthcoming predictive warmup (1.9.0) will be
opt-in and local-only.

## 10-language portable SDK

The cross-language portable surface (Python, TypeScript, Go, Kotlin, Swift,
Rust, C, HarmonyOS, Android) updates in lock-step. The added types
(`PowerBudget`, `GenerationOptions.UsePrefixCache`, `SaveSessionAsync` /
`LoadSessionAsync`) appear in every port with default-impl back-compat — see
the per-language READMEs.

## Credits

Built on Alibaba MNN 3.5.0 + Google Research's TurboQuant codec
(arxiv:2504.19874). Vector compression parity tested against the native C++
port at every release.

## Tasks shipped in this release

- **RT-02** Live snapshot / restore — `IChatGenerator.SaveSessionAsync` / `LoadSessionAsync`
- **RT-06** Cross-session prefix cache — `GenerationOptions.UsePrefixCache` + `PrefixCacheService`
- **RT-11** Power-budget API — `PowerBudget` enum + `GenerationOptions.Budget` + `PowerBudgetPolicy`

Tracked in the runtime-2.0 backlog as RT-01…RT-12.
