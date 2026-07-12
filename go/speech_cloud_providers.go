// speech_cloud_providers.go
//
// Ports the ~12 cloud STT / TTS adapters from CircleAI.Speech.Cloud/:
//   STT (ISpeechRecognizer):  OpenAiSpeechRecognizer, DeepgramSpeechRecognizer,
//       AssemblyAiSpeechRecognizer, GoogleSpeechRecognizer, AzureSpeechRecognizer,
//       CartesiaSpeechRecognizer
//   TTS (ISpeechSynthesizer):  OpenAiSpeechSynthesizer, DeepgramSpeechSynthesizer,
//       AzureSpeechSynthesizer, GoogleSpeechSynthesizer, ElevenLabsSpeechSynthesizer,
//       CartesiaSpeechSynthesizer, PlayHtSpeechSynthesizer
//
// HTTP SEAM: the C# HttpClient dependency is replaced by the package's injected
// ToolHTTPDoer func (see tools_thegeeknetwork.go) — every adapter takes one at
// construction and issues exactly the C#'s request(s) through it. Nothing here
// dials a socket; a caller wires NetToolHTTPDoer(http.DefaultClient) (or a test
// double) to actually reach the wire. The BaseAddress + relative path composition
// mirrors HttpClient.BaseAddress + a relative request Uri (joinBaseAndPath).
//
// FAIL-SOFT: every adapter returns an empty result (never an error) when it is
// not configured or the vendor returns a non-2xx status — matching the C# Empty()
// path so a fallback router can move on. ctx-cancellation is the one real error
// surfaced (the C#'s CancellationToken).
//
// The request-shaping LOGIC is ported faithfully: WAV enveloping (WrapPcmAsWav),
// real multipart/form-data bodies (encodeMultipartForm), base64 audio bodies,
// SSML, μ-law/ticks/duration math, and the vendor response JSON shapes. The C#
// System.Text.Json TryGetProperty lenient reads become the jsonObj/... helpers in
// this file, which tolerate missing/wrong-typed fields exactly like the C#.
//
// CONTRACT NOTE: the Go ISpeechRecognizer/ISpeechSynthesizer interfaces
// (speech_contracts.go) expose BackendID()+Transcribe()/Synthesize() but no
// IsConfigured — so IsConfigured() is a method on each concrete adapter (used
// internally for the fail-soft gate and available to a host router) rather than an
// interface member, keeping the ported contract surface unchanged.

package circleai

import (
	"bytes"
	"context"
	"encoding/base64"
	"encoding/binary"
	"encoding/json"
	"fmt"
	"html"
	"mime/multipart"
	"net/textproto"
	"net/url"
	"regexp"
	"strconv"
	"strings"
	"time"
)

// ---------------------------------------------------------------------------
// Shared helpers
// ---------------------------------------------------------------------------

// emptySpeechTranscription is the fail-soft ASR result (mirrors the C# Empty()).
func emptySpeechTranscription() SpeechTranscriptionResult {
	return SpeechTranscriptionResult{Text: "", Language: "", Segments: []TranscribedSegment{}, TotalDuration: 0}
}

// emptySynthesis is the fail-soft TTS result (mirrors the C# Empty()).
func emptySynthesis() SynthesisResult {
	return SynthesisResult{AudioPcm16Mono: []byte{}, SampleRateHz: 0, Duration: 0}
}

// wrapPcmAsWav wraps PCM-16 mono bytes in a 44-byte WAV header so an endpoint
// that requires a container (Whisper / AssemblyAI / Cartesia) accepts them. Ports
// the identical WrapPcmAsWav helper duplicated across the C# recognizers
// (little-endian header fields, PCM format tag 1, 1 channel, 16 bits/sample).
func wrapPcmAsWav(pcm []byte, sampleRate int) []byte {
	const channels = 1
	const bitsPerSample = 16
	byteRate := sampleRate * channels * (bitsPerSample / 8)
	blockAlign := channels * (bitsPerSample / 8)
	dataSize := len(pcm)
	chunkSize := 36 + dataSize

	buf := make([]byte, 44+dataSize)
	copy(buf[0:4], "RIFF")
	binary.LittleEndian.PutUint32(buf[4:8], uint32(chunkSize))
	copy(buf[8:12], "WAVE")
	copy(buf[12:16], "fmt ")
	binary.LittleEndian.PutUint32(buf[16:20], 16)                    // Subchunk1Size
	binary.LittleEndian.PutUint16(buf[20:22], 1)                     // PCM = 1
	binary.LittleEndian.PutUint16(buf[22:24], uint16(channels))      //
	binary.LittleEndian.PutUint32(buf[24:28], uint32(sampleRate))    //
	binary.LittleEndian.PutUint32(buf[28:32], uint32(byteRate))      //
	binary.LittleEndian.PutUint16(buf[32:34], uint16(blockAlign))    //
	binary.LittleEndian.PutUint16(buf[34:36], uint16(bitsPerSample)) //
	copy(buf[36:40], "data")
	binary.LittleEndian.PutUint32(buf[40:44], uint32(dataSize))
	copy(buf[44:], pcm)
	return buf
}

// stripWavHeader strips a 44-byte WAV header if the buffer starts with "RIFF".
// Ports GoogleSpeechSynthesizer.StripWavHeader.
func stripWavHeader(data []byte) []byte {
	if len(data) > 44 && data[0] == 'R' && data[1] == 'I' && data[2] == 'F' && data[3] == 'F' {
		out := make([]byte, len(data)-44)
		copy(out, data[44:])
		return out
	}
	return data
}

// multipartField is one field of a multipart/form-data body: a plain text field
// (Filename == "") or a file part (Filename + ContentType set, Value carries the
// bytes as a string).
type multipartField struct {
	Name        string
	Value       []byte
	Filename    string
	ContentType string
}

// encodeMultipartForm builds a real multipart/form-data body from ordered fields
// and returns (body, contentType). This is genuine RFC-2388 encoding (the request
// shape the C# MultipartFormDataContent produces) — not a synthetic stand-in — so
// the injected doer can forward it verbatim. Field order is preserved to match the
// C# form.Add(...) sequence.
func encodeMultipartForm(fields []multipartField) ([]byte, string) {
	var buf bytes.Buffer
	w := multipart.NewWriter(&buf)
	for _, f := range fields {
		if f.Filename != "" {
			h := make(textproto.MIMEHeader)
			h.Set("Content-Disposition",
				fmt.Sprintf(`form-data; name=%q; filename=%q`, f.Name, f.Filename))
			if f.ContentType != "" {
				h.Set("Content-Type", f.ContentType)
			}
			part, _ := w.CreatePart(h)
			_, _ = part.Write(f.Value)
		} else {
			fw, _ := w.CreateFormField(f.Name)
			_, _ = fw.Write(f.Value)
		}
	}
	_ = w.Close()
	return buf.Bytes(), w.FormDataContentType()
}

// is2xx reports whether status is a success code (mirrors IsSuccessStatusCode).
func is2xx(status int) bool { return status >= 200 && status < 300 }

// durSeconds converts a floating-point seconds value to a time.Duration.
func durSeconds(sec float64) time.Duration {
	return time.Duration(sec * float64(time.Second))
}

