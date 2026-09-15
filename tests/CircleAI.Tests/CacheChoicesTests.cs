// CacheChoicesTests.cs
//
// The handful of settings a person may pick for the self-managing cache: that
// each choice means the value the engine will run with, that the defaults map to
// CacheEviction's own (so the picker default cannot drift from the engine's), and
// that every choice has a label and a place in the picker.

using System;
using System.Collections.Generic;
using CircleAI.Assistant;
using Xunit;

namespace CircleAI.Tests;

public class CacheChoicesTests
{
    [Theory]
    [InlineData(KeepChoice.ThreeDays, 3)]
    [InlineData(KeepChoice.OneWeek, 7)]
    [InlineData(KeepChoice.OneMonth, 30)]
    public void Keep_choices_are_their_windows(KeepChoice choice, int days)
        => Assert.Equal(TimeSpan.FromDays(days), CacheChoices.Keep(choice));

    [Fact]
    public void Forever_never_ages_out()
        => Assert.Equal(TimeSpan.MaxValue, CacheChoices.Keep(KeepChoice.Forever));

    [Theory]
    [InlineData(CapChoice.Mb64, 64L * 1024 * 1024)]
    [InlineData(CapChoice.Mb256, 256L * 1024 * 1024)]
    [InlineData(CapChoice.Gb1, 1024L * 1024 * 1024)]
    public void Cap_choices_are_their_sizes(CapChoice choice, long bytes)
        => Assert.Equal(bytes, CacheChoices.Cap(choice));

    [Fact]
    public void No_limit_is_zero()
        => Assert.Equal(0, CacheChoices.Cap(CapChoice.NoLimit));

    [Fact]
    public void The_defaults_are_CacheEvictions_own_so_they_cannot_drift()
    {
        // The picker's default and the engine's no-setting default are the SAME
        // number, read from one place.
        Assert.Equal(CacheEviction.KeepDefault, CacheChoices.Keep(CacheChoices.DefaultKeep));
        Assert.Equal(CacheEviction.MaxCacheDefaultBytes, CacheChoices.Cap(CacheChoices.DefaultCap));
    }

    [Fact]
    public void Every_keep_choice_has_a_label_and_a_place_in_the_picker()
    {
        foreach (KeepChoice c in Enum.GetValues<KeepChoice>())
        {
            Assert.False(string.IsNullOrWhiteSpace(CacheChoices.Label(c)));
            Assert.Contains(c, CacheChoices.AllKeep);
        }
        Assert.Equal(Enum.GetValues<KeepChoice>().Length, CacheChoices.AllKeep.Count);
    }

    [Fact]
    public void Every_cap_choice_has_a_label_and_a_place_in_the_picker()
    {
        foreach (CapChoice c in Enum.GetValues<CapChoice>())
        {
            Assert.False(string.IsNullOrWhiteSpace(CacheChoices.Label(c)));
            Assert.Contains(c, CacheChoices.AllCap);
        }
        Assert.Equal(Enum.GetValues<CapChoice>().Length, CacheChoices.AllCap.Count);
    }

    [Fact]
    public void Labels_are_distinct_so_a_picker_never_shows_the_same_word_twice()
    {
        var keep = new HashSet<string>();
        foreach (var c in CacheChoices.AllKeep) Assert.True(keep.Add(CacheChoices.Label(c)));

        var cap = new HashSet<string>();
        foreach (var c in CacheChoices.AllCap) Assert.True(cap.Add(CacheChoices.Label(c)));
    }
}
