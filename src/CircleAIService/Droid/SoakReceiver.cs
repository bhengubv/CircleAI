// SoakReceiver.cs
//
// START AND STOP THE SOAK, OR A QUALITY RUN, WITH ONE ADB COMMAND - ON ANY PHONE.
//
//   adb shell am broadcast -a com.bhengubv.circleai.action.SOAK -n com.bhengubv.circleai.service/ai.circle.SoakReceiver --es do start --es run p30-1
//   adb shell am broadcast -a com.bhengubv.circleai.action.SOAK -n com.bhengubv.circleai.service/ai.circle.SoakReceiver --es do stop
//   adb shell am broadcast -a com.bhengubv.circleai.action.SOAK -n com.bhengubv.circleai.service/ai.circle.SoakReceiver --es do quality
//
// PROTECTED BY DUMP, which the adb shell holds and no ordinary app can: the receiver is
// exported so a developer can reach it, and nothing installed from a store can.

using Android.App;
using Android.Content;

namespace CircleAI.Service.Droid;

/// <summary>Starts or stops the soak run, or a quality run, from adb.</summary>
[BroadcastReceiver(Name = "ai.circle.SoakReceiver", Exported = true, Permission = "android.permission.DUMP")]
[IntentFilter(new[] { Action })]
public sealed class SoakReceiver : BroadcastReceiver
{
    /// <summary>The broadcast action.</summary>
    public const string Action = "com.bhengubv.circleai.action.SOAK";

    /// <summary>The runner. Set by the service's startup.</summary>
    public static CircleAI.Assistant.Quality.SoakRunner? Runner { get; set; }

    /// <summary>Starts a quality run and returns when it is under way. Set by the service's startup.</summary>
    public static Action? Quality { get; set; }

    /// <summary>This process's boot id, for restart detection. Set by the service's startup.</summary>
    public static string BootId { get; set; } = string.Empty;

    /// <inheritdoc />
    public override void OnReceive(Context? context, Intent? intent)
    {
        var what = intent?.GetStringExtra("do") ?? "start";
        global::Android.Util.Log.Info("CircleAI.Soak", "broadcast: " + what);
        switch (what)
        {
            case "start":
                Runner?.Start(intent?.GetStringExtra("run"), BootId, resume: false);
                break;
            case "stop":
                Runner?.Stop();
                break;
            case "quality":
                Quality?.Invoke();
                break;
            case "ask":
                // ONE QUESTION THROUGH THE QUEUE, so a fault can be injected while it runs.
                if (intent?.GetStringExtra("text") is { Length: > 0 } text) Ask?.Invoke(text);
                break;
            case "kill-worker":
                // THE RELIABILITY GATE'S FAULT: end the inference process, as the
                // low-memory killer or a native abort would, and watch the question in
                // flight come back and be answered.
                KillWorker?.Invoke();
                break;
        }
    }

    /// <summary>Ends the inference process (developer fault injection). Set by the service's startup.</summary>
    public static Action? KillWorker { get; set; }

    /// <summary>Submits one question as the adb caller. Set by the service's startup.</summary>
    public static Action<string>? Ask { get; set; }
}