// ----- lenient JSON reads (mirror System.Text.Json TryGetProperty) -----

// jsonObj parses raw JSON into a map, or returns (nil,false) if it is not an
// object. Mirrors JsonDocument.Parse followed by treating the root as an object.
func jsonObj(raw []byte) (map[string]json.RawMessage, bool) {
	var m map[string]json.RawMessage
	if err := json.Unmarshal(raw, &m); err != nil || m == nil {
		return nil, false
	}
	return m, true
}

// objField returns the child object at key, or (nil,false). Mirrors
// TryGetProperty(...) where the value is expected to be an object.
func objField(m map[string]json.RawMessage, key string) (map[string]json.RawMessage, bool) {
	raw, ok := m[key]
	if !ok {
		return nil, false
	}
	return jsonObj(raw)
}

// arrField returns the child array at key, or (nil,false). Mirrors
// TryGetProperty(...) where ValueKind == Array.
func arrField(m map[string]json.RawMessage, key string) ([]json.RawMessage, bool) {
	raw, ok := m[key]
	if !ok {
		return nil, false
	}
	var a []json.RawMessage
	if err := json.Unmarshal(raw, &a); err != nil {
		return nil, false
	}
	return a, true
}

// strField returns the string at key (or "" if missing/not a string). Mirrors
// TryGetProperty(...).GetString() ?? "".
func strField(m map[string]json.RawMessage, key string) string {
	raw, ok := m[key]
	if !ok {
		return ""
	}
	var s string
	if err := json.Unmarshal(raw, &s); err != nil {
		return ""
	}
	return s
}

// numField returns the number at key (or 0 if missing/not a number). Mirrors
// TryGetProperty(...).GetDouble().
func numField(m map[string]json.RawMessage, key string) float64 {
	raw, ok := m[key]
	if !ok {
		return 0
	}
	var f float64
	if err := json.Unmarshal(raw, &f); err != nil {
		return 0
	}
	return f
}

// boolField returns the bool at key (or false). Mirrors ValueKind == True.
func boolField(m map[string]json.RawMessage, key string) bool {
	raw, ok := m[key]
	if !ok {
		return false
	}
	var b bool
	if err := json.Unmarshal(raw, &b); err != nil {
		return false
	}
	return b
}

// arrObj parses a raw array element as an object.
func arrObj(raw json.RawMessage) (map[string]json.RawMessage, bool) { return jsonObj(raw) }

// jsonStringLiteral serialises s as a JSON string literal, quotes-and-escapes
// included (mirrors JsonSerializer.Serialize(text)). Distinct from the package's
// jsonString(json.RawMessage) reader in memory_llm_extractor.go.
func jsonStringLiteral(s string) string {
	b, _ := json.Marshal(s)
	return string(b)
}

// ===========================================================================
// OpenAI Whisper — ISpeechRecognizer
// ===========================================================================

// OpenAiSpeechRecognizer is an ISpeechRecognizer backed by OpenAI Whisper
// (/v1/audio/transcriptions, multipart WAV upload). Ports OpenAiSpeechRecognizer.
type OpenAiSpeechRecognizer struct {
	doer    ToolHTTPDoer
	options OpenAiVoiceOptions
}

// NewOpenAiSpeechRecognizer builds the recognizer against an injected doer.
func NewOpenAiSpeechRecognizer(options OpenAiVoiceOptions, doer ToolHTTPDoer) *OpenAiSpeechRecognizer {
	return &OpenAiSpeechRecognizer{doer: doer, options: options}
}

// BackendID returns "openai-whisper".
func (r *OpenAiSpeechRecognizer) BackendID() string { return "openai-whisper" }

// IsConfigured is true when the API key is present.
func (r *OpenAiSpeechRecognizer) IsConfigured() bool { return !isBlank(r.options.ApiKey) }

// Transcribe uploads WAV-wrapped audio to Whisper and parses verbose_json. Ports
// TranscribeAsync.
func (r *OpenAiSpeechRecognizer) Transcribe(ctx context.Context, audioPcm16Mono []byte, sampleRateHz int, languageHint string) (SpeechTranscriptionResult, error) {
	if err := ctx.Err(); err != nil {
		return SpeechTranscriptionResult{}, err
	}
	if !r.IsConfigured() {
		return emptySpeechTranscription(), nil
	}

	wav := wrapPcmAsWav(audioPcm16Mono, sampleRateHz)
	fields := []multipartField{
		{Name: "file", Value: wav, Filename: "audio.wav", ContentType: "audio/wav"},
		{Name: "model", Value: []byte(r.options.TranscriptionModel)},
		{Name: "response_format", Value: []byte("verbose_json")},
	}
	if !isBlank(languageHint) {
		fields = append(fields, multipartField{Name: "language", Value: []byte(languageHint)})
	}
	body, contentType := encodeMultipartForm(fields)

	resp, err := r.doer(ctx, "POST", joinBaseAndPath(r.options.BaseAddress, "/v1/audio/transcriptions"),
		map[string]string{"Authorization": "Bearer " + r.options.ApiKey, "Content-Type": contentType}, body)
	if err != nil {
		return SpeechTranscriptionResult{}, err
	}
	if !is2xx(resp.StatusCode) {
		return emptySpeechTranscription(), nil
	}

	root, ok := jsonObj(resp.Body)
	if !ok {
		return emptySpeechTranscription(), nil
	}
	text := strField(root, "text")
	language := strField(root, "language")
	duration := durSeconds(numField(root, "duration"))

	segments := []TranscribedSegment{}
	if segs, ok := arrField(root, "segments"); ok {
		for _, sRaw := range segs {
			s, ok := arrObj(sRaw)
			if !ok {
				continue
			}
			segStart := numField(s, "start")
			segEnd := numField(s, "end")
			if _, has := s["end"]; !has {
				segEnd = segStart
			}
			d := segEnd - segStart
			if d < 0 {
				d = 0
			}
			segments = append(segments, TranscribedSegment{
				Text:       strField(s, "text"),
				Offset:     durSeconds(segStart),
				Duration:   durSeconds(d),
				Language:   language,
				Confidence: 0,
			})
		}
	}
	return SpeechTranscriptionResult{Text: text, Language: language, Segments: segments, TotalDuration: duration}, nil
}

// ===========================================================================
// OpenAI TTS — ISpeechSynthesizer
// ===========================================================================

// OpenAiSpeechSynthesizer is an ISpeechSynthesizer backed by OpenAI TTS
// (/v1/audio/speech, response_format=pcm). Ports OpenAiSpeechSynthesizer.
type OpenAiSpeechSynthesizer struct {
	doer    ToolHTTPDoer
	options OpenAiVoiceOptions
}

// NewOpenAiSpeechSynthesizer builds the synthesizer against an injected doer.
func NewOpenAiSpeechSynthesizer(options OpenAiVoiceOptions, doer ToolHTTPDoer) *OpenAiSpeechSynthesizer {
	return &OpenAiSpeechSynthesizer{doer: doer, options: options}
}

// BackendID returns "openai-tts".
func (s *OpenAiSpeechSynthesizer) BackendID() string { return "openai-tts" }

