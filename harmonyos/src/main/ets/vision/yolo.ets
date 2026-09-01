/**
 * vision/yolo
 *
 * ArkTS for HarmonyOS. Ported from the TypeScript source of the same module by
 * `harmonyos/tools/gen_arkts.py`: ArkTS is TypeScript without the dynamic
 * parts, so the logic is shared rather than retyped, and what differs is the
 * platform - Node's filesystem, paths and cryptography are reached here through
 * `src/main/ets/platform.ets`, which is over `@ohos.*`.
 *
 * Edit the TypeScript source, not this file.
 */

// vision/yolo.ts
//
// Shared YOLO-family post-processing ported from OnnxFaceDetector.cs and
// OnnxPlateRecognizer.cs: IoU, non-max suppression, and letterbox→pixel box
// decoding. Kept in one place because both components run the identical
// [1, 4+…, N] channel-first decode + NMS pipeline.
//
// Float sites use Math.fround to match the C# `float` semantics on the box
// maths; the IoU union/inter is integer-pixel arithmetic as in C#.

import { boundingBox, type BoundingBox } from "./primitives";

/** One scored candidate box. */
export interface ScoredBox {
  readonly score: number;
  readonly box: BoundingBox;
}

/** Intersection-over-union of two pixel rectangles. Mirrors the C# `Iou`. */
export function iou(a: BoundingBox, b: BoundingBox): number {
  const ax2 = a.x + a.width;
  const ay2 = a.y + a.height;
  const bx2 = b.x + b.width;
  const by2 = b.y + b.height;
  const ix1 = Math.max(a.x, b.x);
  const iy1 = Math.max(a.y, b.y);
  const ix2 = Math.min(ax2, bx2);
  const iy2 = Math.min(ay2, by2);
  const iw = Math.max(0, ix2 - ix1);
  const ih = Math.max(0, iy2 - iy1);
  const inter = iw * ih;
  const union = a.width * a.height + b.width * b.height - inter;
  return union === 0 ? 0 : Math.fround(inter / union);
}

/**
 * Greedy non-max suppression: sort by score desc, keep a box only if it does not
 * overlap an already-kept box beyond `iouThreshold`. Mirrors the C#
 * `NonMaxSuppression` (and the inline NMS in OnnxPlateRecognizer.cs). Sorts a
 * copy so the caller's array is untouched.
 */
export function nonMaxSuppression(boxes: readonly ScoredBox[], iouThreshold: number): ScoredBox[] {
  const sorted = [...boxes].sort((a, b) => b.score - a.score);
  const kept: ScoredBox[] = [];
  for (const cand of sorted) {
    let keep = true;
    for (const k of kept) {
      if (iou(cand.box, k.box) > iouThreshold) {
        keep = false;
        break;
      }
    }
    if (keep) kept.push(cand);
  }
  return kept;
}

/**
 * Decode a channel-first YOLO output `[1, channels, boxes]` (flattened
 * row-major as `arr[c*boxes + n]`) into scored pixel boxes, unmapping the
 * letterbox transform. Reads the first 5 channels per box (cx, cy, w, h, score).
 * Mirrors the shared decode loop in OnnxFaceDetector.PostprocessYolo /
 * OnnxPlateRecognizer.RecognizeAsync. `expandCeil` toggles the box-width formula:
 *   • detector  uses ceil(x2 - x1) / ceil(y2 - y1)  (expandCeil = true)
 *   • plate     uses ceil(bw / scale) / ceil(bh / scale)  (expandCeil = false)
 */
export function decodeYoloBoxes(
  arr: Float32Array,
  boxes: number,
  origW: number,
  origH: number,
  padX: number,
  padY: number,
  scale: number,
  confidenceThreshold: number,
  expandCeil: boolean,
): ScoredBox[] {
  const out: ScoredBox[] = [];
  for (let n = 0; n < boxes; n++) {
    const cx = arr[0 * boxes + n];
    const cy = arr[1 * boxes + n];
    const bw = arr[2 * boxes + n];
    const bh = arr[3 * boxes + n];
    const score = arr[4 * boxes + n];
    if (score < confidenceThreshold) continue;

    // Convert back from letterbox space to original pixel space (float maths).
    const x1 = Math.fround((cx - bw / 2 - padX) / scale);
    const y1 = Math.fround((cy - bh / 2 - padY) / scale);
    const bx = Math.max(0, Math.floor(x1));
    const by = Math.max(0, Math.floor(y1));

    let bxw: number;
    let bxh: number;
    if (expandCeil) {
      const x2 = Math.fround((cx + bw / 2 - padX) / scale);
      const y2 = Math.fround((cy + bh / 2 - padY) / scale);
      bxw = Math.min(origW - bx, Math.ceil(x2 - x1));
      bxh = Math.min(origH - by, Math.ceil(y2 - y1));
    } else {
      bxw = Math.min(origW - bx, Math.ceil(Math.fround(bw / scale)));
      bxh = Math.min(origH - by, Math.ceil(Math.fround(bh / scale)));
    }
    if (bxw <= 0 || bxh <= 0) continue;
    out.push({ score, box: boundingBox(bx, by, bxw, bxh) });
  }
  return out;
}
