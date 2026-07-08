// src/index.ts
// Barrel export for the @bhengubv/circle-ai package.
// Re-exports the original 9 portable modules + the 1.5.0 parity additions.

// Original 9 portable modules
export * from "./models/index.js";
export * from "./memory/index.js";
export * from "./identity/index.js";
export * from "./languages/index.js";
export * from "./companion/index.js";
export * from "./inference/index.js";
export * from "./tools/index.js";
export * from "./sync/index.js";
export * from "./security/index.js";

// 1.5.0 parity additions
export * from "./device/index.js";
export * from "./prompt/index.js";
export * from "./catalog/index.js";
export * from "./registry/index.js";
export * from "./selector/index.js";
export * from "./agents/peer/index.js";
export * from "./hosting/index.js";

// Disambiguate names that both ./companion and ./hosting export via `export *`.
// The hosting versions are the canonical butler-runtime contracts, so they win
// at the package root; the companion-local variants remain reachable via the
// ./companion subpath. (companion.IAIService is a narrow briefing subset;
// companion.ProactiveMessageHandler is a companion-event handler.)
export type {
  IAIService,
  ProactiveMessageHandler,
} from "./hosting/index.js";

// Companion reasoning core — HER/Jarvis contracts + deterministic in-memory impls
export * from "./companion/reasoning/index.js";

// Companion.Proactive project — scheduling primitives, cron, scheduler, driver
export * from "./proactive/index.js";

// Memory in-memory stores (episodic, persona, feedback, goal)
export * from "./memory/stores.js";

// CircleAI.Memory.Sync — companion-state sync engine (HLC, envelopes, bridges)
export * from "./memory/sync/index.js";

// CircleAI.Memory.Runtime — CompanionRuntime host orchestrator
export * from "./memory/runtime/index.js";

// CircleAI.Core model-management runtime — loaders, managers, downloaders,
// sources, SafeModelHandle/PlatformInterop, CircleEngine, ShardKvCodec,
// auditing + multi-tenant contracts.
export * from "./core/index.js";

// CircleAI.Embeddings — ITextEmbedder + TextEmbedder (backend-injected).
export * from "./embeddings/index.js";

// CircleAI.Embeddings.Local — encoder/store/index contracts + InMemoryEmbeddingStore.
export * from "./embeddings/local/index.js";

// CircleAI.Inference.Server + .Enterprise — OpenAI-compatible in-memory server:
// bridge contracts, OpenAI DTOs, API-key auth, model registry + lifecycle,
// companion session resolver, native-runtime status, bridge factory, in-memory
// endpoint handlers, and the enterprise tenant/batch/shard/offload tier.
export * from "./inference/server/index.js";
