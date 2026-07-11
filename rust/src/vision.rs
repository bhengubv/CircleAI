//! vision.rs
//!
//! Port of `CircleAI.Vision/` — the vision contract surface (face detection /
//! embedding / liveness, KYC document verification, license-plate recognition,
//! BLE/RF anomaly detection, camera capture) plus the ONNX-backed detector /
//! embedder / plate-recognizer logic.
//!
//! C# → Rust map:
//!   * `Primitives.cs`  → the record/struct shapes ([`BoundingBox`], `DetectedFace`, …)
//!   * `Contracts.cs`   → the `I*` traits (`#[async_trait]`, C# `ValueTask`)
//!   * `IVideoCapture.cs` → [`IVideoCapture`] + [`NullVideoCapture`]
//!   * `NullImplementations.cs` → the `Null*` backends
//!   * `OnnxFaceDetector` / `OnnxFaceEmbedder` / `OnnxPlateRecognizer`
//!
//! Conventions / non-1:1 constructs:
//!   * C# `ValueTask`/`ValueTask<T>` → `#[async_trait] async fn`.
//!   * `ReadOnlyMemory<byte>` → `&[u8]` / `Vec<u8>`.
//!   * `IAsyncEnumerable<VideoFrame>` (the capture stream) → an `async fn capture`
//!     returning a `Vec<VideoFrame>`; [`NullVideoCapture`] yields the empty vec.
//!   * The ONNX backends are built on `Microsoft.ML.OnnxRuntime` +
//!     `SixLabors.ImageSharp` — neither is a dependency of this crate. The port
//!     therefore splits each backend into (a) the *pure* geometry/tensor
//!     post-processing (`letterbox`, `postprocess_yolo`, `non_max_suppression`,
//!     `iou`, `l2_normalise`, `clamp_region`), ported verbatim and fully usable,
//!     and (b) an injected [`IOnnxSession`] trait that a host implements against
//!     its own ORT binding. The image-decode step is likewise injected via
//!     [`IImageSource`]. `Onnx*Options` records carry the same fields/defaults.

use async_trait::async_trait;
use chrono::{DateTime, Utc};

// ─────────────────────────────────────────────────────────────────────────────
// VisionError
// ─────────────────────────────────────────────────────────────────────────────

/// Failure surface for the vision subsystem.
#[derive(Debug)]
pub enum VisionError {
    /// A required argument was null / empty / invalid.
    InvalidArgument(String),
    /// A required model or asset file was not found.
    FileNotFound(String),
    /// The injected ONNX session / image source failed.
    Backend(String),
}

impl std::fmt::Display for VisionError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            VisionError::InvalidArgument(m) => write!(f, "invalid argument: {m}"),
            VisionError::FileNotFound(m) => write!(f, "file not found: {m}"),
            VisionError::Backend(m) => write!(f, "backend error: {m}"),
        }
    }
}

impl std::error::Error for VisionError {}

// ─────────────────────────────────────────────────────────────────────────────
// Primitives (Primitives.cs)
// ─────────────────────────────────────────────────────────────────────────────

/// An axis-aligned rectangle in image-pixel coordinates. 1:1 with `BoundingBox`.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct BoundingBox {
    pub x: i32,
    pub y: i32,
    pub width: i32,
    pub height: i32,
}

impl BoundingBox {
    pub fn new(x: i32, y: i32, width: i32, height: i32) -> Self {
        Self {
            x,
            y,
            width,
            height,
        }
    }
}

/// A 2D point on a detected face. 1:1 with `LandmarkPoint`.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct LandmarkPoint {
    pub x: i32,
    pub y: i32,
}

/// One detected face with optional landmark fallback. 1:1 with `DetectedFace`.
#[derive(Debug, Clone, PartialEq)]
pub struct DetectedFace {
    pub region: BoundingBox,
    pub confidence: f32,
    pub landmarks: Option<Vec<LandmarkPoint>>,
}

impl DetectedFace {
    pub fn new(region: BoundingBox, confidence: f32, landmarks: Option<Vec<LandmarkPoint>>) -> Self {
        Self {
            region,
            confidence,
            landmarks,
        }
    }
}

/// A face embedding suitable for similarity search. `vector` is normalised so
/// cosine similarity reduces to a dot product. 1:1 with `FaceEmbedding`.
#[derive(Debug, Clone, PartialEq)]
pub struct FaceEmbedding {
    pub vector: Vec<f32>,
    pub dimension: i32,
}

