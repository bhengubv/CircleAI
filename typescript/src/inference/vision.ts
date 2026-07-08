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
