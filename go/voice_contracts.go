// voice_contracts.go
//
// Ports the CircleAI.Voice contract surface:
//   AudioFormat.cs                 -> AudioFormat (+ AudioFormatPcm16Mono16k)
//   IVoiceTranscriber.cs           -> IVoiceTranscriber, VoiceTranscriptionResult,
//                                     PartialTranscription
//   IWakeWordDetector.cs           -> IWakeWordDetector, WakeWordDetectedEventArgs
//   IVoiceActivityDetector.cs      -> IVoiceActivityDetector, VadSegment
//   ITtsEngine.cs                  -> ITtsEngine, TtsSynthesisResult
//   OnnxSpeechEmotionDetector.cs   -> ISpeechEmotionDetector, SpeechEmotionFrame,
//                                     SpeechEmotionConfig (interface + records only;
//                                     the deterministic impl is voice_emotion.go)
//   OnnxSpeakerIdentity.cs         -> ISpeakerIdentity, EnrolledSpeaker,
//                                     SpeakerIdentityConfig, SpeakerEmbedderInputKind
//                                     (interface + records only; the deterministic
//                                     impl is voice_speaker_identity.go)
//   Null*.cs                       -> NullVoiceTranscriber, NullWakeWordDetector,
//                                     NullVoiceActivityDetector, NullTtsEngine
//
// FLAT-PACKAGE DISAMBIGUATION: the CircleAI.Voice TranscriptionResult is ported
// as VoiceTranscriptionResult (the CircleAI.Speech TranscriptionResult ->
// SpeechTranscriptionResult). IWakeWordDetector / IVoiceActivityDetector /
// NullWakeWordDetector keep the unprefixed Voice name (the Speech per-frame
// variants are the ...Speech... names in speech_*.go). NullVoiceActivityDetector
// is stream-based here; the Speech per-frame null is NullSpeechVoiceActivityDetector.
//
// STREAMS: C# IAsyncEnumerable<T> maps to a <-chan T returned from a method that
// takes a context; the channel closes when the source completes or ctx cancels.
// IAsyncDisposable maps to Close(ctx). The wake-word C# event maps to
// Subscribe(handler)->(unsubscribe func()).

package circleai

import (
	"context"
	"time"
)

// ---------------------------------------------------------------------------
// AudioFormat (AudioFormat.cs)
// ---------------------------------------------------------------------------

// AudioFormat describes a PCM audio format expected or produced by voice
// components. Ports the AudioFormat record.
type AudioFormat struct {
	// SampleRate is samples per second (e.g. 16000 for 16 kHz).
	SampleRate int
	// Channels is the number of interleaved channels (1 = mono, 2 = stereo).
	Channels int
	// BitsPerSample is the bit depth of each sample (e.g. 16 for signed 16-bit PCM).
	BitsPerSample int
}

// AudioFormatPcm16Mono16k is the canonical input format expected by Butler / B!
// voice components: PCM signed 16-bit, mono, 16 kHz. Ports
// AudioFormat.Pcm16Mono16k.
var AudioFormatPcm16Mono16k = AudioFormat{SampleRate: 16000, Channels: 1, BitsPerSample: 16}

// ---------------------------------------------------------------------------
// Transcription (IVoiceTranscriber.cs)
// ---------------------------------------------------------------------------

// VoiceTranscriptionResult is the final result produced by
// IVoiceTranscriber.Transcribe. Ports the CircleAI.Voice TranscriptionResult
// record (prefixed to avoid colliding with SpeechTranscriptionResult).
type VoiceTranscriptionResult struct {
	// Text is the recognised text. Empty string if nothing was recognised.
	Text string
	// Confidence is the engine-reported confidence in [0, 1].
	Confidence float32
	// LanguageCode is the detected language as a BCP-47 / ISO 639 code
	// (e.g. "en", "zu", "und" for unknown).
	LanguageCode string
}

// PartialTranscription is a partial or final transcription produced during
// streaming recognition. Ports the PartialTranscription record.
type PartialTranscription struct {
	// Text is the recognised text so far.
	Text string
	// IsFinal is true when this is the final transcription for the current
	// utterance; false for in-progress hypotheses that may still change.
	IsFinal bool
	// Confidence is the engine-reported confidence in [0, 1].
	Confidence float32
}

// IVoiceTranscriber converts captured audio into text. Implementations consume
// PCM 16-bit, 16 kHz mono input (AudioFormatPcm16Mono16k) unless documented
// otherwise. Ports IVoiceTranscriber (IAsyncDisposable -> Close).
type IVoiceTranscriber interface {
	// Transcribe transcribes a complete PCM 16-bit, 16 kHz mono buffer.
	Transcribe(ctx context.Context, pcmAudio []byte) (VoiceTranscriptionResult, error)
	// StreamTranscribe streams audio chunks and returns partial transcriptions as
	// the engine produces them. The final element has IsFinal=true. audioChunks is
	// a channel of PCM 16-bit, 16 kHz mono chunks; it completing signals no more
	// audio. The returned channel closes when transcription completes or ctx cancels.
	StreamTranscribe(ctx context.Context, audioChunks <-chan []byte) <-chan PartialTranscription
	// Close disposes the transcriber (ports IAsyncDisposable.DisposeAsync).
	Close(ctx context.Context) error
}

