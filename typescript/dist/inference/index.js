"use strict";
// inference/index.ts
// On-device text generation contracts.
// Ported from Circle.AI.Inference (C#).
Object.defineProperty(exports, "__esModule", { value: true });
exports.DEFAULT_GENERATION_OPTIONS = void 0;
/** Default generation options, matching C# defaults. */
exports.DEFAULT_GENERATION_OPTIONS = {
    maxTokens: 512,
    temperature: 0.7,
    topP: 0.9,
    topK: 40,
};