// IsConfigured is true when the API key is present.
func (s *OpenAiSpeechSynthesizer) IsConfigured() bool { return !isBlank(s.options.ApiKey) }

// Synthesize posts the utterance to OpenAI TTS and returns the raw PCM. Ports
// SynthesizeAsync.
func (s *OpenAiSpeechSynthesizer) Synthesize(ctx context.Context, text, voiceID, languageHint string) (SynthesisResult, error) {
	if err := ctx.Err(); err != nil {
		return SynthesisResult{}, err
	}
	if !s.IsConfigured() {
		return emptySynthesis(), nil
	}

	voice := s.options.DefaultVoice
	if !isBlank(voiceID) {
		voice = voiceID
	}
	body, _ := json.Marshal(map[string]any{
		"model":           s.options.SpeechModel,
		"input":           text,
		"voice":           voice,
		"response_format": "pcm",
	})

	resp, err := s.doer(ctx, "POST", joinBaseAndPath(s.options.BaseAddress, "/v1/audio/speech"),
		map[string]string{"Authorization": "Bearer " + s.options.ApiKey, "Content-Type": "application/json"}, body)
	if err != nil {
		return SynthesisResult{}, err
	}
	if !is2xx(resp.StatusCode) {
		return emptySynthesis(), nil
	}
	pcm := append([]byte(nil), resp.Body...)
	samples := len(pcm) / 2
	return SynthesisResult{
		AudioPcm16Mono: pcm,
		SampleRateHz:   s.options.PcmSampleRateHz,
		Duration:       durSeconds(float64(samples) / float64(s.options.PcmSampleRateHz)),
	}, nil
}

// ===========================================================================
// Deepgram — ISpeechRecognizer
// ===========================================================================

// DeepgramSpeechRecognizer is an ISpeechRecognizer backed by Deepgram /v1/listen
// (raw linear16 body, Token auth). Ports DeepgramSpeechRecognizer.
type DeepgramSpeechRecognizer struct {
	doer    ToolHTTPDoer
	options DeepgramOptions
}

// NewDeepgramSpeechRecognizer builds the recognizer against an injected doer.
func NewDeepgramSpeechRecognizer(options DeepgramOptions, doer ToolHTTPDoer) *DeepgramSpeechRecognizer {
	return &DeepgramSpeechRecognizer{doer: doer, options: options}
}

// BackendID returns "deepgram".
func (r *DeepgramSpeechRecognizer) BackendID() string { return "deepgram" }

// IsConfigured is true when the API key is present.
func (r *DeepgramSpeechRecognizer) IsConfigured() bool { return !isBlank(r.options.ApiKey) }

// Transcribe posts raw PCM to Deepgram and parses results.channels[0].alternatives[0].
// Ports TranscribeAsync.
func (r *DeepgramSpeechRecognizer) Transcribe(ctx context.Context, audioPcm16Mono []byte, sampleRateHz int, languageHint string) (SpeechTranscriptionResult, error) {
	if err := ctx.Err(); err != nil {
		return SpeechTranscriptionResult{}, err
	}
	if !r.IsConfigured() {
		return emptySpeechTranscription(), nil
	}

	path := fmt.Sprintf("/v1/listen?model=%s&encoding=linear16&sample_rate=%d&channels=1&punctuate=true",
		url.QueryEscape(r.options.Model), sampleRateHz)
	if !isBlank(languageHint) {
		path += "&language=" + url.QueryEscape(languageHint)
	}

	resp, err := r.doer(ctx, "POST", joinBaseAndPath(r.options.BaseAddress, path),
		map[string]string{"Authorization": "Token " + r.options.ApiKey, "Content-Type": "audio/raw"},
		append([]byte(nil), audioPcm16Mono...))
	if err != nil {
		return SpeechTranscriptionResult{}, err
	}
	if !is2xx(resp.StatusCode) {
		return emptySpeechTranscription(), nil
	}

	root, ok := jsonObj(resp.Body)
	if !ok {
		return emptySpeechTranscription(), nil
	}
	results, ok := objField(root, "results")
	if !ok {
		return emptySpeechTranscription(), nil
	}
	channels, ok := arrField(results, "channels")
	if !ok || len(channels) == 0 {
		return emptySpeechTranscription(), nil
	}
	firstChannel, ok := arrObj(channels[0])
	if !ok {
		return emptySpeechTranscription(), nil
	}
	alts, ok := arrField(firstChannel, "alternatives")
	if !ok || len(alts) == 0 {
		return emptySpeechTranscription(), nil
	}
	firstAlt, ok := arrObj(alts[0])
	if !ok {
		return emptySpeechTranscription(), nil
	}

	text := strField(firstAlt, "transcript")
	segments := []TranscribedSegment{}
	if words, ok := arrField(firstAlt, "words"); ok {
		for _, wRaw := range words {
			w, ok := arrObj(wRaw)
			if !ok {
				continue
			}
			start := numField(w, "start")
			end := numField(w, "end")
			segments = append(segments, TranscribedSegment{
				Text:       strField(w, "word"),
				Offset:     durSeconds(start),
				Duration:   durSeconds(end - start),
				Language:   languageHint,
				Confidence: float32(numField(w, "confidence")),
			})
		}
	}
	duration := time.Duration(0)
	if meta, ok := objField(root, "metadata"); ok {
		duration = durSeconds(numField(meta, "duration"))
	}
	return SpeechTranscriptionResult{Text: text, Language: languageHint, Segments: segments, TotalDuration: duration}, nil
}

// ===========================================================================
// Deepgram Aura — ISpeechSynthesizer
// ===========================================================================

// DeepgramSpeechSynthesizer is an ISpeechSynthesizer backed by Deepgram Aura
// /v1/speak (JSON { text }, Token auth). Ports DeepgramSpeechSynthesizer.
type DeepgramSpeechSynthesizer struct {
	doer    ToolHTTPDoer
	options DeepgramTtsOptions
}

// NewDeepgramSpeechSynthesizer builds the synthesizer against an injected doer.
func NewDeepgramSpeechSynthesizer(options DeepgramTtsOptions, doer ToolHTTPDoer) *DeepgramSpeechSynthesizer {
	return &DeepgramSpeechSynthesizer{doer: doer, options: options}
}

// BackendID returns "deepgram-aura".
func (s *DeepgramSpeechSynthesizer) BackendID() string { return "deepgram-aura" }

// IsConfigured is true when the API key is present.
func (s *DeepgramSpeechSynthesizer) IsConfigured() bool { return !isBlank(s.options.ApiKey) }

