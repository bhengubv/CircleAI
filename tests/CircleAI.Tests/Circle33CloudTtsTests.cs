// Circle33CloudTtsTests.cs
//
// (3.3.0) Tests for the 6 new cloud TTS synthesizers (ElevenLabs,
// Cartesia Sonic, Deepgram Aura, Azure, Google, Play.HT). 7th vendor
// (OpenAI TTS) was already shipped in 3.2.0.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CircleAI.Speech;
using CircleAI.Speech.Cloud;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CircleAI.Tests;

public class Circle33CloudTtsTests
{
    private static readonly byte[] FakePcm = new byte[480]; // 240 samples @ 16-bit
    private const string Text = "hello world";

    // ===== ElevenLabs =====

    [Fact]
    public void ElevenLabs_NotConfigured_WhenKeyMissing()
    {
        var s = new ElevenLabsSpeechSynthesizer(new HttpClient(new TtsRecordingHandler()), new ElevenLabsOptions());
        Assert.False(s.IsConfigured);
    }

    [Fact]
    public async Task ElevenLabs_Configured_ReturnsRawPcm()
    {
        var handler = new TtsRecordingHandler((_ => true, RawPcm(FakePcm)));
        var s = new ElevenLabsSpeechSynthesizer(new HttpClient(handler),
            new ElevenLabsOptions { ApiKey = "k", OutputFormat = "pcm_24000" });

        var r = await s.SynthesizeAsync(Text);

        Assert.Equal(FakePcm.Length, r.AudioPcm16Mono.Length);
        Assert.Equal(24000, r.SampleRateHz);
        Assert.True(r.Duration > TimeSpan.Zero);
        Assert.Equal("k", handler.Requests[0].Headers.GetValues("xi-api-key").Single());
    }

