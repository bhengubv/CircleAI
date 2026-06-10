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