/// Outcome of liveness detection. 1:1 with `LivenessResult`.
#[derive(Debug, Clone, PartialEq)]
pub struct LivenessResult {
    pub is_live: bool,
    pub confidence: f32,
    pub failure_reason: Option<String>,
}

/// One parsed field from an ID document. 1:1 with `DocumentField`.
#[derive(Debug, Clone, PartialEq)]
pub struct DocumentField {
    pub key: String,
    pub value: String,
    pub confidence: f32,
}

/// Outcome of KYC document verification. 1:1 with `DocumentVerificationResult`.
#[derive(Debug, Clone, PartialEq)]
pub struct DocumentVerificationResult {
    pub is_valid: bool,
    pub document_type: String,
    pub issuing_country: String,
    pub fields: Vec<DocumentField>,
    pub overall_confidence: f32,
    pub warnings: Option<Vec<String>>,
}

/// Outcome of license-plate recognition. 1:1 with `PlateRecognitionResult`.
#[derive(Debug, Clone, PartialEq)]
pub struct PlateRecognitionResult {
    pub plate_text: String,
    pub country_hint: Option<String>,
    pub region: BoundingBox,
    pub confidence: f32,
}

/// One observed BLE / RF anomaly. Severity 0-1; higher = more concerning. 1:1
/// with `BluetoothAnomaly`.
#[derive(Debug, Clone, PartialEq)]
pub struct BluetoothAnomaly {
    pub source: String,
    pub kind: String,
    pub severity: f32,
    pub description: String,
    pub observed_at_utc: DateTime<Utc>,
}

// ─────────────────────────────────────────────────────────────────────────────
// Video capture (IVideoCapture.cs)
// ─────────────────────────────────────────────────────────────────────────────

/// 1:1 with `VideoPixelFormat`.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum VideoPixelFormat {
    Yuv420,
    Nv21,
    Rgba32,
    Bgr24,
    Jpeg,
}

/// 1:1 with `VideoFrame`.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct VideoFrame {
    pub bytes: Vec<u8>,
    pub width: i32,
    pub height: i32,
    pub pixel_format: VideoPixelFormat,
    pub captured_at_utc: DateTime<Utc>,
    pub rotation_degrees: Option<i32>,
}

/// Camera-frame source. The C# yields an `IAsyncEnumerable<VideoFrame>`; the port
/// returns a `Vec<VideoFrame>` (the empty vec is the "no frames" stream).
#[async_trait]
pub trait IVideoCapture {
    async fn capture(
        &self,
        preferred_width: i32,
        preferred_height: i32,
    ) -> Result<Vec<VideoFrame>, VisionError>;
}

/// Headless / no-camera fallback — yields nothing. 1:1 with `NullVideoCapture`.
#[derive(Debug, Default, Clone, Copy)]
pub struct NullVideoCapture;