    [Fact]
    public async Task ElevenLabs_VoiceOverride_GoesIntoPath()
    {
        var handler = new TtsRecordingHandler((_ => true, RawPcm(FakePcm)));
        var s = new ElevenLabsSpeechSynthesizer(new HttpClient(handler),
            new ElevenLabsOptions { ApiKey = "k" });

        await s.SynthesizeAsync(Text, voiceId: "voice-xyz");

        Assert.Contains("voice-xyz", handler.Requests[0].RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task ElevenLabs_Non200_ReturnsEmpty()
    {
        var handler = new TtsRecordingHandler((_ => true, new HttpResponseMessage(HttpStatusCode.Unauthorized)));
        var s = new ElevenLabsSpeechSynthesizer(new HttpClient(handler),
            new ElevenLabsOptions { ApiKey = "k" });
        var r = await s.SynthesizeAsync(Text);
        Assert.True(r.AudioPcm16Mono.IsEmpty);
    }

    // ===== Cartesia Sonic =====

    [Fact]
    public void Cartesia_NotConfigured_WhenKeyMissing()
    {
        var s = new CartesiaSpeechSynthesizer(new HttpClient(new TtsRecordingHandler()), new CartesiaTtsOptions());
        Assert.False(s.IsConfigured);
    }

    [Fact]
    public async Task Cartesia_Configured_ReturnsRawPcm()
    {
        var handler = new TtsRecordingHandler((_ => true, RawPcm(FakePcm)));
        var s = new CartesiaSpeechSynthesizer(new HttpClient(handler),
            new CartesiaTtsOptions { ApiKey = "k", PcmSampleRateHz = 24000 });

        var r = await s.SynthesizeAsync(Text);

        Assert.Equal(FakePcm.Length, r.AudioPcm16Mono.Length);
        Assert.Equal(24000, r.SampleRateHz);
        Assert.Equal("Bearer", handler.Requests[0].Headers.Authorization?.Scheme);
        Assert.True(handler.Requests[0].Headers.TryGetValues("Cartesia-Version", out _));
    }

    [Fact]
    public async Task Cartesia_Non200_ReturnsEmpty()
    {
        var handler = new TtsRecordingHandler((_ => true, new HttpResponseMessage(HttpStatusCode.PaymentRequired)));
        var s = new CartesiaSpeechSynthesizer(new HttpClient(handler),
            new CartesiaTtsOptions { ApiKey = "k" });
        var r = await s.SynthesizeAsync(Text);
        Assert.True(r.AudioPcm16Mono.IsEmpty);
    }

    // ===== Deepgram Aura =====

    [Fact]
    public void DeepgramAura_NotConfigured_WhenKeyMissing()
    {
        var s = new DeepgramSpeechSynthesizer(new HttpClient(new TtsRecordingHandler()), new DeepgramTtsOptions());
        Assert.False(s.IsConfigured);
    }

    [Fact]
    public async Task DeepgramAura_Configured_ReturnsRawPcm()
    {
        var handler = new TtsRecordingHandler((_ => true, RawPcm(FakePcm)));
        var s = new DeepgramSpeechSynthesizer(new HttpClient(handler),
            new DeepgramTtsOptions { ApiKey = "k", PcmSampleRateHz = 24000 });

        var r = await s.SynthesizeAsync(Text);

        Assert.Equal(FakePcm.Length, r.AudioPcm16Mono.Length);
        Assert.Equal(24000, r.SampleRateHz);
        var url = handler.Requests[0].RequestUri!.ToString();
        Assert.Contains("encoding=linear16", url);
        Assert.Contains("sample_rate=24000", url);
        Assert.Equal("Token", handler.Requests[0].Headers.Authorization?.Scheme);
    }

    [Fact]
    public async Task DeepgramAura_VoiceOverride_GoesIntoQuery()
    {
        var handler = new TtsRecordingHandler((_ => true, RawPcm(FakePcm)));
        var s = new DeepgramSpeechSynthesizer(new HttpClient(handler),
            new DeepgramTtsOptions { ApiKey = "k" });

        await s.SynthesizeAsync(Text, voiceId: "aura-luna-en");

        Assert.Contains("aura-luna-en", handler.Requests[0].RequestUri!.ToString());
    }

    // ===== Azure TTS =====

    [Fact]
    public void AzureTts_NotConfigured_WhenBaseAddressMissing()
    {
        var s = new AzureSpeechSynthesizer(new HttpClient(new TtsRecordingHandler()),
            new AzureTtsOptions { ApiKey = "k" });
        Assert.False(s.IsConfigured);
    }

    [Fact]
    public async Task AzureTts_Configured_ReturnsRawPcmWithSsmlPosted()
    {
        var handler = new TtsRecordingHandler((_ => true, RawPcm(FakePcm)));
        var s = new AzureSpeechSynthesizer(new HttpClient(handler),
            new AzureTtsOptions
            {
                ApiKey      = "k",
                BaseAddress = new Uri("https://eastus.tts.speech.microsoft.com"),
                PcmSampleRateHz = 24000,
            });

        var r = await s.SynthesizeAsync(Text);

        Assert.Equal(FakePcm.Length, r.AudioPcm16Mono.Length);
        Assert.Equal(24000, r.SampleRateHz);
        Assert.Equal("k", handler.Requests[0].Headers.GetValues("Ocp-Apim-Subscription-Key").Single());
        Assert.Equal("raw-24khz-16bit-mono-pcm",
            handler.Requests[0].Headers.GetValues("X-Microsoft-OutputFormat").Single());
        Assert.Contains("speak", handler.Bodies[0]);
        Assert.Contains("voice name", handler.Bodies[0]);
    }

    [Fact]
    public async Task AzureTts_Non200_ReturnsEmpty()
    {
        var handler = new TtsRecordingHandler((_ => true, new HttpResponseMessage(HttpStatusCode.Forbidden)));
        var s = new AzureSpeechSynthesizer(new HttpClient(handler),
            new AzureTtsOptions
            {
                ApiKey      = "k",
                BaseAddress = new Uri("https://eastus.tts.speech.microsoft.com"),
            });
        var r = await s.SynthesizeAsync(Text);
        Assert.True(r.AudioPcm16Mono.IsEmpty);
    }

    // ===== Google TTS =====

    [Fact]
    public void GoogleTts_NotConfigured_WhenKeyMissing()
    {
        var s = new GoogleSpeechSynthesizer(new HttpClient(new TtsRecordingHandler()), new GoogleTtsOptions());
        Assert.False(s.IsConfigured);
    }

    [Fact]
    public async Task GoogleTts_Configured_StripsWavHeader_AndReturnsPcm()
    {
        // 44-byte WAV header + FakePcm payload, base64-encoded.
        var wav = new byte[44 + FakePcm.Length];
        wav[0] = (byte)'R'; wav[1] = (byte)'I'; wav[2] = (byte)'F'; wav[3] = (byte)'F';
        Array.Copy(FakePcm, 0, wav, 44, FakePcm.Length);
        var b64 = Convert.ToBase64String(wav);
        var handler = new TtsRecordingHandler((_ => true,
            Json($$"""{"audioContent":"{{b64}}"}""")));

        var s = new GoogleSpeechSynthesizer(new HttpClient(handler),
            new GoogleTtsOptions { ApiKey = "k", PcmSampleRateHz = 24000 });

        var r = await s.SynthesizeAsync(Text);

        Assert.Equal(FakePcm.Length, r.AudioPcm16Mono.Length);
        Assert.Equal(24000, r.SampleRateHz);
        Assert.Contains("key=k", handler.Requests[0].RequestUri!.Query);
    }

    [Fact]
    public async Task GoogleTts_Non200_ReturnsEmpty()
    {
        var handler = new TtsRecordingHandler((_ => true, new HttpResponseMessage(HttpStatusCode.Forbidden)));
        var s = new GoogleSpeechSynthesizer(new HttpClient(handler),
            new GoogleTtsOptions { ApiKey = "k" });
        var r = await s.SynthesizeAsync(Text);
        Assert.True(r.AudioPcm16Mono.IsEmpty);
    }

    // ===== Play.HT =====

    [Fact]
    public void PlayHt_NotConfigured_WhenUserIdMissing()
    {
        var s = new PlayHtSpeechSynthesizer(new HttpClient(new TtsRecordingHandler()),
            new PlayHtOptions { ApiKey = "k" });
        Assert.False(s.IsConfigured);
    }

    [Fact]
    public async Task PlayHt_Configured_ReturnsRawPcm()
    {
        var handler = new TtsRecordingHandler((_ => true, RawPcm(FakePcm)));
        var s = new PlayHtSpeechSynthesizer(new HttpClient(handler),
            new PlayHtOptions { ApiKey = "k", UserId = "u" });

        var r = await s.SynthesizeAsync(Text);

        Assert.Equal(FakePcm.Length, r.AudioPcm16Mono.Length);
        Assert.Equal("u", handler.Requests[0].Headers.GetValues("X-USER-ID").Single());
        Assert.StartsWith("Bearer ", handler.Requests[0].Headers.GetValues("Authorization").Single());
    }

    [Fact]
    public async Task PlayHt_Non200_ReturnsEmpty()
    {
        var handler = new TtsRecordingHandler((_ => true, new HttpResponseMessage(HttpStatusCode.Forbidden)));
        var s = new PlayHtSpeechSynthesizer(new HttpClient(handler),
            new PlayHtOptions { ApiKey = "k", UserId = "u" });
        var r = await s.SynthesizeAsync(Text);
        Assert.True(r.AudioPcm16Mono.IsEmpty);
    }

    // ===== DI =====

    [Fact]
    public void DI_AllSixSynthesizers_ResolveByConcreteType()
    {
        var services = new ServiceCollection();
        services.AddElevenLabsSpeechSynthesizer(_ => new ElevenLabsOptions { ApiKey = "x" });
        services.AddCartesiaSpeechSynthesizer  (_ => new CartesiaTtsOptions { ApiKey = "x" });
        services.AddDeepgramSpeechSynthesizer  (_ => new DeepgramTtsOptions { ApiKey = "x" });
        services.AddAzureSpeechSynthesizer     (_ => new AzureTtsOptions    { ApiKey = "x", BaseAddress = new Uri("https://eastus.tts.speech.microsoft.com") });
        services.AddGoogleSpeechSynthesizer    (_ => new GoogleTtsOptions   { ApiKey = "x" });
        services.AddPlayHtSpeechSynthesizer    (_ => new PlayHtOptions      { ApiKey = "x", UserId = "u" });
        using var sp = services.BuildServiceProvider();

        Assert.Equal("elevenlabs",    sp.GetRequiredService<ElevenLabsSpeechSynthesizer>().BackendId);
        Assert.Equal("cartesia-tts",  sp.GetRequiredService<CartesiaSpeechSynthesizer>().BackendId);
        Assert.Equal("deepgram-aura", sp.GetRequiredService<DeepgramSpeechSynthesizer>().BackendId);
        Assert.Equal("azure-tts",     sp.GetRequiredService<AzureSpeechSynthesizer>().BackendId);
        Assert.Equal("google-tts",    sp.GetRequiredService<GoogleSpeechSynthesizer>().BackendId);
        Assert.Equal("playht",        sp.GetRequiredService<PlayHtSpeechSynthesizer>().BackendId);
    }

    private static HttpResponseMessage RawPcm(byte[] pcm) =>
        new(HttpStatusCode.OK) { Content = new ByteArrayContent(pcm) };

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class TtsRecordingHandler : HttpMessageHandler
    {
        private readonly List<(Func<HttpRequestMessage, bool> Match, HttpResponseMessage Response)> _responses;
        public List<HttpRequestMessage> Requests { get; } = new();
        public List<string> Bodies { get; } = new();

        public TtsRecordingHandler(params (Func<HttpRequestMessage, bool>, HttpResponseMessage)[] responses)
        {
            _responses = responses.ToList();
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            Bodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct));
            for (int i = 0; i < _responses.Count; i++)
            {
                if (_responses[i].Match(request))
                {
                    var resp = _responses[i].Response;
                    _responses.RemoveAt(i);
                    return resp;
                }
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }
}
