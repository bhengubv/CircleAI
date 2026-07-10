// vision_contracts.go
//
// Ports the CircleAI.Vision contract surface:
//   Primitives.cs  -> BoundingBox, LandmarkPoint, DetectedFace, FaceEmbedding,
//                     LivenessResult, DocumentField, DocumentVerificationResult,
//                     PlateRecognitionResult, BluetoothAnomaly
//   Contracts.cs   -> IComputerVisionRuntime, IFaceDetector, IFaceEmbedder,
//                     IFaceLivenessDetector, IDocumentVerifier, IPlateRecognizer,
//                     IBluetoothAnomalyDetector
//   IVideoCapture.cs -> VideoPixelFormat, VideoFrame, IVideoCapture, NullVideoCapture
//   NullImplementations.cs -> NullComputerVisionRuntime, NullFaceDetector,
//                     NullFaceEmbedder, NullFaceLivenessDetector,
//                     NullDocumentVerifier, NullPlateRecognizer,
//                     NullBluetoothAnomalyDetector
//
// MAPPING RULES (mirroring the rest of the flat package):
//   - ValueTask<T>/Task<T>            -> synchronous method returning (T, error),
//                                        ctx is the first parameter.
//   - IReadOnlyList<T>               -> []T (nil for the C# null-default case where
//                                        the record allows a null list; callers must
//                                        len()-check).
//   - IAsyncEnumerable<VideoFrame>   -> <-chan VideoFrame returned from a method that
//                                        takes ctx; the channel closes when the source
//                                        completes or ctx cancels.
//   - IAsyncDisposable              -> Close(ctx) error.
//   - DateTimeOffset                -> time.Time. float -> float32.
//   - readonly record struct         -> a plain comparable struct.
//   - The Subscribe(handler)->IDisposable C# pattern maps to
//     Subscribe(handler)->(unsubscribe func()).
//
// FLAT-PACKAGE DISAMBIGUATION: every exported name is prefixed where a bare name
// would clash with another module already in the package. DocumentField becomes
// VisionDocumentField (a Speech/other module could ship a DocumentField); the rest
// (BoundingBox, DetectedFace, ...) are unique across the tree (verified) and keep
// their C# names.

package circleai

import (
	"context"
	"time"
)

// ---------------------------------------------------------------------------
// Primitives.cs
// ---------------------------------------------------------------------------

// BoundingBox is an axis-aligned rectangle in image-pixel coordinates. Ports the
// BoundingBox readonly record struct.
type BoundingBox struct {
	X      int
	Y      int
	Width  int
	Height int
}

// LandmarkPoint is a 2D point on a detected face (eye centre, mouth corner, ...) in
// image-pixel space. Ports the LandmarkPoint readonly record struct.
type LandmarkPoint struct {
	X int
	Y int
}

// DetectedFace is one detected face with optional landmark fallback. Ports the
// DetectedFace record. Landmarks is nil when the backend supplied none (the C#
// default of `null`).
type DetectedFace struct {
	Region     BoundingBox
	Confidence float32
	Landmarks  []LandmarkPoint
}

// FaceEmbedding is a face embedding suitable for similarity search. Vector is
// normalised so cosine similarity reduces to a dot product. Ports the FaceEmbedding
// record.
type FaceEmbedding struct {
	Vector    []float32
	Dimension int
}

// LivenessResult is the outcome of liveness detection — is the camera seeing a real
// human, a printed photo, a screen replay, a 3D mask, ...? Ports LivenessResult.
type LivenessResult struct {
	IsLive        bool
	Confidence    float32
	FailureReason string // "" when the C# FailureReason is null
}

// VisionDocumentField is one parsed field from an ID document. Ports the
// CircleAI.Vision DocumentField record (prefixed to keep the flat package
// unambiguous).
type VisionDocumentField struct {
	Key        string
	Value      string
	Confidence float32
}

// DocumentVerificationResult is the outcome of KYC document verification. Ports the
// DocumentVerificationResult record. Warnings is nil when the C# default of `null`
// applies.
type DocumentVerificationResult struct {
	IsValid           bool
	DocumentType      string
	IssuingCountry    string
	Fields            []VisionDocumentField
	OverallConfidence float32
	Warnings          []string
}

