// LinkAudioCodecTests.cs
//
// The audio link's wire format. These matter more than most codec tests because a
// Parcel is an unframed byte stream: get the field order wrong on one side and the
// reader does not fail, it reads PLAUSIBLE NONSENSE — a verb that happens to be in
// range, a length that happens to parse — and then acts on it.

using System;
using System.Collections.Generic;
using CircleAI.Linking;
using Xunit;

namespace CircleAI.Tests;

public class LinkAudioCodecTests
{
    /// <summary>A Parcel stand-in: same ordered read/write, no Android.</summary>
    private sealed class Wire : LinkAudioCodec.IWriter, LinkAudioCodec.IReader
    {
        private readonly List<object?> _fields = new();
        private int _at;

        public void WriteInt(int value) => _fields.Add(value);
        public void WriteString(string? value) => _fields.Add(value);
        public void WriteBytes(byte[] value) => _fields.Add(value);

        public int ReadInt() => (int)_fields[_at++]!;
        public string? ReadString() => (string?)_fields[_at++];
        public byte[] ReadBytes() => (byte[])_fields[_at++]!;

        public void Rewind() => _at = 0;
        public int Count => _fields.Count;

        /// <summary>Corrupt one field, to prove a mismatch is caught rather than acted on.</summary>
        public void Poke(int index, object? value) => _fields[index] = value;
    }

    private static byte[] Speech(double seconds)
        => new byte[(int)(seconds * LinkAudioFormat.BytesPerSecond)];

    [Fact]
    public void A_transcribe_request_survives_the_wire()
    {
        var w = new Wire();
        var audio = Speech(3);
        audio[0] = 7; audio[^1] = 9;   // ends matter: a truncated array still round-trips

        Assert.True(LinkAudioCodec.TryWriteRequest(
            w, new LinkAudioRequest(LinkAudioVerb.Transcribe, audio, Language: "en-ZA"), out var refusal));
        Assert.Null(refusal);

        w.Rewind();
        var back = LinkAudioCodec.TryReadRequest(w);

        Assert.NotNull(back);
        Assert.Equal(LinkAudioVerb.Transcribe, back!.Verb);
        Assert.Equal("en-ZA", back.Language);
        Assert.Equal(audio.Length, back.Audio.Length);
        Assert.Equal(7, back.Audio[0]);
        Assert.Equal(9, back.Audio[^1]);
    }

    [Fact]
    public void A_speak_request_carries_words_and_no_audio()
    {
        var w = new Wire();
        Assert.True(LinkAudioCodec.TryWriteRequest(
            w, new LinkAudioRequest(LinkAudioVerb.Speak, Array.Empty<byte>(), "Good morning"), out _));

        w.Rewind();
        var back = LinkAudioCodec.TryReadRequest(w)!;

        Assert.Equal(LinkAudioVerb.Speak, back.Verb);
        Assert.Equal("Good morning", back.Text);
        Assert.Empty(back.Audio);
    }

    [Fact]
    public void Audio_past_the_binder_budget_is_refused_before_it_is_written()
    {
        // THE WHOLE REASON THE LIMIT IS DECLARED. A binder transaction has about a
        // megabyte, SHARED across everything in flight in the process — so a call
        // sized to the documented maximum fails only when something else happens to
        // be talking, intermittently, and blames whichever call was unlucky. Refusing
        // early turns that into a sentence.
        var w = new Wire();
        var tooMuch = new byte[LinkAudio.MaxAudioBytes + 1];

        Assert.False(LinkAudioCodec.TryWriteRequest(
            w, new LinkAudioRequest(LinkAudioVerb.Transcribe, tooMuch), out var refusal));
        Assert.Equal(0, w.Count);                      // nothing was written
        Assert.NotNull(refusal);
        Assert.Contains("seconds", refusal!);          // says it in time, not bytes
    }

    [Fact]
    public void The_limit_is_comfortably_under_the_kernels_budget()
    {
        // Leaving a quarter of the budget spare is what makes the failure mode
        // deterministic rather than "whenever something else is talking".
        Assert.True(LinkAudio.MaxAudioBytes < 1024 * 1024,
            "the limit must sit under the ~1 MB binder budget");
        Assert.True(LinkAudio.MaxAudioBytes <= 850 * 1024,
            "leave real headroom: the budget is shared across the process, not owned by this call");

        // and it has to be long enough for a spoken question, or it is useless
        Assert.True(LinkAudio.MaxSeconds >= 20,
            $"only {LinkAudio.MaxSeconds:0.#}s of speech fits; a spoken question needs more");
    }

