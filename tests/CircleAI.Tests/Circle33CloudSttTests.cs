// Circle33CloudSttTests.cs
//
// (3.3.0) Tests for the 5 new cloud STT recognizers (Deepgram,
// AssemblyAI, Google, Azure, Cartesia). Same fake-handler pattern as
// the carrier tests: capture requests + serve canned responses.

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

public class Circle33CloudSttTests
{
    private static readonly byte[] FakeAudio = Enumerable.Range(0, 160).Select(i => (byte)(i % 256)).ToArray();
    private const int SampleRate = 16000;

    // ===== Deepgram =====

    [Fact]
    public void Deepgram_NotConfigured_WhenApiKeyMissing()
    {
        var rec = new DeepgramSpeechRecognizer(new HttpClient(new SttRecordingHandler()), new DeepgramOptions());
        Assert.False(rec.IsConfigured);
    }

    [Fact]
    public async Task Deepgram_NotConfigured_ReturnsEmptyResult()
    {
        var rec = new DeepgramSpeechRecognizer(new HttpClient(new SttRecordingHandler()), new DeepgramOptions());
        var r = await rec.TranscribeAsync(FakeAudio, SampleRate);
        Assert.Equal(string.Empty, r.Text);
    }

    [Fact]
    public async Task Deepgram_Configured_ParsesTranscript()
    {
        var handler = new SttRecordingHandler((_ => true,
            Json("""
            {
              "metadata": { "duration": 1.5 },
              "results": {
                "channels": [{
                  "alternatives": [{
                    "transcript": "hello world",
                    "words": [
                      { "word": "hello", "start": 0.0, "end": 0.5, "confidence": 0.95 },
                      { "word": "world", "start": 0.5, "end": 1.0, "confidence": 0.92 }
                    ]
                  }]
                }]
              }
            }
            """)));
        var rec = new DeepgramSpeechRecognizer(new HttpClient(handler),
            new DeepgramOptions { ApiKey = "key" });

        var r = await rec.TranscribeAsync(FakeAudio, SampleRate, languageHint: "en");

        Assert.Equal("hello world", r.Text);
        Assert.Equal(2, r.Segments.Count);
        Assert.Equal(0.95f, r.Segments[0].Confidence, 2);
        Assert.Equal(TimeSpan.FromSeconds(1.5), r.TotalDuration);
        Assert.Equal("Token", handler.Requests[0].Headers.Authorization?.Scheme);
    }

    [Fact]
    public async Task Deepgram_PassesEncodingAndSampleRate()
    {
        var handler = new SttRecordingHandler((_ => true, Json("""{"results":{"channels":[]}}""")));
        var rec = new DeepgramSpeechRecognizer(new HttpClient(handler),
            new DeepgramOptions { ApiKey = "key" });

        await rec.TranscribeAsync(FakeAudio, sampleRateHz: 16000, languageHint: "en-US");

        var url = handler.Requests[0].RequestUri!.ToString();
        Assert.Contains("encoding=linear16", url);
        Assert.Contains("sample_rate=16000", url);
        Assert.Contains("language=en-US", url);
    }

    [Fact]
    public async Task Deepgram_Non200_ReturnsEmpty()
    {
        var handler = new SttRecordingHandler((_ => true, new HttpResponseMessage(HttpStatusCode.Unauthorized)));
        var rec = new DeepgramSpeechRecognizer(new HttpClient(handler),
            new DeepgramOptions { ApiKey = "key" });
        var r = await rec.TranscribeAsync(FakeAudio, SampleRate);
        Assert.Equal(string.Empty, r.Text);
    }

    // ===== AssemblyAI =====

    [Fact]
    public void AssemblyAi_NotConfigured_WhenApiKeyMissing()
    {
        var rec = new AssemblyAiSpeechRecognizer(new HttpClient(new SttRecordingHandler()), new AssemblyAiOptions());
        Assert.False(rec.IsConfigured);
    }

    [Fact]
    public async Task AssemblyAi_Configured_TwoStepThenPoll_ParsesTranscript()
    {
        var handler = new SttRecordingHandler(
            (r => r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath.EndsWith("/v2/upload"),
             Json("""{"upload_url":"https://example.com/audio"}""")),
            (r => r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath.EndsWith("/v2/transcript"),
             Json("""{"id":"abc","status":"queued"}""")),
            (r => r.Method == HttpMethod.Get && r.RequestUri!.AbsolutePath.Contains("/v2/transcript/abc"),
             Json("""
             {
               "id":"abc","status":"completed","text":"hello world",
               "language_code":"en","audio_duration":2.0,
               "words":[
                 {"text":"hello","start":0,"end":500,"confidence":0.99},
                 {"text":"world","start":500,"end":1000,"confidence":0.97}
               ]
             }
             """)));
        var rec = new AssemblyAiSpeechRecognizer(new HttpClient(handler),
            new AssemblyAiOptions { ApiKey = "key" });

        var r = await rec.TranscribeAsync(FakeAudio, SampleRate);

        Assert.Equal("hello world", r.Text);
        Assert.Equal("en", r.Language);
        Assert.Equal(2, r.Segments.Count);
        Assert.Equal(TimeSpan.FromSeconds(2.0), r.TotalDuration);
    }

