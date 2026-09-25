# Plan — three model capabilities: vision, a bigger brain, image generation

> Written 2026-09-25. Every "is" below was checked against the tree, not recalled.
> Where something is unknown it says so.

## What is actually true today

| | Runtime | Modality | Model catalogued | Real blocker |
|---|---|---|---|---|
| **Qwen-VL** (vision in) | ✅ `KimiVlGenerator` 381 ln | ✅ `ModelModality.Vision` | ✅ **already in `embedded_registry.json`** | reachability + the mmap fix never reached this generator |
| **Bonsai 2** (text brain) | ✅ `QwenTextGenerator` | ✅ `Chat` | ❌ | MNN conversion; 27B will not ride a phone |
| **Qwen-Image** (image out) | ❌ none | ❌ none | ❌ | needs a new modality AND a native diffusion binding |

Three different jobs. Only one is "finish it"; one is a conversion; one is new engineering.

---

## Item 1 — Vision: confirm a round trip that already exists

**Nothing needs sourcing.** `taobao-mnn/Qwen2.5-VL-3B-Instruct-MNN` is already catalogued:
MNN-Q4, QualityRank 9, **2.74 GB**, `MinRamGb 3.9`, files `llm.mnn` + `llm.mnn.weight` +
`llm_config.json` + **`visual.mnn` + `visual.mnn.weight`**. `SmolVLM-256M-Instruct-MNN`
(311 MB, MinRamGb 0.5) is catalogued beside it. So the earlier "what I'd take: a Qwen-VL
in MNN format" was wrong — it has been sitting in the registry.

**Why it has never run.** Four reasons, in the order they bite:

1. **`KimiVlGenerator` has NONE of the mmap work.** Verified: 0 matches for
   `Mmap|TrySetThreads|thread_num` against `QwenTextGenerator`'s 10. So the vision path
   still walks into the exact `kvcache_mmap` fault fixed in `a2f2420` — destroyed mutex,
   SIGSEGV in `ThreadPool::enqueue`. See [[circleai-mmap-kvcache-crash-root-cause]].
2. **`MinRamGb 3.9` exceeds the P30's 3.6 GB**, so `DeviceModelAssessor` marks it
   incompatible and it is never selected. That number is a PRE-MMAP estimate.
3. **The mmap gate would not fire anyway.** `MmapWeightThresholdBytes` is 8 GB and
   `WeightsExceedEagerFit` only mmaps above it — a 2.74 GB bundle loads EAGERLY, which
   is what will not fit.
4. **VL fit maths differ and are not modelled.** A VLM holds `llm.mnn` AND `visual.mnn`
   resident. `ModelModality.Vision`'s own doc says the device-fit maths differs; the
   assessor does not currently account for the vision encoder separately.

**Steps**
1. Port the un-curse into `KimiVlGenerator`: weight-mmap + `TrySetThreads(1)`,
   `kvcache_mmap` deliberately OFF, mirroring `QwenTextGenerator` exactly. Same comment
   explaining why, so nobody re-enables it.
2. Make the mmap gate reachable for VL — either a modality-aware threshold or an explicit
   opt-in for Vision bundles. Do NOT just lower the global 8 GB constant; that changes
   selection for every text model and is a separate, riskier decision.
3. Teach the assessor the VL shape: effective RAM = LLM weights + vision encoder, so
   `MinRamGb` is DERIVED rather than a hand-written 3.9 that nobody can defend.
4. Prove it on the P30 (the only benchmark): pull the bundle, image in → answer out,
   watch resident MB. Start with **SmolVLM-256M (311 MB)** to prove the round trip
   cheaply, THEN Qwen2.5-VL-3B for quality.
5. Tests: assessor picks/rejects VL correctly at 3.6 GB; generator wiring; both legs.

**Honest risk.** `visual.mnn` may not be mmap-able the way `llm.mnn` is — unverified.
If the encoder must be resident, 2.74 GB on a 3.6 GB phone may still lose, and SmolVLM
becomes the P30 answer with Qwen-VL reserved for Redmi/flagship.

---

## Item 2 — Bonsai 2: MEASURED, not guessed (2026-09-25)

**An earlier draft of this plan said "27B won't ride a phone". That was a guess, never
tested, and it is NOT what blocks this.** Size is irrelevant here. What follows was read
out of MNN's own source.

**The model** (verified): `prism-ml/Ternary-Bonsai-2-27B-gguf`, base Qwen3.8-27B. GGUF
only — `PTQ1_0` 5.95 GB (1.75 bits/weight), `PQ2_0` 7.21 GB (2.13), `F16` 53.8 GB, plus
`mmproj` files (so it is multimodal too). **No safetensors exists.** Architecture: hybrid
attention (~75% linear / ~25% full) + SSM (GatedDeltaNet), 64 blocks, blockwise Hadamard
rotation. Stock llama.cpp REJECTS `PQ2_0`/`PTQ1_0` as unknown types; only the
`PrismML-Eng/llama.cpp` fork reads them, with CUDA + Metal kernels (not ARM).

