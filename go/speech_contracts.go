// speech_contracts.go
//
// Ports CircleAI.Speech.Contracts.cs and CircleAI.Speech.NullImplementations.cs
// — the ASR / TTS / wake-word / echo / noise / end-of-turn / VAD / OCR contract
// surface for B! Butler's voice loop, plus the fail-closed "null" defaults for
// each contract.
//
// FLAT-PACKAGE DISAMBIGUATION: CircleAI.Voice declares its OWN, differently
// shaped TranscriptionResult / IWakeWordDetector / IVoiceActivityDetector /
// NullWakeWordDetector / NullVoiceActivityDetector. Since Go has a single flat
// `circleai` package, the Speech-namespace variants are prefixed with `Speech`
// (SpeechTranscriptionResult, ISpeechWakeWordDetector, ISpeechVoiceActivityDetector,
// NullSpeechWakeWordDetector, NullSpeechVoiceActivityDetector). The Voice-namespace
// variants keep the unprefixed name.
//
// C# ValueTask<T>/Task<T> map to (T, error). ReadOnlyMemory<byte>/ReadOnlySpan<byte>
// map to []byte. TimeSpan maps to time.Duration. DateTimeOffset maps to time.Time
// (UTC). IAsyncDisposable maps to a Close(ctx) method. The wake-word
// Subscribe(handler)->IDisposable maps to Subscribe(handler)->(unsubscribe func()).

package circleai

import (
	"context"
	"time"
)

// ---------------------------------------------------------------------------
// Records (Contracts.cs)
// ---------------------------------------------------------------------------

// TranscribedSegment is one transcribed segment. Ports the TranscribedSegment
// record.
type TranscribedSegment struct {
	// Text is the recognised text for this segment.
	Text string
	// Offset is the segment start offset relative to the stream start.
	Offset time.Duration
	// Duration is the segment length.
	Duration time.Duration
	// Language is the detected language (nil-equivalent: empty string).
	Language string
	// Confidence is 0..1.
	Confidence float32
}

// SpeechTranscriptionResult is the outcome of one ASR call. Ports the
// CircleAI.Speech TranscriptionResult record (prefixed to avoid colliding with
// the CircleAI.Voice TranscriptionResult -> VoiceTranscriptionResult).
type SpeechTranscriptionResult struct {
	// Text is the full recognised text.
	Text string
	// Language is the detected language (may be empty).
	Language string
	// Segments are the per-segment breakdown.
	Segments []TranscribedSegment
	// TotalDuration is the total audio duration recognised.
	TotalDuration time.Duration
}

// SynthesisResult is the outcome of one TTS call. Ports the SynthesisResult
// record.
type SynthesisResult struct {
	// AudioPcm16Mono is the synthesised PCM-16 mono audio.
	AudioPcm16Mono []byte
	// SampleRateHz is the sample rate of the audio.
	SampleRateHz int
	// Duration is the audio length.
	Duration time.Duration
}

// OcrResult is one OCR result. Ports the OcrResult record.
type OcrResult struct {
	// Text is the full recognised text.
	Text string
	// Blocks are the detected text blocks.
	Blocks []OcrTextBlock
}

// OcrTextBlock is one detected text block in an OCR result. Ports the
// OcrTextBlock record.
type OcrTextBlock struct {
	// Text is the block text.
	Text string
	// X is the block left edge in pixels.
	X int
	// Y is the block top edge in pixels.
	Y int
	// Width is the block width in pixels.
	Width int
	// Height is the block height in pixels.
	Height int
	// Confidence is 0..1.
	Confidence float32
	// Language is the block language (may be empty).
	Language string
}

// WakeWordEvent is one wake-word fire. Ports the WakeWordEvent record.
type WakeWordEvent struct {
	// Keyword is the wake word that fired.
	Keyword string
	// Confidence is 0..1.
	Confidence float32
	// DetectedAtUtc is the fire time (UTC).
	DetectedAtUtc time.Time
}

// EndOfTurnResult is a verdict on whether a partial transcript represents a
// finished thought. Ports the EndOfTurnResult record.
type EndOfTurnResult struct {
	// IsComplete is true if the speaker likely finished their turn.
	IsComplete bool
	// Confidence is 0..1.
	Confidence float32
	// WaitMoreMs, when IsComplete is false, is how many extra ms to wait before re-asking.
	WaitMoreMs int
}

