// WhoSaidWhatTests.cs
//
// OPEN-GAPS A3: diarisation was written and tested with no model behind it, so a transcript of
// a meeting came back as one voice. These pin the four pieces that close it: the catalogue row
// (CC-BY-4.0, attributed in THIRD-PARTY-NOTICES.md), setup fetching it, WeSpeaker's own features
// out of the existing KaldiFbank, and the labels reaching a transcribed file.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CircleAI.Assistant;
using CircleAI.Core;
using CircleAI.Core.Models;
using CircleAI.Inference;
using CircleAI.Voice;
using Xunit;

namespace CircleAI.Tests;

public sealed class WhoSaidWhatTests
{
    private const string Model = "WeSpeaker-ResNet34-LM";
    private const int Rate = 16_000;

    // ── The catalogue, the selector and setup ──────────────────────────

    [Fact]
    public void One_speaker_model_is_catalogued_with_a_licence_that_ships()
    {
        using var registry = new ModelRegistryService();
        var speakers = registry.AllModels.Where(m => m.Modality == ModelModality.SpeakerEmbedding).ToList();

        var row = Assert.Single(speakers);
        Assert.Equal(Model, row.Name);
        Assert.Equal("CC-BY-4.0", row.License);
        Assert.Equal("onnx/model.onnx", Assert.Single(row.BundleFiles!).Name);
        Assert.Equal(SpeakerEmbedderInputKind.WeSpeakerFbank, OnnxSpeakerIdentity.InputFor(row.Architecture));
    }

    [Fact]
    public void The_attribution_the_licence_asks_for_is_in_the_repo()
    {
        var notices = File.ReadAllText(Path.Combine(Root(), "THIRD-PARTY-NOTICES.md"));
        Assert.Contains("onnx-community/wespeaker-voxceleb-resnet34-LM", notices);
        Assert.Contains("CC BY 4.0", notices);
        Assert.Contains("VoxCeleb", notices);
    }

    [Fact]
    public void A_phone_is_given_the_speaker_model()
    {
        // About the P30's free memory and a phone's free storage.
        var phone = new DeviceProbe(
            RamAvailableBytes: 1_400L * 1024 * 1024,
            StorageFreeBytes:  20L * 1024 * 1024 * 1024,
            Gpu: GpuKind.None, CpuCores: 8,
            Thermal: ThermalClass.Passive, Connectivity: Connectivity.Online);

        using var registry = new ModelRegistryService();
        var plan = new SpeechModelSelector(registry).PlanFor(phone, ModelModality.SpeakerEmbedding);

        Assert.Equal(SelectionQuality.Good, plan.Quality);
        Assert.Equal(Model, plan.Model?.ModelId);
    }

    [Fact]
    public void Setup_fetches_it_on_a_build_that_can_transcribe_and_not_on_one_that_cannot()
    {
        var speech = FirstRun.WantedFor(speech: true);
        var last = speech[^1];
        Assert.Equal(ModelModality.SpeakerEmbedding, last.Modality);
        Assert.Equal("who said what", last.Title);

        Assert.DoesNotContain(FirstRun.WantedFor(speech: false), w => w.Modality == ModelModality.SpeakerEmbedding);
    }

    [Fact]
    public void The_loader_opens_a_speaker_bundle_at_its_graph_not_at_a_config_json()
    {
        // The default arm wanted config.json, which this bundle does not have: a speaker model
        // whole on disk was "no loadable model file".
        var store = Temp();
        try
        {
            var graph = Path.Combine(store, Model, "onnx", "model.onnx");
            Directory.CreateDirectory(Path.GetDirectoryName(graph)!);
            using (var f = File.Create(graph)) f.SetLength(26_535_549);

            using var loader = new BundleModelLoader(store);
            Assert.True(loader.ModelPresent(Model));
            Assert.Equal(Path.GetFullPath(graph), Path.GetFullPath(loader.GetModelPath(Model)));
        }
        finally { Directory.Delete(store, recursive: true); }
    }

    // ── WeSpeaker's features ────────────────────────────────────────────

    [Fact]
    public void The_features_are_time_first_with_the_utterance_mean_taken_off_every_bin()
    {
        var features = OnnxSpeakerIdentity.WeSpeakerFeatures(Tone(1_000, 0.1f, seconds: 1), 80, Rate, 25, 10);

        // Snipped edges: 1 + (16000 - 400) / 160 frames.
        Assert.Equal(new[] { 1, 98, 80 }, features.Dimensions.ToArray());

        for (var b = 0; b < 80; b++)
        {
            double sum = 0;
            for (var f = 0; f < 98; f++) sum += features[0, f, b];
            Assert.True(Math.Abs(sum / 98) < 1e-4, $"bin {b} keeps a mean of {sum / 98}");
        }
    }

    [Fact]
    public void How_loud_the_recording_was_does_not_change_the_voice()
    {
        // Twice the gain is +log 4 in every bin before centring and nothing after it, so the
        // same speaker recorded near or far from the phone gives the same input.
        var quiet = Tone(440, 0.1f, seconds: 1);
        var loud  = quiet.Select(s => s * 2).ToArray();

        var a = OnnxSpeakerIdentity.WeSpeakerFeatures(quiet, 80, Rate, 25, 10);
        var b = OnnxSpeakerIdentity.WeSpeakerFeatures(loud,  80, Rate, 25, 10);

        var worst = a.ToArray().Zip(b.ToArray(), (x, y) => Math.Abs(x - y)).Max();
        Assert.True(worst < 1e-3, $"features moved by {worst} with the gain");
    }

