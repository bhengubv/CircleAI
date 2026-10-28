# CircleAI — Vision & Roadmap

> **Updated 2026-10-22, current release 3.9.1.** This is now the one roadmap. It also holds
> what was in `TODO.md`, `docs/OPEN-GAPS.md`, `docs/P30_LITE_ROADMAP_STATUS.md` and
> `docs/STRATEGY.md`, merged word for word below, and the engine decision of 2026-10-22.
> Each part was last changed on the date shown. Anything dated before 3.9.1 may be out of
> date: it has not been re-checked against the code in this merge.

| Part | Last changed |
|---|---|
| Vision & Roadmap (this part) | 2026-07-24 |
| Our own polyglot engine | 2026-10-22 |
| Circle AI — TODO | 2026-06-20 |
| Open gaps | 2026-10-01 |
| Roadmap × Huawei P30 Lite — 67-item on-device status | 2026-07-25 |
| Circle AI — Strategic Architecture | 2026-05-28 |

> **The thesis.** CircleAI is not a local ChatGPT. It is an on-device **organism**
> that *makes* artifacts and *takes* actions — offline, de-Googled, on the phone
> people already own. Frontier AI **answers**; CircleAI **produces and does**.
> That is a category a data-centre product cannot follow us into.

We do not compete feature-for-feature with GPT/Claude/Gemini — against a 0.6B
model that is a losing scoreboard. We compete on being the thing that still
works when the network doesn't, on a R2,500 phone, with no account and no data
trail, and that hands you a finished CV / advert / invoice at the end.

---

## The organism

The brain/body framing is the organising principle, not a metaphor we drop after
the intro. Each part is a real subsystem; each capability is a **specialist
behind a tool call**, orchestrated by the Neuron we already proved on the Huawei.

| Part | Role | In code | Status |
|---|---|---|---|
| **Brain** | reason + orchestrate | `NeuronNode` + concierge router + 2-slot residency | ✅ proven on Huawei |
| **Nervous system** | route each turn | `HeuristicNeuronRouter` → generalist / specialist | ✅ proven |
| **Memory** | remember across turns | episodic + RAG + knowledge graph | ✅ proven |
| **Metabolism** | use what the body supports | `DeviceAwareModelSelector` — floor-to-ceiling ladder | ✅ chat; extend to all modalities |
| **Hands (call tools)** | act in the world | tool calling | ✅ proven (battery, SKU price) |
| **Ears + mouth** | hear + speak | Whisper ASR + Piper TTS + `VoiceLoop` | 🟡 built, **never run on a phone** |
| **Eyes** | see images | `KimiVlGenerator` | 🟠 runtime built, **no model catalogued** |
| **Hands (make artifacts)** | CVs, invoices, media | — | ⬜ **the new work** |
| **Immune system** | defend, always-on | Watcher / ipblocklist / intercept | ⬜ not built |
| **Between bodies** | borrow a stronger peer's brain | AetherNet mesh offload | 🟠 contracts only (transport stubbed) |

Legend: ✅ built & proven · 🟡 built, unproven on device · 🟠 partial · ⬜ not built

---

## Principles (these shape every decision)

1. **Offline-first. No cloud, ever.** Tariffs closed that door. A weak phone
   scales via the mesh (peer offload), never via a server.
2. **De-Googled.** No GMS, no Play Services, no Google resolvers. Open stack only.
3. **Floor-to-ceiling ladder.** Build for the P30 Lite; the *same code* scales up
   on a Pixel/Samsung because the selector picks the best model the hardware can
   hold. "Dynamic — not all devices are the Huawei" lives in the selector.
4. **Existing ≠ best.** Audit before building to avoid duplicating *wiring* — but
   never settle for what happens to be there. Catalogue a **ladder of rungs**, not
   a single default.
5. **Programmatic media, not generative.** We render templates / HTML / TTS. We do
   not ship a 10 GB diffusion model. "Make a 30s advert from your content" — yes.
   "Dream up a novel image" — no.
6. **Prove on the Huawei before we claim it.** No claimed-vs-delivered gaps.
7. **One vertical fully, then breadth.** Land CVs end-to-end before scaffolding
   ten repos on an unproven base.
8. **Built-in protection is a shipping gate.** Security is defensive by purpose,
   end to end — it exists to shield the user, never to attack. A user must never
   be in the wild unprotected, so the defensive immune system (Phase 3 core) is a
   baseline every *shipped* build carries, not an optional later phase. We can
   prove verticals internally without it; we do not put the app in real users'
   hands without it.

---

## Phase 0 — Close the two open claims *(before anything new)*

We already ship two unproven claims. Close them first; they are the exact
claimed-vs-delivered trap.

- [ ] **Voice on the phone.** Build the Android voice head (`-p:ItVoiceOnAndroid=true`),
      run the wake → ASR → brain → TTS loop on the Huawei, prove it hears and speaks.
- [ ] **Vision has a model.** Fetch a real SHA-256 for an on-device VLM, catalogue it
      as `ModelModality.Vision`, exercise `KimiVlGenerator` on the phone with a photo.

---

## Phase 1 — Documents (the confirmed floor: CVs first)

Highest value to the actual user (no bundle, needs a job), lowest technical risk
(deterministic templating, no big model, trivially offline). The model writes the
content; the template does the layout. Everything after reuses this path.

- [ ] On-device document engine: content (model) → template → **PDF**, fully offline.
- [ ] **CV generator** — the minimum. Tailors bullets to a target role.
- [ ] Cover letters (same engine, different template).
- [ ] Invoices (ties into the ecosystem: BidBaas / SDPKT / LedgerAPI).
- [ ] Reports.
- [ ] Prove each on the Huawei — a PDF you can actually open and send.

**Sources / inspiration:** `career-ops`, `presenton`.
**Open decision:** is v1 a **document CV** (PDF) only, or does the **video CV**
(Phase 2) ship alongside it? Recommendation: document-first, video as fast-follow.

---

## Phase 2 — Media generation (the all-rounder)

Programmatic, not generative. `html-video` is the bet: MAUI already renders HTML,
so a 30s marketing clip or a **video CV** = template + the user's photos + a TTS
voiceover. Achievable offline, needs no giant model.

- [ ] **HTML → video / stills** renderer (30s social clips, video CVs).
- [ ] **TTS ladder** — evaluate `LUXTTS` / `voicebox` / `patter` / `speakr` as
      *rungs above Piper*, on real criteria: on-device quality, size, speed, and
      features Piper lacks (voice cloning, emotion, expressiveness, languages).
      Best rung that fits the device wins. **Audit against the 5 existing voice
      stacks first — do not build a 6th.**
- [ ] Data → graph / charts for reports & decks (`understand-anything`).
- [ ] Presentations (`presenton`).
- [ ] ASCII / stylised stills (`ASCILINE`).
- [ ] Music beds for clips (`suvmusic`) — hardest; on-device small model, tiered.

**Sources:** `html-video`, `ASCILINE`, `understand-anything`, `presenton`,
`suvmusic`, and the TTS cluster (`speakr`, `voicebox`, `patter`, `LUXTTS`).

---

## Phase 3 — Immune system (built-in security)

Autonomic and always-on — not a tool the user launches. **Defensive by purpose,
end to end** — every part exists to shield the user, never to attack. The
defensive core is a **pre-launch baseline** (Principle 8): no user ships into the
wild without it. The "antibodies" are defensive threat-*awareness* — warn and
protect — produced only under a defined threat, never bundled loose.

- [ ] **Reflexes (defensive, always-on):** on-device threat monitor + network
      defence. Pairs with Panik / Nope SOS. Sources: `Watcher`, `ipblocklist`,
      `intercept`, `shizuwall`.
- [ ] **Antibodies (offensive, gated by authorized-use boundary):** OSINT /
      threat-intel, invoked deliberately, never by default. Sources: `malwoverview`,
      `findme`, `deepdarkCTI`, `ghost-osint-crm`, `hacktricks`, `neko-master`.
      **Requires a written authorized-use boundary before it enters the product.**

---

## Phase 4 — Business operations (the operator)

CircleAI doesn't just make the invoice — it runs the small business: invoicing,
follow-ups, scheduling. Ties into the ecosystem economics.

- [ ] Automated operations engine. Sources: `gstack`, `automaton`, `dexter`,
      `career-ops`.

---

## Phase 5 — Code from mobile

Hardware-tiered, never cloud. A Pixel/Samsung (8–12 GB) holds a real 3–7B coding
model *locally* → the feature is genuinely present on capable phones and reports
*Unavailable* on a P30 Lite (exactly what the selector already expresses). Weak
phones get it later via mesh offload.

- [ ] On-device coding agent for capable phones. Sources: `PhonesHarness`,
      `adb-device-manager-2`, `dexter`.
- [ ] Mesh path for weak phones — **blocked on** AetherNet RT-12 (transport stubbed).

---

## Cross-cutting workstreams

- [ ] **Extend the selector ladder to every modality.** `PlanFor` already answers
      per-device for speech/vision; every new capability (documents, media, coding)
      reports the same way, so a Pixel gets the ceiling and a P30 the floor with no
      code change.
- [ ] **Mesh inference-offload** (AetherNet RT-12). The decentralised
      cloud-replacement. Contracts exist; the broadcast transport is stubbed.
- [ ] **Output surfaces beyond the phone** — e.g. cast to TV (`awesome-smart-tv`).

---

## How we work

- Prove it on the Huawei before it's "done." A screencap proves it *rendered*,
  not that it *works*.
- Audit the corpus (github.com/bhengubv forks) before building — a fork usually
  encodes the intended approach.
- Serialize heavy builds. Local test + manual deploy. `[skip ci]` on master.

---

## Our own polyglot engine

*Decided 2026-10-22: make Circle AI its own polyglot engine, no third-party engines.*

What it means:

* Circle AI does all the model maths itself, in our own code, in our repo, under MIT.
* MNN, llama.cpp, whisper.cpp (which we use through Whisper.net) and every other third-party engine in the build come out.
* Polyglot, as I read it: the one engine runs every kind of model Circle AI uses: the language model, speech-to-text, text-to-speech and vision. It also reads every model file format we use today.
* We write the hard parts ourselves: loading the model files, the matrix maths tuned for each processor, and the steps that turn a prompt into an answer.
* It runs everywhere Circle AI runs today.
* The bar stays the same: Qwen3.5 on the P30, at least as fast as MNN runs it now, and the 28 recorded questions still pass 28/28.

One thing to confirm: SQLite and the operating systems’ own image decoders aren’t AI engines. I’ll leave them as they are unless you say they count.

---

## Circle AI — TODO

*Merged here from `TODO.md` on 2026-10-22. That file was last changed 2026-06-20.*

Living list of open work. The full version history is in
[CHANGELOG.md](CHANGELOG.md); this file tracks what's still ahead.

> **Working rule:** finish, don't remove. If a feature is half-built,
> finish it or mark it `[Experimental("CIRCLEAI_*")]` honestly.

Last reconciled: **2026-06-18** (current line: `3.0.1`).

---

### Open — native runtime work (2.1.1 line)

Deferred from 2.1.0 because they all need native `mnnbridge` cross-builds
across 8 RIDs and the C++ patches haven't been written yet. Targeting
the `2.1.1` ship.

- [ ] **RT-01 Tiered KV cache (FP16 recent / TQ3 mid / TQ2 cold)** —
      native. The biggest single payoff for long-context phones. Needs a
      new `mnn_llm_set_kv_compression_policy(handle, recent_fp16_tokens,
      mid_mode, cold_mode)` ABI entry, MNN attention path takes a
      per-token mode tensor instead of a global mode. Likely a vendored
      patch under `native/mnn-bridge/patches/`.
- [ ] **RT-03 Memory-mapped weight loading** — native. Patches
      `MNN::Express::Module::load` (or a local fork) to `mmap` weight
      blobs instead of `fread` into RAM. Combined with RT-01 this puts
      3 B models on 2 GB phones.
- [ ] **RT-05 Speculative decoding** — native. New
      `mnn_llm_speculative_decode_stream` taking both a draft and target
      handle. Draft model variants curated per Qwen3 catalog entry
      (Qwen3-0.1B-Draft, etc.).
- [ ] **RT-14 AirLLM layer-wise streaming inference** —
      native. Lets a sub-2 GB phone run a 7 B model by streaming layers
      from disk on demand. Pattern-port of `airllm`.
- [ ] **2.1.1 shard pattern-port** — alternative KV-compression codec
      port. Investigative; lands in 2.1.1 only if it beats TurboQuant on
      real workloads.

---

### Open — runtime features (2.x backlog)

- [ ] **RT-10 On-device LoRA personalisation** — native + managed. Forward
      + backward + Adam-W optimiser in `mnnbridge` so a host can
      fine-tune a small LoRA adapter on the device's own conversation
      history. Contract surface (`IPersonalLoRA`) already shipped in
      `CircleAI.Domain` 3.0.1 — just needs a real backend.