// ---------------------------------------------------------------------------
// Wake word (IWakeWordDetector.cs)
// ---------------------------------------------------------------------------

// WakeWordDetectedEventArgs describes a single wake-word detection event. Ports
// the WakeWordDetectedEventArgs class.
type WakeWordDetectedEventArgs struct {
	// WakeWord is the phrase that was detected.
	WakeWord string
	// DetectedAt is the UTC timestamp at which the detection fired.
	DetectedAt time.Time
	// Confidence is the detector-reported confidence in [0, 1]. Detectors that do
	// not produce a score report 1.0.
	Confidence float32
}

// IWakeWordDetector detects a configured wake word in a continuous audio stream
// and notifies subscribers when the phrase is recognised. Implementations manage
// their own microphone lifecycle between Start and Stop. Ports the CircleAI.Voice
// IWakeWordDetector (the C# `event` is expressed as Subscribe/unsubscribe;
// IAsyncDisposable -> Close).
type IWakeWordDetector interface {
	// WakeWord is the phrase the detector listens for (e.g. "Hey B").
	WakeWord() string
	// IsListening is true when the detector is actively listening.
	IsListening() bool
	// Subscribe registers a handler for detection events and returns an idempotent
	// unsubscribe func (ports the WakeWordDetected event).
	Subscribe(handler func(WakeWordDetectedEventArgs)) (unsubscribe func())
	// Start begins listening. Idempotent.
	Start(ctx context.Context) error
	// Stop stops listening and releases capture resources. Idempotent.
	Stop(ctx context.Context) error
	// Close disposes the detector (ports IAsyncDisposable.DisposeAsync).
	Close(ctx context.Context) error
}

// ---------------------------------------------------------------------------
// Voice activity detection (IVoiceActivityDetector.cs)
// ---------------------------------------------------------------------------

// VadSegment is a single segment identified by an IVoiceActivityDetector. Ports
// the VadSegment record.
type VadSegment struct {
	// Audio is the raw PCM audio bytes for this segment. Non-empty for speech;
	// may be empty for silence markers.
	Audio []byte
	// IsSpeech is true when this segment contains detected speech and should be
	// forwarded to the transcriber; false for silence/noise markers.
	IsSpeech bool
}

// IVoiceActivityDetector detects speech vs silence in a raw PCM audio stream,
// yielding only the speech-containing segments. Ports the stream-based
// CircleAI.Voice IVoiceActivityDetector (the per-frame Speech variant is
// ISpeechVoiceActivityDetector).
type IVoiceActivityDetector interface {
	// Detect processes an incoming audio stream and returns a channel yielding
	// segments; production implementations yield only speech segments. The channel
	// closes when the source completes or ctx cancels (no error on cancel).
	Detect(ctx context.Context, audioStream <-chan []byte) <-chan VadSegment
}

// ---------------------------------------------------------------------------
// Text to speech (ITtsEngine.cs)
// ---------------------------------------------------------------------------

// TtsSynthesisResult is the result of a single-shot TTS synthesis. Ports the
// TtsSynthesisResult record.
type TtsSynthesisResult struct {
	// AudioData is the complete PCM audio buffer. Empty when the engine produced
	// no audio (empty input or null implementation).
	AudioData []byte
	// SampleRate is samples per second (e.g. 24000 for 24 kHz).
	SampleRate int
	// Channels is the number of interleaved channels (1 = mono, 2 = stereo).
	Channels int
	// BitsPerSample is the bit depth of each sample (e.g. 16 for signed 16-bit PCM).
	BitsPerSample int
}

// ITtsEngine converts generated text into PCM audio. Ports ITtsEngine.
type ITtsEngine interface {
	// Synthesise synthesises text to a single PCM audio buffer.
	Synthesise(ctx context.Context, text string) (TtsSynthesisResult, error)
	// StreamSynthesise streams PCM audio chunks as they are synthesised, enabling
	// low-latency playback. All chunks share the engine's format. The returned
	// channel closes when synthesis completes or ctx cancels.
	StreamSynthesise(ctx context.Context, text string) <-chan []byte
}

// ---------------------------------------------------------------------------
// Speech-emotion (OnnxSpeechEmotionDetector.cs — interface + records)
// ---------------------------------------------------------------------------

