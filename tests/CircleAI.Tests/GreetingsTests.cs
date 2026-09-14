// GreetingsTests.cs
//
// Which languages this product leads with.
//
// THREE LISTS EXISTED AT ONCE and no two agreed: six tags in one head, five
// different ones in the other, and a third six in Welcome. They overlapped on
// two entries. Nothing about the product changed between them - they were
// written at different times by whoever was in that file.

using CircleAI.Assistant;
using Xunit;

namespace CircleAI.Tests;

public class GreetingsTests
{
    [Fact]
    public void English_is_never_first()
    {
        // THE WHOLE POINT OF THE ORDER. This thing speaks languages other
        // assistants do not, so the first sound it makes should be one of them.
        // A carousel that opened in English would be demonstrating nothing.
        Assert.NotEqual("en", Greetings.Carousel[0]);
        Assert.Equal("zu", Greetings.Carousel[0]);
    }

    [Fact]
    public void English_is_last_so_you_only_hear_it_if_you_keep_pressing()
        => Assert.Equal("en", Greetings.Carousel[^1]);

    [Fact]
    public void The_carousel_wraps_rather_than_running_off_the_end()
    {
        var n = Greetings.Carousel.Count;

        Assert.Equal(Greetings.Carousel[0], Greetings.At(n));
        Assert.Equal(Greetings.Carousel[1], Greetings.At(n + 1));
    }

    [Fact]
    public void A_negative_index_is_still_a_language()
    {
        // A screen holds this index across renders and Blazor rebuilds the
        // component around it. An index that went negative would throw inside a
        // render, which takes the page down rather than the greeting.
        Assert.Contains(Greetings.At(-1), Greetings.Carousel);
        Assert.Contains(Greetings.At(-7), Greetings.Carousel);
    }

    [Fact]
    public void Next_visits_every_language_before_repeating()
    {
        // A carousel that skipped one would silently drop a language from the
        // demonstration, which is the one thing it exists to do.
        var seen = new List<string>();
        var i = 0;
        for (var step = 0; step < Greetings.Carousel.Count; step++)
        {
            seen.Add(Greetings.At(i));
            i = Greetings.Next(i);
        }

        Assert.Equal(Greetings.Carousel.Count, seen.Distinct().Count());
        Assert.Equal(0, i);   // back where it started
    }

    [Fact]
    public void No_tag_appears_twice()
        => Assert.Equal(Greetings.Carousel.Count, Greetings.Carousel.Distinct().Count());

    [Fact]
    public void Every_greeting_language_is_one_the_catalogue_knows()
    {
        // A TAG WITH NO VOICE IS A PRESS THAT DOES NOTHING. The carousel calls
        // IVoiceHost.SpeakAsync(tag), which looks the tag up in the catalogue -
        // so a typo here is a silent circle rather than a build error.
        foreach (var tag in Greetings.Carousel)
            Assert.True(SampleLanguages.Find(tag) is not null,
                $"'{tag}' is in the greeting carousel and not in SampleLanguages");
    }
}
