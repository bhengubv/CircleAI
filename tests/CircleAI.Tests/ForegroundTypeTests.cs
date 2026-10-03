// ForegroundTypeTests.cs
//
// The foreground service type that killed the brain on Android 16.
//
// CircleNeuronService claimed dataSync on every start and OR'd the microphone on
// top. From Android 14 a dataSync foreground service carries a DAILY TIME BUDGET -
// spend it and startForeground is refused until it resets:
//
//   ForegroundServiceStartNotAllowedException:
//     Time limit already exhausted for foreground service type unknown
//
// After which the service did not start at all: no pid, no brain, every question
// answered "brain warming up, try again shortly" for ever. Measured on a Circle OS
// device (SDK 36) on 2026-10-04. The P30 is Android 10 and cannot reproduce it.
//
// SOURCE-LEVEL, because the type is decided inside an Android Service that cannot
// be constructed off a device - and the thing worth guarding is the SHAPE of the
// decision, which is what regressed.

using System;
using System.IO;
using Xunit;

namespace CircleAI.Tests;

public sealed class ForegroundTypeTests
{
    private static string Source() => File.ReadAllText(Path.Combine(
        Root(), "src", "CircleAI.Device", "CircleNeuronService.cs"));

    [Fact]
    public void The_microphone_is_claimed_on_its_own_not_alongside_dataSync()
    {
        // THE WHOLE BUG IN ONE OPERATOR. "types |= TypeMicrophone" keeps dataSync in
        // the claim, and dataSync is the budgeted one. Returning the microphone
        // outright is what stops the budget ever being spent.
        var source = Source();

        Assert.DoesNotContain("types |= global::Android.Content.PM.ForegroundService.TypeMicrophone", source);
        Assert.Contains("return global::Android.Content.PM.ForegroundService.TypeMicrophone;", source);
    }

    [Fact]
    public void dataSync_is_still_there_for_the_platform_that_has_nothing_else()
    {
        // ANDROID 10 DOES NOT KNOW THE MICROPHONE BIT. TypeMicrophone is 0x80 and
        // arrived in API 30; claiming it on API 29 gets the StartForeground rejected
        // and then the process killed for not having gone foreground - measured on a
        // P30, the wake word listening and the app dead ten seconds later. There,
        // dataSync is the only vocabulary, and that platform has no budget.
        var source = Source();

        Assert.Contains("return global::Android.Content.PM.ForegroundService.TypeDataSync;", source);
        Assert.Contains("BuildVersionCodes.R", source);
    }

    [Fact]
    public void The_microphone_is_only_claimed_when_it_is_actually_held()
    {
        // A CLAIM IS A STATEMENT TO THE OS. Declaring the microphone while nothing
        // is listening is both untrue and, on a system that checks, a reason to be
        // refused - so the listener and the granted permission are both conditions.
        var source = Source();

        Assert.Contains("Listener is not null", source);
        Assert.Contains("Permission.RecordAudio", source);
    }

    private static string Root()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !Directory.Exists(Path.Combine(dir, "src")))
            dir = Path.GetDirectoryName(dir);

        Assert.NotNull(dir);
        return dir!;
    }
}
