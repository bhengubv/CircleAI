// OnnxSpeechEmotionSensor.cs
//
// (Phase E6) Adapter that implements HER/Jarvis IEmotionSensor by running
// CircleAI.Voice's wav2vec2-style speech-emotion ONNX model on audio frames
// passed in the fused-signal JSON.
//
// The IEmotionSensor contract takes a `string fusedJson` so this adapter
// expects the caller to put a base64-encoded PCM16 audio blob and its
// sample rate under known keys:
//   {
//     "audio_pcm16_b64": "AAA...",
//     "sample_rate_hz":  16000
//   }
//
// When the JSON has no audio we fall back to a neutral frame so callers
// always get a usable EmotionFrame.

using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CircleAI.Companion.HerJarvis;
using CircleAI.Voice;

namespace CircleAI.Companion;

public sealed class OnnxSpeechEmotionSensor : IEmotionSensor
{
    private readonly ISpeechEmotionDetector _detector;
    public OnnxSpeechEmotionSensor(ISpeechEmotionDetector detector)
        => _detector = detector ?? throw new ArgumentNullException(nameof(detector));

    public async ValueTask<EmotionFrame> SenseAsync(string fusedJson, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(fusedJson)) return Neutral();
        try
        {
            using var doc = JsonDocument.Parse(fusedJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return Neutral();
            if (!doc.RootElement.TryGetProperty("audio_pcm16_b64", out var audioProp)) return Neutral();
            var b64 = audioProp.GetString();
            if (string.IsNullOrEmpty(b64)) return Neutral();

            var sampleRateHz = doc.RootElement.TryGetProperty("sample_rate_hz", out var srProp)
                ? srProp.GetInt32() : 16_000;
            var bytes = Convert.FromBase64String(b64);
            var frame = await _detector.SenseAsync(bytes.AsMemory(), sampleRateHz, ct).ConfigureAwait(false);
            return frame is null
                ? Neutral()
                : new EmotionFrame(frame.Label, frame.Arousal, frame.Valence);
        }
        catch (JsonException)        { return Neutral(); }
        catch (FormatException)      { return Neutral(); }
    }

    private static EmotionFrame Neutral() => new("neutral", 0.0, 0.0);
}