- [ ] **RT-12 v3 Transparent mesh offload over AetherNet** — currently
      v2 is shipped (`ICrossTierOffload` in `CircleAI.AetherNet`). v3 is
      transparent automatic offload — host doesn't decide, the runtime
      detects a faster peer with available KV and routes the next token
      stream there with no API change. Needs an AetherNet protocol bump
      to carry KV-fragment payloads.

---

### Open — catalog work (2.1.2 line)

- [ ] **2.1.2 catalog refresh** — add Qwen3-Coder, DeepSeek-Coder-V2-Lite,
      and Qwen3-Draft (the speculative-decoding draft variants for the
      0.6 / 1.7 / 4 / 7 / 14 B line) once their MNN bundles ship on
      ModelScope. Catalog-only — no SDK release required (this is the
      whole point of "NuGet sleeps"), just regenerate
      `embedded_registry.json` via `tools/recalibrate-registry-sha`.

---

### Open — metadata cleanup (description drift)

The 3.0.1 ship was meant to normalise every `<Description>` prefix to
`(3.0.1)`. At least 8 csprojs still talk about earlier versions in their
description, plus ~10 csprojs have empty descriptions. NuGet listing
pages render these literally.

- [ ] **Normalise drifted descriptions** — `CircleAI.Core`,
      `CircleAI.Inference`, `CircleAI.Hosting`,
      `CircleAI.Hosting.InferenceBridge`, `CircleAI.Inference.Server`,
      `CircleAI.Skills`, `CircleAI.Embeddings.Local` — every one talks
      about 2.0.2 / 2.0.3 in its description.
- [ ] **Fill empty descriptions** — `CircleAI.AetherNet` (3.0.1),
      `CircleAI.Aether` (1.3.0), `CircleAI.Networking.AetherNet` (1.0.0),
      `CircleAI.Security` (1.2.0), `CircleAI.Security.AetherNet` (1.1.0),
      `CircleAI.Agents.Peer` (1.4.0), `CircleAI.Companion` (1.2.0),
      `CircleAI.Desktop`, `CircleAI.Web`, `CircleAI.Ambient` (all 1.2.0).
- [ ] **Stale code comment** in
      `src/CircleAI.ContentPolicy/Contracts.cs` — opening doc-comment
      still says *"Namespace `CircleAI.Guardrails` to avoid collision"*
      but the actual namespace is now `CircleAI.ContentPolicy` (renamed
      in 3.0.1).
- [ ] **`Directory.Build.props` default version** still reads
      `<Version>1.0.0</Version>`. Every csproj overrides it, so it's
      harmless in practice — but it's a confusing default for a 3.0.1
      codebase. Bump to `3.0.1`.

---

### Open — documentation backlog

- [ ] **`docs/quickstart/`** — directory referenced from older builds
      but currently empty / not present. Either populate with 10
      per-language quickstart files (one per portable-kernel port) or
      keep the cross-language spec at `docs/CONTRACTS.md` as the single
      landing.
- [ ] **`docs/i18n/`** — translated docs in 10 languages (ar, de, es,
      fa, fr, ja, ko, pt-BR, ru, zh-CN). Probably out of date relative
      to the 3.0 line; needs a translator pass once the English docs
      stabilise.

---

### Open — implementation work behind 3.0 contracts

The 3.0.1 line shipped **42 contract packages** with fail-closed Null
implementations. Real backends land in dot releases. Tracking them as
one bucket because they're independent and can ship out of order.

- [ ] **Vision backends (2.2.1)** — vendor compv, facex,
      FaceLivenessDetection-SDK, KYC-Documents-Verif-SDK,
      ultimateALPR-SDK, Bluehound under `native/<sdk>/`.
- [ ] **Speech backends (2.3.1)** — FunASR (ASR), ChatTTS, hey-snips
      (wake word), PaddleOCR.
- [ ] **Spatial backends (2.5.1)** — deck.gl tile source, RADAR readout,
      skylight tracker, flame 3D scene renderer.
- [ ] **Inputs backends (2.5.1)** — ConvertX scraper, Scrapling stealth
      HTTP, openvid video ingest, mcp-web-scrape, ASCILINE terminal
      cast.
- [ ] **Tools.Catalog backends (2.5.1)** — composio-pattern provider
      directory with built-in Gmail / Slack / GitHub / Drive / Discord
      connectors + an optional Composio adapter package.
- [ ] **Observer backends (2.6.1)** — sensors (camera/mic/GPS/IMU) +
      tool registry + the perceive-reason-act loop wired against the
      companion runtime.
- [ ] **ContentPolicy backends (2.6.1)** — Sponsio refusal model,
      prompt-injection detector, audit log.
- [ ] **ModelAlignment backends (2.6.1)** — OBLITERATUS abliteration
      toolkit + the alignment auditor that refuses to publish modified
      weights upstream.
- [ ] **Banking / Markets / Pipelines / Workflows / Visualization /
      Collaboration / CRM backends (2.8.1)** — vendor OBP-API,
      fineract, hyperswitch, OpenBB, StockSharp, etl, airbyte,
      restate, automatisch, paca, superset, mattermost, twenty.
- [ ] **BuildFarm / DepBot / DocAnalytics / Testing / Distribution /
      MediaHub / WindowsAutomation / MicroAgents backends (2.9.1)** —
      OSX-KVM, renovate, papermark, Verify, FileSync-over-AetherNet,
      pms-docker-plex, beatsync, mcp-windows-automation, picoclaw.
- [ ] **DevTools backends (3.0.x)** — the cornerstone of 3.0. Real
      `ICodeEditor`, `IInlineSuggester`, `IAgentShell`, `IPatchPlanner`,
      `IRefactorTool` implementations. This is the Western-replacement
      umbrella; the contracts are the substrate, the implementations
      are the actual IDE.
- [ ] **Research / Games / AutonomousBiz / CodeUnderstanding backends
      (3.0.x)** — arxiv, the_well, flame, Doom.Mobile,
      show-me-the-money, Understand-Anything.

---

### Done — recent line ships

See [CHANGELOG.md](CHANGELOG.md) for the full release-by-release log.
Headline shipped milestones across the 2.x → 3.0.1 line:

- **Phase 0 architectural shift** — ModelScopeCatalogClient,
  PromptTemplateEngine (Scriban / Jinja2), DefaultDeviceContext,
  IModelSelector.BestFit, AIService auto-resolution. NuGet now sleeps
  for new models (1.7.0 + 2.0.0).
- **Phase 1 device-derived defaults** — context window, concurrency,
  agentic limits, KV-compression mode all derived from device tier
  (2.0.0).
- **Phase 2 hosted server + RAG** — `CircleAI.Inference.Server` with
  OpenAI-compatible endpoints + JWT/API-key auth + Docker/systemd/Windows
  service ship paths + `CircleAI.Embeddings.Local` with HNSW backend
  and TurboQuant-compressed vectors (2.0.0 → 2.1.0).
- **Phase 3 multi-model lifecycle** — `ModelLifecycleManager`, brownout
  hot-swap, snapshot/restore, prefix cache, predictive warmup, fallback
  chain, power-budget API (RT-02 / RT-04 / RT-06 / RT-07 / RT-08 /
  RT-11; 1.7.0 → 2.1.0).
- **Phase 4 native cross-build** — `mnnbridge` + MNN libs cross-built
  for `win-x64`, `linux-x64`, `linux-arm64`, `osx-arm64`, `osx-x64`,
  `android-arm`, `android-arm64`, `ios-arm64` (1.3.x → 1.6.0).
- **Vision (2.2.0)** — `CircleAI.Vision` contract surface.
- **Speech (2.3.0)** — `CircleAI.Speech` contract surface.
- **Domain consolidation (2.4.0)** — nine plug-points (MemPalace,
  HippoRAG, Swarm, Identity.LoRA, Food, Finance, FinancialAgent,
  Presentations, JobSearch) consolidated into `CircleAI.Domain`.
- **Spatial / Tools.Catalog / Inputs (2.5.0)** — three pillar packages.
- **Observer / Safety / ModelAlignment (2.6.0)** — three pillar
  packages; renamed at ship-time to `CircleAI.ContentPolicy` +
  `CircleAI.ModelAlignment` to avoid v1.2.0 lifestyle-adapter collisions.
- **Enterprise tier (2.7.0)** — `CircleAI.Inference.Server.Enterprise`,
  `CircleAI.Observability`, `CircleAI.Operator`, `CircleAI.SDD` +
  RT-12 v2 cross-tier offload.
- **Business apps (2.8.0)** — Banking, Markets, Pipelines, Workflows,
  Visualization, Collaboration, CRM contract surfaces.
- **DevOps (2.9.0)** — BuildFarm, DepBot, DocAnalytics, Testing,
  Distribution, MediaHub, WindowsAutomation, MicroAgents.
- **3.0 strategic cornerstones (3.0.0)** — Research, Games,
  AutonomousBiz, CodeUnderstanding, and the cornerstone
  `CircleAI.DevTools` — the Western-replacement umbrella.
- **3.0.1 cleanup** — renamed `MediaServer → MediaHub` and
  `Guardrails → ContentPolicy`; description-prefix drift normalised
  across most csprojs (8 still need cleanup, see above).

---

### Notes

- Branch hygiene: never push to `master` without authorisation. Every
  commit on a master-bound branch carries `[skip ci]`. See
  [no-ci-until-redesigned.md](../.claude-memory/no-ci-until-redesigned.md).
- Credentials in `Deployment/deployment-credentials.ps1` are owned by
  the user. Never modify, rename, or refactor — ask if something looks
  wrong. See
  [feedback_never_touch_credentials.md](../.claude-memory/feedback_never_touch_credentials.md).
- License hygiene: vendoring requires Apache 2.0 / MIT / BSD-3. AGPL
  or no-license upstreams → pattern-port (study architecture, write
  fresh under Apache 2.0).

---

## Open gaps

*Merged here from `docs/OPEN-GAPS.md` on 2026-10-22. That file was last changed 2026-10-01.*

What is built and unreachable, what is genuinely blocked, and what has never run
on a phone. Every line here was **verified by grep or by a run**, not assumed.

Read this before claiming any capability is done. `csharp-is-core-signoff-language`
says "done" means C# runs **on the device** — code that compiles and is tested
is not done, it is ready.

Last verified: **2026-09-11**, against `main`.

**Closed this pass:** A1, A2, A4, A5, A6 (procedural), A′1, A′3, A′4, A′5, A′6, A‴, C2.
**Found and fixed in passing:** E1–E20 — **E10 and E11 were found by a PHONE**, not by 3271 green tests — **E8 was mine**, introduced and caught in this same pass.
**Found and NOT fixed:** A7 and C3 — both recorded rather than closed, and the rows say why.

**Still open, and what each is actually waiting on:**

| | Waiting on |
|---|---|
| A3 (diarisation) | **A licence call, not code.** `OnnxSpeakerIdentity` already implements `ISpeakerEmbedder`; a CC-BY-4.0 candidate exists. See the row. |
| A6 neural, A″ `Music` | **A catalogued model.** No permissively-licensed on-device music model has been found. |
| A7 (text embedding) | **A consumer.** The model gap closed itself in E6; nothing asks for an embedding. |
| B1–B3 | **The native bridge.** Not reachable from managed code. |
| C1, C3 | **A decision.** |
| **D — all of it** | **A phone.** This is the honest headline of the pass: *everything above was closed on a desktop.* |

**D is not a footnote.** `csharp-is-core-signoff-language` says "done" means C# runs on the device, and by that bar **nothing here is done** — five new screens, a transcript store and a live catalogue refresh have never been installed once. A capability leaves section A when a head calls it; it leaves section D when a P30 runs it. Only the first of those happened.

---

### A. Reachable only from a test — the product can do it, no head calls it

This is the repo's most common defect by a wide margin. Every item in this
section was written, tested, committed, and then had no way in from the app.

