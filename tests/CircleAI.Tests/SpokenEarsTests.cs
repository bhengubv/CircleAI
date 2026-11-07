// SpokenEarsTests.cs
//
// What the ears hand on from a short question: a window sized to it, and the sentence once.

using CircleAI.Voice;
using Xunit;

namespace CircleAI.Tests;

public sealed class SpokenEarsTests
{
    [Theory]
    [InlineData(1.0, 128)]
    [InlineData(2.0, 128)]
    [InlineData(2.2, 192)]
    [InlineData(3.2, 192)]
    [InlineData(3.3, 256)]
    [InlineData(5.3, 320)]
    [InlineData(30.0, 1500)]
    public void The_window_fits_the_clip_with_a_fifth_to_spare(double seconds, int window) =>
        Assert.Equal(window, WhisperNetTranscriber.AudioContextFor(seconds));

    [Theory]
    [InlineData("Why is the sky blue? Why is the sky blue? Why is the sky blue?", "Why is the sky blue?")]
    [InlineData("What is the capital of Kenya? What is the capital of Kenya?", "What is the capital of Kenya?")]
    [InlineData("Name one kind of bird. name one kind of bird.", "Name one kind of bird.")]
    public void A_sentence_in_a_loop_is_said_once(string heard, string expected) =>
        Assert.Equal(expected, WhisperNetTranscriber.WithoutRepeats(heard));

    [Fact]
    public void What_a_cut_off_loop_leaves_at_the_end_goes_with_it() =>
        Assert.Equal("Why is the sky blue?", WhisperNetTranscriber.WithoutRepeats("Why is the sky blue? Why is the sky blue? Why is the"));

    [Theory]
    [InlineData("No. No.")]
    [InlineData("What is the time? What is the date?")]
    [InlineData("Say it again. Say it again please.")]
    [InlineData("It costs 3.5 million")]
    [InlineData("")]
    public void Anything_said_once_is_left_as_it_is(string heard) =>
        Assert.Equal(heard, WhisperNetTranscriber.WithoutRepeats(heard));

    [Fact]
    public void Words_after_a_loop_are_kept() =>
        Assert.Equal("Call my mother. And my father", WhisperNetTranscriber.WithoutRepeats("Call my mother. Call my mother. And my father"));
}