// PlateRecognitionResult is the outcome of license-plate recognition. Ports the
// PlateRecognitionResult record. CountryHint is "" when the C# value is null.
type PlateRecognitionResult struct {
	PlateText   string
	CountryHint string
	Region      BoundingBox
	Confidence  float32
}

// BluetoothAnomaly is one observed BLE / RF anomaly. Severity 0-1; higher = more
// concerning. Ports the BluetoothAnomaly record.
type BluetoothAnomaly struct {
	Source        string
	Kind          string
	Severity      float32
	Description   string
	ObservedAtUtc time.Time
}

// ---------------------------------------------------------------------------
// Contracts.cs
// ---------------------------------------------------------------------------

// IComputerVisionRuntime is the generic CV-runtime primitive. Consumers that need
// basic image decoding / resize / colour-space ops dispatch through this surface.
// The C# ValueTask<object?> maps to (any, error); the backend-private opaque image
// is modelled as `any`. Ports IComputerVisionRuntime.
type IComputerVisionRuntime interface {
	// DecodeAsync decodes bytes into a backend-private opaque image.
	DecodeAsync(ctx context.Context, imageBytes []byte) (any, error)
	// ResizeAsync resizes an opaque image, returning a new opaque image.
	ResizeAsync(ctx context.Context, image any, width, height int) (any, error)
	// BackendID is the backend self-identification — "compv-3.x", "null", etc.
	BackendID() string
}

// IFaceDetector finds faces in an image. Ports IFaceDetector.
type IFaceDetector interface {
	DetectAsync(ctx context.Context, imageBytes []byte) ([]DetectedFace, error)
}

// IFaceEmbedder converts a detected face into a similarity-search vector. Ports
// IFaceEmbedder.
type IFaceEmbedder interface {
	// Dimension is the embedding dimension the embedder produces.
	Dimension() int
	EmbedAsync(ctx context.Context, imageBytes []byte, face DetectedFace) (FaceEmbedding, error)
}

// IFaceLivenessDetector decides whether the camera is looking at a real person.
// Ports IFaceLivenessDetector.
type IFaceLivenessDetector interface {
	CheckAsync(ctx context.Context, imageBytes []byte) (LivenessResult, error)
}

// IDocumentVerifier parses + verifies a KYC document image. Ports IDocumentVerifier.
type IDocumentVerifier interface {
	VerifyAsync(ctx context.Context, imageBytes []byte) (DocumentVerificationResult, error)
}

// IPlateRecognizer reads a license plate from an image. Ports IPlateRecognizer.
type IPlateRecognizer interface {
	RecognizeAsync(ctx context.Context, imageBytes []byte) ([]PlateRecognitionResult, error)
}

// IBluetoothAnomalyDetector is the surface for AetherNet adversary detection —
// BLE / RF anomalies raised by the platform's Bluetooth radio. Implementations are
// long-running (Start/Stop lifecycle). Ports IBluetoothAnomalyDetector (which is
// IAsyncDisposable -> Close(ctx)).
type IBluetoothAnomalyDetector interface {
	// Subscribe registers an anomaly handler and returns an unsubscribe func. Ports
	// Subscribe(Func<BluetoothAnomaly, ValueTask>)->IDisposable.
	Subscribe(handler func(context.Context, BluetoothAnomaly)) (unsubscribe func())
	// Start begins monitoring. Idempotent.
	Start(ctx context.Context) error
	// Stop stops monitoring. Idempotent.
	Stop(ctx context.Context) error
	// BackendID is the backend self-identification — "bluehound-1.x", "null", etc.
	BackendID() string
	// Close disposes the detector (IAsyncDisposable).
	Close(ctx context.Context) error
}

// ---------------------------------------------------------------------------
// IVideoCapture.cs — VideoPixelFormat, VideoFrame, IVideoCapture, NullVideoCapture
// ---------------------------------------------------------------------------

// VideoPixelFormat enumerates the raw frame pixel layouts a capture can emit. Ports
// the VideoPixelFormat enum with stable ordinals matching the C# declaration order.
type VideoPixelFormat int

