// vision_test.go
//
// Verifies the CircleAI.Vision Go port (vision_contracts.go,
// vision_face_detector.go, vision_face_embedder.go, vision_plate_recognizer.go,
// vision_capture_inmemory.go):
//   - VideoPixelFormat ordinals + String
//   - Null* defaults (empty faces/plates, zero-vector embed, fail-closed liveness /
//     doc verify, no-op CV runtime, headless video capture, silent BT detector)
//   - OnnxFaceDetector: letterbox back-projection, confidence threshold, NMS,
//     empty-input and model-error fail-soft
//   - OnnxFaceEmbedder: dimension, ArcFace preprocess path, L2-normalised output,
//     region clamp, model-error zero-vector
//   - OnnxPlateRecognizer: bw/scale sizing, CountryHint stamping, threshold, NMS
//   - ScriptedVideoCapture: ordered replay, loop+cancel, defensive copy
//   - InMemoryBluetoothAnomalyDetector: pre-start buffering, fan-out, unsubscribe,
//     unsubscribe-from-inside-handler (no deadlock), stop/close

package circleai_test

import (
	"context"
	"errors"
	"math"
	"testing"
	"time"

	circleai "github.com/bhengubv/CircleAI/go"
)

// ── VideoPixelFormat ────────────────────────────────────────────────────────

func TestVideoPixelFormat_Ordinals(t *testing.T) {
	cases := []struct {
		f    circleai.VideoPixelFormat
		ord  int
		name string
	}{
		{circleai.VideoPixelFormatYuv420, 0, "Yuv420"},
		{circleai.VideoPixelFormatNv21, 1, "Nv21"},
		{circleai.VideoPixelFormatRgba32, 2, "Rgba32"},
		{circleai.VideoPixelFormatBgr24, 3, "Bgr24"},
		{circleai.VideoPixelFormatJpeg, 4, "Jpeg"},
	}
	for _, c := range cases {
		if int(c.f) != c.ord {
			t.Errorf("%s ordinal = %d want %d", c.name, int(c.f), c.ord)
		}
		if c.f.String() != c.name {
			t.Errorf("String = %q want %q", c.f.String(), c.name)
		}
	}
}

// ── Null implementations ────────────────────────────────────────────────────

func TestNullVisionImpls(t *testing.T) {
	ctx := context.Background()

	if circleai.NullComputerVisionRuntimeInstance.BackendID() != "null" {
		t.Error("cv runtime backend id")
	}
	if img, err := circleai.NullComputerVisionRuntimeInstance.DecodeAsync(ctx, []byte{1}); err != nil || img != nil {
		t.Errorf("decode = %v,%v want nil,nil", img, err)
	}
	if img, err := circleai.NullComputerVisionRuntimeInstance.ResizeAsync(ctx, "x", 1, 1); err != nil || img != nil {
		t.Errorf("resize = %v,%v want nil,nil", img, err)
	}

	if faces, err := circleai.NullFaceDetectorInstance.DetectAsync(ctx, []byte{1}); err != nil || len(faces) != 0 {
		t.Errorf("detect = %v,%v", faces, err)
	}

	emb := circleai.NewNullFaceEmbedder(0) // default 512
	if emb.Dimension() != 512 {
		t.Errorf("dim = %d want 512", emb.Dimension())
	}
	fe, err := emb.EmbedAsync(ctx, []byte{1}, circleai.DetectedFace{})
	if err != nil || len(fe.Vector) != 512 || fe.Dimension != 512 {
		t.Fatalf("embed = %+v,%v", fe, err)
	}
	for _, v := range fe.Vector {
		if v != 0 {
			t.Fatal("null embed must be zero vector")
		}
	}
	if circleai.NewNullFaceEmbedder(128).Dimension() != 128 {
		t.Error("custom dim")
	}

	lr, err := circleai.NullFaceLivenessDetectorInstance.CheckAsync(ctx, nil)
	if err != nil || lr.IsLive || lr.Confidence != 0 || lr.FailureReason != "no liveness backend registered" {
		t.Errorf("liveness = %+v,%v", lr, err)
	}

	dv, err := circleai.NullDocumentVerifierInstance.VerifyAsync(ctx, nil)
	if err != nil || dv.IsValid || dv.DocumentType != "unknown" || dv.IssuingCountry != "unknown" {
		t.Errorf("doc = %+v", dv)
	}
	if len(dv.Warnings) != 1 || dv.Warnings[0] != "no document verifier backend registered" {
		t.Errorf("doc warnings = %v", dv.Warnings)
	}

	if plates, err := circleai.NullPlateRecognizerInstance.RecognizeAsync(ctx, nil); err != nil || len(plates) != 0 {
		t.Errorf("plates = %v,%v", plates, err)
	}
}

