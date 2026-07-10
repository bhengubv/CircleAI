// voice_audio_capture.go
//
// Deterministic in-memory IAudioCapture for the Go port. The C# reference wires
// platform microphone backends (out of scope) plus NullAudioCapture; this
// provides a scripted capture that replays a fixed list of PCM chunks so the
// VoicePipeline and EnergyWakeWordDetector are exercisable end-to-end without a
// real microphone.
//
// ScriptedAudioCapture yields its configured chunks in order, then closes the
// stream (like a finite recording). If Loop is set it re-emits the chunks until
// the context is cancelled (like a live mic). Honours cancellation between chunks.

package circleai

import "context"

// ScriptedAudioCapture replays a fixed list of PCM chunks as an IAudioCapture.
type ScriptedAudioCapture struct {
	format AudioFormat
	chunks [][]byte
	loop   bool
}

// NewScriptedAudioCapture constructs a capture that yields chunks in order at the
// given format, then closes. Chunks are defensively copied.
func NewScriptedAudioCapture(format AudioFormat, chunks [][]byte) *ScriptedAudioCapture {
	cp := make([][]byte, len(chunks))
	for i, c := range chunks {
		cp[i] = append([]byte(nil), c...)
	}
	return &ScriptedAudioCapture{format: format, chunks: cp}
}

// WithLoop returns a shallow copy that re-emits its chunks until the context is
// cancelled (simulating a continuous microphone). The receiver is unchanged.
func (c *ScriptedAudioCapture) WithLoop(loop bool) *ScriptedAudioCapture {
	clone := *c
	clone.loop = loop
	return &clone
}

// Format returns the configured PCM format.
func (c *ScriptedAudioCapture) Format() AudioFormat { return c.format }

// CaptureAsync yields the configured chunks (looping if enabled), then closes.
// Cancellation is honoured between chunks.
func (c *ScriptedAudioCapture) CaptureAsync(ctx context.Context) <-chan []byte {
	out := make(chan []byte)
	go func() {
		defer close(out)
		for {
			for _, chunk := range c.chunks {
				select {
				case <-ctx.Done():
					return
				case out <- append([]byte(nil), chunk...):
				}
			}
			if !c.loop {
				return
			}
			// On loop, re-check cancellation before another pass.
			if ctx.Err() != nil {
				return
			}
		}
	}()
	return out
}

// Close is a no-op.
func (c *ScriptedAudioCapture) Close(context.Context) error { return nil }

// Interface guard.
var _ IAudioCapture = (*ScriptedAudioCapture)(nil)
