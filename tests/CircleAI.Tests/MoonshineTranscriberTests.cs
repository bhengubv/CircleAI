// MoonshineTranscriberTests.cs
//
// The streaming ears' own arithmetic: a vocabulary file read, tokens turned back into words. The
// model itself is opened only when CIRCLEAI_MOONSHINE_DIR names one and CIRCLEAI_MOONSHINE_WAV a
// recording (16 kHz mono PCM), because it is 45 MB this repo does not carry.

using System.Text;
using CircleAI.Voice;
using Xunit;
using Xunit.Abstractions;

namespace CircleAI.Tests;

public sealed class MoonshineTranscriberTests(ITestOutputHelper output)
{
    private static string? Model =>
        Environment.GetEnvironmentVariable("CIRCLEAI_MOONSHINE_DIR") is { Length: > 0 } d && Directory.Exists(d) ? d : null;

    private static string? Wav =>
        Environment.GetEnvironmentVariable("CIRCLEAI_MOONSHINE_WAV") is { Length: > 0 } f && File.Exists(f) ? f : null;

    private static byte[] Entry(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        return [(byte)bytes.Length, .. bytes];
    }

    [Fact]
    public void A_vocabulary_with_no_header_is_read_to_the_end_of_the_file()
    {
        byte[] file = [.. Entry("<s>"), 0, .. Entry("▁the")];

        var vocabulary = MoonshineTranscriber.ParseVocabulary(file);

        Assert.Equal(3, vocabulary.Length);
        Assert.Equal("<s>", Encoding.UTF8.GetString(vocabulary[0]));
        Assert.Empty(vocabulary[1]);
        Assert.Equal("▁the", Encoding.UTF8.GetString(vocabulary[2]));
    }

    [Fact]
    public void A_header_says_how_many_tokens_there_are_and_what_follows_them_is_not_one()
    {
        byte[] header = [0xFF, 0xFF, (byte)'B', (byte)'T', (byte)'O', (byte)'K', 1, 1, 2, 0, 0, 0, 0xFF, 0xFF, 0xFF, 0xFF];
        byte[] file = [.. header, .. Entry("a"), .. Entry("b"), .. Entry("scores")];

        var vocabulary = MoonshineTranscriber.ParseVocabulary(file);

        Assert.Equal(["a", "b"], vocabulary.Select(Encoding.UTF8.GetString));
    }

    [Fact]
    public void A_long_token_takes_two_bytes_to_say_how_long_it_is()
    {
        var token = new string('x', 300);

        // 300 = 2 * 128 + 172 - 128: the first byte is over 127 and the second counts the 128s.
        byte[] file = [172, 2, .. Encoding.UTF8.GetBytes(token), .. Entry("y")];

        var vocabulary = MoonshineTranscriber.ParseVocabulary(file);

        Assert.Equal(token, Encoding.UTF8.GetString(vocabulary[0]));
        Assert.Equal("y", Encoding.UTF8.GetString(vocabulary[1]));
    }

    [Fact]
    public void Tokens_spell_the_words_and_control_tokens_spell_nothing()
    {
        byte[][] vocabulary =
        [
            .. new[] { "<s>", "▁Why", "▁is", "▁the", "▁sk", "y", "?", "</s>" }.Select(Encoding.UTF8.GetBytes),
        ];

        Assert.Equal("Why is the sky?", MoonshineTranscriber.TextOf([0, 1, 2, 3, 4, 5, 6, 7], vocabulary));
    }

    [Fact]
    public async Task A_recording_comes_out_the_same_however_it_is_fed()
    {
        if (Model is not { } model || Wav is not { } wav)
        {
            output.WriteLine("CIRCLEAI_MOONSHINE_DIR and CIRCLEAI_MOONSHINE_WAV not set");
            return;
        }

        var pcm = File.ReadAllBytes(wav)[44..];
        await using var ears = new MoonshineTranscriber(model);

        var whole = (await ears.TranscribeAsync(pcm)).Text;

        using var hearing = ears.Begin();
        for (var at = 0; at < pcm.Length; at += 16_000)
        {
            hearing.Hear(pcm.AsSpan(at, Math.Min(16_000, pcm.Length - at)));
            hearing.Update();
        }

        output.WriteLine(whole);
        Assert.NotEmpty(whole);
        Assert.Equal(whole, hearing.Finish());
    }

    [Fact]
    public async Task A_quiet_room_writes_nothing()
    {
        if (Model is not { } model)
        {
            output.WriteLine("CIRCLEAI_MOONSHINE_DIR not set");
            return;
        }

        await using var ears = new MoonshineTranscriber(model);

        Assert.Empty((await ears.TranscribeAsync(new byte[64_000])).Text);
    }
}