#[async_trait]
impl IVideoCapture for NullVideoCapture {
    async fn capture(
        &self,
        _preferred_width: i32,
        _preferred_height: i32,
    ) -> Result<Vec<VideoFrame>, VisionError> {
        Ok(Vec::new())
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Contracts (Contracts.cs)
// ─────────────────────────────────────────────────────────────────────────────

/// Generic CV-runtime primitive. The opaque C# `object?` image handle is modelled
/// as an opaque `Vec<u8>` (backend-private bytes). 1:1 with
/// `IComputerVisionRuntime`.
#[async_trait]
pub trait IComputerVisionRuntime {
    async fn decode(&self, image_bytes: &[u8]) -> Option<Vec<u8>>;
    async fn resize(&self, image: &[u8], width: i32, height: i32) -> Option<Vec<u8>>;
    fn backend_id(&self) -> &str;
}

/// Find faces in an image. 1:1 with `IFaceDetector`.
#[async_trait]
pub trait IFaceDetector {
    async fn detect(&self, image_bytes: &[u8]) -> Result<Vec<DetectedFace>, VisionError>;
}

/// Convert a detected face into a similarity-search vector. 1:1 with
/// `IFaceEmbedder`.
#[async_trait]
pub trait IFaceEmbedder {
    fn dimension(&self) -> i32;
    async fn embed(
        &self,
        image_bytes: &[u8],
        face: &DetectedFace,
    ) -> Result<FaceEmbedding, VisionError>;
}

/// Decide whether the camera is looking at a real person. 1:1 with
/// `IFaceLivenessDetector`.
#[async_trait]
pub trait IFaceLivenessDetector {
    async fn check(&self, image_bytes: &[u8]) -> Result<LivenessResult, VisionError>;
}

/// Parse + verify a KYC document image. 1:1 with `IDocumentVerifier`.
#[async_trait]
pub trait IDocumentVerifier {
    async fn verify(
        &self,
        image_bytes: &[u8],
    ) -> Result<DocumentVerificationResult, VisionError>;
}

/// Read a license plate from an image. 1:1 with `IPlateRecognizer`.
#[async_trait]
pub trait IPlateRecognizer {
    async fn recognize(
        &self,
        image_bytes: &[u8],
    ) -> Result<Vec<PlateRecognitionResult>, VisionError>;
}

/// Surface for AetherNet adversary detection — BLE / RF anomalies. The C#
/// `Subscribe` returns an unsubscribe handle + has a Start/Stop lifecycle. The
/// Rust port models subscription as registering a boxed handler and returning a
/// [`AnomalySubscription`] guard. 1:1 with `IBluetoothAnomalyDetector`.
#[async_trait]
pub trait IBluetoothAnomalyDetector {
    fn subscribe(&self, handler: AnomalyHandler) -> AnomalySubscription;
    async fn start(&self) -> Result<(), VisionError>;
    async fn stop(&self) -> Result<(), VisionError>;
    fn backend_id(&self) -> &str;
}

/// Boxed anomaly handler — the C# `Func<BluetoothAnomaly, ValueTask>`.
pub type AnomalyHandler = Box<dyn Fn(BluetoothAnomaly) + Send + Sync>;

/// Unsubscribe guard — the C# `IDisposable` returned by `Subscribe`. Dropping it
/// is the unsubscribe (no-op for the null detector).
pub struct AnomalySubscription;

// ─────────────────────────────────────────────────────────────────────────────
// Null implementations (NullImplementations.cs)
// ─────────────────────────────────────────────────────────────────────────────

/// No-op vision runtime. 1:1 with `NullComputerVisionRuntime`.
#[derive(Debug, Default, Clone, Copy)]
pub struct NullComputerVisionRuntime;
#[async_trait]
impl IComputerVisionRuntime for NullComputerVisionRuntime {
    async fn decode(&self, _image_bytes: &[u8]) -> Option<Vec<u8>> {
        None
    }
    async fn resize(&self, _image: &[u8], _width: i32, _height: i32) -> Option<Vec<u8>> {
        None
    }
    fn backend_id(&self) -> &str {
        "null"
    }
}

/// Returns no faces. 1:1 with `NullFaceDetector`.
#[derive(Debug, Default, Clone, Copy)]
pub struct NullFaceDetector;
#[async_trait]
impl IFaceDetector for NullFaceDetector {
    async fn detect(&self, _image_bytes: &[u8]) -> Result<Vec<DetectedFace>, VisionError> {
        Ok(Vec::new())
    }
}

/// Returns a zero-vector at the configured dimension. 1:1 with `NullFaceEmbedder`.
#[derive(Debug, Clone, Copy)]
pub struct NullFaceEmbedder {
    dimension: i32,
}
impl NullFaceEmbedder {
    pub fn new(dimension: i32) -> Self {
        Self { dimension }
    }
}
impl Default for NullFaceEmbedder {
    fn default() -> Self {
        Self { dimension: 512 }
    }
}
#[async_trait]
impl IFaceEmbedder for NullFaceEmbedder {
    fn dimension(&self) -> i32 {
        self.dimension
    }
    async fn embed(
        &self,
        _image_bytes: &[u8],
        _face: &DetectedFace,
    ) -> Result<FaceEmbedding, VisionError> {
        Ok(FaceEmbedding {
            vector: vec![0.0; self.dimension.max(0) as usize],
            dimension: self.dimension,
        })
    }
}

/// Reports "no liveness backend" — fail-closed default. 1:1 with
/// `NullFaceLivenessDetector`.
#[derive(Debug, Default, Clone, Copy)]
pub struct NullFaceLivenessDetector;
#[async_trait]
impl IFaceLivenessDetector for NullFaceLivenessDetector {
    async fn check(&self, _image_bytes: &[u8]) -> Result<LivenessResult, VisionError> {
        Ok(LivenessResult {
            is_live: false,
            confidence: 0.0,
            failure_reason: Some("no liveness backend registered".into()),
        })
    }
}

/// Reports unverified — fail-closed default. 1:1 with `NullDocumentVerifier`.
#[derive(Debug, Default, Clone, Copy)]
pub struct NullDocumentVerifier;
#[async_trait]
impl IDocumentVerifier for NullDocumentVerifier {
    async fn verify(
        &self,
        _image_bytes: &[u8],
    ) -> Result<DocumentVerificationResult, VisionError> {
        Ok(DocumentVerificationResult {
            is_valid: false,
            document_type: "unknown".into(),
            issuing_country: "unknown".into(),
            fields: Vec::new(),
            overall_confidence: 0.0,
            warnings: Some(vec!["no document verifier backend registered".into()]),
        })
    }
}

/// Returns no plates. 1:1 with `NullPlateRecognizer`.
#[derive(Debug, Default, Clone, Copy)]
pub struct NullPlateRecognizer;
#[async_trait]
impl IPlateRecognizer for NullPlateRecognizer {
    async fn recognize(
        &self,
        _image_bytes: &[u8],
    ) -> Result<Vec<PlateRecognitionResult>, VisionError> {
        Ok(Vec::new())
    }
}

/// Reports no anomalies; subscribers never fire. 1:1 with
/// `NullBluetoothAnomalyDetector`.
#[derive(Debug, Default, Clone, Copy)]
pub struct NullBluetoothAnomalyDetector;
#[async_trait]
impl IBluetoothAnomalyDetector for NullBluetoothAnomalyDetector {
    fn subscribe(&self, _handler: AnomalyHandler) -> AnomalySubscription {
        AnomalySubscription
    }
    async fn start(&self) -> Result<(), VisionError> {
        Ok(())
    }
    async fn stop(&self) -> Result<(), VisionError> {
        Ok(())
    }
    fn backend_id(&self) -> &str {
        "null"
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// ONNX backends — injected inference + image source, verbatim post-processing.
// ─────────────────────────────────────────────────────────────────────────────

/// A decoded RGB24 image the ONNX preprocessing operates on. Stands in for the
/// `SixLabors.ImageSharp` `Image<Rgb24>`. `pixels` is tightly packed
/// `[r,g,b, r,g,b, …]` in row-major order, `width * height * 3` bytes.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct RgbImage {
    pub width: i32,
    pub height: i32,
    /// Row-major RGB triples.
    pub pixels: Vec<u8>,
}

impl RgbImage {
    pub fn new(width: i32, height: i32, pixels: Vec<u8>) -> Self {
        Self {
            width,
            height,
            pixels,
        }
    }
    /// `(r, g, b)` at pixel `(x, y)`; `(0,0,0)` when out of bounds.
    pub fn pixel(&self, x: i32, y: i32) -> (u8, u8, u8) {
        if x < 0 || y < 0 || x >= self.width || y >= self.height {
            return (0, 0, 0);
        }
        let idx = ((y * self.width + x) * 3) as usize;
        (self.pixels[idx], self.pixels[idx + 1], self.pixels[idx + 2])
    }
}

/// Decodes raw image bytes into an [`RgbImage`]. Stands in for `Image.Load<Rgb24>`
/// — the crate has no image codec, so a host supplies one.
pub trait IImageSource {
    fn load_rgb(&self, image_bytes: &[u8]) -> Result<RgbImage, VisionError>;
}

/// A single-input / single-output ONNX inference session yielding a flat
/// `[batch, channel, box]` float tensor. Stands in for
/// `Microsoft.ML.OnnxRuntime.InferenceSession.Run`. The host implements this
/// against its ORT binding; `run` receives the NCHW input as
/// `(data, n, c, h, w)` and returns `(data, dims)`.
#[async_trait]
pub trait IOnnxSession {
    async fn run(
        &self,
        input: &[f32],
        input_dims: [i32; 4],
    ) -> Result<(Vec<f32>, Vec<i32>), VisionError>;
}

// ── Shared preprocessing / postprocessing (ported verbatim) ──────────────────

/// Letterbox-resize an image into a square `input_size` canvas padded with grey
/// (114,114,114). Returns `(resized, pad_x, pad_y, scale)`. Verbatim port of the
/// C# `OnnxFaceDetector.LetterboxResize`.
pub fn letterbox_resize(image: &RgbImage, input_size: i32) -> (RgbImage, i32, i32, f32) {
    let scale = f32::min(
        input_size as f32 / image.width as f32,
        input_size as f32 / image.height as f32,
    );
    let new_w = (image.width as f32 * scale).round() as i32;
    let new_h = (image.height as f32 * scale).round() as i32;
    let pad_x = (input_size - new_w) / 2;
    let pad_y = (input_size - new_h) / 2;

    let mut pixels = vec![114u8; (input_size * input_size * 3) as usize];
    // Nearest-neighbour resize of the source into the padded region.
    for dy in 0..new_h {
        let sy = ((dy as f32) / scale).floor() as i32;
        for dx in 0..new_w {
            let sx = ((dx as f32) / scale).floor() as i32;
            let (r, g, b) = image.pixel(sx, sy);
            let cx = pad_x + dx;
            let cy = pad_y + dy;
            if cx >= 0 && cy >= 0 && cx < input_size && cy < input_size {
                let idx = ((cy * input_size + cx) * 3) as usize;
                pixels[idx] = r;
                pixels[idx + 1] = g;
                pixels[idx + 2] = b;
            }
        }
    }
    (RgbImage::new(input_size, input_size, pixels), pad_x, pad_y, scale)
}

/// Build an NCHW `[1,3,size,size]` tensor of `pixel/255` in R,G,B channel order —
/// the C# `OnnxFaceDetector.ToTensor` (and the plate recognizer's inline copy).
pub fn to_tensor_rgb_normalised(image: &RgbImage) -> Vec<f32> {
    let w = image.width;
    let h = image.height;
    let mut tensor = vec![0.0f32; (3 * h * w) as usize];
    let plane = (h * w) as usize;
    for y in 0..h {
        for x in 0..w {
            let (r, g, b) = image.pixel(x, y);
            let p = (y * w + x) as usize;
            tensor[p] = r as f32 / 255.0;
            tensor[plane + p] = g as f32 / 255.0;
            tensor[2 * plane + p] = b as f32 / 255.0;
        }
    }
    tensor
}

/// YOLOv8 postprocess: flat `[1, channels, boxes]` → surviving boxes in original
/// pixel space, after confidence threshold + NMS. Verbatim port of the C#
/// `OnnxFaceDetector.PostprocessYolo`.
pub fn postprocess_yolo(
    output: &[f32],
    dims: &[i32],
    orig_w: i32,
    orig_h: i32,
    pad_x: i32,
    pad_y: i32,
    scale: f32,
    confidence_threshold: f32,
    iou_threshold: f32,
) -> Vec<(f32, BoundingBox)> {
    if dims.len() != 3 {
        return Vec::new();
    }
    let boxes = dims[2] as usize;
    let mut candidates: Vec<(f32, BoundingBox)> = Vec::new();
    for n in 0..boxes {
        let cx = output[n];
        let cy = output[boxes + n];
        let bw = output[2 * boxes + n];
        let bh = output[3 * boxes + n];
        let score = output[4 * boxes + n];
        if score < confidence_threshold {
            continue;
        }
        let x1 = (cx - bw / 2.0 - pad_x as f32) / scale;
        let y1 = (cy - bh / 2.0 - pad_y as f32) / scale;
        let x2 = (cx + bw / 2.0 - pad_x as f32) / scale;
        let y2 = (cy + bh / 2.0 - pad_y as f32) / scale;
        let bx = 0.max(x1.floor() as i32);
        let by = 0.max(y1.floor() as i32);
        let bxw = (orig_w - bx).min((x2 - x1).ceil() as i32);
        let bxh = (orig_h - by).min((y2 - y1).ceil() as i32);
        if bxw <= 0 || bxh <= 0 {
            continue;
        }
        candidates.push((score, BoundingBox::new(bx, by, bxw, bxh)));
    }
    non_max_suppression(candidates, iou_threshold)
}

/// Greedy non-max suppression over `(score, box)` candidates. Verbatim port of
/// the C# `NonMaxSuppression`.
pub fn non_max_suppression(
    mut boxes: Vec<(f32, BoundingBox)>,
    iou_threshold: f32,
) -> Vec<(f32, BoundingBox)> {
    boxes.sort_by(|a, b| b.0.partial_cmp(&a.0).unwrap_or(std::cmp::Ordering::Equal));
    let mut kept: Vec<(f32, BoundingBox)> = Vec::new();
    for cand in boxes {
        let mut keep = true;
        for k in &kept {
            if iou(cand.1, k.1) > iou_threshold {
                keep = false;
                break;
            }
        }
        if keep {
            kept.push(cand);
        }
    }
    kept
}

/// Intersection-over-union of two boxes. Verbatim port of the C# `Iou`.
pub fn iou(a: BoundingBox, b: BoundingBox) -> f32 {
    let ax2 = a.x + a.width;
    let ay2 = a.y + a.height;
    let bx2 = b.x + b.width;
    let by2 = b.y + b.height;
    let ix1 = a.x.max(b.x);
    let iy1 = a.y.max(b.y);
    let ix2 = ax2.min(bx2);
    let iy2 = ay2.min(by2);
    let iw = 0.max(ix2 - ix1);
    let ih = 0.max(iy2 - iy1);
    let inter = iw * ih;
    let union = a.width * a.height + b.width * b.height - inter;
    if union == 0 {
        0.0
    } else {
        inter as f32 / union as f32
    }
}

/// Clamp a face region to the image bounds. Verbatim port of the C#
/// `OnnxFaceEmbedder.ClampRegion`.
pub fn clamp_region(region: BoundingBox, image_width: i32, image_height: i32) -> BoundingBox {
    let x = region.x.clamp(0, image_width - 1);
    let y = region.y.clamp(0, image_height - 1);
    let w = region.width.clamp(1, image_width - x);
    let h = region.height.clamp(1, image_height - y);
    BoundingBox::new(x, y, w, h)
}

/// L2-normalise a vector in place (guards against a near-zero norm). Verbatim
/// port of the C# `OnnxFaceEmbedder.L2Normalise`.
pub fn l2_normalise(v: &mut [f32]) {
    let mut sum_sq = 0.0f64;
    for x in v.iter() {
        sum_sq += (*x as f64) * (*x as f64);
    }
    let norm = sum_sq.sqrt() as f32;
    if norm < 1e-9 {
        return;
    }
    for x in v.iter_mut() {
        *x /= norm;
    }
}

/// Crop `region` out of `image` and nearest-neighbour resize to
/// `size`×`size` — the geometry the C# `OnnxFaceEmbedder` does via ImageSharp
/// `Crop(...).Resize(...)`, expressed on [`RgbImage`].
fn crop_and_resize(image: &RgbImage, region: BoundingBox, size: i32) -> RgbImage {
    let mut pixels = vec![0u8; (size * size * 3) as usize];
    for dy in 0..size {
        let sy = region.y + (dy as f32 / size as f32 * region.height as f32).floor() as i32;
        for dx in 0..size {
            let sx = region.x + (dx as f32 / size as f32 * region.width as f32).floor() as i32;
            let (r, g, b) = image.pixel(sx, sy);
            let idx = ((dy * size + dx) * 3) as usize;
            pixels[idx] = r;
            pixels[idx + 1] = g;
            pixels[idx + 2] = b;
        }
    }
    RgbImage::new(size, size, pixels)
}

// ── OnnxFaceDetector ─────────────────────────────────────────────────────────

/// Options for [`OnnxFaceDetector`]. 1:1 with `OnnxFaceDetectorOptions`
/// (defaults: `input_size = 640`, `confidence_threshold = 0.5`,
/// `iou_threshold = 0.45`).
#[derive(Debug, Clone, PartialEq)]
pub struct OnnxFaceDetectorOptions {
    pub model_path: String,
    pub input_size: i32,
    pub confidence_threshold: f32,
    pub iou_threshold: f32,
}
impl OnnxFaceDetectorOptions {
    pub fn new(model_path: impl Into<String>) -> Self {
        Self {
            model_path: model_path.into(),
            input_size: 640,
            confidence_threshold: 0.5,
            iou_threshold: 0.45,
        }
    }
}

/// Real [`IFaceDetector`] backed by a YOLO-family ONNX model. The ORT session and
/// image decode are injected; the letterbox + YOLO postprocess are the ported
/// logic. Generic over `Sess: IOnnxSession` and `Img: IImageSource`.
pub struct OnnxFaceDetector<Sess: IOnnxSession, Img: IImageSource> {
    opts: OnnxFaceDetectorOptions,
    session: Sess,
    images: Img,
}

impl<Sess: IOnnxSession, Img: IImageSource> OnnxFaceDetector<Sess, Img> {
    pub fn new(opts: OnnxFaceDetectorOptions, session: Sess, images: Img) -> Self {
        Self {
            opts,
            session,
            images,
        }
    }
}

#[async_trait]
impl<Sess, Img> IFaceDetector for OnnxFaceDetector<Sess, Img>
where
    Sess: IOnnxSession + Send + Sync,
    Img: IImageSource + Send + Sync,
{
    async fn detect(&self, image_bytes: &[u8]) -> Result<Vec<DetectedFace>, VisionError> {
        if image_bytes.is_empty() {
            return Ok(Vec::new());
        }
        let image = self.images.load_rgb(image_bytes)?;
        let orig_w = image.width;
        let orig_h = image.height;
        let (resized, pad_x, pad_y, scale) = letterbox_resize(&image, self.opts.input_size);
        let tensor = to_tensor_rgb_normalised(&resized);
        let dims = [1, 3, self.opts.input_size, self.opts.input_size];

        // The C# swallows inference failures and returns empty. Preserve that.
        let (output, out_dims) = match self.session.run(&tensor, dims).await {
            Ok(o) => o,
            Err(_) => return Ok(Vec::new()),
        };
        let kept = postprocess_yolo(
            &output,
            &out_dims,
            orig_w,
            orig_h,
            pad_x,
            pad_y,
            scale,
            self.opts.confidence_threshold,
            self.opts.iou_threshold,
        );
        Ok(kept
            .into_iter()
            .map(|(score, bbox)| DetectedFace::new(bbox, score, None))
            .collect())
    }
}

// ── OnnxFaceEmbedder ─────────────────────────────────────────────────────────

/// Options for [`OnnxFaceEmbedder`]. 1:1 with `OnnxFaceEmbedderOptions`
/// (defaults: `input_size = 112`, `dimension = 512`).
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct OnnxFaceEmbedderOptions {
    pub model_path: String,
    pub input_size: i32,
    pub dimension: i32,
}
impl OnnxFaceEmbedderOptions {
    pub fn new(model_path: impl Into<String>) -> Self {
        Self {
            model_path: model_path.into(),
            input_size: 112,
            dimension: 512,
        }
    }
}

/// Real [`IFaceEmbedder`] backed by an ArcFace-family ONNX model. Input is
/// 112×112 BGR mean-subtracted `(pixel - 127.5) / 128.0`; output is L2-normalised.
pub struct OnnxFaceEmbedder<Sess: IOnnxSession, Img: IImageSource> {
    opts: OnnxFaceEmbedderOptions,
    session: Sess,
    images: Img,
}

impl<Sess: IOnnxSession, Img: IImageSource> OnnxFaceEmbedder<Sess, Img> {
    pub fn new(opts: OnnxFaceEmbedderOptions, session: Sess, images: Img) -> Self {
        Self {
            opts,
            session,
            images,
        }
    }

    /// Build the ArcFace NCHW tensor from a 112×112 crop: BGR channel order,
    /// `(pixel - 127.5) / 128.0`. The C# `OnnxFaceEmbedder.EmbedAsync` inline.
    fn to_arcface_tensor(crop: &RgbImage, size: i32) -> Vec<f32> {
        let mut tensor = vec![0.0f32; (3 * size * size) as usize];
        let plane = (size * size) as usize;
        for y in 0..size {
            for x in 0..size {
                let (r, g, b) = crop.pixel(x, y);
                let p = (y * size + x) as usize;
                tensor[p] = (b as f32 - 127.5) / 128.0;
                tensor[plane + p] = (g as f32 - 127.5) / 128.0;
                tensor[2 * plane + p] = (r as f32 - 127.5) / 128.0;
            }
        }
        tensor
    }
}

#[async_trait]
impl<Sess, Img> IFaceEmbedder for OnnxFaceEmbedder<Sess, Img>
where
    Sess: IOnnxSession + Send + Sync,
    Img: IImageSource + Send + Sync,
{
    fn dimension(&self) -> i32 {
        self.opts.dimension
    }

    async fn embed(
        &self,
        image_bytes: &[u8],
        face: &DetectedFace,
    ) -> Result<FaceEmbedding, VisionError> {
        let image = self.images.load_rgb(image_bytes)?;
        let region = clamp_region(face.region, image.width, image.height);
        let crop = crop_and_resize(&image, region, self.opts.input_size);
        let tensor = Self::to_arcface_tensor(&crop, self.opts.input_size);
        let dims = [1, 3, self.opts.input_size, self.opts.input_size];

        // The C# swallows inference failures and returns a zero-vector.
        let (mut raw, _dims) = match self.session.run(&tensor, dims).await {
            Ok(o) => o,
            Err(_) => {
                return Ok(FaceEmbedding {
                    vector: vec![0.0; self.opts.dimension.max(0) as usize],
                    dimension: self.opts.dimension,
                })
            }
        };
        l2_normalise(&mut raw);
        let dim = raw.len() as i32;
        Ok(FaceEmbedding {
            vector: raw,
            dimension: dim,
        })
    }
}

// ── OnnxPlateRecognizer ──────────────────────────────────────────────────────

/// Options for [`OnnxPlateRecognizer`]. 1:1 with `OnnxPlateRecognizerOptions`
/// (defaults: `input_size = 640`, `confidence_threshold = 0.5`,
/// `iou_threshold = 0.45`, `country_hint = None`).
#[derive(Debug, Clone, PartialEq)]
pub struct OnnxPlateRecognizerOptions {
    pub model_path: String,
    pub input_size: i32,
    pub confidence_threshold: f32,
    pub iou_threshold: f32,
    pub country_hint: Option<String>,
}
impl OnnxPlateRecognizerOptions {
    pub fn new(model_path: impl Into<String>) -> Self {
        Self {
            model_path: model_path.into(),
            input_size: 640,
            confidence_threshold: 0.5,
            iou_threshold: 0.45,
            country_hint: None,
        }
    }
}

/// Real [`IPlateRecognizer`] backed by an ONNX detector. Same letterbox + YOLO
/// postprocess as the face detector, but emits [`PlateRecognitionResult`] with
/// empty `plate_text` (OCR is a separate downstream stage, as in the C#).
pub struct OnnxPlateRecognizer<Sess: IOnnxSession, Img: IImageSource> {
    opts: OnnxPlateRecognizerOptions,
    session: Sess,
    images: Img,
}

impl<Sess: IOnnxSession, Img: IImageSource> OnnxPlateRecognizer<Sess, Img> {
    pub fn new(opts: OnnxPlateRecognizerOptions, session: Sess, images: Img) -> Self {
        Self {
            opts,
            session,
            images,
        }
    }
}

#[async_trait]
impl<Sess, Img> IPlateRecognizer for OnnxPlateRecognizer<Sess, Img>
where
    Sess: IOnnxSession + Send + Sync,
    Img: IImageSource + Send + Sync,
{
    async fn recognize(
        &self,
        image_bytes: &[u8],
    ) -> Result<Vec<PlateRecognitionResult>, VisionError> {
        if image_bytes.is_empty() {
            return Ok(Vec::new());
        }
        let image = self.images.load_rgb(image_bytes)?;
        let orig_w = image.width;
        let orig_h = image.height;

        // The C# plate path uses the same letterbox geometry but derives box w/h
        // directly from bw/bh (not x2-x1). Port that exact arithmetic here rather
        // than reusing postprocess_yolo, to stay byte-faithful to the reference.
        let (resized, pad_x, pad_y, scale) = letterbox_resize(&image, self.opts.input_size);
        let tensor = to_tensor_rgb_normalised(&resized);
        let dims = [1, 3, self.opts.input_size, self.opts.input_size];

        let (output, out_dims) = match self.session.run(&tensor, dims).await {
            Ok(o) => o,
            Err(_) => return Ok(Vec::new()),
        };
        if out_dims.len() != 3 {
            return Ok(Vec::new());
        }
        let boxes = out_dims[2] as usize;
        let mut hits: Vec<(f32, BoundingBox)> = Vec::new();
        for n in 0..boxes {
            let cx = output[n];
            let cy = output[boxes + n];
            let bw = output[2 * boxes + n];
            let bh = output[3 * boxes + n];
            let score = output[4 * boxes + n];
            if score < self.opts.confidence_threshold {
                continue;
            }
            let x1 = (cx - bw / 2.0 - pad_x as f32) / scale;
            let y1 = (cy - bh / 2.0 - pad_y as f32) / scale;
            let bx = 0.max(x1.floor() as i32);
            let by = 0.max(y1.floor() as i32);
            let bxw = (orig_w - bx).min((bw / scale).ceil() as i32);
            let bxh = (orig_h - by).min((bh / scale).ceil() as i32);
            if bxw <= 0 || bxh <= 0 {
                continue;
            }
            hits.push((score, BoundingBox::new(bx, by, bxw, bxh)));
        }
        let kept = non_max_suppression(hits, self.opts.iou_threshold);
        Ok(kept
            .into_iter()
            .map(|(score, bbox)| PlateRecognitionResult {
                plate_text: String::new(), // OCR pass is a separate model
                country_hint: self.opts.country_hint.clone(),
                region: bbox,
                confidence: score,
            })
            .collect())
    }
}