// Synthesize posts { text } to Deepgram Aura and returns the raw PCM. Ports
// SynthesizeAsync.
func (s *DeepgramSpeechSynthesizer) Synthesize(ctx context.Context, text, voiceID, languageHint string) (SynthesisResult, error) {
	if err := ctx.Err(); err != nil {
		return SynthesisResult{}, err
	}
	if !s.IsConfigured() {
		return emptySynthesis(), nil
	}

	voice := s.options.Voice
	if !isBlank(voiceID) {
		voice = voiceID
	}
	path := fmt.Sprintf("/v1/speak?model=%s&encoding=linear16&sample_rate=%d",
		url.QueryEscape(voice), s.options.PcmSampleRateHz)
	body, _ := json.Marshal(map[string]any{"text": text})

	resp, err := s.doer(ctx, "POST", joinBaseAndPath(s.options.BaseAddress, path),
		map[string]string{"Authorization": "Token " + s.options.ApiKey, "Content-Type": "application/json"}, body)
	if err != nil {
		return SynthesisResult{}, err
	}
	if !is2xx(resp.StatusCode) {
		return emptySynthesis(), nil
	}
	pcm := append([]byte(nil), resp.Body...)
	samples := len(pcm) / 2
	return SynthesisResult{
		AudioPcm16Mono: pcm,
		SampleRateHz:   s.options.PcmSampleRateHz,
		Duration:       durSeconds(float64(samples) / float64(s.options.PcmSampleRateHz)),
	}, nil
}

// ===========================================================================
// AssemblyAI — ISpeechRecognizer (upload -> submit -> poll)
// ===========================================================================

// AssemblyAiPollInterval is the delay between AssemblyAI transcript polls (ports
// the C# Task.Delay(500)). Exposed so a host/test can shrink it.
var AssemblyAiPollInterval = 500 * time.Millisecond

// AssemblyAiMaxPolls is the maximum number of poll attempts (60 * 500 ms = 30 s in
// the C#). Exposed so a host/test can shrink it.
var AssemblyAiMaxPolls = 60

// AssemblyAiSpeechRecognizer is an ISpeechRecognizer backed by AssemblyAI's
// three-step flow: POST /v2/upload -> POST /v2/transcript -> poll
// GET /v2/transcript/{id}. Ports AssemblyAiSpeechRecognizer.
type AssemblyAiSpeechRecognizer struct {
	doer    ToolHTTPDoer
	options AssemblyAiOptions
	// sleep lets a test stub the poll delay; nil => real time.Sleep honouring ctx.
	sleep func(ctx context.Context, d time.Duration) error
}

// NewAssemblyAiSpeechRecognizer builds the recognizer against an injected doer.
func NewAssemblyAiSpeechRecognizer(options AssemblyAiOptions, doer ToolHTTPDoer) *AssemblyAiSpeechRecognizer {
	return &AssemblyAiSpeechRecognizer{doer: doer, options: options}
}

// BackendID returns "assemblyai".
func (r *AssemblyAiSpeechRecognizer) BackendID() string { return "assemblyai" }

// IsConfigured is true when the API key is present.
func (r *AssemblyAiSpeechRecognizer) IsConfigured() bool { return !isBlank(r.options.ApiKey) }

// ctxSleep waits d honouring ctx, returning ctx.Err() if cancelled first. Ports
// the C# await Task.Delay(500, ct).
func ctxSleep(ctx context.Context, d time.Duration) error {
	t := time.NewTimer(d)
	defer t.Stop()
	select {
	case <-ctx.Done():
		return ctx.Err()
	case <-t.C:
		return nil
	}
}

// Transcribe runs the upload/submit/poll flow. Ports TranscribeAsync.
func (r *AssemblyAiSpeechRecognizer) Transcribe(ctx context.Context, audioPcm16Mono []byte, sampleRateHz int, languageHint string) (SpeechTranscriptionResult, error) {
	if err := ctx.Err(); err != nil {
		return SpeechTranscriptionResult{}, err
	}
	if !r.IsConfigured() {
		return emptySpeechTranscription(), nil
	}
	auth := map[string]string{"Authorization": r.options.ApiKey}

	// 1) Upload audio (WAV bytes as octet-stream).
	wav := wrapPcmAsWav(audioPcm16Mono, sampleRateHz)
	uploadHeaders := map[string]string{"Authorization": r.options.ApiKey, "Content-Type": "application/octet-stream"}
	uploadResp, err := r.doer(ctx, "POST", joinBaseAndPath(r.options.BaseAddress, "/v2/upload"), uploadHeaders, wav)
	if err != nil {
		return SpeechTranscriptionResult{}, err
	}
	if !is2xx(uploadResp.StatusCode) {
		return emptySpeechTranscription(), nil
	}
	uploadRoot, ok := jsonObj(uploadResp.Body)
	if !ok {
		return emptySpeechTranscription(), nil
	}
	uploadURL := strField(uploadRoot, "upload_url")
	if isBlank(uploadURL) {
		return emptySpeechTranscription(), nil
	}

	// 2) Submit transcript job (JSON body built to match the C# StringBuilder).
	var sb strings.Builder
	sb.WriteByte('{')
	sb.WriteString(fmt.Sprintf("%q:%s,", "audio_url", jsonStringLiteral(uploadURL)))
	sb.WriteString(fmt.Sprintf("%q:%s", "speech_model", jsonStringLiteral(r.options.SpeechModel)))
	if !isBlank(languageHint) {
		sb.WriteString(fmt.Sprintf(",%q:%s", "language_code", jsonStringLiteral(languageHint)))
	}
	sb.WriteByte('}')
	submitHeaders := map[string]string{"Authorization": r.options.ApiKey, "Content-Type": "application/json"}
	submitResp, err := r.doer(ctx, "POST", joinBaseAndPath(r.options.BaseAddress, "/v2/transcript"), submitHeaders, []byte(sb.String()))
	if err != nil {
		return SpeechTranscriptionResult{}, err
	}
	if !is2xx(submitResp.StatusCode) {
		return emptySpeechTranscription(), nil
	}
	submitRoot, ok := jsonObj(submitResp.Body)
	if !ok {
		return emptySpeechTranscription(), nil
	}
	transcriptID := strField(submitRoot, "id")
	if isBlank(transcriptID) {
		return emptySpeechTranscription(), nil
	}

	// 3) Poll until completed (max AssemblyAiMaxPolls attempts of AssemblyAiPollInterval).
	sleep := r.sleep
	if sleep == nil {
		sleep = ctxSleep
	}
	for attempt := 0; attempt < AssemblyAiMaxPolls; attempt++ {
		if err := ctx.Err(); err != nil {
			return SpeechTranscriptionResult{}, err
		}
		if err := sleep(ctx, AssemblyAiPollInterval); err != nil {
			return SpeechTranscriptionResult{}, err
		}

		pollResp, err := r.doer(ctx, "GET", joinBaseAndPath(r.options.BaseAddress, "/v2/transcript/"+transcriptID), auth, nil)
		if err != nil {
			return SpeechTranscriptionResult{}, err
		}
		if !is2xx(pollResp.StatusCode) {
			continue
		}
		pollRoot, ok := jsonObj(pollResp.Body)
		if !ok {
			continue
		}
		status := strField(pollRoot, "status")
		switch status {
		case "completed":
			text := strField(pollRoot, "text")
			lang := languageHint
			if _, has := pollRoot["language_code"]; has {
				lang = strField(pollRoot, "language_code")
			}
			duration := durSeconds(numField(pollRoot, "audio_duration"))
			segments := []TranscribedSegment{}
			if words, ok := arrField(pollRoot, "words"); ok {
				for _, wRaw := range words {
					w, ok := arrObj(wRaw)
					if !ok {
						continue
					}
					start := numField(w, "start") / 1000.0
					end := start
					if _, has := w["end"]; has {
						end = numField(w, "end") / 1000.0
					}
					d := end - start
					if d < 0 {
						d = 0
					}
					segments = append(segments, TranscribedSegment{
						Text:       strField(w, "text"),
						Offset:     durSeconds(start),
						Duration:   durSeconds(d),
						Language:   lang,
						Confidence: float32(numField(w, "confidence")),
					})
				}
			}
			return SpeechTranscriptionResult{Text: text, Language: lang, Segments: segments, TotalDuration: duration}, nil
		case "error":
			return emptySpeechTranscription(), nil
		}
	}
	return emptySpeechTranscription(), nil
}