**What MNN can actually do** (MNN 3.6.1 installed; export scripts sparse-cloned to
`_tools/MNN`):
- `gguf2mnn.py` **is not a converter, it is a weight-filler.** It loads an EXISTING MNN
  graph (`llm_config` json, then iterates `opmap`/`convs`) and swaps in weights from GGUF.
  The graph must already have been built by `llmexport.py`.
- Its tensor-type branches: F16, F32, Q4_0, Q4_1, Q4_K, Q5_0, Q5_1, Q6_K, Q8_0. Anything
  else hits one of its four `assert(False)`.
- `gguf/constants.py` DOES define `TQ1_0`=34 and `TQ2_0`=35 (inherited from llama.cpp's
  gguf-py), block sizes (256, 54B) and (256, 66B) — but there is **no dequant for them**, so
  even STANDARD ternary falls through. (An earlier draft said "MNN has no ternary support";
  the constants exist, the dequant does not. Correct the claim, keep the verdict.)
- `llmexport.py` families: qwen3, qwen3_vl, qwen3_vl_moe, qwen3_asr, qwen3_tts, gemma4,
  glm_ocr. **No linear-attention, no SSM, no GatedDeltaNet.**

**Conversion needs all four, and only one is small:**
1. A graph builder for the architecture in `llmexport.py` — does not exist.
2. **MNN C++ ops to EXECUTE linear attention + GatedDeltaNet — do not exist.** This is the
   wall, and it is exactly the kernel work PrismML already did for llama.cpp.
3. `PTQ1_0`/`PQ2_0` dequant in `gguf2mnn.py` — writable, needs the fork's block layout.
4. A source model to build the graph from — GGUF only.

**Verdict: Bonsai 2 cannot be converted to MNN with existing tooling, and the blocker is
the ARCHITECTURE, not the quantization.** Item 3 alone gets weights in and still yields
nothing runnable.

**The path that actually runs Bonsai 2:** it already runs, on the PrismML llama.cpp fork.
Give CircleAI a **second inference backend (llama.cpp/GGUF)** — a new `IChatGenerator` plus
a native bridge, the same shape as the existing `mnnbridge`. No conversion, no lost ternary
compression, and it unlocks the whole GGUF ecosystem rather than one model. The alternative
is porting a novel architecture into MNN, duplicating PrismML's kernel work on ARM.

## Item 3 — Qwen-Image 2.1: new modality + new runtime

7B DiT image generation. This is the one line in the plan with no CircleAI answer.

**Verified gaps:** `ModelModality` has no image-generation value (Chat, Asr, Tts, Vad,
WakeWord, Vision, Music, Video, Coding, Phonemizer, SpeakerEmbedding, Embedding), and
`grep` finds **no diffusion/unet/vae binding** in the native bridge at all.

**Steps**
1. Add `ModelModality.ImageGeneration` — **APPENDED LAST**. The enum's own comment is
   explicit: values are persisted, so inserting mid-enum renumbers every later entry and
   silently re-labels the catalogue.
2. Decide the honest fallback story. `Music` and `Video` each ship a managed built-in, so
   their absence is `HeuristicFallback` not `Unavailable`. Image generation has no
   procedural stand-in — so it is `Unavailable` until a model exists, like `Coding`.
3. Native: expose MNN-Diffusion through `mnnbridge` (new entry points — none exist).
   This is C++ work and a per-ABI build, not a C# wrapper.
4. Managed: an `IImageGenerator` seam + generator, mirroring `IChatGenerator`.
5. Catalogue the model with real sizes; gate hard by tier (7B DiT is not a phone job).
6. Prove end to end on a desk target.

**Scope honesty.** Items 1 and 2 are days. This is the largest of the three because it
crosses the native boundary, and it is the only one that can break the existing MNN build.

---

## Order, and why

1. **Vision** — everything exists; it is the only one that converts to working software
   without new runtime or conversion, and today's mmap fix is what unblocks it.
2. **Bonsai 2** — conversion spike FIRST (a day), because it may be impossible; better to
   learn that before planning around it.
3. **Qwen-Image** — largest, native, last. Start it only once vision is confirmed, because
   vision proves the multimodal seam that image generation will sit beside.

## What is deliberately NOT promised
- That Qwen2.5-VL-3B fits a P30. It may not; SmolVLM is the fallback answer.
- That Bonsai 2 converts. Unverified, and ternary is the risk.
- Any image generation on a phone.
