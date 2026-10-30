// SoakReceiver.cs
//
// START AND STOP THE SOAK, OR A QUALITY RUN, WITH ONE ADB COMMAND - ON ANY PHONE.
//
//   adb shell am broadcast -a com.bhengubv.circleai.action.SOAK -n com.bhengubv.circleai.service/ai.circle.SoakReceiver --es do start --es run p30-1
//   adb shell am broadcast -a com.bhengubv.circleai.action.SOAK -n com.bhengubv.circleai.service/ai.circle.SoakReceiver --es do stop
//   adb shell am broadcast -a com.bhengubv.circleai.action.SOAK -n com.bhengubv.circleai.service/ai.circle.SoakReceiver --es do quality
//   adb shell am broadcast -a com.bhengubv.circleai.action.SOAK -n com.bhengubv.circleai.service/ai.circle.SoakReceiver --es do arm
//   adb shell am broadcast -a com.bhengubv.circleai.action.SOAK -n com.bhengubv.circleai.service/ai.circle.SoakReceiver --es do bench
//
// "bench" times the C# engine's kernels on every instruction path this phone's runtime
// can take, and logs them under the tag CircleAI.Engine.Bench.
//
// "arm" lets the files in the app's soak/ folder act as test switches (prompt dump, notes
// wording, sampler, question list) until "disarm"; without it they are ignored, because
// that folder is writable by other apps on Android 10.
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
            case "bench":
                // THE C# ENGINE'S KERNELS, TIMED IN THIS PROCESS'S OWN RUNTIME (S0.2): the
                // Release build's AOT Mono is what a phone runs, and it accelerates only part
                // of the vector API, so the kernel path is chosen from this, not from a desktop.
                // Through CircleLog, so each run also proves the S1 log reaches logcat
                // (tag CircleAI.Engine.Bench) and the process's log file.
                System.Threading.Tasks.Task.Run(() =>
                {
                    try
                    {
                        foreach (var line in CircleAI.Inference.Managed.Kernels.KernelBench.Report())
                            CircleAI.Core.Logging.CircleLog.Info("Engine.Bench", line);
                    }
                    catch (Exception ex)
                    {
                        CircleAI.Core.Logging.CircleLog.Error("Engine.Bench", "bench failed", ex);
                    }
                });
                break;
            case "engine":
                // THE C# ENGINE ANSWERING ON THIS PHONE (S2): the 2B loaded in this process's own
                // runtime, asked one question with the persona as its system turn, greedily, and
                // released - so its answer can be compared word for word with the desktop's.
                // `path` names the file; without it the app's model store and then the sideload
                // folder (the external files directory, where adb can put it) are looked in.
                //   ... --es do engine [--es text "Hello! Who are you?"] [--ei max 48] [--es path <file>]
                var question = intent?.GetStringExtra("text");
                var model = intent?.GetStringExtra("path");
                var max = intent?.GetIntExtra("max", 48) ?? 48;
                System.Threading.Tasks.Task.Run(() => AnswerOnTheManagedEngineAsync(model, question, max));
                break;
            case "arm":
                ArmTestSwitches?.Invoke(true);
                break;
            case "disarm":
                ArmTestSwitches?.Invoke(false);
                break;
        }
    }

    /// <summary>Arms (true) or disarms the soak/ test switches. Set by the service's startup.</summary>
    public static Action<bool>? ArmTestSwitches { get; set; }

    /// <summary>Ends the inference process (developer fault injection). Set by the service's startup.</summary>
    public static Action? KillWorker { get; set; }

    /// <summary>Submits one question as the adb caller. Set by the service's startup.</summary>
    public static Action<string>? Ask { get; set; }

    // Where the 2B is looked for when the broadcast names no file: the store, then the sideload folder.
    private static string[] TwoBCandidates()
    {
        const string id = "Ternary-Bonsai-2-2B", file = "Ternary-Bonsai-2-2B-PTQ1_0.gguf";
        var store = System.IO.Path.Combine(CircleAI.Assistant.Device.ModelStore.Path, id, file);
        return Application.Context.GetExternalFilesDir(null)?.AbsolutePath is { } sideload
            ? [store, System.IO.Path.Combine(sideload, "models", id, file)]
            : [store];
    }

    private static async System.Threading.Tasks.Task AnswerOnTheManagedEngineAsync(string? path, string? question, int maxTokens)
    {
        const string area = "Engine";
        try
        {
            var tried = path is null ? TwoBCandidates() : [path];
            path = tried.FirstOrDefault(System.IO.File.Exists);
            if (path is null)
            {
                CircleAI.Core.Logging.CircleLog.Warn(area, "engine check: no model at " + string.Join(" or ", tried));
                return;
            }

            using var generator = CircleAI.Inference.Managed.ManagedChatGenerator.Load(path, new() { ContextLength = 2048 });
            var timing = new CircleAI.Inference.TurnTimingRecorder();
            var response = await generator.GenerateResponseAsync(
                [new("system", CircleAI.Assistant.AssistantPersona.Prompt), new("user", question ?? "Hello! Who are you?")],
                new CircleAI.Inference.GenerationOptions
                {
                    Temperature = 0,
                    MaxTokens = maxTokens,
                    Budget = CircleAI.Inference.PowerBudget.None,
                    Timing = timing,
                }).ConfigureAwait(false);
            CircleAI.Core.Logging.CircleLog.Info(area,
                $"engine check: {response.TokensIn} prompt tokens in {timing.PrefillMs} ms, {response.TokensOut} tokens in {timing.DecodeMs} ms, {response.FinishReason}: {response.Text}");
        }
        catch (Exception ex)
        {
            CircleAI.Core.Logging.CircleLog.Error(area, "engine check failed", ex);
        }
        finally
        {
            CircleAI.Core.Logging.CircleLog.Info(area, "engine check done");
        }
    }
}