    [Fact]
    public async Task AssemblyAi_UploadFailure_ReturnsEmpty()
    {
        var handler = new SttRecordingHandler((_ => true, new HttpResponseMessage(HttpStatusCode.Forbidden)));
        var rec = new AssemblyAiSpeechRecognizer(new HttpClient(handler),
            new AssemblyAiOptions { ApiKey = "key" });
        var r = await rec.TranscribeAsync(FakeAudio, SampleRate);
        Assert.Equal(string.Empty, r.Text);
    }

    [Fact]
    public async Task AssemblyAi_ErrorStatus_ReturnsEmpty()
    {
        var handler = new SttRecordingHandler(
            (r => r.RequestUri!.AbsolutePath.EndsWith("/v2/upload"),
             Json("""{"upload_url":"x"}""")),
            (r => r.RequestUri!.AbsolutePath.EndsWith("/v2/transcript"),
             Json("""{"id":"err","status":"queued"}""")),
            (r => r.Method == HttpMethod.Get,
             Json("""{"status":"error","error":"bad audio"}""")));
        var rec = new AssemblyAiSpeechRecognizer(new HttpClient(handler),
            new AssemblyAiOptions { ApiKey = "key" });

        var r = await rec.TranscribeAsync(FakeAudio, SampleRate);

        Assert.Equal(string.Empty, r.Text);
    }

    // ===== Google =====

    [Fact]
    public void Google_NotConfigured_WhenApiKeyMissing()
    {
        var rec = new GoogleSpeechRecognizer(new HttpClient(new SttRecordingHandler()), new GoogleSpeechOptions());
        Assert.False(rec.IsConfigured);
    }

    [Fact]
    public async Task Google_Configured_ParsesTranscript()
    {
        var handler = new SttRecordingHandler((_ => true,
            Json("""
            {
              "results": [{
                "alternatives": [{
                  "transcript": "hello world",
                  "confidence": 0.95,
                  "words": [
                    { "startTime": "0.000s", "endTime": "0.500s", "word": "hello", "confidence": 0.95 },
                    { "startTime": "0.500s", "endTime": "1.000s", "word": "world", "confidence": 0.92 }
                  ]
                }]
              }]
            }
            """)));
        var rec = new GoogleSpeechRecognizer(new HttpClient(handler),
            new GoogleSpeechOptions { ApiKey = "key" });

        var r = await rec.TranscribeAsync(FakeAudio, SampleRate, languageHint: "en-US");

        Assert.Equal("hello world", r.Text);
        Assert.Equal(2, r.Segments.Count);
        Assert.Contains("key=key", handler.Requests[0].RequestUri!.Query);
    }

    [Fact]
    public async Task Google_Non200_ReturnsEmpty()
    {
        var handler = new SttRecordingHandler((_ => true, new HttpResponseMessage(HttpStatusCode.Forbidden)));
        var rec = new GoogleSpeechRecognizer(new HttpClient(handler),
            new GoogleSpeechOptions { ApiKey = "key" });
        var r = await rec.TranscribeAsync(FakeAudio, SampleRate);
        Assert.Equal(string.Empty, r.Text);
    }

    // ===== Azure =====

    [Fact]
    public void Azure_NotConfigured_WhenKeyMissing()
    {
        var rec = new AzureSpeechRecognizer(new HttpClient(new SttRecordingHandler()),
            new AzureSpeechOptions { BaseAddress = new Uri("https://eastus.stt.speech.microsoft.com") });
        Assert.False(rec.IsConfigured);
    }

    [Fact]
    public void Azure_NotConfigured_WhenBaseAddressMissing()
    {
        var rec = new AzureSpeechRecognizer(new HttpClient(new SttRecordingHandler()),
            new AzureSpeechOptions { ApiKey = "key" });
        Assert.False(rec.IsConfigured);
    }

    [Fact]
    public async Task Azure_Configured_ParsesTranscript()
    {
        var handler = new SttRecordingHandler((_ => true,
            Json("""
            {
              "RecognitionStatus": "Success",
              "DisplayText": "hello world",
              "Offset": 1000000,
              "Duration": 15000000,
              "NBest": [{ "Confidence": 0.95 }]
            }
            """)));
        var rec = new AzureSpeechRecognizer(new HttpClient(handler),
            new AzureSpeechOptions
            {
                ApiKey      = "key",
                BaseAddress = new Uri("https://eastus.stt.speech.microsoft.com"),
            });

        var r = await rec.TranscribeAsync(FakeAudio, SampleRate, languageHint: "en-US");

        Assert.Equal("hello world", r.Text);
        Assert.Single(r.Segments);
        Assert.Equal(TimeSpan.FromTicks(15000000), r.TotalDuration);
        Assert.Equal("key", handler.Requests[0].Headers.GetValues("Ocp-Apim-Subscription-Key").Single());
    }

