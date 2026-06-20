# Circle AI — TODO

Living list of open work. The full version history is in
[CHANGELOG.md](CHANGELOG.md); this file tracks what's still ahead.

> **Working rule:** finish, don't remove. If a feature is half-built,
> finish it or mark it `[Experimental("CIRCLEAI_*")]` honestly.

Last reconciled: **2026-06-18** (current line: `3.0.1`).

---

## Open — native runtime work (2.1.1 line)

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

## Open — runtime features (2.x backlog)

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

## Open — catalog work (2.1.2 line)

- [ ] **2.1.2 catalog refresh** — add Qwen3-Coder, DeepSeek-Coder-V2-Lite,
      and Qwen3-Draft (the speculative-decoding draft variants for the
      0.6 / 1.7 / 4 / 7 / 14 B line) once their MNN bundles ship on
      ModelScope. Catalog-only — no SDK release required (this is the
      whole point of "NuGet sleeps"), just regenerate
      `embedded_registry.json` via `tools/recalibrate-registry-sha`.

---

## Open — metadata cleanup (description drift)

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

## Open — documentation backlog

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

## Open — implementation work behind 3.0 contracts

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

## Done — recent line ships

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

## Notes

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
