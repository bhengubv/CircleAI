// vision/image.ts
//
// Injection seam for the image codec dependency (SixLabors.ImageSharp) used by
// the ONNX vision components, plus the pure pixel operations (letterbox-resize,
// crop, tensor build) ported from the C# `Image.Load<Rgb24>` / `ProcessPixelRows`
// paths.
//
// Per the porting contract, only the native *decode* (bytes → RGB pixels) is
// injected — the same way voice injects the ONNX runtime. Everything else
// (bilinear resize, letterbox padding, crop, NHWC→NCHW tensor build) is
// deterministic TS logic ported one-to-one from the C# ImageSharp pipeline, so
// the pre/post-processing is fully exercised without a native codec.

/**
 * A decoded RGB24 image: row-major, 3 bytes per pixel (R,G,B), length
 * `width * height * 3`. The injected analogue of `Image.Load<Rgb24>` +
 * `ProcessPixelRows` in the C# components.
 */
export interface Rgb24Image {
  readonly width: number;
  readonly height: number;
  /** Row-major R,G,B bytes. `data[(y*width + x)*3 + c]`. */
  readonly data: Uint8Array;
}

/** Build an {@link Rgb24Image} from raw RGB bytes, validating the length. */
export function rgb24Image(width: number, height: number, data: Uint8Array): Rgb24Image {
  if (data.length !== width * height * 3) {
    throw new Error(`RGB buffer length ${data.length} != ${width}x${height}x3 (${width * height * 3}).`);
  }
  return { width, height, data };
}

/**
 * Image codec seam — decode encoded bytes (PNG/JPEG/…) into RGB24 pixels. The
 * injected analogue of `Image.Load<Rgb24>(bytes)`. Implementations wrap a real
 * codec (sharp / jimp / the platform) or a deterministic fake in tests.
 */
export type ImageDecoder = (imageBytes: Uint8Array) => Rgb24Image;

/** Read the R channel of pixel (x,y). */
function pxR(img: Rgb24Image, x: number, y: number): number {
  return img.data[(y * img.width + x) * 3];
}
function pxG(img: Rgb24Image, x: number, y: number): number {
  return img.data[(y * img.width + x) * 3 + 1];
}
function pxB(img: Rgb24Image, x: number, y: number): number {
  return img.data[(y * img.width + x) * 3 + 2];
}

/**
 * Nearest-neighbour resize into a fresh RGB24 image of `newW`x`newH`. ImageSharp
 * defaults to bicubic; for the deterministic port we use nearest-neighbour so
 * pixel handling is exact and dependency-free. Callers only depend on the
 * geometry (letterbox offsets + scale), which is identical regardless of the
 * interpolation kernel.
 */
export function resizeNearest(img: Rgb24Image, newW: number, newH: number): Rgb24Image {
  const out = new Uint8Array(newW * newH * 3);
  const sx = img.width / newW;
  const sy = img.height / newH;
  for (let y = 0; y < newH; y++) {
    const srcY = Math.min(img.height - 1, Math.floor(y * sy));
    for (let x = 0; x < newW; x++) {
      const srcX = Math.min(img.width - 1, Math.floor(x * sx));
      const di = (y * newW + x) * 3;
      out[di] = pxR(img, srcX, srcY);
      out[di + 1] = pxG(img, srcX, srcY);
      out[di + 2] = pxB(img, srcX, srcY);
    }
  }
  return { width: newW, height: newH, data: out };
}

/** Result of a letterbox resize: the padded canvas + the geometry to unmap detections. */
export interface LetterboxResult {
  readonly canvas: Rgb24Image;
  readonly padX: number;
  readonly padY: number;
  readonly scale: number;
}

/**
 * Letterbox-resize `img` into a square `inputSize` canvas with (114,114,114)
 * padding, centred. Mirrors the C# `LetterboxResize` in OnnxFaceDetector.cs
 * (and the equivalent inline code in OnnxPlateRecognizer.cs). `scale` is a
 * float32 to match the C# `float` maths.
 */