// VadFrameResult is one verdict from a voice-activity detector (Speech
// per-frame variant). Ports the VadFrameResult record.
type VadFrameResult struct {
	// IsSpeech is true if this frame contains speech.
	IsSpeech bool
	// SpeechProbability is 0..1 confidence the frame is speech.
	SpeechProbability float32
	// Offset is the frame start offset relative to the stream start.
	Offset time.Duration
}

// ---------------------------------------------------------------------------
// Interfaces (Contracts.cs)
// ---------------------------------------------------------------------------

// ISpeechRecognizer converts audio to text. Ports ISpeechRecognizer.
type ISpeechRecognizer interface {
	// BackendID is the backend self-identification — "funasr-1.x" / "yapsnap" / "null".
	BackendID() string
	// Transcribe recognises one buffer of PCM-16 mono audio. languageHint may be
	// empty. Returns the transcription result.
	Transcribe(ctx context.Context, audioPcm16Mono []byte, sampleRateHz int, languageHint string) (SpeechTranscriptionResult, error)
}

// ISpeechSynthesizer converts text to spoken audio. Ports ISpeechSynthesizer.
type ISpeechSynthesizer interface {
	// BackendID is the backend self-identification — "chattts" / "null".
	BackendID() string
	// Synthesize synthesises one utterance. voiceID / languageHint may be empty.
	// Returns PCM-16 mono audio.
	Synthesize(ctx context.Context, text, voiceID, languageHint string) (SynthesisResult, error)
}

// ISpeechWakeWordDetector spots a wake word ("Hey B") in a continuous audio
// stream. Implementations are long-running (Start/Stop) and disposable. Ports
// the CircleAI.Speech IWakeWordDetector (prefixed to avoid colliding with the
// CircleAI.Voice IWakeWordDetector).
type ISpeechWakeWordDetector interface {
	// BackendID is the backend self-identification — "hey-snips" / "null".
	BackendID() string
	// Subscribe registers a handler for wake-word fire events and returns an
	// idempotent unsubscribe func.
	Subscribe(handler func(WakeWordEvent)) (unsubscribe func())
	// Start begins listening on the system mic. Idempotent.
	Start(ctx context.Context) error
	// Stop stops listening. Idempotent.
	Stop(ctx context.Context) error
	// Close disposes the detector (ports IAsyncDisposable.DisposeAsync).
	Close(ctx context.Context) error
}

// IEchoCanceller is an acoustic echo canceller — subtracts the far-end
// reference from the near-end mic input. Ports IEchoCanceller.
type IEchoCanceller interface {
	// BackendID is the backend self-identification — "nlms" / "webrtc-aec3" / "null".
	BackendID() string
	// Cancel cancels echo of farEndReference out of nearEndMicrophone, writing
	// the result into destination. Both inputs must be the same sample rate and
	// length (PCM-16 mono). Returns the number of bytes written.
	Cancel(nearEndMicrophone, farEndReference []byte, sampleRateHz int, destination []byte) int
	// Reset resets adaptive-filter state at the start of a new call.
	Reset()
}

// INoiseReducer cleans a frame of PCM-16 mono audio. Ports INoiseReducer.
type INoiseReducer interface {
	// BackendID is the backend self-identification — "krisp" / "deepfilternet" / "passthrough" / "null".
	BackendID() string
	// IsAvailable is true when the underlying model / runtime is available.
	IsAvailable() bool
	// Reduce reduces noise in audioPcm16Mono and writes into destination (which
	// must be at least as long as the input). Returns the number of bytes written.
	Reduce(audioPcm16Mono []byte, sampleRateHz int, destination []byte) int
}

// IEndOfTurnDetector decides whether the caller has finished their turn given
// the latest partial transcript + the trailing-silence duration. Ports
// IEndOfTurnDetector.
type IEndOfTurnDetector interface {
	// BackendID is the backend self-identification — "rules" / "smart-turn-v2" / "null".
	BackendID() string
	// Predict classifies the current state.
	Predict(partialTranscript string, trailingSilence time.Duration) EndOfTurnResult
	// Reset resets internal state at the start of a fresh turn.
	Reset()
}

// ISpeechVoiceActivityDetector classifies each 10-30 ms audio frame as speech
// or silence. Ports the CircleAI.Speech IVoiceActivityDetector (prefixed to
// avoid colliding with the stream-based CircleAI.Voice IVoiceActivityDetector).
type ISpeechVoiceActivityDetector interface {
	// BackendID is the backend self-identification — "energy" / "silero" / "null".
	BackendID() string
	// SpeechThreshold is the speech-probability threshold for IsSpeech.
	SpeechThreshold() float32
	// Classify classifies one frame of PCM-16 mono audio.
	Classify(audioPcm16Mono []byte, sampleRateHz int, offset time.Duration) VadFrameResult
	// Reset resets any internal hangover state at the start of a fresh utterance.
	Reset()
}