func TestNullBluetoothAnomalyDetector(t *testing.T) {
	ctx := context.Background()
	d := circleai.NullBluetoothAnomalyDetectorInstance
	if d.BackendID() != "null" {
		t.Error("backend id")
	}
	fired := false
	unsub := d.Subscribe(func(context.Context, circleai.BluetoothAnomaly) { fired = true })
	if err := d.Start(ctx); err != nil {
		t.Fatal(err)
	}
	if err := d.Stop(ctx); err != nil {
		t.Fatal(err)
	}
	unsub()
	if err := d.Close(ctx); err != nil {
		t.Fatal(err)
	}
	if fired {
		t.Error("null detector must never fire")
	}
}

func TestNullVideoCapture(t *testing.T) {
	ctx := context.Background()
	var cap circleai.NullVideoCapture
	n := 0
	for range cap.CaptureAsync(ctx, 640, 480) {
		n++
	}
	if n != 0 {
		t.Errorf("null capture yielded %d frames, want 0", n)
	}
	if err := cap.Close(ctx); err != nil {
		t.Fatal(err)
	}
}

// ── OnnxFaceDetector ────────────────────────────────────────────────────────

// solidImage builds a w×h DecodedImage filled with one RGB colour.
func solidImage(w, h int, r, g, b byte) circleai.DecodedImage {
	rows := make([][]byte, h)
	for y := 0; y < h; y++ {
		row := make([]byte, w*3)
		for x := 0; x < w; x++ {
			row[x*3], row[x*3+1], row[x*3+2] = r, g, b
		}
		rows[y] = row
	}
	return circleai.DecodedImage{Width: w, Height: h, Pixels: rows}
}

// yoloOut packs a [1, channels, boxes] output from per-box [cx,cy,w,h,score] rows.
func yoloOut(boxes [][5]float32) circleai.DetectorOutput {
	n := len(boxes)
	channels := 5
	data := make([]float32, channels*n)
	for i, b := range boxes {
		for c := 0; c < 5; c++ {
			data[c*n+i] = b[c]
		}
	}
	return circleai.DetectorOutput{Channels: channels, Boxes: n, Data: data}
}

func TestOnnxFaceDetector_LetterboxBackProjection(t *testing.T) {
	ctx := context.Background()
	// Square 640×640 source → scale 1, padX=padY=0. A box centred at (320,320) size
	// 100×100 back-projects to X=270,Y=270,W=100,H=100.
	dec := func(context.Context, []byte) (circleai.DecodedImage, error) {
		return solidImage(640, 640, 10, 20, 30), nil
	}
	model := func(_ context.Context, in circleai.DetectorTensor) (circleai.DetectorOutput, error) {
		// Sanity on the tensor packing: value at channel 0 must be 10/255.
		if got := in.At(0, 0, 0); math.Abs(float64(got)-10.0/255.0) > 1e-6 {
			t.Errorf("tensor R = %v want %v", got, 10.0/255.0)
		}
		return yoloOut([][5]float32{{320, 320, 100, 100, 0.9}}), nil
	}
	det, err := circleai.NewOnnxFaceDetector(circleai.DefaultOnnxFaceDetectorOptions("m.onnx"), dec, model)
	if err != nil {
		t.Fatal(err)
	}
	faces, err := det.DetectAsync(ctx, []byte{1, 2, 3})
	if err != nil {
		t.Fatal(err)
	}
	if len(faces) != 1 {
		t.Fatalf("faces = %d want 1", len(faces))
	}
	f := faces[0]
	if f.Region.X != 270 || f.Region.Y != 270 || f.Region.Width != 100 || f.Region.Height != 100 {
		t.Errorf("region = %+v want {270 270 100 100}", f.Region)
	}
	if f.Confidence != 0.9 {
		t.Errorf("conf = %v want 0.9", f.Confidence)
	}
}

