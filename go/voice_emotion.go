// voice_emotion.go
//
// Ports CircleAI.Voice.OnnxSpeechEmotionDetector.cs — the ISpeechEmotionDetector
// implementation, refactored around an injected IEmotionModelRunner seam (the C#
// ONNX InferenceSession is the injected dependency; native/ONNX is out of scope).
//
// PORTED FAITHFULLY:
//   - the Russell-circumplex (arousal, valence) label map, verbatim,
//   - the numerically-stable Softmax (subtract max, exp, argmax by probability),
//   - the SenseAsync control flow: empty audio -> no reading; sample-rate mismatch
//     -> no reading; MaxClipMs clamp; PCM-16 -> float32/32768 windowing; label
//     lowercased; circumplex lookup ((0,0) for unknown).
//
// A deterministic default runner (HashEmotionModelRunner) makes the detector
// genuinely functional with no external dependency: it derives stable per-clip
// logits from the audio bytes, so identical audio always yields the same emotion.

package circleai

import (
	"context"
	"encoding/binary"
	"math"
	"strings"
	"sync"
)

// defaultEmotionLabels is the SUPERB-ER + IEMOCAP standard 4-class layout. Ports
// OnnxSpeechEmotionDetector.DefaultLabels.
var defaultEmotionLabels = []string{"neutral", "happy", "angry", "sad"}

// emotionCircumplex holds Russell-circumplex (arousal, valence) coordinates for
// the standard discrete emotion labels. Ports OnnxSpeechEmotionDetector.Circumplex
// verbatim (case-insensitive lookup; unknown -> (0,0)).
var emotionCircumplex = map[string][2]float64{
	"neutral":    {0.00, 0.00},
	"happy":      {0.55, 0.81},
	"happiness":  {0.55, 0.81},
	"joy":        {0.60, 0.82},
	"angry":      {0.74, -0.62},
	"anger":      {0.74, -0.62},
	"sad":        {-0.43, -0.65},
	"sadness":    {-0.43, -0.65},
	"fear":       {0.78, -0.64},
	"fearful":    {0.78, -0.64},
	"surprise":   {0.85, 0.40},
	"surprised":  {0.85, 0.40},
	"disgust":    {0.45, -0.60},
	"disgusted":  {0.45, -0.60},
	"calm":       {-0.40, 0.45},
	"excited":    {0.82, 0.70},
	"bored":      {-0.65, -0.20},
	"frustrated": {0.55, -0.55},
	"contempt":   {0.20, -0.55},
}

// IEmotionModelRunner is the injected seam that scores a float32 waveform window
// into per-class logits (the C# ONNX InferenceSession's role). The result length
// should match the configured label count.
type IEmotionModelRunner interface {
	// ScoreLogits returns per-class logits for the given normalised waveform.
	ScoreLogits(window []float32) []float32
}

// InMemorySpeechEmotionDetector is a deterministic ISpeechEmotionDetector. It
// windows PCM-16 audio to float32, delegates logit scoring to an IEmotionModelRunner,
// softmaxes the logits, and maps the winning label through the circumplex. Ports
// OnnxSpeechEmotionDetector's logic (ONNX session -> injected runner).
type InMemorySpeechEmotionDetector struct {
	runner       IEmotionModelRunner
	labels       []string
	sampleRateHz int
	maxClipMs    int

	mu       sync.Mutex
	disposed bool
}

// NewInMemorySpeechEmotionDetector constructs a detector. Pass nil runner to use
// the deterministic HashEmotionModelRunner. cfg fields default like the C# record
// (Labels nil -> 4-class default; SampleRateHz<=0 -> 16000; MaxClipMs<=0 -> 8000).
func NewInMemorySpeechEmotionDetector(cfg SpeechEmotionConfig, runner IEmotionModelRunner) *InMemorySpeechEmotionDetector {
	labels := cfg.Labels
	if labels == nil {
		labels = defaultEmotionLabels
	}
	sr := cfg.SampleRateHz
	if sr <= 0 {
		sr = 16000
	}
	maxClip := cfg.MaxClipMs
	if maxClip <= 0 {
		maxClip = 8000
	}
	if runner == nil {
		runner = HashEmotionModelRunner{NClasses: len(labels)}
	}
	return &InMemorySpeechEmotionDetector{runner: runner, labels: labels, sampleRateHz: sr, maxClipMs: maxClip}
}