    [Fact]
    public async Task Azure_NonSuccess_ReturnsEmpty()
    {
        var handler = new SttRecordingHandler((_ => true,
            Json("""{"RecognitionStatus":"NoMatch","DisplayText":""}""")));
        var rec = new AzureSpeechRecognizer(new HttpClient(handler),
            new AzureSpeechOptions
            {
                ApiKey      = "key",
                BaseAddress = new Uri("https://eastus.stt.speech.microsoft.com"),
            });
        var r = await rec.TranscribeAsync(FakeAudio, SampleRate);
        Assert.Equal(string.Empty, r.Text);
    }

    // ===== Cartesia =====

    [Fact]
    public void Cartesia_NotConfigured_WhenApiKeyMissing()
    {
        var rec = new CartesiaSpeechRecognizer(new HttpClient(new SttRecordingHandler()), new CartesiaSttOptions());
        Assert.False(rec.IsConfigured);
    }

    [Fact]
    public async Task Cartesia_Configured_ParsesTranscript()
    {
        var handler = new SttRecordingHandler((_ => true,
            Json("""{"text":"hello world","language":"en","duration":2.5}""")));
        var rec = new CartesiaSpeechRecognizer(new HttpClient(handler),
            new CartesiaSttOptions { ApiKey = "key" });

        var r = await rec.TranscribeAsync(FakeAudio, SampleRate);

        Assert.Equal("hello world", r.Text);
        Assert.Equal("en", r.Language);
        Assert.Equal(TimeSpan.FromSeconds(2.5), r.TotalDuration);
        Assert.Equal("Bearer", handler.Requests[0].Headers.Authorization?.Scheme);
        Assert.True(handler.Requests[0].Headers.TryGetValues("Cartesia-Version", out _));
    }

    [Fact]
    public async Task Cartesia_Non200_ReturnsEmpty()
    {
        var handler = new SttRecordingHandler((_ => true, new HttpResponseMessage(HttpStatusCode.PaymentRequired)));
        var rec = new CartesiaSpeechRecognizer(new HttpClient(handler),
            new CartesiaSttOptions { ApiKey = "key" });
        var r = await rec.TranscribeAsync(FakeAudio, SampleRate);
        Assert.Equal(string.Empty, r.Text);
    }

    // ===== DI =====

    [Fact]
    public void DI_AddAllFiveRecognizers_AllResolvable()
    {
        // Last-write-wins on ISpeechRecognizer is fine; what we want is
        // that each concrete type resolves cleanly.
        var services = new ServiceCollection();
        services.AddDeepgramSpeechRecognizer  (_ => new DeepgramOptions    { ApiKey = "x" });
        services.AddAssemblyAiSpeechRecognizer(_ => new AssemblyAiOptions  { ApiKey = "x" });
        services.AddGoogleSpeechRecognizer    (_ => new GoogleSpeechOptions { ApiKey = "x" });
        services.AddAzureSpeechRecognizer     (_ => new AzureSpeechOptions  { ApiKey = "x", BaseAddress = new Uri("https://eastus.stt.speech.microsoft.com") });
        services.AddCartesiaSpeechRecognizer  (_ => new CartesiaSttOptions  { ApiKey = "x" });
        using var sp = services.BuildServiceProvider();

        Assert.Equal("deepgram",     sp.GetRequiredService<DeepgramSpeechRecognizer>().BackendId);
        Assert.Equal("assemblyai",   sp.GetRequiredService<AssemblyAiSpeechRecognizer>().BackendId);
        Assert.Equal("google-stt",   sp.GetRequiredService<GoogleSpeechRecognizer>().BackendId);
        Assert.Equal("azure-stt",    sp.GetRequiredService<AzureSpeechRecognizer>().BackendId);
        Assert.Equal("cartesia-stt", sp.GetRequiredService<CartesiaSpeechRecognizer>().BackendId);
    }

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    /// <summary>Records requests + replays first-match canned responses.</summary>
    private sealed class SttRecordingHandler : HttpMessageHandler
    {
        private readonly List<(Func<HttpRequestMessage, bool> Match, HttpResponseMessage Response)> _responses;
        public List<HttpRequestMessage> Requests { get; } = new();

        public SttRecordingHandler(params (Func<HttpRequestMessage, bool>, HttpResponseMessage)[] responses)
        {
            _responses = responses.ToList();
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            for (int i = 0; i < _responses.Count; i++)
            {
                if (_responses[i].Match(request))
                {
                    var resp = _responses[i].Response;
                    // Don't consume — AssemblyAI re-uses the polling response on a second match.
                    if (!(request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath.Contains("/v2/transcript/")))
                    {
                        _responses.RemoveAt(i);
                    }
                    return Task.FromResult(resp);
                }
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent($"No fake response for {request.Method} {request.RequestUri}"),
            });
        }
    }
}