// ===========================================================================
// Google Cloud STT — ISpeechRecognizer
// ===========================================================================

// GoogleSpeechRecognizer is an ISpeechRecognizer backed by Google Cloud
// Speech-to-Text v1 (base64 LINEAR16 body, ?key= auth). Ports
// GoogleSpeechRecognizer.
type GoogleSpeechRecognizer struct {
	doer    ToolHTTPDoer
	options GoogleSpeechOptions
}

// NewGoogleSpeechRecognizer builds the recognizer against an injected doer.
func NewGoogleSpeechRecognizer(options GoogleSpeechOptions, doer ToolHTTPDoer) *GoogleSpeechRecognizer {
	return &GoogleSpeechRecognizer{doer: doer, options: options}
}

// BackendID returns "google-stt".
func (r *GoogleSpeechRecognizer) BackendID() string { return "google-stt" }

// IsConfigured is true when the API key is present.
func (r *GoogleSpeechRecognizer) IsConfigured() bool { return !isBlank(r.options.ApiKey) }

// Transcribe posts base64 LINEAR16 to Google STT and concatenates top
// alternatives across results. Ports TranscribeAsync.
func (r *GoogleSpeechRecognizer) Transcribe(ctx context.Context, audioPcm16Mono []byte, sampleRateHz int, languageHint string) (SpeechTranscriptionResult, error) {
	if err := ctx.Err(); err != nil {
		return SpeechTranscriptionResult{}, err
	}
	if !r.IsConfigured() {
		return emptySpeechTranscription(), nil
	}

	lang := r.options.LanguageCode
	if !isBlank(languageHint) {
		lang = languageHint
	}
	audioB64 := base64.StdEncoding.EncodeToString(audioPcm16Mono)
	body, _ := json.Marshal(map[string]any{
		"config": map[string]any{
			"encoding":              "LINEAR16",
			"sampleRateHertz":       sampleRateHz,
			"languageCode":          lang,
			"enableWordTimeOffsets": true,
			"enableWordConfidence":  true,
		},
		"audio": map[string]any{"content": audioB64},
	})
	path := "/v1/speech:recognize?key=" + url.QueryEscape(r.options.ApiKey)

	resp, err := r.doer(ctx, "POST", joinBaseAndPath(r.options.BaseAddress, path),
		map[string]string{"Content-Type": "application/json"}, body)
	if err != nil {
		return SpeechTranscriptionResult{}, err
	}
	if !is2xx(resp.StatusCode) {
		return emptySpeechTranscription(), nil
	}

	root, ok := jsonObj(resp.Body)
	if !ok {
		return emptySpeechTranscription(), nil
	}
	var allText strings.Builder
	segments := []TranscribedSegment{}
	if results, ok := arrField(root, "results"); ok {
		for _, rRaw := range results {
			rObj, ok := arrObj(rRaw)
			if !ok {
				continue
			}
			alts, ok := arrField(rObj, "alternatives")
			if !ok || len(alts) == 0 {
				continue
			}
			alt, ok := arrObj(alts[0])
			if !ok {
				continue
			}
			if allText.Len() > 0 {
				allText.WriteByte(' ')
			}
			allText.WriteString(strField(alt, "transcript"))

			if words, ok := arrField(alt, "words"); ok {
				for _, wRaw := range words {
					w, ok := arrObj(wRaw)
					if !ok {
						continue
					}
					start := parseGoogleSeconds(w, "startTime")
					end := parseGoogleSeconds(w, "endTime")
					d := end - start
					if d < 0 {
						d = 0
					}
					segments = append(segments, TranscribedSegment{
						Text:       strField(w, "word"),
						Offset:     durSeconds(start),
						Duration:   durSeconds(d),
						Language:   lang,
						Confidence: float32(numField(w, "confidence")),
					})
				}
			}
		}
	}
	return SpeechTranscriptionResult{Text: allText.String(), Language: lang, Segments: segments, TotalDuration: 0}, nil
}

// parseGoogleSeconds parses a Google duration string like "1.500s" (or a bare
// number) into seconds. Ports GoogleSpeechRecognizer.ParseSeconds.
func parseGoogleSeconds(m map[string]json.RawMessage, key string) float64 {
	raw, ok := m[key]
	if !ok {
		return 0
	}
	var s string
	if err := json.Unmarshal(raw, &s); err != nil {
		// Google can also send a numeric — tolerate it.
		var f float64
		if json.Unmarshal(raw, &f) == nil {
			return f
		}
		return 0
	}
	if isBlank(s) {
		return 0
	}
	s = strings.TrimSuffix(s, "s")
	f, err := strconv.ParseFloat(s, 64)
	if err != nil {
		return 0
	}
	return f
}

// ===========================================================================
// Google Cloud TTS — ISpeechSynthesizer
// ===========================================================================

// GoogleSpeechSynthesizer is an ISpeechSynthesizer backed by Google Cloud TTS v1
// (/v1/text:synthesize, base64 LINEAR16 response). Ports GoogleSpeechSynthesizer.
type GoogleSpeechSynthesizer struct {
	doer    ToolHTTPDoer
	options GoogleTtsOptions
}

// NewGoogleSpeechSynthesizer builds the synthesizer against an injected doer.
func NewGoogleSpeechSynthesizer(options GoogleTtsOptions, doer ToolHTTPDoer) *GoogleSpeechSynthesizer {
	return &GoogleSpeechSynthesizer{doer: doer, options: options}
}

// BackendID returns "google-tts".
func (s *GoogleSpeechSynthesizer) BackendID() string { return "google-tts" }

// IsConfigured is true when the API key is present.
func (s *GoogleSpeechSynthesizer) IsConfigured() bool { return !isBlank(s.options.ApiKey) }