const (
	// VideoPixelFormatYuv420 is planar YUV 4:2:0 (ordinal 0).
	VideoPixelFormatYuv420 VideoPixelFormat = iota
	// VideoPixelFormatNv21 is Android NV21 (ordinal 1).
	VideoPixelFormatNv21
	// VideoPixelFormatRgba32 is 32-bit RGBA (ordinal 2).
	VideoPixelFormatRgba32
	// VideoPixelFormatBgr24 is 24-bit BGR (ordinal 3).
	VideoPixelFormatBgr24
	// VideoPixelFormatJpeg is a JPEG-compressed frame (ordinal 4).
	VideoPixelFormatJpeg
)

// String returns the C# enum member name for the pixel format.
func (f VideoPixelFormat) String() string {
	switch f {
	case VideoPixelFormatYuv420:
		return "Yuv420"
	case VideoPixelFormatNv21:
		return "Nv21"
	case VideoPixelFormatRgba32:
		return "Rgba32"
	case VideoPixelFormatBgr24:
		return "Bgr24"
	case VideoPixelFormatJpeg:
		return "Jpeg"
	default:
		return "Yuv420"
	}
}

// VideoFrame is one raw camera frame plus metadata. Ports the VideoFrame record.
// RotationDegrees uses *int so the C# nullable `int?` (default null) is preserved.
type VideoFrame struct {
	Bytes           []byte
	Width           int
	Height          int
	PixelFormat     VideoPixelFormat
	CapturedAtUtc   time.Time
	RotationDegrees *int
}

// IVideoCapture is the async-stream of camera frames. Ports IVideoCapture (which is
// IAsyncDisposable). CaptureAsync opens the camera at the requested resolution and
// streams frames on the returned channel until the source completes or ctx cancels.
type IVideoCapture interface {
	CaptureAsync(ctx context.Context, preferredWidth, preferredHeight int) <-chan VideoFrame
	// Close disposes the capture (IAsyncDisposable).
	Close(ctx context.Context) error
}

// NullVideoCapture is the headless / no-camera fallback — yields nothing. Ports
// NullVideoCapture.
type NullVideoCapture struct{}

// CaptureAsync returns an already-closed channel (yields no frames), matching the C#
// `yield break`. If ctx is already cancelled the channel is still returned closed —
// the C# ThrowIfCancellationRequested throws before the first yield, and since there
// is never a yield the observable behaviour (no frames) is identical.
func (NullVideoCapture) CaptureAsync(ctx context.Context, preferredWidth, preferredHeight int) <-chan VideoFrame {
	out := make(chan VideoFrame)
	close(out)
	return out
}

// Close is a no-op (ValueTask.CompletedTask).
func (NullVideoCapture) Close(context.Context) error { return nil }

// ---------------------------------------------------------------------------
// NullImplementations.cs
// ---------------------------------------------------------------------------

// NullComputerVisionRuntime is the no-op vision runtime. Ports
// NullComputerVisionRuntime.
type NullComputerVisionRuntime struct{}

// NullComputerVisionRuntimeInstance mirrors NullComputerVisionRuntime.Instance.
var NullComputerVisionRuntimeInstance = NullComputerVisionRuntime{}

// BackendID returns "null".
func (NullComputerVisionRuntime) BackendID() string { return "null" }

// DecodeAsync returns (nil, nil) — the C# ValueTask.FromResult<object?>(null).
func (NullComputerVisionRuntime) DecodeAsync(context.Context, []byte) (any, error) {
	return nil, nil
}

// ResizeAsync returns (nil, nil).
func (NullComputerVisionRuntime) ResizeAsync(context.Context, any, int, int) (any, error) {
	return nil, nil
}

// NullFaceDetector returns no faces. Useful as the default DI registration. Ports
// NullFaceDetector.
type NullFaceDetector struct{}

// NullFaceDetectorInstance mirrors NullFaceDetector.Instance.
var NullFaceDetectorInstance = NullFaceDetector{}

// DetectAsync returns an empty slice (Array.Empty<DetectedFace>()).
func (NullFaceDetector) DetectAsync(context.Context, []byte) ([]DetectedFace, error) {
	return []DetectedFace{}, nil
}

// NullFaceEmbedder returns a zero-vector at the configured dimension. Ports
// NullFaceEmbedder(int dimension = 512).
type NullFaceEmbedder struct {
	dimension int
}