| # | Capability | State |
|---|---|---|
| A1 | **Transcribe a file** | **CLOSED on the deployed head.** `IConversation.TranscribeFileAsync` over `Transcribing.FileAsync`, and `TranscribeActivity` on the native Android head — a picker for audio *and video* (the audio people want written down is often inside a video), progress, and `AndroidAudioDecoder` so a `.m4a` voice memo works. `CircleAIListener.HearAsync` no longer carries its own WAV reader — that was the **third** copy of one. `ModelModality.Asr` had also been a download to nowhere; **Listening now leads somewhere.** The Hybrid head's `Transcribe.razor` now opens a recording too, via `InputFile` spooled to a path. |
| A2 | **Subtitles** | **CLOSED on the deployed head.** `IConversation.AsSubtitles` renders SRT/WebVTT, and `TranscribeActivity` saves an `.srt` through `ActionCreateDocument` — the person chooses where, and the app needs no storage permission. |
| A3 | **Diarisation** | **The C# is complete; the blocker is one catalogue entry and a licence call.** `Diarisation` is written and tested, and `OnnxSpeakerIdentity` **already implements `ISpeakerEmbedder`** - so nothing is missing in code. `ModelModality.SpeakerEmbedding` is still empty. The model contract is known exactly from the consumer: ONNX, 16 kHz, 80-bin Kaldi fbank, one input and one output - the WeSpeaker / 3D-Speaker shape. **Four candidates were checked against the real Hugging Face API, not assumed:** `csukuangfj/speaker-embedding-models` states **no licence** (refused by this product's own rule - unstated is not permissive), `Revai/reverb-diarization-v1` is `other` and **gated**, and both `pyannote/wespeaker-voxceleb-resnet34-LM` and the `onnx-community` conversion of it are **CC-BY-4.0**, which the allowlist admits. So there IS a shippable candidate. What stops it being catalogued here is that the weights are VoxCeleb-trained and the dataset terms are a separate question from the model licence - **that is a call about what the product ships, not a coding gap**, and `fully-free-opensource-always` says licence FIRST. Left open deliberately, with the work done. |
| A4 | **Translation** | **FULLY CLOSED.** `IConversation.TranslateAsync` for the web head and `CircleAISession.TranslateAsync` for the native one, which holds a session rather than an `IConversation` and so had no door at all. `TranslateActivity` is the screen: two halves with the far one rotated 180°, because the phone goes on the table between two people. The engine owns the prompt — the screen and the engine had been two owners of it, and the clause that stops the model ANSWERING "where is the toilet" instead of translating it must live in one place. A translation goes through `StreamAsync`, **not** `ChatAsync`, because `ChatAsync` writes an episode and translation scaffolding in episodic memory degrades the recall that memory exists for. |
| A5 | **Search across everything** | **CLOSED on the deployed head.** The old note here named the blocker correctly - `LexicalIndex` + `Bm25` ranked text fine and had **two of three sources missing**, so "across everything" meant "across memory", which has a better search of its own. **A transcript was shown, optionally written out as an `.srt` to a location the person chose, and then discarded**, so somebody who recorded a clinic appointment could not find it an hour later. `IKeepsTranscripts` + `FileTranscriptStore` are the store, `CircleAISession.SearchAsync` runs both searches, and `SearchActivity` is the screen. **Where transcripts live was the privacy decision, and it is `FilesDir`** - app-private, not on the shared card, not readable by another app, gone on uninstall; nothing syncs it and forgetting one deletes it. Memory goes through its own recall (corrections, staleness, wear); transcripts go through BM25, which needs **no model**, so search works on a handset with nothing downloaded. The rows say which source each result came from, because "you told me this" and "a microphone caught this" are different kinds of answer. A head that cannot write files (WASM) gets `KeepsNoTranscripts`, which **declines rather than appearing to save**. **AND THE WEB HEAD IS STILL WHERE THE NATIVE ONE WAS.** `IConversation` - the shared contract the Blazor heads use - has `TranscribeFileAsync`, `TranslateAsync` and `AsSubtitles` and **no search and no transcript keeping**, so `Pages/Transcribe.razor` still shows a transcript and discards it, and there is no `Search.razor` at all. That is deliberate rather than forgotten: a browser tab has no app-private folder, so closing it properly means an IndexedDB store behind `IKeepsTranscripts` and a page for it - and half of it (search with nothing to search) would reproduce exactly the defect this row was opened for. **Recorded, not built.** |
| A6 | **Music generation** | **Procedural path CLOSED.** `MusicActivity` on the native head: nine moods, thirty seconds, plays it and saves a WAV through the system picker. It is the only capability that works on a phone with **nothing installed** — arithmetic, not a download — which needed a `NeedsNoModel` flag on the abilities row, because that list decides a row's state by whether a MODEL is present and would have rendered the one always-available capability as unavailable. **The NEURAL path is still an empty seam**: no `Music` model is catalogued, and a procedural chord bed is a bed, not music generation. The screen says bed. |
| A7 | **On-device text embedding** | **Built, tested, zero callers - and now it has a model too.** `TextEmbedder` is a complete MNN-backed embedder with L2 normalisation, lazy load and a serialised init; `grep` finds it constructed in `Fakes.cs`, `TextEmbedderTests.cs` and `GapCoverageTests.cs` and **nowhere in `src`, `samples` or `tools`**. `ModelModality.Embedding` has the same count of non-test references: **zero**. The A″ row below said the blocker was a catalogued model, and the E6 fix supplies two (`bge-large-zh-MNN` and `gte_sentence-embedding_multilingual-base-MNN`, both Apache-2.0, both live) - **so the blocker was never the model.** It is that nothing asks for an embedding. The obvious consumer is a semantic leg on `SearchAsync` beside BM25, and that is a real decision rather than a wiring job: it costs a 600 MB-1.3 GB download for a second opinion on a search that already works with nothing installed, and memory - the other source - already ranks itself. **Not closed, and deliberately so.** |
| A8 | **Two engines with no model and no caller** | Found by sweeping `CircleAI.Voice` for types with zero references outside their own file. `KokoroTtsEngine` and `OnnxSpeechEmotionDetector` each have **none** in `src`, `samples` or `tools`, and **nothing is catalogued that either could load** - every one of the 69 voice entries is `vits` or `vits-espnet`, which `OnnxTtsEngine` handles, and there is no emotion model at all. **Less serious than the rest of this section, and worth saying why:** A1, A4 and A5 were capabilities a SCREEN OFFERED with nothing behind them, which costs somebody a download and a dead end. These two are promised nowhere, so they cost only the reading. Listed so the next sweep does not have to rediscover them, and because "no caller" is this repo's signature defect and the register should hold all of it, not just the expensive half. |

### A′. Two owners of one fact — found while closing the above

| # | Fact | The disagreement |
|---|---|---|
| A′1 | ~~Which languages exist~~ | **CLOSED.** The two tables now hold the same 78 languages with an empty symmetric difference and zero name disagreements. `KnownLanguages` had **20**; the 58 it lacked were **sourced, not typed** — script from the Unicode of each language's own native name, direction from `CultureInfo.TextInfo.IsRightToLeft` (ICU/CLDR), region from ICU likely-subtags, with the nine ICU had no answer for put back to ICU as region-qualified cultures and kept only where ICU confirmed them. **The derivation was checked against the 20 hand-written rows before it was trusted: script 20/20, right-to-left 20/20.** `WritingSystem` also gained 12 scripts — Bengali, Gujarati, Gurmukhi, Hangul, Japanese, Kannada, Malayalam, Myanmar, Sinhala, Tamil, Telugu, Thai — because 12 languages the app offers were collapsing to `Other`. They remain two TYPES (`CircleAI.Assistant` has zero ProjectReferences so a WASM head can load it), but they no longer disagree. |
| A′2 | **Where WAV is read** | Was three: `WavIo`, `tools/stt-hear`, and `CircleAIListener`. Now one (`WavIo`). `BrowserSubtitles` is a deliberate second owner of the subtitle formats — a WASM head cannot load `CircleAI.Voice` — and `SubtitleParityTests` asserts the two produce byte-identical output. |
| A′3 | **Where `Capabilities` lives** | The generated `Capabilities.cs` moved to `src/CircleAI.Assistant/` during the rename and kept `namespace CircleAI.Samples.Console;`. It resolved only for code whose own namespace walked up into that one — `Services.razor` did, the bUnit project did not — so **`tests/CircleAI.Samples.Ui.Tests` stopped compiling and its 308 tests stopped running** (308 was the count THEN; it is 390 as of 2026-10-28), silently, because no other command builds it. Generator fixed, namespace is now `CircleAI.Assistant`, 308 passing. `CLAUDE.md` now names both test projects. |

| A′4 | **Where the model list comes from** | The live catalogue existed and nothing called it. `ModelScopeCatalogClient` fetches, caches, checks a signature and honours a cadence; `ModelRegistryService` takes one; `AIOptions.CatalogClient` and the DI wiring read it — and **nothing ever set it, nothing ever called `PrimeFromCatalogAsync`**, and ~20 sites bypass DI with `new ModelRegistryService()` so fixing the first two would still not have reached the app. **`ModelCatalogue` is the missing caller**; the fetched catalogue is now process-wide so one refresh reaches instances built afterwards. Wired into `CircleAISession` startup, fire-and-forget, daily cadence, never throws. |
| A′5 | **A refresh would have emptied the catalogue** | `AllModels` was `(_remoteRegistry ?? _embeddedRegistry)` — a **wholesale replacement** — and the two catalogues are disjoint in both name and host. Measured: **0 of the 88 shipped entries are sourced from ModelScope** (1 Asr, 14 Chat and 2 Vision on HuggingFace; 64 Tts on HuggingFaceBucket; 5 Tts and 1 Phonemizer on GitHubRelease; 1 WakeWord on HuggingFaceBucket). The live query asks ModelScope for MNN bundles, so **the first successful refresh would have replaced the entire catalogue with a disjoint set** — losing the voice, the ears, the wake word, the phonemiser dictionary *and* the curated chat ladder — and flattened model choice from "best that fits" to "smallest that fits", because the live client never sets `QualityRank`. `CatalogueMerge` makes curated the spine; a refresh can only ADD. Pinned by `CatalogueMergeTests` and `ModelCatalogueTests`. |
| A′6 | **The live fetch had never worked, twice over** | Proven against the real API on 2026-09-11, not inferred. `GET /api/v1/models?Name=MNN` answers **404** and always has; the working call is `PUT /api/v1/dolphin/models` with a JSON body. Independently, the parser read `Data.Models` where the API returns `Data.Model.Models` — one level deeper — and a missing property there is a `yield break`, so even with the URL corrected it would have fetched 519 models and catalogued **zero**, with no exception and nothing logged. Both fixed; `CatalogueListingTests` pins the shape against a captured real response. **Live run: 25 models fetched in 25 s, 18 new, and all 69 voices / 1 ASR / 1 wake word still present.** |
| A′7 | **How many test projects there are** | **CLAUDE.md said TWO. There are SEVENTEEN** (re-counted 2026-10-28; `CircleAI.DeviceTests` landed 2026-10-08 and nothing counted it, so the figure that was written to end this confusion joined it), **and the other fourteen hold 527 tests that this session never ran while reporting "3271 passed" as though it were the suite. **This is A′3 again, in the register that documents A′3.** That row records 308 bUnit tests silently not running because nothing built their project, found by accident; this was found by accident too, in the stale output of an abandoned command. **AND THE CORRECT INSTRUCTION ALREADY EXISTED.** `docs/BUILD.md` says `dotnet test CircleAI.sln` - "Everything at once - recommended" - so the repo knew. CLAUDE.md, the file an agent is told to read FIRST, said something narrower, and the narrower one won because it looked authoritative. **Three owners, three answers:** `CircleAI.sln` carries 186 projects and 15 test projects; `CircleAI.slnx` carries **15 and 2** (counted 2026-10-28, and both files were last written the same day — the `.slnx` is not stale, it is incomplete); and `CircleAI.Samples.Ui.Tests` is in **NEITHER** - nor is `CircleAI.DeviceTests`, added 2026-10-08 - which is precisely why its **390 tests** (RUN AND COUNTED 2026-10-28, all green in 15 s; this file said 308 and `CLAUDE.md` said 344, so the one project no solution builds was also the one nobody could count) could stop running unnoticed - no solution would have missed it if any solution contained it. A developer opening the `.slnx`, the newer format tooling increasingly prefers, sees two test projects out of seventeen and nothing tells them otherwise. |

### A″. One blocker, three capabilities: the catalogue has no model for the modality

Diarisation, search and neural music are not three separate problems. Each has
working managed code and **nothing catalogued to run it with** — and until this
pass, two of them had no modality to be catalogued *under*.

| Modality | Consumer | Catalogued |
|---|---|---|
| `SpeakerEmbedding` | `CircleAI.Voice.Diarisation` | **nothing** (modality added 2026-09-11) |
| `Embedding` | `CircleAI.Embeddings.TextEmbedder` (MNN-backed, real) | **two, live and Apache-2.0** - `bge-large-zh-MNN` and `gte_sentence-embedding_multilingual-base-MNN`, catalogable since E6. **And it made no difference**, because `TextEmbedder` has no caller - see A7. |
| `Music` | `CircleAI.Music` neural path | **nothing** (modality already existed) |

`ModelChoice.AnyCatalogued` already distinguishes "nothing exists yet" from
"nothing that runs on this phone", so an empty modality is a state the app can
describe rather than trip over.

**The next step for all three is the same**: source a permissively-licensed
model, pin its real SHA-256, and add it to the registry — `fully-free-opensource-always`
says licence FIRST, and `model-cataloguing-via-browser-pane` says a real hash,
not a guessed one.

### A‴. Voices: every offered language has one, and one of them could not speak

Audited 2026-09-11 against the shipped catalogue. **Both directions are clean.**

| Question | Answer |
|---|---|
| Languages the app offers | **78** |
| Voice entries catalogued | **69**, covering **78** tags |
| **Offered with no voice** | **ZERO** — `VoiceCoverageTests` keeps it that way |
| **Voices for languages never offered** | **ZERO.** Was 3 — Spanish, Dutch and Portuguese had six Piper voices catalogued (es_ES, es_MX, nl_BE, nl_NL, pt_BR, pt_PT) and were absent from the picker, so ~380 MB of voices were downloadable and unselectable. All three added; the change cost no new bytes and unlocked six voices. (Counts here read 69/72 at first — a case-sensitive regex dropped the six regional tags; the true figures were 75 then 78.) Portuguese is Angola and Mozambique. |

