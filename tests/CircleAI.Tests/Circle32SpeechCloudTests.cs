// Circle32SpeechCloudTests.cs
//
// (3.2.0) Tests for CircleAI.Speech.Cloud — OpenAI Whisper recognizer
// and OpenAI TTS synthesizer fail-soft behaviour (empty result when
// no API key, NOT throw), plus KeywordVoiceIntentRouter regex matching
// and capture extraction.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using CircleAI.Speech.Cloud;
using Xunit;

namespace CircleAI.Tests;

public sealed class Circle32SpeechCloudTests
{
    // ── OpenAiSpeechRecognizer (Whisper) ──────────────────────────────

    [Fact]
    public void OpenAiSpeechRecognizer_BackendId_IsStable()
    {
        var r = new OpenAiSpeechRecognizer(new HttpClient(), new OpenAiVoiceOptions());
        Assert.Equal("openai-whisper", r.BackendId);
    }

    [Fact]
    public void OpenAiSpeechRecognizer_IsConfigured_RequiresApiKey()
    {
        var noKey  = new OpenAiSpeechRecognizer(new HttpClient(), new OpenAiVoiceOptions());
        var hasKey = new OpenAiSpeechRecognizer(new HttpClient(), new OpenAiVoiceOptions { ApiKey = "sk-test" });

        Assert.False(noKey.IsConfigured);
        Assert.True(hasKey.IsConfigured);
    }

    [Fact]
    public async Task OpenAiSpeechRecognizer_NoKey_ReturnsEmptyResult()
    {
        var r = new OpenAiSpeechRecognizer(new HttpClient(), new OpenAiVoiceOptions());
        var pcm = new byte[16_000]; // 1 second @ 8 kHz / 0.5 s @ 16 kHz
        var result = await r.TranscribeAsync(pcm, sampleRateHz: 16_000);

        Assert.NotNull(result);
        Assert.Equal(string.Empty, result.Text);
        Assert.Empty(result.Segments);
        Assert.Equal(TimeSpan.Zero, result.TotalDuration);
    }

    // ── OpenAiSpeechSynthesizer (TTS) ─────────────────────────────────

    [Fact]
    public void OpenAiSpeechSynthesizer_BackendId_IsStable()
    {
        var s = new OpenAiSpeechSynthesizer(new HttpClient(), new OpenAiVoiceOptions());
        Assert.Equal("openai-tts", s.BackendId);
    }

    [Fact]
    public async Task OpenAiSpeechSynthesizer_NoKey_ReturnsEmptyAudio()
    {
        var s = new OpenAiSpeechSynthesizer(new HttpClient(), new OpenAiVoiceOptions());
        var result = await s.SynthesizeAsync("hello world");

        Assert.NotNull(result);
        Assert.Equal(0, result.AudioPcm16Mono.Length);
        Assert.Equal(0, result.SampleRateHz);
        Assert.Equal(TimeSpan.Zero, result.Duration);
    }

    [Fact]
    public void OpenAiVoiceOptions_PcmDefaults_Match_OpenAiSpec()
    {
        var o = new OpenAiVoiceOptions();
        Assert.Equal(24_000, o.PcmSampleRateHz);
        Assert.Equal("whisper-1", o.TranscriptionModel);
        Assert.Equal("tts-1", o.SpeechModel);
        Assert.Equal("alloy", o.DefaultVoice);
    }

    // ── KeywordVoiceIntentRouter ──────────────────────────────────────

    [Fact]
    public async Task Router_EmptyTranscript_ReturnsFallback()
    {
        var router = new KeywordVoiceIntentRouter(
            new[] { new VoiceIntent("open", new Regex(@"open\s+(?<note>.+)", RegexOptions.IgnoreCase)) });

        var match = await router.RouteAsync("");
        Assert.Equal("ask-ai", match.IntentName);
        Assert.Empty(match.Captures);
    }

    [Fact]
    public async Task Router_FirstMatchWins()
    {
        var router = new KeywordVoiceIntentRouter(
            new[]
            {
                new VoiceIntent("open",   new Regex(@"^open\s+(?<note>.+)$", RegexOptions.IgnoreCase)),
                new VoiceIntent("search", new Regex(@"^search\s+(?<q>.+)$",  RegexOptions.IgnoreCase)),
            });

        var match = await router.RouteAsync("open the door");
        Assert.Equal("open", match.IntentName);
        Assert.Equal("the door", match.Captures["note"]);
    }

    [Fact]
    public async Task Router_NoMatch_ReturnsFallback()
    {
        var router = new KeywordVoiceIntentRouter(
            new[] { new VoiceIntent("foo", new Regex(@"^foo\s+(?<x>.+)$", RegexOptions.IgnoreCase)) },
            fallbackIntentName: "ask-ai");

        var match = await router.RouteAsync("hello world");
        Assert.Equal("ask-ai", match.IntentName);
        Assert.Equal("hello world", match.Transcript);
        Assert.Empty(match.Captures);
    }

    [Fact]
    public async Task Router_TrimsTranscript_AndIgnoresImplicitGroup()
    {
        var router = new KeywordVoiceIntentRouter(
            new[] { new VoiceIntent("greet", new Regex(@"^hi\s+(?<name>\w+)$", RegexOptions.IgnoreCase)) });

        var match = await router.RouteAsync("   hi  Lerato   ");
        Assert.Equal("greet", match.IntentName);
        Assert.Equal("Lerato", match.Captures["name"]);
        Assert.False(match.Captures.ContainsKey("0"));
    }

    [Fact]
    public async Task Router_MultipleNamedGroups_AllSurfaced()
    {
        var router = new KeywordVoiceIntentRouter(
            new[] { new VoiceIntent("translate",
                new Regex(@"translate\s+""(?<text>[^""]+)""\s+to\s+(?<lang>\w+)",
                    RegexOptions.IgnoreCase)) });

        var match = await router.RouteAsync("translate \"hello\" to Zulu");
        Assert.Equal("translate", match.IntentName);
        Assert.Equal("hello", match.Captures["text"]);
        Assert.Equal("Zulu",  match.Captures["lang"]);
    }

    [Fact]
    public async Task NullVoiceIntentRouter_AlwaysReturnsFallback()
    {
        var router = NullVoiceIntentRouter.Instance;
        var match = await router.RouteAsync("anything");
        Assert.Equal("ask-ai", match.IntentName);
        Assert.Equal("anything", match.Transcript);
        Assert.Empty(match.Captures);
    }

    [Fact]
    public void Router_BackendIds_AreStable()
    {
        Assert.Equal("keyword",
            new KeywordVoiceIntentRouter(Array.Empty<VoiceIntent>()).BackendId);
        Assert.Equal("null", NullVoiceIntentRouter.Instance.BackendId);
    }

    [Fact]
    public async Task Router_CustomFallback_IsHonored()
    {
        var router = new KeywordVoiceIntentRouter(
            Array.Empty<VoiceIntent>(),
            fallbackIntentName: "panic");

        var match = await router.RouteAsync("blah");
        Assert.Equal("panic", match.IntentName);
    }
}