    [Fact]
    public void The_service_re_checks_the_size_rather_than_trusting_the_caller()
    {
        // A service that trusts its callers on sizes is a service any app can crash.
        // Writing past the limit is impossible through TryWriteRequest, so this
        // fabricates the wire a hostile client would send.
        var w = new Wire();
        w.WriteInt(LinkAudioCodec.WireVersion);
        w.WriteInt((int)LinkAudioVerb.Transcribe);
        w.WriteString("");
        w.WriteString(null);
        w.WriteBytes(new byte[LinkAudio.MaxAudioBytes + 1]);

        w.Rewind();
        Assert.Null(LinkAudioCodec.TryReadRequest(w));
    }

    [Fact]
    public void A_wire_version_mismatch_is_declined_rather_than_guessed_at()
    {
        // Two apps on one phone update independently, so a client CAN be newer than
        // the service it binds. Reading a changed layout with old expectations gives
        // structurally valid rubbish; declining lets somebody be told which app needs
        // updating.
        var w = new Wire();
        LinkAudioCodec.TryWriteRequest(
            w, new LinkAudioRequest(LinkAudioVerb.Transcribe, Speech(1)), out _);
        w.Poke(0, LinkAudioCodec.WireVersion + 1);

        w.Rewind();
        Assert.Null(LinkAudioCodec.TryReadRequest(w));
    }

    [Fact]
    public void A_reply_that_does_not_parse_says_which_side_is_old()
    {
        var w = new Wire();
        LinkAudioCodec.WriteReply(w, LinkAudioReply.Transcribed("hello"));
        w.Poke(0, 99);

        w.Rewind();
        var reply = LinkAudioCodec.ReadReply(w);

        Assert.False(reply.Ok);
        Assert.Contains("v99", reply.Error!);
        Assert.Contains("updating", reply.Error!);
    }

    [Fact]
    public void Synthesis_that_overruns_the_budget_comes_back_as_a_failure_not_a_dead_binder()
    {
        // The direction nobody checks: a short sentence makes several seconds of
        // speech and a paragraph makes more than the link carries. Throwing inside
        // the service's own reply leaves the client with a dead binder and no reason.
        var w = new Wire();
        LinkAudioCodec.WriteReply(w, LinkAudioReply.Spoke(new byte[LinkAudio.MaxAudioBytes + 1]));

        w.Rewind();
        var reply = LinkAudioCodec.ReadReply(w);

        Assert.False(reply.Ok);
        Assert.Contains("seconds", reply.Error!);
        Assert.Empty(reply.Audio);
    }

    [Fact]
    public void Voice_is_its_own_scope_and_not_implied_by_chat()
    {
        // What is being approved differs in kind. Chat is "ask the shared brain a
        // question"; Voice is "send it whatever the microphone picked up", which can
        // include a conversation nobody meant to share. A person must be able to
        // refuse the second while allowing the first.
        Assert.NotEqual(LinkScope.Chat, LinkScope.Voice);
        Assert.False(LinkScope.Chat.HasFlag(LinkScope.Voice));
        Assert.False(LinkScope.Voice.HasFlag(LinkScope.Chat));

        // and it is a distinct flag bit, so a grant can hold both
        Assert.Equal(LinkScope.Chat | LinkScope.Voice, (LinkScope)(1 | 8));
    }

    [Fact]
    public void The_agreed_format_is_the_one_the_recognisers_expect()
    {
        // Stated rather than negotiated: a negotiation is a thing that can disagree,
        // and everything catalogued here resamples to 16 kHz mono 16-bit anyway.
        Assert.Equal(16_000, LinkAudioFormat.SampleRate);
        Assert.Equal(1, LinkAudioFormat.Channels);
        Assert.Equal(16, LinkAudioFormat.BitsPerSample);
        Assert.Equal(32_000, LinkAudioFormat.BytesPerSecond);
    }
}