**And Japanese could not speak on the path that speaks.** `JSUT-VITS` is an ESPnet
VITS: it consumes Open JTalk phoneme ids, so it needs the 104 MB naist-jdic
dictionary, catalogued separately because every Japanese voice shares it.
`CircleAITtsProbe` fetched it; **`CircleAISpeaker` mentioned it nowhere**. So the
speaker downloaded 144 MB, asked `OpenJTalkPhonemizer.Open` for a phonemiser and
got `null` — the diagnostic screen could speak Japanese and the app could not,
and the error named a missing phonemiser rather than a missing download.

Fixed: the rule lives in `ModelPrerequisites`, both paths ask it, and it keys on
**architecture** rather than an entry-name prefix — which the probe's own comment
had asked for and could not have, because `ModelEntry` **dropped the registry's
`Architecture` field on deserialise**. It no longer does.

### B. Blocked or unbuilt — not a wiring job

| # | Item | State |
|---|---|---|
| B1 | **Grammar-constrained tool decoding** | **Blocked at the native layer.** Constraining the sampler so an invalid token cannot be emitted needs logit access; `mnnbridge` exposes generate-and-stream with no hook into sampling. Not reachable from managed code without changing the native bridge. `ToolCallReader` does the achievable part — accept every spelling of a correct intention, and turn a wrong call into a sentence the model can act on. |
| B2 | **On-device image generation** | Only `CircleAI.Vision.Cloud` exists (OpenAI, Stability). Nothing on-device. Needs a diffusion runtime; MNN supports Stable Diffusion but the bridge does not expose it. |
| B3 | **Photo → 3D** | `CircleAI.Spatial` is `Contracts` + `InMemorySpatial` + `NullImplementations`. Nothing real behind it. |
| B4 | **Vision generation returns -6 from the native bridge** | **THE MANAGED CHAIN IS FIXED AND THIS IS WHAT IS LEFT.** On a P30, versionCode 15: the image is scaled to 478x1024, `BestFit(Vision)` selects `SmolVLM-256M-Instruct-MNN`, the slot **admits** it, `KimiVlGenerator` is genuinely the generator, and MNN loads the bundle as a visual model - `"is_visual":true`, `image_size:512`, `vision_start:49189`, `image_pad:49190`. Then `mnn_llm_generate_with_image_stream_ex` returns **-6** and the screen says so: "That did not work: MNN vision generation failed with code -6." **That is an improvement worth naming** - the old behaviour was a TEXT model politely inventing a non-answer about a picture it never received, which is the worst failure mode available. Candidates for -6, none verified: the 478x1024 input against the model's own `image_size` of 512, the `mllm` CPU backend on this SoC, or memory. Diagnosing it means instrumenting `mnnbridge`, which is below the managed boundary - the same wall as B1. |
| E13 | **THREE model directories on one phone, and the fix that found them was mine breaking it** | The abilities screen derived its own answer - `AllModels.Where(Modality)` + `ModelExists` + a local `Fits` - so it used a different rule from `DeviceFacts`, paid **9.4 s per candidate** hashing a 470 MB anchor file to ask "is it there", and never learned `AbilityState.Ready` (it printed "✓ On" for a file on disk, the exact lie `Ready` was added to stop). Pointing it at the shared `ModelChoice`/`IDeviceFacts` answer turned **every row to "Turn on"** on a P30 that had not lost a byte, and home from "Tap and talk" to "Let's set it up". **The models were somewhere else - in three places.** A census printed on launch, rather than another argument from source:
<br>`Context.FilesDir` → `files` (ModelStore - empty on every device)
<br>`SpecialFolder.Personal` → `files/Documents` (CircleAISession, via ModelPaths.Default)
<br>`SpecialFolder.ApplicationData` → `files/.config` (nineteen sites in the native head, four of them spelling it **"Circle AI"** with a space)
<br>**`ModelPaths.cs` asserted Personal WAS the files directory.** It is not - it is `Documents` underneath it - and that false sentence is precisely why the original fix stopped halfway: a reader who believed it had no reason to check `ModelStore`, which was the third copy. Chat worked the whole time because the session fetched Qwen into `Documents` while the screen reported on `.config`; the two were reading different halves of a split library, and `.config` held **duplicate** copies of Qwen and SmolVLM - the double-download `ModelPaths` describes, now confirmed with real bytes rather than inferred. `ModelStore` now adopts from every legacy spelling into `ModelPaths.Default`, reclaims a duplicate only when the keeper is at least as large byte-for-byte (so a truncated download loses to the good copy, never the reverse), and prunes with a NON-recursive delete that refuses to remove anything still holding files. **858 MB came back on the P30.** **And the rule is testable now**: `ModelChoice` had no Android in it but sat in a `net10.0-android` library that no test project can reference, which is the entire reason the rule deciding what a phone downloads had zero tests and the native head could drift from it silently. It lives in `CircleAI.Inference`; the On/Ready/Available/TooBig decision lives in `CircleAI.Assistant` as `AbilityRules`. 36 tests, from none. |
| E14 | **1,378 skills reached the phone, and two defects between the store and the prompt did not show up in any test** | `ISkillStore` has been injected into the system prompt since the capability manifest landed, and the only thing ever wired to it was that manifest - another seam with no producer. `SqliteSkillStore` (FTS5 + bm25, LIKE fallback) now carries the library: **1,378 skills from 1,405 SKILL.md files** across 11 attributed packs, 5.1 MB in the APK, 20 MB unpacked once. **THE GENERATOR LOST 70 SKILLS SILENTLY ON ITS FIRST RUN.** `ImportAsync` keys on the skill's own slug and eleven repos independently ship `brand-guidelines`, `code-review`, `pdf` - the eleventh overwrote the other ten, five per cent of the library gone with nothing in the output saying so. Caught only because the tool prints found-versus-added. Ids are pack-qualified now, and `1378 + 27 within-pack duplicates = 1405`, every file accounted for. **AND THE PACK RULE WAS WRONG ON ONE OUTLIER**: "a directory is a pack if ANY child holds SKILL.md directly" was true of the whole downloaded folder, because `bhengubv-loki-mode` is a single-skill repo with SKILL.md at its root - collapsing eleven attributable repos into one unattributable name. Now "EVERY". **THE REAL DEFECT WAS AT THE SEAM, AND EVERY EXISTING TEST STAYED GREEN OVER IT.** `CapabilityManifestSkillStore` matches the WHOLE query as a substring, so "what can you do" hits nothing in it; `SkillContextBuilder` treats zero hits as a signal to list capability names instead - that is how the model learns what it is. A library that ORs every term made "do" and "you" match hundreds of skills, turning "no match" into "junk match", flipping the builder to full-detail mode over five arbitrary community skills with the capability manifest nowhere in the prompt. The phone answered *"my primary capabilities include assisting with various queries related to user needs"*. The manifest's tests test the manifest, the library's test the library, and the bug lived between them. Stopword filtering fixes it; `CompositeSkillStoreTests` now asserts the seam. **A REPLACEMENT WOULD HAVE BEEN WORSE AND ALSO GREEN.** `AIOptions.SkillStore` is one reference, so the obvious wiring assigns over the manifest - the store whose own header calls an unchecked capability list "a machine for confident lying". Composed instead, manifest first. |
| E15 | **I changed working code on a corrupted measurement** | A device run of "threat modeling for a mobile app" returned 1,106 hits topped by `mobile-game-development` and `web-game-development`, so I diagnosed the ranker and replaced OR with a strict AND. **The query in the log read `"VTHREAT modeling for a mobile app"`** - a stray keystroke from the adb test harness (tapping the input at a fixed coordinate while the keyboard was already up lands on the `v` key) had eaten the word "threat". The search was ranking on "modeling/mobile/app" and game development was the CORRECT answer to the question actually asked. **The "fix" made it materially worse**: AND demanded "mobile" and "app" too - incidental context, not the subject - which EXCLUDED the two skills literally named `threat-modeling` and `security-threat-model`, leaving four OSINT pages. Backed out. Plain OR with bm25 puts both ideal skills first and second, because bm25 already weights a rare term far above a common one. **The stray `v` had appeared TWICE before and been correctly called out as the harness**, and the third instance was still read as product evidence. Check the query before the ranker. |
| E16 | **One of the three heads had never compiled — not "was broken", had never produced a DLL** | `CircleAI.Samples.Web.Client` is in `CircleAI.sln`, is referenced by `CircleAI.Samples.Web`, and its `bin` directory contained **no assembly at all**. Every one of its seventeen `Browser*.cs` files implements a seam from `CircleAI.Assistant` — `IBrain`, `IConversation`, `IDeviceFacts`, twenty-one types — and **not one had a `using` for it**: thirty-eight CS0246s, the whole web head. <br>**The cause is the namespace-shadowing mechanism again, inverted.** The MAUI services declare `namespace CircleAI.Assistant.Device`, and C# walks the enclosing namespaces on the way out — so the SDK's types resolve there for free and nobody writing one ever typed a using. The browser services sit in `CircleAI.Samples.Web.Client.Services`, which encloses nothing, and got none. `_Imports.razor` did carry `@using CircleAI.Assistant` — **twice**, added by hand twice by somebody who did not check — but that file reaches `.razor` and nothing else. <br>`CircleAI.Samples.Web` was separately still referring to itself by its **pre-rebrand name** in four places (`CircleAI.Assistant.Web.Components`, `.Services`, `.styles.css`, `typeof(CircleAI.Assistant._Imports)`), had no `@using` for the library that owns `<Routes>`, and served `_content/CircleAI.Assistant/theme.js` and `app.css` — from a project that **has no `wwwroot` at all**; they live in `CircleAI.Samples.Shared`, which is where the MAUI head has always read them from. <br>**Nothing caught it because nothing built it.** A green `dotnet test` run says nothing about a head with no test project, and the solution builds every project but nobody had run it to completion on this box — see `CLAUDE.md`, where the 195-project solution build ran 85 minutes and produced no result. |
| E17 | **The test that exists to catch an unregistered seam had a hand-written list, and four pages were not on it** | `EveryPageRendersTests`' own header says it is the test that would have caught `Settings.razor` injecting an `IResidentAssistant` the web head never registered. It was **fourteen `[Fact]`s naming pages by hand**, and `Find`, `Seeing`, `Music` and `Transcribe` had been added without anybody adding a line — so the four newest screens had no coverage of any kind. <br>**And they could not have been added without the other half.** `WireEverything` — the container whose remarks say "DELIBERATELY EVERYTHING, not the minimum each test needs" — registered neither `IMakesMusic`, `IPlaysMedia` nor `IKeepsTranscripts`, so three of those pages were **not constructible by the test container at all**. Two ways of not noticing, each looking like the other's job. <br>**Four tests were already failing and nobody had run them.** `TranscribeSessionTests` builds its own container and `Transcribe.razor` had gained `@inject IKeepsTranscripts` earlier the same day; all four threw on the injection rather than on anything they were testing. The suite is not built by any solution — `CLAUDE.md` records that it "stopped running when it stopped compiling, and stayed that way with every other suite green". <br>It now scans the shared assembly for `RouteAttribute`, which is the same question `Routes.razor` asks the Router at runtime, plus a guard asserting the scan found something — **a reflection query that matches nothing passes every theory it feeds**, which would have been worse than the list it replaced. 308 → 333 tests. <br>**One stale assertion surfaced underneath**: `ChatScreenTests` asserted the empty chat says "Ask it something", which the screen had deliberately stopped saying — that line asks somebody to invent an example before they have seen the thing work. Re-pointed at the behaviour (there are examples, and pressing one asks it) rather than the copy. |
| E18 | **Two unrelated public types called `Capabilities`, in one namespace** | `CircleAI.Assistant.Runtime/FirstRun.cs` declared `public readonly record struct Capabilities` in `namespace CircleAI.Assistant` — where `CircleAI.Assistant/Capabilities.cs` already has a `public static class Capabilities`, the catalogue of what this product can do. One is a census of what is installed; the other is the ability list every screen renders. The compiler picked whichever was nearer and said **CS0436 only where both happened to be visible**, which is a warning nobody reads in a build of this size. <br>This is the same mechanism that let the retired sample's types silently shadow the SDK's for weeks, sitting inside the product itself. Renamed to `CapabilityCensus` — three sites, one of them a `var` — which is also the right name: it is what `CensusAsync` returns. |
| E19 | **Every question carrying a tool cue died with "Object reference not set to an instance of an object"** | Found by asking the shipping app the question its own suggestion chip offers: "What is the weather like today?". The chat bubble showed the raw NRE message and the turn ended. <br>`AIService.cs:1395` latched tool names without a null check — `foreach (var t in wanted) _latchedTools.Add(t.Name);` — while **the very next statement already guarded `t is not null`**, so somebody had met a null entry there before and hardened one of the two lines. `RelevantTools` skips nulls on its way through, which is why the guessing path was safe; `forceTools` hands `all` straight back, and `AvailableToolsAsync` only guards the LIST being null, never its elements. <br>**It could only ever fire on a tool-cue question**, which is why "What is the capital of Kenya" (`tools=0`) answered and "weather" did not — `Web` at `AIService.cs:1651` lists "weather" as a cue. A whole class of question, on the phone, on the first tap of the first suggestion offered. |
| E20 | **The prompt and the search query were the same string, and the search was drowning** | Two defects that compounded, both found by driving the deployed app rather than by reading it. <br>**(1) RETRIEVAL WAS SEARCHING THE PROMPT.** `Recalling.Ask` puts remembered facts about the PERSON in front of the question, and that whole composed string was handed to the skill store AND the RAG index as the thing to search for. The device log: `q="Things you already know about them:` / `- Never deploy with -t:Install on this phone, it wipes the models every time` / `What is the weather like todayo"`. The facts are the least relevant possible search terms — they are about the person, and what is being looked for is about the subject. Fixed with `Recalling.Question` (this assembly owns the format, so it hands the question back) reached through a new `AIOptions.RetrievalQuery` hook, because `CircleAI.Hosting` cannot reference `CircleAI.Assistant` — the dependency runs the other way, and a hosting layer that rebuilt that heading by hand would be a second owner of it. <br>**(2) A FIXED STOPWORD LIST CANNOT KNOW A CORPUS.** "name" is on no stopword list anywhere and appears in **79% of these 1,378 skills**, so "Name three colours" matched **1,082 of them** and the phone was handed `ui-ux-design`, `threejs`, `inspira-ui`, `better-chatbot`, `creative-coding` — `enrichment=1593`, against `131` on the previous build. On a 0.6B model with a 4096-token window the answer came back *"Here is my name as an AI and the current weather as shown on your screen."* Terms appearing in more than a third of the library are now dropped BEFORE the match; ranking is untouched. **1,082 → 65.** <br>**THE RANKER WAS NOT THE PROBLEM AND WAS NOT TOUCHED** — E15 is the scar from concluding otherwise on a corrupted measurement, so `SkillRelevanceMeasure` keeps E15's exact query as a regression guard and the fix had to satisfy both at once. <br>**AND THE FIRST CUT OF THE FIX WAS WRONG, CAUGHT BY THREE EXISTING TESTS.** Applying a frequency threshold whatever the corpus size meant that in a store holding ONE skill every matching word sat at 100% and was discarded as noise: "how do I docker compose things" threw away "docker" and "compose" and found nothing. 79% of 1,378 is evidence; 50% of two is an accident of there being two. The filter is now off below 100 skills. |
| E21 | **The phone was listening, and three of its own screens said it was not** | Found by looking at Settings on a P30 on 2026-10-01: "Answer to its name" ticked, and four rows below it the **Waking** ability offering **"Turn on"** — same screen, same second, two answers. Four defects under one symptom, none of them visible to any test. <br>**(1) THE LISTENER TOLD NOBODY IT WAS RUNNING.** `ResidentListeningControl` started its `ScreenUpWakeWord` directly and kept it private, so `CircleNeuronService.IsListening` — the one static the ability row, `DeviceConversation`'s microphone hand-off and the notification shade all read — stayed **false while the microphone was open**. The checkbox reads the object; the ability row reads the static. It also silently forfeited the **partial wake lock** (so "it answers with the screen off" would have died the moment the CPU suspended — the failure already measured on 2026-09-05) and the microphone disclosure, and it left the spoken turn and the wake loop free to contend for an exclusive `AudioRecord`, because the hand-off is gated on that same false flag. <br>**(2) THE LINK DROPPED THE COLUMN THAT MAKES THE ROW DO ANYTHING.** `LinkSetupRows.Ability` carried `[title, blurb, state, bytes]` and not `AbilityRow.TryRoute`, which is how the abilities screen tells *start the listener* from *fetch the model*. A linked client therefore sent every Waking tap down the **download** path — a full setup run for a 6.4 MB bundle already on the phone — after which the row still said "Turn on". Nothing threw; the field was just null. <br>**(3) SUCCESS WAS RENDERED AS THE FAILURE REASON.** `IDeviceFacts.TurnOnAsync` returns the literal `"On"` for success and the screen compares against it; the thin client returned a friendly `"Waking is ready."`, which landed in the warning slot on every successful run. <br>**(4) TWO WRITERS, ONE NOTIFICATION, AND THE MICROPHONE LOST THE RACE.** The listener writes the shade through `RefreshNotification` and the brain writes "loading the model…" then "ready" as it comes up — seconds later, so the shade read **"ready" over an open microphone**, which is exactly what `ResidentListening`'s own remarks spend two paragraphs forbidding. No ordering of two independent writers is safe, so the disclosure is now composed in inside `Notify`, where every write passes. <br>**And the screen the fixed route finally made reachable was itself a dead end**: `/wake` reported one fixed line, "Open CircleAI to turn it on", about an app with no launcher icon — on a phone that was already listening. It now follows the service's listener (`LinkedWakeWord`, in the product, not the sample), and because a binder cannot push, the wake **count** rides the resident row so a client can see it was heard. <br>**Verified on the P30**: Waking ✓ with a working "Try it", the wake screen reading **Listening**, the shade reading **Listening for “HEY B” — nothing is kept or sent** through the model load, and `wake lock held` in the log. |
| E22 | **Both APKs were debug-signed and the service had no icon, and neither failed any build** | Asked "are we ready for release" and went looking. <br>**(1) A RELEASE ANDROID BUILD WITH NO SIGNING CONFIG DOES NOT FAIL** — the SDK falls back to the platform debug keystore (`_ResolveAndroidSigningKey`: `Value="$(_ApkDebugKeyStore)" Condition="'$(AndroidKeyStore)'!='True'"`), so you get a Release package that installs, runs, passes every suite and can be uploaded nowhere. `dumpsys package` on the P30 read back the debug signature for both. **And the key already existed**: `TheGeekAlias.keystore`, wired this exact way for the seventeen apps under `thegeeknetwork\code`; this repo was simply never given the block. <br>**(2) THE SERVICE HAD NO ICON AND NO NAME OF ITS OWN.** Its `<application>` element was bare and `[Application(Label = "CircleAI")]` gave it the same string as the app — two indistinguishable entries in Settings > Apps, one of them holding a microphone, under Android's generic placeholder glyph. `src/CircleAIService` had no `Resources` directory at all. The app's own manifest already documents the trap one file over: "android:icon HAS TO BE HERE, and its absence is invisible in every build log". <br>**AND THE GUARD WAS SILENTLY DEAD TWICE BEFORE IT WORKED.** `BeforeTargets="Build"` runs AFTER `$(BuildDependsOn)`, which already contains the signing, so XA4314 won; hooking the SDK's own `_ResolveAndroidSigningKey` did not fire either; and the condition `'$(OutputType)' == 'Exe'` was **false** — the csproj says `Exe` and the property evaluates to `Library`, because the Android SDK rewrites it. A Target whose Condition does not hold is skipped without a word, so the guard looked wired and did nothing. Found with `dotnet msbuild -getProperty`, not by reading. It is `BeforeTargets="PrepareForBuild"` on `'$(AndroidApplication)' == 'true'` now. <br>**(3) THE LABEL HAS ONE OWNER AND IT IS NOT THE MANIFEST.** `[Application]` wins over the template's `<application>`: the manifest said "Circle AI Service" and `aapt2 dump badging` read back "CircleAI". <br>Verified: `application: label='Circle AI Service' icon='res/mipmap-anydpi-v26/appicon.xml'`, and the P30's App info page shows the Circle mark. **Still open: `ANDROID_KEYSTORE_PASS` is not set on this machine, so no release-signed package has been produced yet** — `-p:CircleAIAllowDebugSignedRelease=true` is the named, warning-on-every-build escape hatch that keeps on-device testing going until it is. |
| E23 | **The thin app kept the resident listener's permissions, and the button that needed one of them had been returning false since the split** | Found by running the Android release gate rather than reasoning about it. <br>**(1) ELEVEN PERMISSIONS, SEVEN OF THEM UNUSABLE.** `Platforms/Android/` in the app head holds exactly three files — the manifest, `MainActivity`, `MainApplication` — no Service, no BroadcastReceiver, and the project references neither `CircleAI.Device` nor `CircleAI.Assistant.Device`. It was still declaring `FOREGROUND_SERVICE`, `FOREGROUND_SERVICE_DATA_SYNC`, `FOREGROUND_SERVICE_MICROPHONE`, `POST_NOTIFICATIONS`, `WAKE_LOCK`, `RECEIVE_BOOT_COMPLETED` and `REQUEST_IGNORE_BATTERY_OPTIMIZATIONS`, with the comments about the wake lock and `BootReceiver` still sitting above them. `ServiceAnnouncer` posts nothing on purpose — "the shade belongs to the service that is actually doing the work" — so the notification permission went too. A thin client asking a store for a microphone-typed foreground service it cannot start is the first thing a reviewer reads. **11 → 4**, read back off the device. <br>**(2) "Fix it" WAS DEAD, AND FOR TWO REASONS.** `ServiceSetup.AllowBackgroundAsync` was `=> Task.FromResult(false)` and `BackgroundAllowedAsync` likewise, so Settings printed "This phone may stop it listening — Fix it" on every launch and the button did nothing, for ever. The deeper half: the package that gets hibernated is **CircleAI's**, and `ACTION_REQUEST_IGNORE_BATTERY_OPTIMIZATIONS` names one package and only shows for an app holding that permission — an app cannot ask for another app's exemption. It opens `ACTION_IGNORE_BATTERY_OPTIMIZATION_SETTINGS` now, which takes no package and needs no permission, which is also why the app could drop that permission in the same change; and `BackgroundAllowedAsync` queries `IsIgnoringBatteryOptimizations(LinkIpc.HostPackage)` — the service's package, through the same constant the bind uses. The screen's own contract already said the return "says only that something opened" and that the re-read belongs on the way back in; nothing had ever satisfied it. <br>**AND THE RELEASE PUBLISH HAD NEVER BEEN RUN.** Everything to date was `dotnet build -t:Install`, the dev loop. `dotnet publish -c Release -f net10.0-android -m:1` on both heads reaches signing and stops only at XA4314 — so AOT, trimming, dex and resources all pass, on the head that had AOT and trimming switched on and never exercised. <br>Verified on the P30 (app 47): the permission list reads INTERNET / RECORD_AUDIO / MODIFY_AUDIO_SETTINGS, and Fix it lands on `Settings$HighPowerApplicationsActivity`. |