// Synthesize posts to Google TTS and decodes the base64 LINEAR16 (WAV-stripped)
// audio. Ports SynthesizeAsync.
func (s *GoogleSpeechSynthesizer) Synthesize(ctx context.Context, text, voiceID, languageHint string) (SynthesisResult, error) {
	if err := ctx.Err(); err != nil {
		return SynthesisResult{}, err
	}
	if !s.IsConfigured() {
		return emptySynthesis(), nil
	}

	voice := s.options.DefaultVoiceName
	if !isBlank(voiceID) {
		voice = voiceID
	}
	lang := s.options.LanguageCode
	if !isBlank(languageHint) {
		lang = languageHint
	}
	body, _ := json.Marshal(map[string]any{
		"input": map[string]any{"text": text},
		"voice": map[string]any{"languageCode": lang, "name": voice},
		"audioConfig": map[string]any{
			"audioEncoding":   "LINEAR16",
			"sampleRateHertz": s.options.PcmSampleRateHz,
		},
	})
	path := "/v1/text:synthesize?key=" + url.QueryEscape(s.options.ApiKey)

	resp, err := s.doer(ctx, "POST", joinBaseAndPath(s.options.BaseAddress, path),
		map[string]string{"Content-Type": "application/json"}, body)
	if err != nil {
		return SynthesisResult{}, err
	}
	if !is2xx(resp.StatusCode) {
		return emptySynthesis(), nil
	}
	root, ok := jsonObj(resp.Body)
	if !ok {
		return emptySynthesis(), nil
	}
	b64 := strField(root, "audioContent")
	if b64 == "" {
		return emptySynthesis(), nil
	}
	decoded, err := base64.StdEncoding.DecodeString(b64)
	if err != nil {
		return emptySynthesis(), nil
	}
	pcm := stripWavHeader(decoded)
	samples := len(pcm) / 2
	return SynthesisResult{
		AudioPcm16Mono: pcm,
		SampleRateHz:   s.options.PcmSampleRateHz,
		Duration:       durSeconds(float64(samples) / float64(s.options.PcmSampleRateHz)),
	}, nil
}

// ===========================================================================
// Azure STT — ISpeechRecognizer
// ===========================================================================

// AzureSpeechRecognizer is an ISpeechRecognizer backed by Azure Cognitive
// Services STT (raw PCM body, Ocp-Apim-Subscription-Key). Ports
// AzureSpeechRecognizer.
type AzureSpeechRecognizer struct {
	doer    ToolHTTPDoer
	options AzureSpeechOptions
}

// NewAzureSpeechRecognizer builds the recognizer against an injected doer.
func NewAzureSpeechRecognizer(options AzureSpeechOptions, doer ToolHTTPDoer) *AzureSpeechRecognizer {
	return &AzureSpeechRecognizer{doer: doer, options: options}
}

// BackendID returns "azure-stt".
func (r *AzureSpeechRecognizer) BackendID() string { return "azure-stt" }

// IsConfigured is true when the API key AND the region BaseAddress are present.
func (r *AzureSpeechRecognizer) IsConfigured() bool {
	return !isBlank(r.options.ApiKey) && !isBlank(r.options.BaseAddress)
}

// Transcribe posts raw PCM to Azure STT and parses the detailed JSON. Azure
// offsets/durations are 100-ns ticks. Ports TranscribeAsync.
func (r *AzureSpeechRecognizer) Transcribe(ctx context.Context, audioPcm16Mono []byte, sampleRateHz int, languageHint string) (SpeechTranscriptionResult, error) {
	if err := ctx.Err(); err != nil {
		return SpeechTranscriptionResult{}, err
	}
	if !r.IsConfigured() {
		return emptySpeechTranscription(), nil
	}

	lang := r.options.LanguageCode
	if !isBlank(languageHint) {
		lang = languageHint
	}
	path := fmt.Sprintf("/speech/recognition/conversation/cognitiveservices/v1?language=%s&format=detailed",
		url.QueryEscape(lang))
	headers := map[string]string{
		"Content-Type":              fmt.Sprintf("audio/wav; codecs=audio/pcm; samplerate=%d", sampleRateHz),
		"Ocp-Apim-Subscription-Key": r.options.ApiKey,
		"Accept":                    "application/json",
	}

	resp, err := r.doer(ctx, "POST", joinBaseAndPath(r.options.BaseAddress, path), headers, append([]byte(nil), audioPcm16Mono...))
	if err != nil {
		return SpeechTranscriptionResult{}, err
	}
	if !is2xx(resp.StatusCode) {
		return emptySpeechTranscription(), nil
	}
	root, ok := jsonObj(resp.Body)
	if !ok {
		return emptySpeechTranscription(), nil
	}
	if strField(root, "RecognitionStatus") != "Success" {
		return emptySpeechTranscription(), nil
	}
	text := strField(root, "DisplayText")
	// Ticks: 1 tick = 100 ns => duration = ticks * 100 ns.
	offsetTicks := int64(numField(root, "Offset"))
	durationTicks := int64(numField(root, "Duration"))
	ticksToDur := func(t int64) time.Duration { return time.Duration(t*100) * time.Nanosecond }
	duration := ticksToDur(durationTicks)

	var confidence float32
	if nb, ok := arrField(root, "NBest"); ok && len(nb) > 0 {
		if first, ok := arrObj(nb[0]); ok {
			confidence = float32(numField(first, "Confidence"))
		}
	}
	segment := TranscribedSegment{
		Text:       text,
		Offset:     ticksToDur(offsetTicks),
		Duration:   duration,
		Language:   lang,
		Confidence: confidence,
	}
	return SpeechTranscriptionResult{Text: text, Language: lang, Segments: []TranscribedSegment{segment}, TotalDuration: duration}, nil
}

// ===========================================================================
// Azure TTS — ISpeechSynthesizer
// ===========================================================================

// AzureSpeechSynthesizer is an ISpeechSynthesizer backed by Azure Cognitive
// Services TTS (SSML body, X-Microsoft-OutputFormat raw PCM). Ports
// AzureSpeechSynthesizer.
type AzureSpeechSynthesizer struct {
	doer    ToolHTTPDoer
	options AzureTtsOptions
}

// NewAzureSpeechSynthesizer builds the synthesizer against an injected doer.
func NewAzureSpeechSynthesizer(options AzureTtsOptions, doer ToolHTTPDoer) *AzureSpeechSynthesizer {
	return &AzureSpeechSynthesizer{doer: doer, options: options}
}

// BackendID returns "azure-tts".
func (s *AzureSpeechSynthesizer) BackendID() string { return "azure-tts" }

// IsConfigured is true when the API key AND the region BaseAddress are present.
func (s *AzureSpeechSynthesizer) IsConfigured() bool {
	return !isBlank(s.options.ApiKey) && !isBlank(s.options.BaseAddress)
}

// Synthesize posts SSML to Azure TTS and returns the raw PCM. Ports
// SynthesizeAsync.
func (s *AzureSpeechSynthesizer) Synthesize(ctx context.Context, text, voiceID, languageHint string) (SynthesisResult, error) {
	if err := ctx.Err(); err != nil {
		return SynthesisResult{}, err
	}
	if !s.IsConfigured() {
		return emptySynthesis(), nil
	}

	voice := s.options.DefaultVoiceName
	if !isBlank(voiceID) {
		voice = voiceID
	}
	lang := s.options.LanguageCode
	if !isBlank(languageHint) {
		lang = languageHint
	}
	rate := s.options.PcmSampleRateHz
	ssml := fmt.Sprintf("<speak version='1.0' xml:lang='%s'>\n  <voice name='%s'>%s</voice>\n</speak>",
		lang, voice, html.EscapeString(text))
	headers := map[string]string{
		"Content-Type":              "application/ssml+xml",
		"Ocp-Apim-Subscription-Key": s.options.ApiKey,
		"X-Microsoft-OutputFormat":  fmt.Sprintf("raw-%dkhz-16bit-mono-pcm", rate/1000),
		"User-Agent":                "CircleAI",
	}

	resp, err := s.doer(ctx, "POST", joinBaseAndPath(s.options.BaseAddress, "/cognitiveservices/v1"), headers, []byte(ssml))
	if err != nil {
		return SynthesisResult{}, err
	}
	if !is2xx(resp.StatusCode) {
		return emptySynthesis(), nil
	}
	pcm := append([]byte(nil), resp.Body...)
	samples := len(pcm) / 2
	return SynthesisResult{
		AudioPcm16Mono: pcm,
		SampleRateHz:   rate,
		Duration:       durSeconds(float64(samples) / float64(rate)),
	}, nil
}