// SpeechEmotionFrame is an output emotion frame from a speech-emotion model.
// Ports the SpeechEmotionFrame record.
type SpeechEmotionFrame struct {
	// Label is the top-1 emotion label (lowercase, e.g. "happy", "angry").
	Label string
	// Arousal is the Russell-circumplex arousal coordinate in [-1, 1].
	Arousal float64
	// Valence is the Russell-circumplex valence coordinate in [-1, 1].
	Valence float64
	// Probability is the softmax probability of the winning class.
	Probability float64
}

// SpeechEmotionConfig configures a speech-emotion detector. Ports the
// SpeechEmotionConfig record (defaults applied by the constructor).
type SpeechEmotionConfig struct {
	// ModelPath is the path to the model (unused by the deterministic impl but
	// retained for parity with the ONNX config surface).
	ModelPath string
	// Labels is the class label list; nil selects the default 4-class layout.
	Labels []string
	// SampleRateHz is the model's expected sample rate.
	SampleRateHz int
	// MaxClipMs is the maximum clip length considered.
	MaxClipMs int
}

// ISpeechEmotionDetector senses the emotion in a PCM-16 audio clip. Ports
// ISpeechEmotionDetector (IAsyncDisposable -> Close). A nil frame (ok=false)
// means "no reading" (empty audio, sample-rate mismatch, or inference failure).
type ISpeechEmotionDetector interface {
	// Sense senses the emotion in audioPcm16. Returns (frame, true) on a reading,
	// or (zero, false) when there is nothing to report.
	Sense(ctx context.Context, audioPcm16 []byte, sampleRateHz int) (SpeechEmotionFrame, bool, error)
	// Close disposes the detector (ports IAsyncDisposable.DisposeAsync).
	Close(ctx context.Context) error
}

// ---------------------------------------------------------------------------
// Speaker identity (OnnxSpeakerIdentity.cs — interface + records)
// ---------------------------------------------------------------------------

// SpeakerEmbedderInputKind selects whether a speaker-embedding model consumes
// mel-spectrograms or raw waveform. Ordinals match the C# enum. Ports
// SpeakerEmbedderInputKind.
type SpeakerEmbedderInputKind int

const (
	// SpeakerEmbedderInputKindLogMel — model consumes a log-mel spectrogram.
	SpeakerEmbedderInputKindLogMel SpeakerEmbedderInputKind = iota
	// SpeakerEmbedderInputKindRawWaveform — model consumes the raw waveform.
	SpeakerEmbedderInputKindRawWaveform
)

// String renders the C# enum member name for a SpeakerEmbedderInputKind.
func (k SpeakerEmbedderInputKind) String() string {
	switch k {
	case SpeakerEmbedderInputKindLogMel:
		return "LogMel"
	case SpeakerEmbedderInputKindRawWaveform:
		return "RawWaveform"
	default:
		return "Unknown"
	}
}

// EnrolledSpeaker is a per-user enrollment record used for cosine-similarity ID.
// Ports the EnrolledSpeaker record.
type EnrolledSpeaker struct {
	// UserId identifies the enrolled speaker.
	UserId string
	// Centroid is the L2-normalised mean speaker embedding.
	Centroid []float32
	// SampleCount is how many enrollment utterances contributed to the centroid.
	SampleCount int
}

// SpeakerIdentityConfig configures a speaker-identity engine. Ports the
// SpeakerIdentityConfig record (defaults applied by the constructor).
type SpeakerIdentityConfig struct {
	// ModelPath is the embedding model path (unused by the deterministic impl).
	ModelPath string
	// EnrollmentStorePath is where centroids are persisted (unused by the
	// deterministic impl, retained for parity).
	EnrollmentStorePath string
	// InputKind selects mel vs waveform input.
	InputKind SpeakerEmbedderInputKind
	// SampleRateHz is the model's expected sample rate.
	SampleRateHz int
	// MinUtteranceMs is the minimum utterance length considered.
	MinUtteranceMs int
	// MaxUtteranceMs is the maximum utterance length considered.
	MaxUtteranceMs int
	// MatchThreshold is the minimum cosine similarity for an identification.
	MatchThreshold float64
}

// ISpeakerIdentity is the identify-or-enroll surface. Ports ISpeakerIdentity
// (IAsyncDisposable -> Close). Identify returns (userId, true) on a match above
// the threshold, or ("", false) when no enrolled user passes.
type ISpeakerIdentity interface {
	// Identify identifies the speaker of audioPcm16. Returns (userId, true) on a
	// match, or ("", false) when nothing passes the threshold.
	Identify(ctx context.Context, audioPcm16 []byte, sampleRateHz int) (string, bool, error)
	// Enroll enrolls audioPcm16 under userId, updating the running centroid.
	Enroll(ctx context.Context, userId string, audioPcm16 []byte, sampleRateHz int) error
	// Close disposes the engine (ports IAsyncDisposable.DisposeAsync).
	Close(ctx context.Context) error
}