### C. Decisions left open

| # | Decision | Why it is open |
|---|---|---|
| C1 | `QwenTextGenerator` memory mapping | **CODE GAP CLOSED; the default is still a decision.** It read `CIRCLEAI_MNN_MMAP` and nothing else — and **an Android app cannot set an environment variable for itself**, so on the only platform this ships to it was not "off by default", it was off with no way on and no way to exercise either branch on the device where the crash was found. Now `QwenTextGenerator.AllowMemoryMapping`, a settable property, with the variable still overriding in **both** directions (a phone that is crashing needs a way to turn it off without a new build). Seven tests. **Still open:** whether off is permanent, or whether the underlying `use_mmap` / `kvcache_mmap` SIGSEGV is fixable — that needs the P30, not more C#. |
| C2 | ~~14 libraries net10.0-only~~ | **CLOSED.** All fourteen — including the three `CircleAI.Assistant` projects, which are the product — now multi-target `net9.0;net10.0`. They needed nothing from net10; they were net10-only because that is what a new project defaults to, and both legs build clean. `TargetFrameworkTests` guards it, since nothing else can: a net10-only library builds fine and the net9 test leg stayed green precisely because the test project could not reference them. |
| C3 | **A second embedded model catalogue, shipped in every build** | `CircleAI.Core` embeds **two** registries. `ModelRegistryService` reads `Models/embedded_registry.json` - **88 models**, the one the app actually chooses from. `LocalModelLoader` and `ModelDownloader` read `registry.json` - **31 models**, a different file, and the csproj comment calls it "legacy file kept for callers that load it by name". **Compared rather than assumed:** the 31 are a strict subset of the 88 and **every shared hash is identical**, so there is no wrong-hash download today - the risk is a second owner drifting, not one that already has. **Traced rather than assumed too:** DI resolves `IModelLoader` to `BundleModelLoader` over `ModelRegistryService`, and `LocalModelManager` (the only thing that constructs `ModelDownloader`) has no callers at all - so the legacy pair is off the live path entirely. What is left is 36 KB of stale catalogue in every APK, nine tracked `.bak` files beside it totalling 576 KB, and two **public** classes that would answer "which models exist" with 31 if anybody ever resolved them. **Open because removing public API and deleting files is not a silent change** - it needs a nod, and `feedback_never_delete_without_asking` is a standing rule here. |

### E. Fixed in passing