// ===========================================================================
// ElevenLabs TTS — ISpeechSynthesizer
// ===========================================================================

// elevenLabsPcmRE extracts the sample rate from an ElevenLabs "pcm_NNNNN" format.
var elevenLabsPcmRE = regexp.MustCompile(`pcm_(\d+)`)

// ElevenLabsSpeechSynthesizer is an ISpeechSynthesizer backed by ElevenLabs
// /v1/text-to-speech (xi-api-key, output_format=pcm_*). Ports
// ElevenLabsSpeechSynthesizer.
type ElevenLabsSpeechSynthesizer struct {
	doer    ToolHTTPDoer
	options ElevenLabsOptions
}

// NewElevenLabsSpeechSynthesizer builds the synthesizer against an injected doer.
func NewElevenLabsSpeechSynthesizer(options ElevenLabsOptions, doer ToolHTTPDoer) *ElevenLabsSpeechSynthesizer {
	return &ElevenLabsSpeechSynthesizer{doer: doer, options: options}
}

// BackendID returns "elevenlabs".
func (s *ElevenLabsSpeechSynthesizer) BackendID() string { return "elevenlabs" }

// IsConfigured is true when the API key is present.
func (s *ElevenLabsSpeechSynthesizer) IsConfigured() bool { return !isBlank(s.options.ApiKey) }

// Synthesize posts to ElevenLabs TTS and returns the raw PCM at the format's
// sample rate. Ports SynthesizeAsync.
func (s *ElevenLabsSpeechSynthesizer) Synthesize(ctx context.Context, text, voiceID, languageHint string) (SynthesisResult, error) {
	if err := ctx.Err(); err != nil {
		return SynthesisResult{}, err
	}
	if !s.IsConfigured() {
		return emptySynthesis(), nil
	}

	voice := s.options.DefaultVoiceId
	if !isBlank(voiceID) {
		voice = voiceID
	}
	rate := parseElevenLabsPcmRate(s.options.OutputFormat, s.options.PcmSampleRateHz)
	path := fmt.Sprintf("/v1/text-to-speech/%s?output_format=%s", url.QueryEscape(voice), s.options.OutputFormat)
	body, _ := json.Marshal(map[string]any{"text": text, "model_id": s.options.Model})

	resp, err := s.doer(ctx, "POST", joinBaseAndPath(s.options.BaseAddress, path),
		map[string]string{"xi-api-key": s.options.ApiKey, "Content-Type": "application/json"}, body)
	if err != nil {
		return SynthesisResult{}, err
	}
	if !is2xx(resp.StatusCode) {
		return emptySynthesis(), nil
	}
	pcm := append([]byte(nil), resp.Body...)
	samples := len(pcm) / 2
	return SynthesisResult{
		AudioPcm16Mono: pcm,
		SampleRateHz:   rate,
		Duration:       durSeconds(float64(samples) / float64(rate)),
	}, nil
}

// parseElevenLabsPcmRate extracts the rate from "pcm_22050"/"pcm_24000"/etc,
// falling back to fallback. Ports ElevenLabsSpeechSynthesizer.ParsePcmRate.
func parseElevenLabsPcmRate(outputFormat string, fallback int) int {
	m := elevenLabsPcmRE.FindStringSubmatch(outputFormat)
	if m == nil {
		return fallback
	}
	r, err := strconv.Atoi(m[1])
	if err != nil {
		return fallback
	}
	return r
}

// ===========================================================================
// Cartesia Sonic TTS — ISpeechSynthesizer
// ===========================================================================

// CartesiaSpeechSynthesizer is an ISpeechSynthesizer backed by Cartesia Sonic
// /v1/tts/bytes (Bearer + Cartesia-Version). Ports CartesiaSpeechSynthesizer.
type CartesiaSpeechSynthesizer struct {
	doer    ToolHTTPDoer
	options CartesiaTtsOptions
}

// NewCartesiaSpeechSynthesizer builds the synthesizer against an injected doer.
func NewCartesiaSpeechSynthesizer(options CartesiaTtsOptions, doer ToolHTTPDoer) *CartesiaSpeechSynthesizer {
	return &CartesiaSpeechSynthesizer{doer: doer, options: options}
}

// BackendID returns "cartesia-tts".
func (s *CartesiaSpeechSynthesizer) BackendID() string { return "cartesia-tts" }

// IsConfigured is true when the API key is present.
func (s *CartesiaSpeechSynthesizer) IsConfigured() bool { return !isBlank(s.options.ApiKey) }

// Synthesize posts to Cartesia Sonic and returns the raw PCM. Ports
// SynthesizeAsync.
func (s *CartesiaSpeechSynthesizer) Synthesize(ctx context.Context, text, voiceID, languageHint string) (SynthesisResult, error) {
	if err := ctx.Err(); err != nil {
		return SynthesisResult{}, err
	}
	if !s.IsConfigured() {
		return emptySynthesis(), nil
	}

	voice := s.options.DefaultVoiceId
	if !isBlank(voiceID) {
		voice = voiceID
	}
	lang := "en"
	if languageHint != "" {
		lang = languageHint
	}
	body, _ := json.Marshal(map[string]any{
		"model_id":   s.options.Model,
		"transcript": text,
		"voice":      map[string]any{"mode": "id", "id": voice},
		"output_format": map[string]any{
			"container":   s.options.OutputContainer,
			"encoding":    s.options.OutputEncoding,
			"sample_rate": s.options.PcmSampleRateHz,
		},
		"language": lang,
	})
	headers := map[string]string{
		"Authorization":    "Bearer " + s.options.ApiKey,
		"Cartesia-Version": s.options.CartesiaVersion,
		"Content-Type":     "application/json",
	}

	resp, err := s.doer(ctx, "POST", joinBaseAndPath(s.options.BaseAddress, "/v1/tts/bytes"), headers, body)
	if err != nil {
		return SynthesisResult{}, err
	}
	if !is2xx(resp.StatusCode) {
		return emptySynthesis(), nil
	}
	pcm := append([]byte(nil), resp.Body...)
	samples := len(pcm) / 2
	return SynthesisResult{
		AudioPcm16Mono: pcm,
		SampleRateHz:   s.options.PcmSampleRateHz,
		Duration:       durSeconds(float64(samples) / float64(s.options.PcmSampleRateHz)),
	}, nil
}