// IOpticalCharacterRecognizer reads text out of an image. Ports
// IOpticalCharacterRecognizer.
type IOpticalCharacterRecognizer interface {
	// BackendID is the backend self-identification — "paddleocr-2.x" / "null".
	BackendID() string
	// Recognize recognises text in an image. languageHint e.g. "eng" / "chi" / "auto".
	Recognize(ctx context.Context, imageBytes []byte, languageHint string) (OcrResult, error)
}

// ---------------------------------------------------------------------------
// Null implementations (NullImplementations.cs)
// ---------------------------------------------------------------------------

// NullSpeechRecognizer is the fail-closed default ISpeechRecognizer. Ports
// NullSpeechRecognizer.
type NullSpeechRecognizer struct{}

// NullSpeechRecognizerInstance mirrors NullSpeechRecognizer.Instance.
var NullSpeechRecognizerInstance = NullSpeechRecognizer{}

// BackendID returns "null".
func (NullSpeechRecognizer) BackendID() string { return "null" }

// Transcribe returns an empty result echoing the language hint.
func (NullSpeechRecognizer) Transcribe(_ context.Context, _ []byte, _ int, languageHint string) (SpeechTranscriptionResult, error) {
	return SpeechTranscriptionResult{
		Text:          "",
		Language:      languageHint,
		Segments:      []TranscribedSegment{},
		TotalDuration: 0,
	}, nil
}

// NullSpeechSynthesizer is the fail-closed default ISpeechSynthesizer. Ports
// NullSpeechSynthesizer.
type NullSpeechSynthesizer struct{}

// NullSpeechSynthesizerInstance mirrors NullSpeechSynthesizer.Instance.
var NullSpeechSynthesizerInstance = NullSpeechSynthesizer{}

// BackendID returns "null".
func (NullSpeechSynthesizer) BackendID() string { return "null" }

// Synthesize returns empty audio at 16 kHz.
func (NullSpeechSynthesizer) Synthesize(_ context.Context, _, _, _ string) (SynthesisResult, error) {
	return SynthesisResult{
		AudioPcm16Mono: []byte{},
		SampleRateHz:   16000,
		Duration:       0,
	}, nil
}

// NullSpeechWakeWordDetector is the fail-closed default ISpeechWakeWordDetector.
// It never fires. Ports NullWakeWordDetector (CircleAI.Speech).
type NullSpeechWakeWordDetector struct{}

// BackendID returns "null".
func (NullSpeechWakeWordDetector) BackendID() string { return "null" }

// Subscribe returns a no-op unsubscribe func (the detector never fires).
func (NullSpeechWakeWordDetector) Subscribe(_ func(WakeWordEvent)) (unsubscribe func()) {
	return func() {}
}

// Start is a no-op.
func (NullSpeechWakeWordDetector) Start(context.Context) error { return nil }

// Stop is a no-op.
func (NullSpeechWakeWordDetector) Stop(context.Context) error { return nil }

// Close is a no-op.
func (NullSpeechWakeWordDetector) Close(context.Context) error { return nil }

// NullOpticalCharacterRecognizer is the fail-closed default OCR. Ports
// NullOpticalCharacterRecognizer.
type NullOpticalCharacterRecognizer struct{}

// NullOpticalCharacterRecognizerInstance mirrors
// NullOpticalCharacterRecognizer.Instance.
var NullOpticalCharacterRecognizerInstance = NullOpticalCharacterRecognizer{}

// BackendID returns "null".
func (NullOpticalCharacterRecognizer) BackendID() string { return "null" }

// Recognize returns an empty result.
func (NullOpticalCharacterRecognizer) Recognize(context.Context, []byte, string) (OcrResult, error) {
	return OcrResult{Text: "", Blocks: []OcrTextBlock{}}, nil
}

// Interface guards.
var (
	_ ISpeechRecognizer             = NullSpeechRecognizer{}
	_ ISpeechSynthesizer            = NullSpeechSynthesizer{}
	_ ISpeechWakeWordDetector       = NullSpeechWakeWordDetector{}
	_ IOpticalCharacterRecognizer   = NullOpticalCharacterRecognizer{}
)