func TestOnnxFaceDetector_ThresholdAndNMS(t *testing.T) {
	ctx := context.Background()
	dec := func(context.Context, []byte) (circleai.DecodedImage, error) {
		return solidImage(640, 640, 0, 0, 0), nil
	}
	// Three boxes: two heavily overlapping (0.9, 0.8 → NMS drops the 0.8), one below
	// the 0.5 threshold (dropped), one distinct high-score box.
	model := func(context.Context, circleai.DetectorTensor) (circleai.DetectorOutput, error) {
		return yoloOut([][5]float32{
			{100, 100, 80, 80, 0.90},
			{105, 105, 80, 80, 0.80}, // ~IoU>0.45 with the first → suppressed
			{300, 300, 40, 40, 0.30}, // below threshold → dropped
			{500, 500, 60, 60, 0.70}, // distinct → kept
		}), nil
	}
	det, _ := circleai.NewOnnxFaceDetector(circleai.DefaultOnnxFaceDetectorOptions("m"), dec, model)
	faces, err := det.DetectAsync(ctx, []byte{1})
	if err != nil {
		t.Fatal(err)
	}
	if len(faces) != 2 {
		t.Fatalf("faces = %d want 2 (%v)", len(faces), faces)
	}
	// Sorted by score desc through NMS.
	if faces[0].Confidence != 0.90 || faces[1].Confidence != 0.70 {
		t.Errorf("kept confidences = %v,%v want 0.90,0.70", faces[0].Confidence, faces[1].Confidence)
	}
}

func TestOnnxFaceDetector_EmptyAndModelError(t *testing.T) {
	ctx := context.Background()
	dec := func(context.Context, []byte) (circleai.DecodedImage, error) {
		return solidImage(64, 64, 0, 0, 0), nil
	}
	// Empty input → empty, decoder/model never called.
	det, _ := circleai.NewOnnxFaceDetector(circleai.DefaultOnnxFaceDetectorOptions("m"), dec,
		func(context.Context, circleai.DetectorTensor) (circleai.DetectorOutput, error) {
			t.Fatal("model must not run on empty input")
			return circleai.DetectorOutput{}, nil
		})
	if faces, err := det.DetectAsync(ctx, nil); err != nil || len(faces) != 0 {
		t.Errorf("empty input = %v,%v", faces, err)
	}

	// Model error → fail-soft empty (the C# catch).
	det2, _ := circleai.NewOnnxFaceDetector(circleai.DefaultOnnxFaceDetectorOptions("m"), dec,
		func(context.Context, circleai.DetectorTensor) (circleai.DetectorOutput, error) {
			return circleai.DetectorOutput{}, errors.New("boom")
		})
	if faces, err := det2.DetectAsync(ctx, []byte{1}); err != nil || len(faces) != 0 {
		t.Errorf("model error = %v,%v want empty,nil", faces, err)
	}
}

func TestNewOnnxFaceDetector_RequiresDeps(t *testing.T) {
	if _, err := circleai.NewOnnxFaceDetector(circleai.DefaultOnnxFaceDetectorOptions("m"), nil, nil); err == nil {
		t.Error("nil decoder must error")
	}
	dec := func(context.Context, []byte) (circleai.DecodedImage, error) { return circleai.DecodedImage{}, nil }
	if _, err := circleai.NewOnnxFaceDetector(circleai.DefaultOnnxFaceDetectorOptions("m"), dec, nil); err == nil {
		t.Error("nil model must error")
	}
}

// ── OnnxFaceEmbedder ────────────────────────────────────────────────────────