// Sense senses the emotion in audioPcm16. Returns (frame, true) on a reading, or
// (zero, false) when there is nothing to report (empty audio, sample-rate mismatch,
// or zero usable samples). Ports OnnxSpeechEmotionDetector.SenseAsync.
func (d *InMemorySpeechEmotionDetector) Sense(ctx context.Context, audioPcm16 []byte, sampleRateHz int) (SpeechEmotionFrame, bool, error) {
	d.mu.Lock()
	disposed := d.disposed
	d.mu.Unlock()
	if disposed {
		return SpeechEmotionFrame{}, false, errDisposed("emotion detector")
	}
	if len(audioPcm16) == 0 {
		return SpeechEmotionFrame{}, false, nil
	}
	if sampleRateHz != d.sampleRateHz {
		// Mismatched sample rate — no reading (mirrors the C# debug-log + null).
		return SpeechEmotionFrame{}, false, nil
	}
	if err := ctx.Err(); err != nil {
		return SpeechEmotionFrame{}, false, err
	}

	maxSamples := sampleRateHz * d.maxClipMs / 1000
	nSamples := len(audioPcm16) / 2
	if nSamples > maxSamples {
		nSamples = maxSamples
	}
	if nSamples == 0 {
		return SpeechEmotionFrame{}, false, nil
	}

	window := make([]float32, nSamples)
	for i := 0; i < nSamples; i++ {
		s := int16(binary.LittleEndian.Uint16(audioPcm16[i*2 : i*2+2]))
		window[i] = float32(s) / 32768.0
	}

	logits := d.runner.ScoreLogits(window)
	bestIdx, bestProb := emotionSoftmax(logits)
	label := "unknown"
	if bestIdx >= 0 && bestIdx < len(d.labels) {
		label = d.labels[bestIdx]
	}
	label = strings.ToLower(label)
	arousal, valence := 0.0, 0.0
	if coords, ok := emotionCircumplex[label]; ok {
		arousal, valence = coords[0], coords[1]
	}
	return SpeechEmotionFrame{Label: label, Arousal: arousal, Valence: valence, Probability: bestProb}, true, nil
}

// Close disposes the detector.
func (d *InMemorySpeechEmotionDetector) Close(context.Context) error {
	d.mu.Lock()
	d.disposed = true
	d.mu.Unlock()
	return nil
}

// emotionSoftmax returns the argmax index and its softmax probability. Ports
// OnnxSpeechEmotionDetector.Softmax (numerically stable; empty -> (-1, 0)).
func emotionSoftmax(logits []float32) (int, float64) {
	if len(logits) == 0 {
		return -1, 0
	}
	max := logits[0]
	for i := 1; i < len(logits); i++ {
		if logits[i] > max {
			max = logits[i]
		}
	}
	var denom float64
	for i := 0; i < len(logits); i++ {
		denom += math.Exp(float64(logits[i] - max))
	}
	bestIdx := 0
	bestProb := 0.0
	for i := 0; i < len(logits); i++ {
		p := math.Exp(float64(logits[i]-max)) / denom
		if p > bestProb {
			bestProb = p
			bestIdx = i
		}
	}
	return bestIdx, bestProb
}

// HashEmotionModelRunner is a deterministic IEmotionModelRunner: it derives stable
// per-class logits from the waveform (a rolling checksum spread across NClasses),
// so identical audio yields identical logits. Purely for hermetic operation in
// place of an injected ONNX model.
type HashEmotionModelRunner struct {
	// NClasses is the number of output classes (defaults to 4 when <= 0).
	NClasses int
}

// ScoreLogits produces NClasses deterministic logits from window.
func (r HashEmotionModelRunner) ScoreLogits(window []float32) []float32 {
	n := r.NClasses
	if n <= 0 {
		n = 4
	}
	logits := make([]float32, n)
	if len(window) == 0 {
		return logits
	}
	// Accumulate per-class energy buckets by sample index modulo class count, plus
	// a mean-magnitude term, giving a stable but input-sensitive distribution.
	var totalMag float64
	for i, s := range window {
		mag := math.Abs(float64(s))
		totalMag += mag
		logits[i%n] += float32(mag)
	}
	mean := float32(totalMag / float64(len(window)))
	for i := range logits {
		logits[i] = logits[i] + mean*float32(i+1)
	}
	return logits
}

// Interface guards.
var (
	_ ISpeechEmotionDetector = (*InMemorySpeechEmotionDetector)(nil)
	_ IEmotionModelRunner    = HashEmotionModelRunner{}
)
