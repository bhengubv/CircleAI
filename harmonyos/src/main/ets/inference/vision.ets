/**
 * inference/vision
 *
 * ArkTS for HarmonyOS. Ported from the TypeScript source of the same module by
 * `harmonyos/tools/gen_arkts.py`: ArkTS is TypeScript without the dynamic
 * parts, so the logic is shared rather than retyped, and what differs is the
 * platform - Node's filesystem, paths and cryptography are reached here through
 * `src/main/ets/platform.ets`, which is over `@ohos.*`.
 *
 * Edit the TypeScript source, not this file.
 */

// inference/vision.ts
//
// Port of CircleAI.Inference.VisionInput — raw image data to be embedded by the
// vision encoder before text generation begins.

/**
 * Raw image data to be embedded by the vision encoder before text generation
 * begins. Ported from CircleAI.Inference.VisionInput.
 */
export interface VisionInput {
  /** Raw image bytes (JPEG, PNG, or any format the encoder accepts). Required. */
  readonly imageBytes: Uint8Array;
  /** Optional MIME type hint (e.g. "image/jpeg"). Useful for callers to track format. */
  readonly mimeType?: string;
}
