# vision/_geometry.py
#
# Shared box geometry helpers ported verbatim from the private static helpers in
# CircleAI.Vision/OnnxFaceDetector.cs and OnnxPlateRecognizer.cs (C# — the EXACT
# spec). IoU + greedy non-max-suppression are byte-identical between the two C#
# files, so they live once here.

from __future__ import annotations

from typing import List, Tuple

from .primitives import BoundingBox

# One NMS candidate: (score, box) — mirrors the C# ``(float Score, BoundingBox Box)`` tuple.
Candidate = Tuple[float, BoundingBox]


def iou(a: BoundingBox, b: BoundingBox) -> float:
    """Intersection-over-union of two axis-aligned boxes.

    Faithful port of the C# ``static float Iou(BoundingBox a, BoundingBox b)``.
    """
    ax2 = a.x + a.width
    ay2 = a.y + a.height
    bx2 = b.x + b.width
    by2 = b.y + b.height
    ix1 = max(a.x, b.x)
    iy1 = max(a.y, b.y)
    ix2 = min(ax2, bx2)
    iy2 = min(ay2, by2)
    iw = max(0, ix2 - ix1)
    ih = max(0, iy2 - iy1)
    inter = iw * ih
    union = a.width * a.height + b.width * b.height - inter
    return 0.0 if union == 0 else float(inter) / union


def non_max_suppression(boxes: List[Candidate], iou_threshold: float) -> List[Candidate]:
    """Greedy NMS: sort by descending score, keep a box unless it overlaps an
    already-kept box beyond ``iou_threshold``.

    Faithful port of the C# ``NonMaxSuppression`` / the inline plate-NMS loop.
    Sorts in-place (C# ``boxes.Sort(...)`` mutates its argument).
    """
    boxes.sort(key=lambda c: c[0], reverse=True)
    kept: List[Candidate] = []
    for cand in boxes:
        keep = True
        for k in kept:
            if iou(cand[1], k[1]) > iou_threshold:
                keep = False
                break
        if keep:
            kept.append(cand)
    return kept


__all__ = ["Candidate", "iou", "non_max_suppression"]
