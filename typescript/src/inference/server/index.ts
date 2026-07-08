// inference/server/index.ts
//
// Barrel for the CircleAI.Inference.Server + CircleAI.Inference.Server.Enterprise
// ports. HTTP servers are expressed as in-memory handlers behind
// IInferenceServerHandler — no real socket is stood up.

// Bridge contracts + in-process bridge + backend enums
export * from "./bridge.js";

// OpenAI-compatible request/response DTOs
export * from "./openai.js";

// Configuration options
export * from "./options.js";

// API-key auth handler + schemes
export * from "./auth.js";

// Server counters, admission control, model registry, native runtime status
export * from "./runtime.js";

// Model lifecycle manager + types
export * from "./lifecycle.js";

// Companion session resolver
export * from "./resolver.js";

// Bridge factory (production contract + deterministic stand-in)
export * from "./bridge_factory.js";

// Server-sent-events writer
export * from "./sse.js";

// In-memory HTTP endpoint handlers
export * from "./handlers.js";

// Enterprise tier: tenant routing, batch scheduling, sharding, cross-tier offload
export * from "./enterprise.js";
