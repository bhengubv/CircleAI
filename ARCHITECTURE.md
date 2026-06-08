# Architecture — Why NuGet sleeps

The single architectural promise Circle AI is built around:

> **The SDK has zero model knowledge. ModelScope's catalog is the source
> of truth, discovered at runtime. A new Qwen / Kimi / DeepSeek variant
> lands on ModelScope → the SDK picks it up on the next refresh. NuGet
> sleeps.**

This document explains what that means and why. For the full
Chinese-sovereign stack rationale (MNN runtime, model licensing, the
zero-Western-runtime rule), see [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md).
This page is the higher-level "why does the SDK refuse to know about
specific models" story.

---

## The shape of the promise

```
┌────────────────────────────────────────────────────────────────┐
│  Consumer                                                       │
│    "RequiredCapabilities = Default | Tools"                    │
│    SystemPrompt = "You are B!"                                 │
│    DeviceContext = MyPlatformAdapter.Current                   │
└────────────────────────────────────────────────────────────────┘
            │
            ▼
┌────────────────────────────────────────────────────────────────┐
│  CircleAI SDK                                                  │
│    1. DeviceProbe.Snapshot()  ── RAM, storage, CPU, GPU, …    │
│    2. ModelScopeCatalogClient.GetCachedCatalog()              │
│         ├─ remote refresh (signed, daily)                     │
│         └─ disk-cached fallback (offline)                     │
│    3. IModelSelector.BestFit(probe, capabilities)             │
│         → ModelSelection                                       │
│    4. ModelDownloadService.EnsureBundleAsync(…)               │
│    5. QwenTextGenerator / KimiVlGenerator / …                 │
│         (rendered via PromptTemplateEngine —                  │
│          no hardcoded ChatML, no hardcoded format)            │
└────────────────────────────────────────────────────────────────┘
            │
            ▼
        Real tokens
```

Every model-specific decision sits **inside the SDK at runtime**, not
inside source code at NuGet-pack time. Consumers tell the SDK *what they
need*. The SDK answers *with what runs on this device, picked from
what's available right now*.

---

## What this rules out

In Circle AI's source code you will not find:

- A `string` literal `"Qwen3-4B-MNN"` or any other specific model name
  outside of the registry JSON / catalog fixtures / tests.
- A `Dictionary<string, ModelTier>` keyed by model family.
- A `switch` over model family in `BuildPrompt`.
- A `ModelId` enum.
- An "I support these models" list in any service registration.

Every place those would normally appear is replaced by one of:

- **`IModelSelector.BestFit(deviceProbe, capabilities)`** — picks by
  walking the live catalog.
- **`PromptTemplateEngine.Render(messages)`** — reads the model's own
  `tokenizer_config.json` and renders via Jinja2 (Scriban .NET).
- **`AIOptions.RequiredCapabilities`** — the consumer declares
  capabilities as flags; the selector resolves them to a model.

When a new model family arrives — say a Qwen 4.0 with a different
ChatML — the SDK doesn't need a code path for it. The model ships its
own `chat_template`, and the template engine renders. Same for
DeepSeek, GLM, Mistral, whatever comes next.

---

## Why ModelScope and not Hugging Face

[docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) covers the full
Chinese-sovereign rationale. The short version: ModelScope hosts every
model Circle AI uses (Qwen, Kimi-VL, DeepSeek, GLM, SenseVoice) with a
Chinese-controlled CDN, Apache-2.0 / Tongyi-licensed weights, and an
official MNN-optimised quantised variant published by Alibaba directly.

Hugging Face is the fallback for redundancy. It never takes a
load-bearing role.

The catalog client polls ModelScope's file-listing API
(`api/v1/models/{repo}/repo/files`), filters for MNN-compatible bundles,
parses each bundle's metadata, signs the result, and caches it. The
SDK then reads the cache. If ModelScope is unreachable, the cache keeps
working.

---

## Why NuGet sleeps

Reason 1: **a new model is not a new SDK.** "We support Qwen 4 now" is
catalog metadata, not code. Forcing every consumer through a NuGet
upgrade for that would burn goodwill for no engineering reason.

Reason 2: **catalogs evolve faster than SDKs.** ModelScope ships new
quantisations weekly. An SDK release cadence built around quantisation
publish events would either drown in releases or be permanently behind.

Reason 3: **the consumer's "model" mental model is wrong anyway.** The
consumer doesn't care which model runs. They care that "chat works,"
"vision works," "tool calling works." Those map to
`ChatCapability` flags, which are stable across model families.

Reason 4: **sanctions resilience.** If a specific repo ever vanishes
from ModelScope, the catalog refresh skips it and the selector
gracefully picks something else. No SDK release required to react. The
worst case is "the catalog hasn't refreshed and the cache is stale" —
still serviceable from disk.

NuGet releases happen when:

- A bug ships in the SDK itself.
- A new runtime backend lands (`mnnbridge.dll` ABI change, new RID
  shipped, new GPU surface supported).
- A new injection point opens up (`IModelSelector`, `ITransportSelector`,
  `IPromptTemplateEngine`).
- A new core type is added that consumers need to reference (the trinity
  evolves).

NuGet releases do **not** happen when:

- ModelScope publishes a new Qwen variant.
- A new quantisation level is preferred.
- A model's recommended context window changes.
- A model's chat template tweaks.

---

## The catalog contract

Every entry in the cached catalog declares:

| Field | What it tells the selector |
|---|---|
| `Name` / `Repo` | ModelScope identifier — drives the download URL |
| `BundleFiles[]` (each with `Sha256`, `SizeBytes`) | What to fetch + verify |
| `TotalBytes` | Storage gate |
| `MinRamGb` | RAM gate |
| `MinStorageGb` | Storage headroom gate |
| `Capabilities[]` | Which `ChatCapability` flags this satisfies |
| `QualityRank` | Tiebreaker — higher wins |
| `Architecture` | Hint for the prompt template engine |
| `Quantization` | Display label |
| `Version` | Display label |

That's all the selector needs. Adding a new model means adding an entry.
Adding a new model **family** means publishing a `tokenizer_config.json`
with a `chat_template` — the SDK reads it and renders. No C# code path
required.

---

## The principle, restated

The consumer is the only party in the system that knows their persona,
their secrets, their tool surface, and the capabilities their
conversation needs. The device is the only party that knows its own
RAM, storage, GPU, thermal class, and battery. The SDK sits between
them, asking each party for what they alone can answer, and refusing
to make either party guess on behalf of the other.

That refusal is the whole architecture. Everything else is implementation.
