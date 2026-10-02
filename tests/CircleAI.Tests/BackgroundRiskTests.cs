// BackgroundRiskTests.cs
//
// The rule behind "This phone may stop it listening".
//
// THE WARNING USED TO READ A DIFFERENT FACT AND FIRED EVERYWHERE. Its condition was
// "listening, and the service does not hold Android's battery-optimisation
// exemption" - and Android grants that exemption to nothing until somebody asks, so
// it was true on essentially every phone, permanently. The sentence it printed went
// further still and asserted how that particular phone was configured.
//
// This is the narrower claim, and the reason it is a string rather than a platform
// call: it can be checked without a phone.

using CircleAI.Assistant;
using Xunit;

namespace CircleAI.Tests;

public sealed class BackgroundRiskTests
{
    [Theory]
    [InlineData("HUAWEI")]
    [InlineData("Huawei")]
    [InlineData("HONOR")]
    [InlineData("Xiaomi")]
    [InlineData("Redmi")]
    [InlineData("POCO")]
    [InlineData("OPPO")]
    [InlineData("realme")]
    [InlineData("OnePlus")]
    [InlineData("vivo")]
    [InlineData("Meizu")]
    public void Knows_the_makes_that_close_background_apps(string make)
        => Assert.True(BackgroundRisk.Known(make));

    [Theory]
    [InlineData("Google")]
    [InlineData("motorola")]
    [InlineData("Nokia")]
    [InlineData("Sony")]
    [InlineData("Fairphone")]
    public void Leaves_the_rest_alone(string make)
        => Assert.False(BackgroundRisk.Known(make));

    [Fact]
    public void Samsung_is_deliberately_not_on_the_list()
    {
        // ITS SLEEPING-APPS LIST IS OPT-IN PER APP and does not pre-emptively stop a
        // foreground service. Including it would put the warning back in front of a
        // very large number of people for whom it is not true, which is the defect
        // this rule exists to fix. Pinned so adding it is a decision somebody makes
        // on purpose rather than by pattern-matching "big Android OEM".
        Assert.False(BackgroundRisk.Known("samsung"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_phone_that_will_not_say_is_given_the_benefit_of_the_doubt(string? make)
    {
        // UNKNOWN MEANS NO. Warning on a make nobody has measured is the failure
        // being fixed; staying quiet on a bad one costs somebody one attempt.
        Assert.False(BackgroundRisk.Known(make));
    }

    [Fact]
    public void Matches_the_make_inside_a_longer_string()
    {
        // Build.Manufacturer is not a tidy enum - phones report things like
        // "Xiaomi Communications Co., Ltd." - so the test is containment, not equality.
        Assert.True(BackgroundRisk.Known("Xiaomi Communications Co., Ltd."));
        Assert.True(BackgroundRisk.Known("HUAWEI TECHNOLOGIES CO., LTD."));
    }
}