func TestOnnxFaceEmbedder_NormalisedOutput(t *testing.T) {
	ctx := context.Background()
	opts := circleai.DefaultOnnxFaceEmbedderOptions("arc.onnx")
	if opts.InputSize != 112 || opts.Dimension != 512 {
		t.Fatalf("defaults = %d,%d", opts.InputSize, opts.Dimension)
	}
	dec := func(context.Context, []byte) (circleai.DecodedImage, error) {
		return solidImage(200, 200, 128, 64, 200), nil
	}
	// Model returns a fixed non-unit vector; the embedder must L2-normalise it.
	model := func(_ context.Context, in circleai.DetectorTensor) ([]float32, error) {
		if in.Width != 112 || in.Height != 112 || in.Channels != 3 {
			t.Errorf("input dims = %d×%d×%d want 3×112×112", in.Channels, in.Height, in.Width)
		}
		// ArcFace preprocess for R=128 on channel 2: (128-127.5)/128 ≈ 0.003906.
		if got := in.At(2, 0, 0); math.Abs(float64(got)-(128-127.5)/128.0) > 1e-6 {
			t.Errorf("preproc R = %v", got)
		}
		return []float32{3, 4}, nil // norm 5 → {0.6, 0.8}
	}
	emb, err := circleai.NewOnnxFaceEmbedder(opts, dec, model)
	if err != nil {
		t.Fatal(err)
	}
	fe, err := emb.EmbedAsync(ctx, []byte{1}, circleai.DetectedFace{
		Region: circleai.BoundingBox{X: 10, Y: 10, Width: 50, Height: 50},
	})
	if err != nil {
		t.Fatal(err)
	}
	if fe.Dimension != 2 {
		t.Fatalf("dim = %d want 2", fe.Dimension)
	}
	if math.Abs(float64(fe.Vector[0])-0.6) > 1e-6 || math.Abs(float64(fe.Vector[1])-0.8) > 1e-6 {
		t.Errorf("vector = %v want [0.6 0.8]", fe.Vector)
	}
}

func TestOnnxFaceEmbedder_ModelErrorZeroVector(t *testing.T) {
	ctx := context.Background()
	dec := func(context.Context, []byte) (circleai.DecodedImage, error) {
		return solidImage(64, 64, 0, 0, 0), nil
	}
	emb, _ := circleai.NewOnnxFaceEmbedder(circleai.DefaultOnnxFaceEmbedderOptions("m"), dec,
		func(context.Context, circleai.DetectorTensor) ([]float32, error) {
			return nil, errors.New("boom")
		})
	fe, err := emb.EmbedAsync(ctx, []byte{1}, circleai.DetectedFace{Region: circleai.BoundingBox{X: 0, Y: 0, Width: 10, Height: 10}})
	if err != nil {
		t.Fatal(err)
	}
	if len(fe.Vector) != 512 {
		t.Fatalf("zero vector len = %d want 512", len(fe.Vector))
	}
	for _, v := range fe.Vector {
		if v != 0 {
			t.Fatal("model error must yield zero vector")
		}
	}
}

func TestOnnxFaceEmbedder_RegionClampDoesNotPanic(t *testing.T) {
	ctx := context.Background()
	dec := func(context.Context, []byte) (circleai.DecodedImage, error) {
		return solidImage(32, 32, 255, 255, 255), nil
	}
	captured := false
	emb, _ := circleai.NewOnnxFaceEmbedder(circleai.DefaultOnnxFaceEmbedderOptions("m"), dec,
		func(context.Context, circleai.DetectorTensor) ([]float32, error) {
			captured = true
			return make([]float32, 512), nil
		})
	// Region far outside the image → clamped to inside; must not panic.
	_, err := emb.EmbedAsync(ctx, []byte{1}, circleai.DetectedFace{
		Region: circleai.BoundingBox{X: 1000, Y: 1000, Width: 5000, Height: 5000},
	})
	if err != nil {
		t.Fatal(err)
	}
	if !captured {
		t.Error("model should have run over clamped region")
	}
}

// ── OnnxPlateRecognizer ─────────────────────────────────────────────────────