| # | Item | What it was |
|---|---|---|
| E1 | **Twelve tests were timing the machine** | `Circle34LoopbackRealtimeTests.SendText_CustomTtsDelegate_IsInvoked` failed once on the **net9 leg only** of a full run, at 4 s — and passed in 8 ms, 88 ms and 165 ms run on its own. The cause was a 2-second `CancellationTokenSource` in a test that does no real I/O: a performance assertion wearing a timeout's clothes, measuring whether the thread pool got round to the pump while three thousand other tests ran. Twelve of these existed across four files at 1–2 s. All now 30 s, which still fails a genuine hang and cannot be reached by a loaded machine doing the right thing. |
| E2 | **`SampleLanguages.Find` refused the tags it is actually handed** | A plain ordinal dictionary lookup, so it answered only for an exact lowercase primary subtag. Android hands back `en-US` and `pt-BR`; a model config writes `ZU`. Every one of those returned null — and null here means a screen prints the raw tag where the language's name belongs, so the commonest input was the one that failed. Now exact, then case-insensitive, then primary subtag. Found by `LanguageTableTests`, written minutes earlier for a different reason. |
| E3 | **Two native names disagreed between the tables** | `ha` was "Hausa" in `KnownLanguages` and "Harshen Hausa" in `SampleLanguages`; `ig` was "Igbo" vs "Asụsụ Igbo". Aligned the **unused** table to the **user-visible** one, which cannot make anybody's screen worse — but a Hausa or Igbo speaker should still be asked which they would rather read. |
| E4 | **Two more tests timing the machine** | Found by the same full-run/isolation comparison as E1, on the net9 leg. `Circle33ReassuranceFillerTests.FastWork_SkipsFiller` does 20 ms of work and asserts no filler fired after a 5-second threshold — under load the 20 ms took **9 seconds**, so the filler fired and the test measured the scheduler rather than the branch (threshold now 60 s, so the gap is the test). `VoicePipelineTests.ActivationFailed_Event_FiredWhenTranscriberThrows` polled for an event for 3 seconds; the loop exits the moment the event lands, so 30 s costs a passing run nothing. Both pass in ~90 ms in isolation. |
| E5 | **The test project could not see the product, and nobody could tell** | `tests/CircleAI.Tests` pulled seven `CircleAI.Assistant` files in by **path** - `<Compile Include>` with a `Link` - rather than referencing the project, and the comment above them gave the reason: the Assistant projects were net10.0-only and the test project builds a net9.0 leg, so a `ProjectReference` was refused outright. **C2 removed that reason** and the workaround stayed. Its cost is not drift in CONTENT - a linked file is the file the app ships - it is drift by **OMISSION**: the tests see only what somebody remembered to list. `Transcript`, `IKeepsTranscripts` and `FileTranscriptStore` could not be tested AT ALL until this changed, and the failure reads as `CS0246: type could not be found`, which looks like a missing type rather than a missing line in a `.csproj`. Two `ProjectReference`s now, seven `<Compile Include>` gone, both legs green. |
| E6 | **The live catalogue would have offered Stable Diffusion as a model to talk to** | `InferModality` knew two answers - Vision for a VLM, **Chat for everything else** - and the fall-through was the defect. Asked of the real listing on 2026-09-11: **4 of the first 100** models this publisher returns are not chat models. `stable-diffusion-v1-5-mnn-opencl`, `bge-large-zh-MNN`, `gte_sentence-embedding_multilingual-base-MNN` and a `speech_paraformer-...-online-mnn`. `CatalogueMerge` lets a live entry **ADD**, so the first successful refresh put all four in the chat ladder: the diffusion checkpoint would download, be selected, and answer noise. **The licence allowlist does not catch it** - the SD row carries `apache-2.0` in the listing and SD 1.5 is CreativeML OpenRAIL-M, so the tag is simply wrong at the source and the modality gate is the only thing standing there. The rule is now **what this product can LOAD**, not what the model is: an MNN bundle is openable by the MNN LLM runtime (chat), the same with an image encoder (vision), and `MnnEmbeddingBackend` (embedding) - and by nothing else, because the ears are Whisper.net over ggml and the voice is ONNX. Anything else returns `null` and is **not catalogued at all**, which is not a lost feature but a refusal to list a file nothing can open. Cataloguing an MNN ASR bundle under its TRUE modality would have been worse than the mislabelling: `PlanFor(Asr)` would rank a large fresh paraformer above the 78 MB ggml Whisper that works, and **the phone would lose hearing it already had.** Re-run against the live listing after the fix: exactly those 4 reclassified, 83 Chat and 13 Vision untouched. |
| E7 | **The licence gate admitted NonCommercial, NoDerivatives and OpenRAIL — and a test said otherwise** | Found by reading the gate that E6 had just proven was load-bearing. `fully-free-opensource-always` is a hard rule here and this allowlist is the only thing enforcing it. **It was not untested — it was tested into a false sense of safety, which is worse.** `CatalogueListingTests.A_restricted_licence_is_refused` asserted exactly the right thing over a fixture reading `"CC BY-NC 4.0"`, and passed **for a reason that had nothing to do with NonCommercial**: the old gate was `String.Contains` against `"cc-by"`, and the SPACED spelling contains no `cc-by`. The same fixture written `cc-by-nc-4.0` - the commoner spelling by far - would have been catalogued, with a green test standing over it. Three defects, each demonstrated against the shipped matcher rather than argued: (1) **`openrail` was ON the allowlist** - OpenRAIL carries behavioural use restrictions and is not OSI-approved, and it is what `stable-diffusion-v1-5` actually ships under, so the one live model that must never be catalogued was admitted **by name**; (2) the allowlist is matched with `String.Contains`, so **`cc-by-nc-4.0` passed by containing `cc-by`** - a NonCommercial licence through a gate for free software - and so did `cc-by-nd-4.0`. No longer allowlist can fix that, because NC and ND are SUFFIXES on a stem that is genuinely allowed, which is why the fix is a **denylist checked first**; (3) `Limited Commercial Licence` passed, because **li-MIT-ed contains `mit`**. Also normalised, so `CC BY-NC-SA 4.0` and `cc-by-nc-sa-4.0` now get the same answer - the spaced spelling was refused before only by the accident of not containing a hyphen, and the same accident would have ADMITTED a spaced spelling of something worse. **AND ONE EXISTING EXPECTATION CHANGED, deliberately.** `An_empty_licence_list_means_any_licence` asserted that an empty allowlist admits an NC model. "Empty means any" is a real contract - a host pointing this client at its OWN models has a reason to switch the allowlist off - so it keeps its meaning, unstated licences included. But an empty ALLOWLIST is not a host asking for NonCommercial content: the allowlist answers "which may we ship against" and the denylist answers "which must we NEVER", and only the first is what emptying switches off. A host that genuinely means any empties `RefusedLicences` too, which is explicit rather than a side effect, and `A_host_that_truly_means_any_licence_can_still_say_so` pins that hatch open. `CatalogueLicenceTests` adds 28 cases beside the three that existed, and `The_restricted_fixture_was_being_refused_by_accident` pins both spellings so the next reader is not reassured by the same accident. |
| E8 | **The search I had just built starved its own new half** | **Self-inflicted, found by re-reading my own diff, and the same defect as everything else in this file.** `CircleAISession.SearchAsync` built ONE list - memory results, then transcript results - and capped it with `.Take(take)`. So a memory answering with twenty hits left room for **no transcript at all**, and the transcript store, which is the entire reason A5 stopped meaning "search memory", vanished from the results **exactly when the other source was doing well**. `SearchActivity` renders "you told me this" and "a microphone caught this" as deliberately different kinds of answer, and would only ever have shown the first. The `MEMORY FIRST WHEN BOTH ANSWER` reasoning above it was sound - a remembered fact was told to the app on purpose - but **first is not instead**. **AND IT WENT UNNOTICED FOR THE USUAL REASON:** `FileTranscriptStore` has 13 tests and `LexicalIndex`/`Bm25` have 18, while the step that COMBINES them had none - combining lived on a session, and building a session means a model, so the one piece with no test was the one piece with the bug. `Merge` is now public and static for the reason `InferModality` is, with 14 cases in `SearchMergeTests` including the exact 20-and-5 case that was silently wrong. |
| E9 | **A fifteenth test timing the machine, surfaced by giving it a net10 leg** | Thirteen of the sixteen test projects were **net9.0-only** - C2 in a mirror, since C2 found fourteen LIBRARIES that were net10-only. Those libraries multi-target now, so their net10 behaviour was covered by `CircleAI.Tests` and nothing else. Multi-targeting all thirteen was **measured before it was committed**: one project first (`Federation`, clean on both legs), then all of them - **13 of 14 clean, one failure.** `MockInferenceBridgeTests.CompleteAsync_RespectsLatencyMillis` asserted a 150 ms simulated latency landed in `[120, 900]` ms and overran while thirteen projects built and ran alongside it; **it passes in 178 ms on its own.** The comment above it read "generous tolerance for timer resolution and CI noise" - the same phrasing that hid the twelve in E1 and the two in E4. The lower bound IS the assertion (did the simulation actually delay); the upper bound only ever asked whether the thread pool got round to us, so it is 30 s now, per E1's precedent. **A sweep of all fourteen projects found no other timing assertion**, so this was the last of that shape outside the main suite. |
| E10 | **The search screen hung on the phone, and the tests could not have known** | `SearchActivity` called `CircleAISessionHost.GetAsync`, which runs the full `StartAsync` - device probe, model selection, model load. But `SearchAsync` touches **no instance state**: `Transcripts` and `Remembers` are both static. So the screen whose own header says it "needs no model and so works on a handset with nothing downloaded" waited on everything a model needs. **Ninety seconds on "Looking...", process alive, a .NET thread-pool worker enumerating network interfaces.** `SearchAsync` is static now and the screen takes no session; the same query returns in about three seconds. **3271 green tests said nothing**, because every one of them called the method directly. |
| E11 | **Every answer the phone gave leaked the model's private monologue** | The first message ever typed into the deployed app came back as `What was your original appointment time?` then a literal `</think>` then `I don't need information about who the user is because I am just running the app.` **The cause is the PROMPT, not the model.** Qwen3's chat template pre-fills `<think>` at the END of the prompt, so the model generates the reasoning and the CLOSING tag and never the opening one - and `MnnTokenRouter.RouteUntil` only ever searched for `<think>`, so it stayed in Content mode and streamed the lot. The reasoning router was not broken; **it never started.** Whichever tag comes first now decides. **The router that shapes every answer on every head had ZERO tests**; there are seven now, and **four of them fail on the old code** - checked by stashing the fix, because a test that passes either way proves nothing. It is arithmetic over a `StringBuilder`: it needed no phone, only someone to look. |
| A9 | **The phone remembers now - and still not what you ASK it to** | **WIRED AND PROVEN ON THE P30.** `CircleAIApplication` constructs `DeviceMemory` over a real `MemoryService` at `FilesDir/Memory` (app-private, off the shared card, gone on uninstall - what somebody tells their phone is at least as private as what its microphone caught), and `RunTurnStreamingAsync` hands each utterance to `Remembers.LearnAsync` after the answer is delivered. Deliberately the WHOLE utterance: `IRemembers` says the store decides what is worth keeping, and filtering in the session would be inventing a judgement the memory library already owns. Device proof, versionCode 9: told it "I prefer tea to coffee" in chat, searched "tea", got back **1 result, "Something you told it"** - `learn:` and `recall: 1 offered, 1 kept` both in logcat. **WHAT IS STILL OPEN, AND IT IS NOT WIRING.** The original test - "Remember that my clinic appointment moved to Friday" - still finds nothing, and that is the store behaving as designed: `CueExtractor` is a deliberate NO-MODEL extractor with 43 cues for rules, preferences, decisions and corrections ("never ", "i prefer ", "lets use ", "it failed"), and **"remember" is not among them.** Diagnosed rather than guessed: a fuller phrase ruled out a keyword-vs-`Situation` mismatch, because `0 offered` counts BEFORE filtering, so the store was genuinely empty; then the tea case proved the pipeline end to end. **The sharp edge:** an explicit "remember that X" is the clearest instruction a person can give, and it is the one phrase the extractor ignores - while a bare `"use "` counts as a decision. Somebody who says "remember" and cannot find it later will conclude the phone is broken, and they will be right. Adding explicit-request cues changes what the product stores about people, so it waits on a decision rather than being slipped in. |
| E12 | **Seeing: four defects between the button and the model, none visible to 3291 green tests** | Found by pointing the deployed app at a real photo. In the order they were peeled back: **(1) `ImageBudget.SampleFor` never shrank anything in the 1024-2048 band** - `inSampleSize` is powers of two and the function deliberately undershoots, so a 1156px edge got a factor of 1 and went over the wire unchanged, while `Describe` printed "within 1024 - unchanged" one line above the session saying "over budget". The host now scales exactly after the cheap decode. **(2) Vision was the one ability `FirstRun` never fetched**, so Seeing alone sat behind a "Turn on" button on a phone that had already pulled the voice, the ears, the wake word and the brain unasked. **(3) The Neuron slot loader had no vision branch** - it built a `QwenTextGenerator` for whatever id it was handed, and that class does not contain the string `ImageBytes` anywhere. **(4) THE VISION SELECTOR EXCLUDED VISION MODELS.** `EnumerateEntries` filtered to `Modality == Chat` BEFORE the capability gate, so both entries declaring `["Default","Vision"]` were discarded one line before anything asked what they could do; `BestFit` threw "No model in the registry satisfies required capabilities 'Vision'" over a registry containing exactly two that do, and a `catch { return generalist; }` swallowed it. **AND A TEST WAS GREEN OVER (4) THE WHOLE TIME.** `Vision_ThrowsUntilAVisionModelIsCatalogued` asserted the throw, expecting an EMPTY catalogue; it got a throw from the modality filter instead. Right outcome, wrong cause, and the agreement hid the bug on every run - the third instance of that pattern today. Its own name said "until", and a vision model had been catalogued twice over. **Three of the four were fixed by guessing from source and none of those guesses landed.** What found it was one `Console.WriteLine` per silent fallback in `SelectSlotAsync` - the file already said, above `CIRCLEAI-PROMPT`: "Guessing at prompt size from source has now been wrong twice; this prints it." |

### D. Never run on hardware

`feedback_p30_is_the_only_benchmark` — the P30 is the benchmark, desktop is a
compile gate. Everything here passes tests and has never been on a phone.

| # | Item | Status |
|---|---|---|
| D1 | **Five new screens on the native head** | `SeeingActivity`, `TranscribeActivity`, `MusicActivity`, `TranslateActivity` and `SearchActivity` all build clean for `net10.0-android` and **not one has been installed**. Everything section A now calls "closed" is closed *in the sense that a head calls it* - which is the bar A sets and not the bar `csharp-is-core-signoff-language` sets. The user declined the device call this pass, so this stayed open on purpose rather than by omission. |
| D1a | `ImageBudget` subsampling | The `BitmapFactory.inSampleSize` path has never seen a real photo, only synthesised bitmaps. |
| D1b | `AndroidAudioDecoder` | `MediaExtractor`/`MediaCodec` is the one piece of the transcribe path with **no desktop equivalent** - `tools/stt-hear` proves Whisper, not this. Codec support varies by OEM, so a `.m4a` that decodes on one phone is the test this cannot skip. |
| D1c | `FileTranscriptStore` on real storage | The atomic write is `.writing` + `File.Move` on a **desktop** filesystem. A phone killed mid-write is the case it exists for, and that has only ever been simulated. |
| D1d | The catalogue refresh on a handset | 25 models fetched in 25 s **from Windows**. A P30 on mobile data, or behind a captive portal, or offline, has never run it - and the whole design of the merge is about what happens when that fetch goes wrong. |
| D2 | `Transcribing.FileAsync` on a phone | Proven on Windows via `tools/stt-hear` (JFK sample, verbatim, 0,81 confidence, SRT written). Never run on ARM64. |
| D3 | AlarmManager FGS notification reaper | Written after `SetTimeoutAfter` was **measured to fail** on EMUI. The replacement has never been verified. |
| D4 | `ToolCallReader` against a real model | Every malformed shape it handles is a real shape a 0.6B model produces, but they are reproduced in tests, not captured from a live Qwen run on the phone. |

---

### How this list is meant to be used

A capability moves out of section A only when a head calls it. It moves out of
section D only when it has run on the P30. Nothing here is "nearly done" — the
distance between a tested library and a working feature is exactly the distance
this file measures, and this repo has repeatedly mistaken the first for the
second.

---

## Roadmap × Huawei P30 Lite — 67-item on-device status

*Merged here from `docs/P30_LITE_ROADMAP_STATUS.md` on 2026-10-22. That file was last changed 2026-07-25.*

Per-item result for the reference device: **Huawei P30 Lite** (MAR-LX1M, 3.6 GB RAM, EMUI, **no GMS**). Honest and graded.

**Ground truth about method (stated plainly):** the tests are written and run against **C# as the reference**, and the other-language work is **ported from that C#**. The P30 runs the **C#/.NET MAUI** app — so the C# reference is exactly what executes on this phone; the Go/Rust/Python/TypeScript/Kotlin/Swift/C ports run on *their* runtimes (desktop/Mac) and are validated there, not on the P30. That's why the port rows read "not a P30 run" — it's a different target, not a failure.

**Legend**
- ✅ **Proven** — ran on the P30 and produced real, verified output (pulled off the device).
- ◐ **Exercised / verdict** — runs on the P30 as part of the app, but the specific heavy path (or inference) wasn't independently demonstrated yet.
- ⬚ **Not a P30 run** — a port to another language, or a desktop/Mac test / audit / build step. The C# it mirrors runs on the P30; the item itself targets another runtime.
- 🚫 **Exception** — deliberately not done.

Evidence for ✅ rows: `adb exec-out run-as com.bhengubv.itsample cat files/capability-report.txt` + the pulled PDFs/WAV/PNG, plus the on-device coding run. Companion: [HARDWARE_FINDINGS_HUAWEI_P30.md](docs/HARDWARE_FINDINGS_HUAWEI_P30.md).

---

### Ports, parity & cross-language tests (#1–#24)

| # | Item | P30 | Note |
|---|------|:---:|---|
| 1 | Personality + Federation → Go & Rust | ⬚ | Port; the C# reference runs on the P30 |
| 2 | Per-language core gaps (telephony/speech/skills) | ⬚ | Multi-language port |
| 3 | Cross-cutting holes across ports | ⬚ | Port work |
| 4 | Complete C port | ⬚ | C runtime, verified on Mac |
| 5 | Kotlin gap closure | ⬚ | Kotlin/JVM |
| 6 | Swift track | ⬚ | Swift, verified on Mac |
| 7 | HarmonyOS full port | 🚫 | Deliberate exception |
| 8 | Reconcile + code-parity report | ⬚ | Cross-language audit |
| 9 | Unify safety-phrase hash (FNV-1a) | ⬚ | C# runs on P30; ports validated on their runtimes |
| 10 | Federation delta dispatcher | ⬚ | C# runs on P30; ports elsewhere |
| 11 | Re-verify all builds | ⬚ | Build step (desktop) |
| 12 | Surface-parity audit | ⬚ | Audit |
| 13 | Close audit gaps (7 ports) | ⬚ | Port work |
| 14 | Test: C# reference suite | ⬚ | Desktop test of the C# that *does* run on the P30 |
| 15 | Test: Go | ⬚ | Port test |
| 16 | Test: Rust | ⬚ | Port test |
| 17 | Test: Python | ⬚ | Port test |
| 18 | Test: TypeScript | ⬚ | Port test |
| 19 | Test: Kotlin | ⬚ | Port test |
| 20 | Test: Swift | ⬚ | Port test (Mac) |
| 21 | Test: C | ⬚ | Port test (Mac) |
| 22 | Full-green report | ⬚ | Meta |
| 23 | Port cascade (15 pkgs) | ⬚ | Port work |
| 24 | Re-verify port suites | ⬚ | Port test |

### Neuron engine (#25–#33) — the C# runtime that IS the app on the P30

| # | Item | P30 | Note |
|---|------|:---:|---|
| 25 | IChatRuntime seam | ✅ | The runtime the app drives; chat runs on the phone |
| 26 | Concierge router | ✅ | Routing exercised on device (the Sweep) |
| 27 | ResidentSlotManager (RAM admission) | ✅ | Drove the OOM fix; now picks a model that fits **free** RAM (0.6B) and loads |
| 28 | Two-slot residency | ◐ | Brownout exercised (Sweep); full specialist hot-swap not demonstrated |
| 29 | Router option + Save/Load | ◐ | Present + compiled; not independently demonstrated on device |
| 30 | NeuronNode facade + DI | ✅ | The app's brain *is* this; runs on the phone |
| 31 | Voice into the Neuron | ◐ | Voice-flagged head compiles; wake→ASR→TTS loop not run |
| 32 | Neuron tests + standalone host | ◐ | Host runs on device; the tests are desktop |
| 33 | Build + regression gate | ⬚ | Meta |

### Neuron ports (#34–#40)

| # | Item | P30 | Note |
|---|------|:---:|---|
| 34 | Neuron → Python | ⬚ | Port |
| 35 | Neuron → Go | ⬚ | Port |
| 36 | Neuron → Rust | ⬚ | Port |
| 37 | Neuron → TypeScript | ⬚ | Port |
| 38 | Neuron → Kotlin | ⬚ | Port |
| 39 | Neuron → Swift | ⬚ | Port (Mac) |
| 40 | Neuron → C | ⬚ | Port (Mac) |

### Selection / enrichment gaps (#41–#48)

| # | Item | P30 | Note |
|---|------|:---:|---|
| 41 | Fit-vs-function verdict in BestFit | ✅ | The per-modality verdicts in the on-device report are this code |
| 42 | Actionable native-load error | ✅ | Surfaced on the phone (the OOM/load path) — drove the fix |
| 43 | Collapse model-storage dir | ◐ | Models land in one dir on device (`files/.config/CircleAI/Models`) |
| 44 | Enrichment on caller-owned system turns | ◐ | Chat runs on device |
| 45 | Reconcile the two registries | ✅ | The embedded registry loads on device; the selector reads it |
| 46 | Populate skill store from capabilities | ◐ | "what can you do?" exercised in the Sweep |
| 47 | Speech ladder foundation | ✅ | ASR/TTS verdicts on device |
| 48 | Compile + test gaps | ⬚ | Meta |

### Capabilities (#50–#68) — the device-facing features

| # | Item | P30 | Evidence |
|---|------|:---:|---|
| 50 | Voice on the phone | ◐ | Voice head runs on device; the **TTS half now works fully** (see #56 — real speech WAV via out-of-process espeak). Full wake→ASR→TTS loop still needs a live mic (wake + ASR not yet run on-device). Selector: ASR/TTS `Good` |
| 51 | Vision model | ◐ | Pipeline **proven** on device: select → download (311 MB) → load SmolVLM-256M. But **inference blocked on this phone**: SmolVLM loads yet MNN's image path fails (`code -6` — its SigLIP arch isn't what the bridge's Qwen-VL/Kimi-VL vision generation supports); the Qwen-VL VLMs the bridge *does* support need ~2.4 GB+ and don't fit the ~1 GB free. Would run on a 4 GB+ phone. |
| 52 | Document engine → PDF | ✅ | CV PDF on device (64,172 B) |
| 53 | CV generator | ✅ | Rendered in the on-device suite |
| 54 | Corpus audit | ⬚ | Documentation deliverable |
| 55 | HTML → video / stills | ◐ | PNG **still** on device (`89504E47`); H.264/MP4 is a seam |
| 56 | TTS ladder | ✅ | **Full on-device TTS, end to end.** select → `Piper-en_US-lessac-high` (Good) → download 108 MB (HF) → ONNX-Runtime load → grapheme→phoneme via the **out-of-process espeak G2P app** (`com.bhengubv.espeakng`, GPL-3.0, quarantined like DOOM) → a real **2.6 s, 114,732-byte WAV** (RIFF/WAVE, 22 050 Hz mono 16-bit, 71% peak amplitude = genuine speech, not silence). espeak-ng is **never linked into CircleAI**; CircleAI reaches it across a ContentProvider. If the G2P app is absent, TTS degrades to text. This closes the earlier `DllNotFoundException: espeak-ng` wall the licence-clean way. |
| 57 | Charts | ✅ | Chart PDF on device (42,757 B) |
| 58 | Presentations | ✅ | Deck PDF on device (63,388 B) |
| 59 | Music beds | ✅ | WAV on device (705,644 B, `RIFF/WAVE`) |
| 60 | Security — defensive | ✅ | Blocklist matched `malware.example` on device |
| 61 | Security — antibodies | ✅ | Deny-by-default gate on device |
| 62 | Business ops | ✅ | Invoice `INV-2026-0001`, `R 11 500.00` on device |
| 63 | Code from mobile | ✅ | 1.5B wrote a correct `is_prime()` on the phone |
| 64 | Cast to smart TV | ◐ | DLNA discovery ran on device (0 offline); no live cast |
| 65 | Cover letter + invoice | ✅ | Both PDFs on device |
| 66 | Report kind | ✅ | Report PDF on device (60,389 B) |
| 67 | Selector ladder → modalities | ✅ | Per-modality verdicts on device |
| 68 | Mesh hand-off router | ✅ | Capability advertise + list on device |

---

### Tally (67 items)

| P30 result | Count | Which |
|---|---:|---|
| ✅ Proven on device | **22** | 25,26,27,30,41,42,45,47,52,53,56,57,58,59,60,61,62,63,65,66,67,68 |
| ◐ Exercised / verdict | **11** | 28,29,31,32,43,44,46,50,51,55,64 |
| ⬚ Not a P30 run (port / test / meta) | **33** | 1–6, 8–24, 33, 34–40, 48, 54 |
| 🚫 Exception | **1** | 7 |

**Read it as:** on the P30 itself, **22 items ran with verified output** and **11 more run as part of the live app** (verdicts or compiled-but-not-exercised). **33 are ports or cross-language tests** — validated against the C# reference and on their own runtimes, not this phone. **1** is the agreed exception.

**TTS now works fully on the phone** — the earlier grapheme→phoneme licence wall was solved by moving espeak-ng out-of-process into its own GPL app (`com.bhengubv.espeakng`), which CircleAI calls across a ContentProvider; a real Piper WAV comes out and CircleAI stays permissive. Two ◐ remain that convert to ✅ with more device work: the **voice loop** (needs a live mic for wake + ASR) and **vision inference** (SmolVLM — needs a 4 GB+ phone; the bridge-supported VLMs don't fit here). Coding and TTS have both crossed the line.

*Snapshot 2026-07 on MAR-LX1M. Updated as the ◐ inference paths are run on the device.*

---

## Circle AI — Strategic Architecture

*Merged here from `docs/STRATEGY.md` on 2026-10-22. That file was last changed 2026-05-28.*

> **Status:** Living document. Last meaningful revision: 2026-05-28.
> Audience: Engineers extending CircleAI; investors evaluating the personal-AI moat.

---

### TL;DR

CircleAI is not a chatbot. It is **the only personal AI runtime that ships on every operating system, in every language the user speaks, owns its own transport substrate, owns its own OS surface, and stores its memory and personality as files the user keeps.**

No competitor can match that combination. Apple Intelligence runs on iOS only. Gemini Nano runs on Pixel only. Pi.ai, Replika, Personal.ai, Inflection, Rabbit R1, and Humane all require cloud round-trips and hide your memory in a database they own.

---

### The 10-layer personal AI stack

Every viable personal AI needs ten capability layers. The stack below shows what CircleAI ships today and where each layer lives.

| # | Layer | Packages | Status |
|---|---|---|---|
| 1 | **Perception** — face, voice, gesture, biosignal, context | `CircleAI.Voice`, `CircleAI.Wearable.Biosignals`, facex integration | Voice ✓ in C#, ports needed; Biosignals ✓ new this commit |
| 2 | **Memory** — episodic + semantic + procedural + working | `CircleAI.Memory`, `CircleAI.Embeddings`, `CircleAI.Search`, `CircleAI.Knowledge` | Schemas + algorithms ✓; Markdown-backed store ✓ new this commit |
| 3 | **Identity** — UHID, trust, biometric, key management | `CircleAI.Identity`, `CircleAI.Security` | Biometric matcher + UhidKeyRing + watchdog ✓ |
| 4 | **Reasoning** — LLM inference, planning, simulation | `CircleAI.Inference`, `CircleAI.Hosting.InferenceBridge`, `CircleAI.Simulation` | Inference contracts ✓; cross-OS bridge ✓ new this commit; simulation ✓ |
| 5 | **Affect** — engagement, mood, rapport, VAD | `CircleAI.Memory.AffectState`, `AffectVad` | 5-dim + VAD projection ✓ across 10 languages |
| 6 | **Action** — tools, agents, automations | `CircleAI.Tools`, `CircleAI.Skills`, `CircleAI.Orchestration`, `CircleAI.Agents.Peer` | Tool catalogue ✓; agent dispatch ✓; agent-to-agent over mesh ✓ new this commit |
| 7 | **Language** — multilingual, cultural context | `CircleAI.Languages` | 20 languages, Africa-first registry ✓ |
| 8 | **Transport** — mesh, sync, federation | `CircleAI.Aether`, `CircleAI.Federation` | Mesh ✓; federated learning model ✓ new this commit |
| 9 | **Security** — immune system, audit, attestation | `CircleAI.Security`, `CircleAI.Security.Aether` | Watchdog, checkpoints, key rotation, BugHunter CI ✓ |
| 10 | **Distribution** — apps, OS integration, embeds | `CircleAI.Hosting.InferenceBridge`, `CircleAI.Personality`, `CircleAI.Personal`, `CircleAI.Knowledge` | Cross-OS daemon contract ✓ new this commit; personal-data adapters ✓ new this commit |

---

### What ships in CircleAI today (post-this-commit)

#### Existing — already in production

- **Portable core (10 languages)**: C#, Rust, Go, Python, TypeScript, Kotlin JVM, Swift, C, ArkTS (HarmonyOS), Android Kotlin
- **Reference C# modules**: Core, Memory (Affect/Affect­Vad/PersonaState/EpisodicMemory/Goal), Identity (BiometricMatcher, UhidKeyRing), Languages (20-entry registry), Companion (FaceAffectMapper, FaceCompanionBridge), Inference, Tools (FacialMetricMatrix), Sync, Voice (Whisper + ONNX TTS + Energy VAD + Wake-word), Embeddings, Search, Skills, Hosting, Aether, Security (peer + immune system), Security.Aether, Accessibility, Orchestration, Simulation
- **Fixtures**: affect_state, affect_vad_derivation, anomaly_signal_schema, biometric_vectors, language_tags, goal_progress, persona_state, graph_schema, bughunter_vrt_taxonomy

#### New in this commit

| Package | What it does | Why it matters |
|---|---|---|
| `CircleAI.Personality` | User-DECLARED persona (distinct from learned `PersonaState`). JSON document the user owns and edits. | Replika hides personality. Pi hides personality. Ours is `notes.md`-like, owned by the user. |
| `CircleAI.Knowledge` | Markdown-on-disk memory backing. Episodic memories become Git-diffable `.md` files with YAML frontmatter. | Pi.ai / Personal.ai hide memory in databases. Ours is a folder. Export, version, audit, delete. |
| `CircleAI.Hosting.InferenceBridge` | Cross-OS LLM daemon contract. One model loaded once per device, every app on the device shares it. | Apple Intelligence is iOS-only. Gemini Nano is Android-only. **Nobody has cross-OS.** This is the single sharpest differentiator. |
| `CircleAI.Wearable.Biosignals` | HR/HRV/SpO2/accelerometer/temperature → AffectState mutations. Deterministic, fixture-validated. | Personal AI without biosignal awareness can't claim to be "personal." |
| `CircleAI.Personal` | Permission-gated Calendar + Email + Contacts adapter contracts. Every call requires a UhidKeyRing-signed `UserConsentToken`. | Apple/Google adapters lock you into their ecosystem. Ours is portable across both. |
| `CircleAI.Federation` | Federated learning round model. Only deltas leave the device, never raw data. Designed for Aether mesh aggregation. | Federated learning over BLE / Wi-Fi Direct is structurally impossible for cloud-only competitors. |
| `CircleAI.Agents.Peer` | Agent-to-agent protocol over Aether mesh. One person's AI talks to another's directly. | Uniquely available because of the Aether substrate. No competitor can offer offline P2P AI federation. |

---

### What still needs porting

The Voice, Embeddings, and Skills modules exist in C# with substantial implementations but haven't been ported to the other 9 languages. Priority order for the next porting round:

1. **`CircleAI.Voice` → 9 langs** — Pi.ai's whole pitch is voice. ITtsEngine + IVoiceTranscriber + IWakeWordDetector + IVoiceActivityDetector interfaces, plus null impls. Concrete impls (Whisper, ONNX TTS) stay C#-only for now.
2. **`CircleAI.Embeddings` → 9 langs** — ITextEmbedder interface + a fixture-validated cosine-search helper.
3. **`CircleAI.Skills` → 9 langs** — Skill catalogue contract (ISkillStore, SkillDetail, SkillDraft).
4. **Personality + Knowledge → 9 langs** — straightforward port using the same parallel-agent pattern as AnomalySignal / AffectVad.
5. **InferenceBridge → 9 langs** — contract-only port; OS-specific adapters live per platform.

---

### Strategic positioning

#### What competitors look like

| Competitor | Their pitch | What they ship | The hole |
|---|---|---|---|
| Pi.ai (Inflection) | Empathetic voice companion | Cloud-only, single platform (web/iOS) | No offline. No multi-OS. No mesh. No African languages. |
| Replika | Emotional connection | Cloud-only, iOS/Android | Same as Pi. Memory locked in their DB. |
| Personal.ai | "Your personal AI model" | Cloud SaaS | Same as Pi/Replika. No on-device. |
| Apple Intelligence | OS-integrated | iOS 18 + new hardware required | Single OS. Single language tier. Apple-locked. |
| Gemini Nano | OS-integrated | Pixel 8+ Android | Single OS. Google-locked. |
| Rabbit R1 / Humane Pin | Embodied hardware | Custom hardware + cloud | Hardware-locked, cloud-dependent. |

#### What CircleAI looks like

| Differentiator | How it lands |
|---|---|
| **10-language portable runtime** | Ships natively on every device class that exists. Pi.ai cannot run on a 2020 Redmi. CircleAI does. |
| **Cross-OS inference daemon** (InferenceBridge) | One contract, four OS adapters. Apple and Google each ship one. We ship four. |
| **Mesh-native transport** (Aether) | Works offline, peer-to-peer, no infra dependency. Critical for African + Asian + rural-North markets. |
| **Africa-first language depth** (20-lang registry + circleone siNtu syllabary) | Western competitors need a decade of linguistics to catch up. We shipped it. |
| **User-owned memory + personality** (Knowledge + Personality) | Files on disk, not rows in a database. Audit trail. Export. Delete. Versioning. |
| **Self-defending immune system** (Security) | Watchdog detects anomalies, rotates keys, isolates sessions. No competitor has this. |
| **Multi-agent foundation** (Orchestration + Agents.Peer) | Your AI can spawn specialised agents AND talk to other people's AIs over the mesh. |
| **Predictive simulation** (Simulation + MiroFish) | Before acting, the AI simulates impact via knowledge-graph diffusion. |
| **Economic loop built in** (Wallet, agent-tipping) | Pi.ai burns money. Replika needs subscriptions. CircleAI agents can earn-to-help. |
| **Trust receipts everywhere** (UhidKeyRing signatures on consent tokens, agent messages, federation deltas) | On-device proof of every action — healthcare/banking/government story. |

---

### Memory architecture — the mempalace pattern

Investigation of `bhengubv/mempalace` (96.6% R@5 on LongMemEval, 500 questions, independently reproduced on M2 Ultra in under 5 minutes) confirms the technique is **discipline, not exotic ML**. The portable algorithm:

1. **Store verbatim, never summarise on write.** Episodic memory writes are append-only and lossless. AI-deciding "what's worth remembering" is the documented anti-pattern.
2. **Hierarchical metadata scope keys** — wing (person/project) → hall/room (subject) → closet (pointer) → drawer (original file). Every chunk carries scope tags.
3. **Filter-then-ANN retrieval** — narrow by scope metadata first, then run vector similarity within the filtered set. Cheap, deterministic recall boost, no novel data structure.
4. **Wake-up projection** — at session start, produce a tight ~170-token capsule of pinned facts (identity, preferences, active projects) instead of hot-loading history.
5. **Source-typed ingest channels** — distinguish code/docs vs conversation vs auto-classified general content.

Every CircleAI target language already has either an HNSW library (`hnswlib`, `usearch`) or a vector DB binding. The novelty is the discipline, not the algorithm. **Skip AAAK compression** — by their own benchmark it regresses 12.4 points and saves no tokens at realistic scales.

This will be implemented as an enhancement to `CircleAI.Memory.IEpisodicMemoryStore` + `CircleAI.Search` in the next porting round:

- `IEpisodicMemoryStore.AppendVerbatimAsync(EpisodicMemoryEntry entry, IReadOnlyDictionary<string,string> scopeKeys)` — verbatim, scope-tagged
- `IEpisodicMemoryStore.GetSessionCapsuleAsync(string uhid)` — wake-up projection
- `CircleAI.Search.IFilterableVectorSearch` — filter-then-ANN contract
- `fixtures/memory_recall_benchmark.json` — LongMemEval-style golden vectors so all 10 ports can compare R@5 against the reference

---

### Roadmap

#### Immediate (next session)

1. Port Voice + Embeddings + Skills interfaces to the 9 non-C# languages
2. Port Personality + Knowledge schemas to 9 languages
3. Port AnomalySignal / Personality / Persona prompt fixtures to fixture-interop.yml

#### Near-term (next 2-3 sessions)

4. mempalace pattern integration into CircleAI.Memory + CircleAI.Search (filter-then-ANN, wake-up capsule, verbatim discipline)
5. Wake-word concrete implementation per OS (Android Porcupine binding, iOS Speech framework binding, etc.)
6. First Calendar adapter implementation (`CircleAI.Personal.Google`, `CircleAI.Personal.Microsoft`)
7. InferenceBridge Android adapter (Binder service binding)
8. InferenceBridge iOS adapter (XPC service)

#### Medium-term

9. `CircleAI.Generation.{Image,Audio}` — multimodal output via distilled local models
10. `CircleAI.AR` — HUD-projection contract for Apple Vision / Meta Ray-Bans / future glasses
11. `CircleAI.Adaptation` — on-device LoRA fine-tuning, federated rounds over mesh
12. `CircleAI.Memory.Forgetting` — explicit memory garbage-collection with user-controlled forgetting policy

#### Long-term

13. CircleOS integration — InferenceBridge lifts up into the OS as a system service (replacing the per-app daemon model)
14. SDPKT marketplace for community agents — third parties publish capabilities, users tip
15. Federated learning rounds at scale — one model fine-tuned across thousands of devices without uploading raw data

---

### The single most important sentence

**The work for the next year is not invention. It is porting and integration.**

CircleAI already has the architectural primitives that none of the competitors have. The job now is to ship them across every language and every OS surface, prove the cross-OS daemon, and let the unique combination land in users' hands.
