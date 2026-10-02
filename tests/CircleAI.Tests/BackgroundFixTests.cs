// BackgroundFixTests.cs
//
// Where somebody is sent to stop this phone killing the assistant.
//
// "ALLOW IT TO RUN IN THE BACKGROUND" NAMES NOTHING ON THESE PHONES. The screen that
// actually decides it is called App launch on EMUI and Autostart on MIUI, and an app
// left on "Manage automatically" is closed whatever Android's own battery list says.
// Somebody who follows the generic instruction, does exactly what it asked, and then
// loses the assistant overnight concludes the software is broken.

using CircleAI.Assistant;
using Xunit;

namespace CircleAI.Tests;

public sealed class BackgroundFixTests
{
    [Theory]
    [InlineData("HUAWEI", "Settings › Battery › App launch")]
    [InlineData("HONOR", "Settings › Battery › App launch")]
    [InlineData("Xiaomi", "Autostart")]
    [InlineData("Redmi", "Autostart")]
    [InlineData("POCO", "Autostart")]
    [InlineData("OPPO", "Startup manager")]
    [InlineData("realme", "Startup manager")]
    [InlineData("OnePlus", "Startup manager")]
    [InlineData("vivo", "Background app refresh")]
    [InlineData("iQOO", "Background app refresh")]
    public void Names_the_setting_in_the_makers_own_words(string make, string expected)
        => Assert.Equal(expected, BackgroundFix.SettingName(make));

    [Theory]
    [InlineData("Google")]
    [InlineData("samsung")]
    [InlineData("Nokia")]
    [InlineData(null)]
    public void Says_nothing_for_a_phone_with_no_second_screen(string? make)
    {
        // A NAME WE INVENTED WOULD BE WORSE THAN NONE: they would go looking for it,
        // not find it, and conclude the instruction was wrong. Null means the screen
        // falls back to the generic wording, which is true everywhere.
        Assert.Null(BackgroundFix.SettingName(make));
        Assert.Empty(BackgroundFix.Screens(make));
    }

    [Fact]
    public void The_huawei_screens_are_the_ones_read_off_the_phone()
    {
        // VERIFIED ON A P30, EMUI 12, 2026-10-02, with dumpsys package
        // com.huawei.systemmanager. Pinned because a typo here is invisible: the
        // start fails, the fallback opens the stock battery screen, and the person
        // is sent somewhere that does not fix their problem.
        var screens = BackgroundFix.Screens("HUAWEI");

        Assert.Equal("com.huawei.systemmanager", screens[0].Package);
        Assert.Equal(
            "com.huawei.systemmanager.startupmgr.ui.StartupNormalAppListActivity",
            screens[0].Activity);
        Assert.Equal(
            "com.huawei.systemmanager.appcontrol.activity.StartupAppControlActivity",
            screens[1].Activity);
    }

    [Fact]
    public void Every_make_with_a_name_also_has_somewhere_to_send_them()
    {
        // A NAME WITHOUT A SCREEN IS AN INSTRUCTION WITH NO BUTTON BEHIND IT. If one
        // is ever added without the other, this is where it shows up.
        foreach (var make in new[] { "huawei", "honor", "xiaomi", "redmi", "poco",
                                     "oppo", "realme", "oneplus", "vivo", "iqoo" })
        {
            Assert.NotNull(BackgroundFix.SettingName(make));
            Assert.NotEmpty(BackgroundFix.Screens(make));
        }
    }

    [Fact]
    public void Matches_the_make_inside_a_longer_string()
    {
        // Build.Manufacturer is not a tidy enum.
        Assert.Equal("Settings › Battery › App launch", BackgroundFix.SettingName("HUAWEI TECHNOLOGIES CO., LTD."));
        Assert.Equal("Autostart", BackgroundFix.SettingName("Xiaomi Communications Co., Ltd."));
    }
}