func TestOnnxPlateRecognizer_SizingAndCountryHint(t *testing.T) {
	ctx := context.Background()
	opts := circleai.DefaultOnnxPlateRecognizerOptions("plate.onnx")
	opts.CountryHint = "ZA"
	dec := func(context.Context, []byte) (circleai.DecodedImage, error) {
		return solidImage(640, 640, 0, 0, 0), nil // scale 1, no pad
	}
	// cx=320,cy=320,w=120,h=40,score=0.8. x1 = 320-60 = 260, y1 = 320-20 = 300.
	// Plate sizing: bxw = ceil(120/1) = 120, bxh = ceil(40/1) = 40.
	model := func(context.Context, circleai.DetectorTensor) (circleai.DetectorOutput, error) {
		return yoloOut([][5]float32{{320, 320, 120, 40, 0.8}}), nil
	}
	rec, err := circleai.NewOnnxPlateRecognizer(opts, dec, model)
	if err != nil {
		t.Fatal(err)
	}
	plates, err := rec.RecognizeAsync(ctx, []byte{1})
	if err != nil {
		t.Fatal(err)
	}
	if len(plates) != 1 {
		t.Fatalf("plates = %d want 1", len(plates))
	}
	p := plates[0]
	if p.Region.X != 260 || p.Region.Y != 300 || p.Region.Width != 120 || p.Region.Height != 40 {
		t.Errorf("region = %+v want {260 300 120 40}", p.Region)
	}
	if p.CountryHint != "ZA" {
		t.Errorf("country = %q want ZA", p.CountryHint)
	}
	if p.PlateText != "" {
		t.Errorf("text = %q want empty (OCR is a separate pass)", p.PlateText)
	}
	if p.Confidence != 0.8 {
		t.Errorf("conf = %v", p.Confidence)
	}
}

func TestOnnxPlateRecognizer_EmptyInput(t *testing.T) {
	ctx := context.Background()
	dec := func(context.Context, []byte) (circleai.DecodedImage, error) {
		return solidImage(8, 8, 0, 0, 0), nil
	}
	rec, _ := circleai.NewOnnxPlateRecognizer(circleai.DefaultOnnxPlateRecognizerOptions("m"), dec,
		func(context.Context, circleai.DetectorTensor) (circleai.DetectorOutput, error) {
			t.Fatal("model must not run on empty input")
			return circleai.DetectorOutput{}, nil
		})
	if plates, err := rec.RecognizeAsync(ctx, nil); err != nil || len(plates) != 0 {
		t.Errorf("empty = %v,%v", plates, err)
	}
}

// ── ScriptedVideoCapture ────────────────────────────────────────────────────

func TestScriptedVideoCapture_OrderedReplay(t *testing.T) {
	ctx := context.Background()
	frames := []circleai.VideoFrame{
		{Bytes: []byte{1}, Width: 2, Height: 2, PixelFormat: circleai.VideoPixelFormatRgba32, CapturedAtUtc: time.Unix(1, 0)},
		{Bytes: []byte{2}, Width: 2, Height: 2, PixelFormat: circleai.VideoPixelFormatRgba32, CapturedAtUtc: time.Unix(2, 0)},
	}
	cap := circleai.NewScriptedVideoCapture(frames)
	var got [][]byte
	for f := range cap.CaptureAsync(ctx, 2, 2) {
		got = append(got, f.Bytes)
	}
	if len(got) != 2 || got[0][0] != 1 || got[1][0] != 2 {
		t.Fatalf("replay = %v want [[1] [2]]", got)
	}
}

func TestScriptedVideoCapture_LoopUntilCancel(t *testing.T) {
	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()
	frames := []circleai.VideoFrame{{Bytes: []byte{7}, Width: 1, Height: 1}}
	cap := circleai.NewScriptedVideoCapture(frames).WithLoop(true)
	ch := cap.CaptureAsync(ctx, 1, 1)
	count := 0
	for range ch {
		count++
		if count == 5 {
			cancel()
			break
		}
	}
	if count != 5 {
		t.Errorf("looped %d times before cancel, want 5", count)
	}
}

func TestScriptedVideoCapture_DefensiveCopy(t *testing.T) {
	ctx := context.Background()
	src := []byte{9}
	frames := []circleai.VideoFrame{{Bytes: src, Width: 1, Height: 1}}
	cap := circleai.NewScriptedVideoCapture(frames)
	src[0] = 42 // mutate after construction
	for f := range cap.CaptureAsync(ctx, 1, 1) {
		if f.Bytes[0] != 9 {
			t.Errorf("frame byte = %d, want 9 (defensive copy failed)", f.Bytes[0])
		}
	}
}

// ── InMemoryBluetoothAnomalyDetector ────────────────────────────────────────

func mkAnomaly(kind string) circleai.BluetoothAnomaly {
	return circleai.BluetoothAnomaly{Source: "ble", Kind: kind, Severity: 0.5, Description: kind, ObservedAtUtc: time.Unix(0, 0)}
}

