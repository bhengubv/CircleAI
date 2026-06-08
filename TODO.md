# Circle AI — TODO

Living list of open work, reconciled against the codebase at the date
shown next to each section. Items move to `[done]` when they ship.

> **Working rule:** finish, don't remove. If a feature is half-built,
> finish it or mark it `[Experimental("CIRCLEAI_*")]` honestly. The
> only thing removed from this list is text that no longer reflects
> reality — and even that gets edited in place, not deleted, so the
> story is preserved.

---

## Architectural shift (P0) — in design

The SDK's source of truth for "what models exist" must become
ModelScope's live catalog, not an embedded JSON resource. Once this
lands, NuGet sleeps for new models entirely.

- [ ] `ModelScopeCatalogClient` — GET `https://www.modelscope.cn/api/v1/models`
      with `mnn` / `MNN-LLM` filter, parse model cards, cache to disk
      under `IDeviceContext`-supplied storage path. Default refresh:
      daily, gracefully degrades to "use cached" when offline.
- [ ] `PromptTemplateEngine` — read `chat_template` from each entry's
      `tokenizer_config.json`, render via Scriban (Jinja2-compatible
      for .NET). `QwenTextGenerator.BuildQwenChatPrompt` becomes a
      template render, not hardcoded ChatML.
- [ ] Ed25519 catalog signature verification in `ModelRegistryService.VerifySignature`.
      Public key as embedded resource. Replaces the
      `NotSupportedException` guard.
- [ ] `DefaultDeviceContext` (replaces `NullDeviceContext` as the
      registered default) — probes RAM, storage, CPU cores, GPU kind,
      thermal class, connectivity at construction.
- [ ] `IModelSelector.BestFit(deviceProbe, requiredCapabilities)` —
      walks the cached catalog (not a hardcoded list of model IDs),
      filters by capability + device fit, ranks by `quality_rank`.
      Returns `ModelSelection { ModelId, RequiresDownload, EstimatedBytes }`.
- [ ] `AIService.StartAsync` calls the selector when
      `AIOptions.ModelId is null`. Observer fires
      `OnModelFetching(selection)` on the download path.

Foundational types for this shift are parked on branch
`fix/p1-device-inference` (stash
`p1-foundational-types-parked-for-p0`): `ChatCapability`,
`DeviceProbe`, `IModelSelector`, `DeviceAwareModelSelector`,
`DefaultDeviceContext`, plus `ModelRegistryService.AllModels` and the
new metadata fields on `ModelEntry` (`MinRamGb`, `MinStorageGb`,
`Capabilities`, `QualityRank`).

---

## Device-derived defaults (P1) — needs P0 first

Every "hardcoded number" in the codebase that the device could answer:

- [ ] **Context window** — replace `MnnInferenceBridgeFactory:188`
      hardcoded `4096` with `DeviceTierDefaults.ContextWindow(tier)`.
      Wearable 2k → Phone 4k → Tablet 8k → Desktop 32k → Workstation 128k.
- [ ] **Concurrency** — `LokiOrchestrator` `MaxConcurrency = 4` → null
      default. When null, derive from device tier. Add thermal hook:
      throttle event halves next-round budget.
- [ ] **KV-cache compression** — wire the per-tier default map
      (Wearable 2Bit → Phone 3Bit → Tablet 4Bit → Desktop+ None) once
      the native algorithm in P2 lands.
- [ ] **Agentic max iterations** — `AIOptions.AgenticMaxIterations`
      hardcoded `5` → derived. Wearable 2 / Phone 3 / Tablet 5 / Desktop+ 10.
      Battery hook: low battery halves.
- [ ] **Embedding model** — `CircleAI.Embeddings` should resolve via the
      same `IModelSelector`. Wearable / Phone → MiniLM-class. Desktop+ →
      BGE-small. Workstation → BGE-large.

---

## Finish half-built features (P2)

- [ ] **`KimiVlGenerator`** — `MnnInterop.cs:103` references it by name
      and `mnn_llm_generate_with_image_stream_ex` is bound, but no
      C# class exists. Implement, mirroring `QwenTextGenerator`. Uses
      the P0 `PromptTemplateEngine` so format isn't hardcoded.
