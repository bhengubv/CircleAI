> **This file is the frozen 2.0.0 release-notes record.** For every
> release since (2.0.1 → 3.0.1), see [CHANGELOG.md](CHANGELOG.md) —
> that is the authoritative version history. This file stays as the
> historical 2.0.0 marker.

# CircleAI 2.0.0 — "Fallback chain + brownout + RAG"

**Released:** 2026-06-16
**Programme:** runtime-2.0 (2nd shipped release of the programme)

This release continues the runtime-2.0 programme. Three more features ship
in 2.0.0, all managed-side; the remaining four runtime-2.0 features (tiered
KV, mmap, speculative decode, LoRA) need native mnnbridge cross-builds and
land in 2.1.0.

The pitch stays the same:

> **Drop CircleAI in. Your app runs on the bottom 60 % of the smartphone market.**

---

## What you get today

### Fallback chain across the catalog (RT-08)

Every Qwen3 and Qwen2.5 entry in the embedded catalog now declares a
`FallbackModelId` so the runtime knows which smaller sibling to fall back
to. `IModelSelector.ChainFor("Qwen3-8B-MNN")` returns
`["Qwen3-8B-MNN","Qwen3-4B-MNN","Qwen3-1.7B-MNN","Qwen3-0.6B-MNN"]` —
the runtime walks the chain when memory or thermal pressure forces a
downshift.

### Brownout hot-swap (RT-04)

`AIService.BrownoutAsync(BrownoutReason.MemoryPressure)` cancels in-flight
generations gracefully, disposes the current generator, resolves the next
fallback in the chain, and reloads — all without the host needing to
restart. The new `IMemoryPressureSource` contract has a `Null` default
(brownout never fires) and a `Manual` implementation that hosting layers
wire to Android `onTrimMemory` critical / iOS memory warning. The
existing `IAIObserver` gets a new `OnBrownoutAsync(from, to, reason)`
hook so analytics see every swap.

### Embeddings-as-a-Service (RT-09)

New package `CircleAI.Embeddings.Local 2.0.0` ships an on-device embedding
store with built-in RAG primitives:

```csharp
var encoder = new MyOnnxSentenceEncoder(dim: 384);
await using var store = new InMemoryEmbeddingStore(encoder);

await store.AddAsync(new EmbeddingDocument("doc-42", "the quick brown fox"));
var hits = await store.SearchAsync("fox jumps over the dog", topK: 3);
```

Vectors are TurboQuant-compressed at 4 bits/dim — about 8× shrink vs FP32 —
so a phone holds ~250K short paragraphs in 1 GB. Brute-force cosine for
v1; HNSW upgrade in 2.1.0. The whole store round-trips to a single file
via `SaveAsync` / `LoadAsync`.

## What's already in 1.7.0 (still in 2.0.0)

- **RT-02** Live session snapshot / restore — conversations survive OOM kills.
- **RT-06** Cross-session prefix cache — sub-200 ms first token on repeats.
- **RT-11** Power-budget API — declarative per-call energy ceiling.

## What's coming next

| Quarter | Lands |
|---|---|
| 2.0.x | RT-07 predictive warmup, RT-12 mesh-offload v1 (managed-only follow-ups) |
| 2.1.0 | RT-01 tiered KV cache (4-8× longer context); native cross-build |
| 2.1.0 | RT-03 mmap weight loading (3 B models on 2 GB phones) |
| 2.1.0 | RT-05 speculative decoding (2-3× faster decode) |
| 2.2.0 | RT-10 on-device LoRA personalisation |

## Breaking changes

None. Every new API is purely additive. Existing 1.7.0 callers compile
and run unchanged on 2.0.0.

## Upgrade path

```xml
<PackageReference Include="CircleAI.Core"                    Version="2.0.0" />
<PackageReference Include="CircleAI.Inference"               Version="2.0.0" />
<PackageReference Include="CircleAI.Hosting"                 Version="2.0.0" />
<PackageReference Include="CircleAI.Hosting.InferenceBridge" Version="2.0.0" />
<PackageReference Include="CircleAI.Inference.Server"        Version="2.0.0" />
<PackageReference Include="CircleAI.Embeddings.Local"        Version="2.0.0" />
```

10-language portable SDK ports (Python, TS, Go, Kotlin, Swift, Rust, C,
HarmonyOS, Android) carry the 1.7.0 public surface and pick up the
`PowerBudget`, `IChatGenerator.SaveSessionAsync`, and
`GenerationOptions.UsePrefixCache` parity from that release.

## Telemetry / privacy

None of the 2.0.0 features call home. The embedding store is on-disk
only; vectors never leave the device. Brownout fires on a pressure
signal the host gives us — we don't poll the OS, the host hands us the
event.

## Credits

Built on Alibaba MNN 3.5.0 + Google's TurboQuant codec. Vector
compression parity tested against the native C++ port at every release.