func TestBTAnomaly_PreStartBufferingAndFanOut(t *testing.T) {
	ctx := context.Background()
	d := circleai.NewInMemoryBluetoothAnomalyDetector("")
	if d.BackendID() != "in-memory" {
		t.Errorf("backend = %q", d.BackendID())
	}

	var aGot, bGot []string
	d.Subscribe(func(_ context.Context, a circleai.BluetoothAnomaly) { aGot = append(aGot, a.Kind) })
	d.Subscribe(func(_ context.Context, a circleai.BluetoothAnomaly) { bGot = append(bGot, a.Kind) })

	// Published before Start → buffered, flushed to BOTH subscribers on Start.
	d.Publish(ctx, mkAnomaly("pre1"))
	d.Publish(ctx, mkAnomaly("pre2"))
	if len(aGot) != 0 {
		t.Fatal("must not deliver before Start")
	}
	if err := d.Start(ctx); err != nil {
		t.Fatal(err)
	}
	if len(aGot) != 2 || aGot[0] != "pre1" || aGot[1] != "pre2" {
		t.Errorf("flush a = %v want [pre1 pre2]", aGot)
	}
	if len(bGot) != 2 {
		t.Errorf("flush b = %v want 2", bGot)
	}

	// Live publish fans out to both.
	d.Publish(ctx, mkAnomaly("live"))
	if aGot[len(aGot)-1] != "live" || bGot[len(bGot)-1] != "live" {
		t.Error("live fan-out failed")
	}
}

func TestBTAnomaly_Unsubscribe(t *testing.T) {
	ctx := context.Background()
	d := circleai.NewInMemoryBluetoothAnomalyDetector("bh")
	_ = d.Start(ctx)
	n := 0
	unsub := d.Subscribe(func(context.Context, circleai.BluetoothAnomaly) { n++ })
	d.Publish(ctx, mkAnomaly("x"))
	unsub()
	d.Publish(ctx, mkAnomaly("y"))
	if n != 1 {
		t.Errorf("delivered %d, want 1 (unsubscribe ignored)", n)
	}
}

// A handler that unsubscribes itself must not deadlock: fan-out snapshots the subs
// under the lock, releases it, then invokes handlers.
func TestBTAnomaly_UnsubscribeFromInsideHandler(t *testing.T) {
	ctx := context.Background()
	d := circleai.NewInMemoryBluetoothAnomalyDetector("bh")
	_ = d.Start(ctx)
	var unsub func()
	n := 0
	unsub = d.Subscribe(func(context.Context, circleai.BluetoothAnomaly) {
		n++
		unsub() // re-takes the lock; must not deadlock
	})
	done := make(chan struct{})
	go func() {
		d.Publish(ctx, mkAnomaly("z"))
		close(done)
	}()
	select {
	case <-done:
	case <-time.After(2 * time.Second):
		t.Fatal("deadlock: handler unsubscribing under fan-out")
	}
	if n != 1 {
		t.Errorf("n = %d want 1", n)
	}
	// Subsequent publish reaches nobody.
	d.Publish(ctx, mkAnomaly("z2"))
	if n != 1 {
		t.Errorf("n = %d after unsub, want 1", n)
	}
}

func TestBTAnomaly_StopBuffersAndCloseDisposes(t *testing.T) {
	ctx := context.Background()
	d := circleai.NewInMemoryBluetoothAnomalyDetector("bh")
	got := 0
	d.Subscribe(func(context.Context, circleai.BluetoothAnomaly) { got++ })
	_ = d.Start(ctx)
	_ = d.Stop(ctx)
	// While stopped, publishes buffer; a re-Start flushes them.
	d.Publish(ctx, mkAnomaly("s1"))
	if got != 0 {
		t.Fatal("stopped detector must not deliver")
	}
	_ = d.Start(ctx)
	if got != 1 {
		t.Errorf("re-start flush = %d want 1", got)
	}
	// Close disposes: Start/Stop report disposed, Publish is a no-op.
	if err := d.Close(ctx); err != nil {
		t.Fatal(err)
	}
	if err := d.Start(ctx); err == nil {
		t.Error("Start after Close must error")
	}
	if err := d.Stop(ctx); err == nil {
		t.Error("Stop after Close must error")
	}
	d.Publish(ctx, mkAnomaly("after-close")) // no-op, must not panic
}