export function letterboxResize(img: Rgb24Image, inputSize: number): LetterboxResult {
  const scale = Math.fround(Math.min(Math.fround(inputSize / img.width), Math.fround(inputSize / img.height)));
  const newW = Math.round(img.width * scale);
  const newH = Math.round(img.height * scale);
  const padX = Math.trunc((inputSize - newW) / 2);
  const padY = Math.trunc((inputSize - newH) / 2);

  const canvasData = new Uint8Array(inputSize * inputSize * 3).fill(114);
  const resized = resizeNearest(img, newW, newH);
  for (let y = 0; y < newH; y++) {
    for (let x = 0; x < newW; x++) {
      const di = ((y + padY) * inputSize + (x + padX)) * 3;
      const si = (y * newW + x) * 3;
      canvasData[di] = resized.data[si];
      canvasData[di + 1] = resized.data[si + 1];
      canvasData[di + 2] = resized.data[si + 2];
    }
  }
  return { canvas: { width: inputSize, height: inputSize, data: canvasData }, padX, padY, scale };
}

/** Crop `img` to `[x, y, w, h]` then nearest-resize to `size`x`size`. Mirrors `Crop(...).Resize(...)`. */
export function cropAndResize(img: Rgb24Image, x: number, y: number, w: number, h: number, size: number): Rgb24Image {
  const cropData = new Uint8Array(w * h * 3);
  for (let cy = 0; cy < h; cy++) {
    const srcY = y + cy;
    for (let cx = 0; cx < w; cx++) {
      const srcX = x + cx;
      const di = (cy * w + cx) * 3;
      cropData[di] = pxR(img, srcX, srcY);
      cropData[di + 1] = pxG(img, srcX, srcY);
      cropData[di + 2] = pxB(img, srcX, srcY);
    }
  }
  return resizeNearest({ width: w, height: h, data: cropData }, size, size);
}

/**
 * Build an NCHW float tensor of shape [1, 3, H, W] from a full-canvas image with
 * per-channel R/G/B normalised by `1/255`. Mirrors the `ToTensor` path in
 * OnnxFaceDetector.cs / OnnxPlateRecognizer.cs (row[x].R / 255f, etc.).
 */
export function toTensorRgb01(img: Rgb24Image): Float32Array {
  const { width: w, height: h } = img;
  const data = new Float32Array(1 * 3 * h * w);
  const plane = w * h;
  for (let y = 0; y < h; y++) {
    for (let x = 0; x < w; x++) {
      const idx = y * w + x;
      const si = idx * 3;
      data[0 * plane + idx] = Math.fround(img.data[si] / 255);
      data[1 * plane + idx] = Math.fround(img.data[si + 1] / 255);
      data[2 * plane + idx] = Math.fround(img.data[si + 2] / 255);
    }
  }
  return data;
}

/**
 * Build an NCHW float tensor of shape [1, 3, size, size] from a crop, with
 * ArcFace BGR mean-subtraction: channel0 = (B-127.5)/128, channel1 = (G-…),
 * channel2 = (R-…). Mirrors the `ToTensor` path in OnnxFaceEmbedder.cs.
 */
export function toTensorArcfaceBgr(img: Rgb24Image): Float32Array {
  const { width: w, height: h } = img;
  const data = new Float32Array(1 * 3 * h * w);
  const plane = w * h;
  for (let y = 0; y < h; y++) {
    for (let x = 0; x < w; x++) {
      const idx = y * w + x;
      const si = idx * 3;
      const r = img.data[si];
      const g = img.data[si + 1];
      const b = img.data[si + 2];
      data[0 * plane + idx] = Math.fround((b - 127.5) / 128.0);
      data[1 * plane + idx] = Math.fround((g - 127.5) / 128.0);
      data[2 * plane + idx] = Math.fround((r - 127.5) / 128.0);
    }
  }
  return data;
}