// ===========================================================================
// Cartesia STT — ISpeechRecognizer
// ===========================================================================

// CartesiaSpeechRecognizer is an ISpeechRecognizer backed by Cartesia
// /v1/transcribe (multipart WAV upload, Bearer + Cartesia-Version). Ports
// CartesiaSpeechRecognizer.
type CartesiaSpeechRecognizer struct {
	doer    ToolHTTPDoer
	options CartesiaSttOptions
}

// NewCartesiaSpeechRecognizer builds the recognizer against an injected doer.
func NewCartesiaSpeechRecognizer(options CartesiaSttOptions, doer ToolHTTPDoer) *CartesiaSpeechRecognizer {
	return &CartesiaSpeechRecognizer{doer: doer, options: options}
}

// BackendID returns "cartesia-stt".
func (r *CartesiaSpeechRecognizer) BackendID() string { return "cartesia-stt" }

// IsConfigured is true when the API key is present.
func (r *CartesiaSpeechRecognizer) IsConfigured() bool { return !isBlank(r.options.ApiKey) }

// Transcribe uploads WAV-wrapped audio to Cartesia and parses { text, language,
// duration }. Ports TranscribeAsync.
func (r *CartesiaSpeechRecognizer) Transcribe(ctx context.Context, audioPcm16Mono []byte, sampleRateHz int, languageHint string) (SpeechTranscriptionResult, error) {
	if err := ctx.Err(); err != nil {
		return SpeechTranscriptionResult{}, err
	}
	if !r.IsConfigured() {
		return emptySpeechTranscription(), nil
	}

	wav := wrapPcmAsWav(audioPcm16Mono, sampleRateHz)
	fields := []multipartField{
		{Name: "file", Value: wav, Filename: "audio.wav", ContentType: "audio/wav"},
		{Name: "model", Value: []byte(r.options.Model)},
	}
	if !isBlank(languageHint) {
		fields = append(fields, multipartField{Name: "language", Value: []byte(languageHint)})
	}
	body, contentType := encodeMultipartForm(fields)
	headers := map[string]string{
		"Authorization":    "Bearer " + r.options.ApiKey,
		"Cartesia-Version": r.options.CartesiaVersion,
		"Content-Type":     contentType,
	}

	resp, err := r.doer(ctx, "POST", joinBaseAndPath(r.options.BaseAddress, "/v1/transcribe"), headers, body)
	if err != nil {
		return SpeechTranscriptionResult{}, err
	}
	if !is2xx(resp.StatusCode) {
		return emptySpeechTranscription(), nil
	}
	root, ok := jsonObj(resp.Body)
	if !ok {
		return emptySpeechTranscription(), nil
	}
	text := strField(root, "text")
	lang := languageHint
	if _, has := root["language"]; has {
		lang = strField(root, "language")
	}
	duration := durSeconds(numField(root, "duration"))
	return SpeechTranscriptionResult{Text: text, Language: lang, Segments: []TranscribedSegment{}, TotalDuration: duration}, nil
}

// ===========================================================================
// PlayHT TTS — ISpeechSynthesizer
// ===========================================================================

// PlayHtSpeechSynthesizer is an ISpeechSynthesizer backed by Play.HT streaming
// TTS /api/v2/tts/stream (Bearer + X-USER-ID, output_format=raw). Ports
// PlayHtSpeechSynthesizer.
type PlayHtSpeechSynthesizer struct {
	doer    ToolHTTPDoer
	options PlayHtOptions
}

// NewPlayHtSpeechSynthesizer builds the synthesizer against an injected doer.
func NewPlayHtSpeechSynthesizer(options PlayHtOptions, doer ToolHTTPDoer) *PlayHtSpeechSynthesizer {
	return &PlayHtSpeechSynthesizer{doer: doer, options: options}
}

// BackendID returns "playht".
func (s *PlayHtSpeechSynthesizer) BackendID() string { return "playht" }

// IsConfigured is true when BOTH the API key and the user id are present.
func (s *PlayHtSpeechSynthesizer) IsConfigured() bool {
	return !isBlank(s.options.ApiKey) && !isBlank(s.options.UserId)
}

// Synthesize posts to Play.HT and returns the raw PCM. Ports SynthesizeAsync.
func (s *PlayHtSpeechSynthesizer) Synthesize(ctx context.Context, text, voiceID, languageHint string) (SynthesisResult, error) {
	if err := ctx.Err(); err != nil {
		return SynthesisResult{}, err
	}
	if !s.IsConfigured() {
		return emptySynthesis(), nil
	}

	voice := s.options.DefaultVoice
	if !isBlank(voiceID) {
		voice = voiceID
	}
	lang := "english"
	if languageHint != "" {
		lang = languageHint
	}
	body, _ := json.Marshal(map[string]any{
		"text":          text,
		"voice":         voice,
		"voice_engine":  s.options.Model,
		"output_format": "raw",
		"sample_rate":   s.options.PcmSampleRateHz,
		"language":      lang,
	})
	headers := map[string]string{
		"Authorization": "Bearer " + s.options.ApiKey,
		"X-USER-ID":     s.options.UserId,
		"Accept":        "audio/raw",
		"Content-Type":  "application/json",
	}

	resp, err := s.doer(ctx, "POST", joinBaseAndPath(s.options.BaseAddress, "/api/v2/tts/stream"), headers, body)
	if err != nil {
		return SynthesisResult{}, err
	}
	if !is2xx(resp.StatusCode) {
		return emptySynthesis(), nil
	}
	pcm := append([]byte(nil), resp.Body...)
	samples := len(pcm) / 2
	return SynthesisResult{
		AudioPcm16Mono: pcm,
		SampleRateHz:   s.options.PcmSampleRateHz,
		Duration:       durSeconds(float64(samples) / float64(s.options.PcmSampleRateHz)),
	}, nil
}

// ---------------------------------------------------------------------------
// Interface guards
// ---------------------------------------------------------------------------

var (
	_ ISpeechRecognizer  = (*OpenAiSpeechRecognizer)(nil)
	_ ISpeechRecognizer  = (*DeepgramSpeechRecognizer)(nil)
	_ ISpeechRecognizer  = (*AssemblyAiSpeechRecognizer)(nil)
	_ ISpeechRecognizer  = (*GoogleSpeechRecognizer)(nil)
	_ ISpeechRecognizer  = (*AzureSpeechRecognizer)(nil)
	_ ISpeechRecognizer  = (*CartesiaSpeechRecognizer)(nil)
	_ ISpeechSynthesizer = (*OpenAiSpeechSynthesizer)(nil)
	_ ISpeechSynthesizer = (*DeepgramSpeechSynthesizer)(nil)
	_ ISpeechSynthesizer = (*AzureSpeechSynthesizer)(nil)
	_ ISpeechSynthesizer = (*GoogleSpeechSynthesizer)(nil)
	_ ISpeechSynthesizer = (*ElevenLabsSpeechSynthesizer)(nil)
	_ ISpeechSynthesizer = (*CartesiaSpeechSynthesizer)(nil)
	_ ISpeechSynthesizer = (*PlayHtSpeechSynthesizer)(nil)
)