    [Fact]
    public void A_tone_lands_in_the_mel_band_that_holds_its_frequency()
    {
        var options = new KaldiFbankOptions(HighFreqHz: 0f, SnipEdges: true, ScaleToInt16: true, Window: KaldiWindow.Hamming);
        var fbank = new KaldiFbank(options);
        fbank.AcceptWaveform(Tone(1_000, 0.1f, seconds: 1));
        fbank.Flush();

        var frame = fbank.GetFrame(50);
        var loudest = Array.IndexOf(frame, frame.Max());

        // The band whose centre is nearest 1 kHz, on Kaldi's mel scale from 20 Hz to nyquist.
        static double Mel(double hz) => 1127.0 * Math.Log(1 + hz / 700.0);
        var delta = (Mel(8_000) - Mel(20)) / 81;
        var expected = Enumerable.Range(0, 80)
            .OrderBy(m => Math.Abs(Mel(20) + (m + 1) * delta - Mel(1_000)))
            .First();

        Assert.InRange(loudest, expected - 1, expected + 1);
    }

    [Fact]
    public void The_streaming_ears_keep_the_povey_window()
    {
        // Hamming is for the speaker model only; the wake word and streaming ASR read the default.
        Assert.Equal(KaldiWindow.Povey, new KaldiFbankOptions().Window);
    }

    // ── Labels reaching a transcript ────────────────────────────────────

    [Fact]
    public async Task Float_samples_are_labelled_the_same_way_bytes_are()
    {
        var samples = TwoVoices();
        var segments = Thirds();

        var named = await Diarisation.OfAsync(segments, samples, Rate, new LoudnessEmbedder());

        Assert.Equal(["Speaker 1", "Speaker 2", "Speaker 1"], named.Select(s => s.Speaker));
    }

    [Fact]
    public async Task A_transcribed_file_says_who_spoke_each_line_when_there_is_a_speaker_model()
    {
        var dir = Temp();
        try
        {
            var path = Path.Combine(dir, "meeting.wav");
            File.WriteAllBytes(path, Wav(TwoVoices()));
            await using var ears = new ThreeLines();

            var labelled = await Transcribing.FileAsync(ears, path, speakers: new LoudnessEmbedder());
            Assert.Equal(["Speaker 1", "Speaker 2", "Speaker 1"], labelled.Timed.Select(s => s.Speaker));
            Assert.Equal(2, Diarisation.SpeakerCount(labelled.Timed));

            // Without the model nothing is invented: the same lines, no speakers.
            var plain = await Transcribing.FileAsync(ears, path);
            Assert.All(plain.Timed, s => Assert.Null(s.Speaker));
            Assert.Equal(labelled.Timed.Select(s => s.Text), plain.Timed.Select(s => s.Text));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    private static float[] Tone(double hz, float amplitude, double seconds) =>
        Enumerable.Range(0, (int)(Rate * seconds))
            .Select(i => amplitude * (float)Math.Sin(2 * Math.PI * hz * i / Rate))
            .ToArray();

    /// <summary>Six seconds: a quiet voice, a loud one, the quiet one again.</summary>
    private static float[] TwoVoices() =>
        [.. Tone(300, 0.05f, 2), .. Tone(300, 0.5f, 2), .. Tone(300, 0.05f, 2)];

    private static List<TranscriptSegment> Thirds() =>
    [
        new("first",  TimeSpan.FromSeconds(0), TimeSpan.FromSeconds(2)),
        new("second", TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4)),
        new("third",  TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(6)),
    ];

    /// <summary>Two "voices" by loudness - enough to prove the plumbing, which is the question here.</summary>
    private sealed class LoudnessEmbedder : ISpeakerEmbedder
    {
        public ValueTask<float[]?> EmbedAsync(ReadOnlyMemory<byte> pcm16, int sampleRateHz, CancellationToken ct = default)
        {
            var span = pcm16.Span;
            double sum = 0;
            for (var i = 0; i + 1 < span.Length; i += 2)
            {
                var s = (short)(span[i] | (span[i + 1] << 8)) / 32768.0;
                sum += s * s;
            }
            var rms = Math.Sqrt(sum / Math.Max(1, span.Length / 2));
            return ValueTask.FromResult<float[]?>(rms < 0.1 ? [1f, 0f] : [0f, 1f]);
        }
    }

    /// <summary>Hears three lines, a third of the file each.</summary>
    private sealed class ThreeLines : IVoiceTranscriber
    {
        public Task<TranscriptionResult> TranscribeAsync(
            ReadOnlyMemory<byte> pcmAudio, CancellationToken ct = default, string? language = null)
            => Task.FromResult(new TranscriptionResult("first second third", 1f, "en", Thirds()));

        public async IAsyncEnumerable<PartialTranscription> StreamTranscribeAsync(
            IAsyncEnumerable<ReadOnlyMemory<byte>> audioChunks,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            yield return new PartialTranscription(string.Empty, true, 0f);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>A 16 kHz mono 16-bit WAV of the samples.</summary>
    private static byte[] Wav(float[] samples)
    {
        var data = WavIo.ToPcm16(samples);
        using var ms = new MemoryStream();
        using var w  = new BinaryWriter(ms);
        w.Write("RIFF"u8);
        w.Write(36 + data.Length);
        w.Write("WAVE"u8);
        w.Write("fmt "u8);
        w.Write(16);
        w.Write((short)1);
        w.Write((short)1);
        w.Write(Rate);
        w.Write(Rate * 2);
        w.Write((short)2);
        w.Write((short)16);
        w.Write("data"u8);
        w.Write(data.Length);
        w.Write(data);
        w.Flush();
        return ms.ToArray();
    }

    private static string Temp()
    {
        var dir = Path.Combine(Path.GetTempPath(), "circleai-who-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "CircleAI.sln"))) directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("CircleAI.sln is not above the test output.");
    }
}