// NewNullFaceEmbedder constructs a null embedder. Pass 0 to take the C# default of
// 512.
func NewNullFaceEmbedder(dimension int) *NullFaceEmbedder {
	if dimension == 0 {
		dimension = 512
	}
	return &NullFaceEmbedder{dimension: dimension}
}

// Dimension returns the configured embedding dimension.
func (e *NullFaceEmbedder) Dimension() int { return e.dimension }

// EmbedAsync returns a zero-vector FaceEmbedding at the configured dimension.
func (e *NullFaceEmbedder) EmbedAsync(context.Context, []byte, DetectedFace) (FaceEmbedding, error) {
	return FaceEmbedding{Vector: make([]float32, e.dimension), Dimension: e.dimension}, nil
}

// NullFaceLivenessDetector reports "no liveness backend" — fail-closed default.
// Ports NullFaceLivenessDetector.
type NullFaceLivenessDetector struct{}

// NullFaceLivenessDetectorInstance mirrors NullFaceLivenessDetector.Instance.
var NullFaceLivenessDetectorInstance = NullFaceLivenessDetector{}

// CheckAsync reports not-live with a zero confidence and the fixed failure reason.
func (NullFaceLivenessDetector) CheckAsync(context.Context, []byte) (LivenessResult, error) {
	return LivenessResult{IsLive: false, Confidence: 0, FailureReason: "no liveness backend registered"}, nil
}

// NullDocumentVerifier reports unverified — fail-closed default. Ports
// NullDocumentVerifier.
type NullDocumentVerifier struct{}

// NullDocumentVerifierInstance mirrors NullDocumentVerifier.Instance.
var NullDocumentVerifierInstance = NullDocumentVerifier{}

// VerifyAsync reports an unverified "unknown" document with the fixed warning.
func (NullDocumentVerifier) VerifyAsync(context.Context, []byte) (DocumentVerificationResult, error) {
	return DocumentVerificationResult{
		IsValid:           false,
		DocumentType:      "unknown",
		IssuingCountry:    "unknown",
		Fields:            []VisionDocumentField{},
		OverallConfidence: 0,
		Warnings:          []string{"no document verifier backend registered"},
	}, nil
}

// NullPlateRecognizer returns no plates. Ports NullPlateRecognizer.
type NullPlateRecognizer struct{}

// NullPlateRecognizerInstance mirrors NullPlateRecognizer.Instance.
var NullPlateRecognizerInstance = NullPlateRecognizer{}

// RecognizeAsync returns an empty slice.
func (NullPlateRecognizer) RecognizeAsync(context.Context, []byte) ([]PlateRecognitionResult, error) {
	return []PlateRecognitionResult{}, nil
}

// NullBluetoothAnomalyDetector reports no anomalies; subscribers never fire. Ports
// NullBluetoothAnomalyDetector.
type NullBluetoothAnomalyDetector struct{}

// NullBluetoothAnomalyDetectorInstance is a shared no-op detector.
var NullBluetoothAnomalyDetectorInstance = NullBluetoothAnomalyDetector{}

// BackendID returns "null".
func (NullBluetoothAnomalyDetector) BackendID() string { return "null" }

// Subscribe returns a no-op unsubscribe func (this detector never fires).
func (NullBluetoothAnomalyDetector) Subscribe(func(context.Context, BluetoothAnomaly)) func() {
	return func() {}
}

// Start is a no-op (Task.CompletedTask).
func (NullBluetoothAnomalyDetector) Start(context.Context) error { return nil }

// Stop is a no-op (Task.CompletedTask).
func (NullBluetoothAnomalyDetector) Stop(context.Context) error { return nil }

// Close is a no-op (ValueTask.CompletedTask).
func (NullBluetoothAnomalyDetector) Close(context.Context) error { return nil }

// Interface guards.
var (
	_ IComputerVisionRuntime    = NullComputerVisionRuntime{}
	_ IFaceDetector             = NullFaceDetector{}
	_ IFaceEmbedder             = (*NullFaceEmbedder)(nil)
	_ IFaceLivenessDetector     = NullFaceLivenessDetector{}
	_ IDocumentVerifier         = NullDocumentVerifier{}
	_ IPlateRecognizer          = NullPlateRecognizer{}
	_ IBluetoothAnomalyDetector = NullBluetoothAnomalyDetector{}
	_ IVideoCapture             = NullVideoCapture{}
)