- [ ] **KV-cache compression native** — `MnnInterop.cs:295-345` C-ABI
      is there but the native side returns `KvCompressionApplyResult.NotImplemented`.
      Implement TurboQuant4Bit first; 3Bit / 2Bit fall back to 4Bit
      with a clear log until they land.
- [ ] **Networking transports** — `Net_HttpNetworkTransport.cs:63-70`
      `ReceiveAsync` returns an empty enumerator. Assume the rest
      similar. Finish HTTP + WebSocket + AetherNet (the consumer-default
      trio). Add `ITransportSelector` that picks per-message based on
      connectivity (Online → HTTP/WS, Mesh → AetherNet, Offline+BLE →
      Bluetooth, Offline alone → DTN store-and-forward). Mark
      unfinished ones `[Experimental("CIRCLEAI_TRANSPORT_*")]`.

---

## API surface gaps + quiet bugs (P3)

- [ ] **Structured tool-call output** — `IChatGenerator` returns
      free-text `<tool_call>{json}</tool_call>` markers today, parsed by
      regex in `AIService.ParseToolCall`. Add `ChatResponse` record
      (`Content` + `ToolCalls[]` + `Usage`), overload `GenerateAsync` to
      return it. Keep `Task<string>` for back-compat.
- [ ] **Agentic role mapping** — `AIService.cs:382, 393` appends turns
      with `role: "tool"` but Qwen ChatML only knows
      system/user/assistant. Fix in the P0 template engine: remap
      `role:tool` → `role:user` with `[Tool result: <name>]` prefix.
- [ ] **`LokiOrchestrator` semaphore** —
      `LokiOrchestrator.cs:64-68` — `WaitAsync` sits outside the
      per-task lambda. Schedules serially even though MaxConcurrency
      is honoured downstream. Move `WaitAsync` inside the lambda.
- [ ] **AgentBus correlation ID** —
      `InMemoryAgentPeerProtocol.cs:367-380` extracts a 16-byte GUID
      from the FIRST 16 BYTES of payload. Every Response/Decline
      silently sacrifices the leading 16 bytes. Move `CorrelationId`
      to an `AgentMessage` header field.

---

## Coverage that reduces consumer thinking (P4)

- [ ] **Domain-pack loading by device class** — `domains.json`
      manifest with per-domain metadata (`required_sensors[]`,
      `required_ui_class`, `default_for_tier[]`). Source generator
      emits ~50 near-identical `XxxCompanionAdapter.cs` files at build
      time. Runtime: `CircleEngine` loads only adapters whose
      `required_sensors` / UI fit the device probe. Wearable doesn't
      need RealEstate.

---

## Done — recent landings

- [x] Multi-file MNN bundle support — `EnsureBundleAsync`,
      `BundleFileSpec`, per-file SHA-256 verification (1.3.0).
- [x] Windows DLL resolution + `NativeRuntimePrep` flatten + preload +
      self-check, `/v1/diagnostics` native_runtime block (1.3.1).
- [x] linux-x64 mnnbridge + MNN ship in NuGet (1.3.2).
- [x] osx-arm64 mnnbridge + MNN ship in NuGet (1.3.3).
- [x] osx-x64 + android-arm64 + macOS codesigning with Apple
      Distribution cert (1.3.4).
- [x] android-arm (ARMv7) + ios-arm64 static (1.3.5).
- [x] linux-arm64 — MNN built from source via aarch64-linux-gnu cross
      toolchain (1.3.6).
- [x] All 1.3.x published to **nuget.org** + GitHub Packages.
- [x] Registry refreshed via `tools/recalibrate-registry-sha` against
      ModelScope file-listing API; per-file SHA-256 sample-verified.
- [x] SHA-256 prefix-strip fix in `ModelDownloadService.VerifySha256Async` (1.2.3).

---

## Notes

- Branch hygiene: never push to `master`. Every commit on a master-bound
  branch carries `[skip ci]`.
- See [feedback_never_touch_credentials.md](../.claude-memory/feedback_never_touch_credentials.md):
  credentials in `Deployment/deployment-credentials.ps1` are owned by
  the user. Never modify, rename, or refactor. Ask if something looks
  wrong.
