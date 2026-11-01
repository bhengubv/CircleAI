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
                // ONE QUESTION THROUGH THE INFERENCE PROCESS'S OWN BUILD SITE (S2): the model
                // loaded there with the engine named - the C# engine unless told otherwise - and
                // asked as every question is asked there (AIService, the persona), then let go so
                // the next question loads the chosen brain again. The same file on the same path
                // with "--es engine LlamaCpp" is the native fork, which is how the two are timed
                // side by side. Without `dir`, the store and then the sideload folder (the external
                // files directory, where adb can put a model) are looked in.
                //   ... --es do engine [--es engine Managed|LlamaCpp] [--es text "Hello! Who are you?"]
                //       [--ei max 48] [--es model Ternary-Bonsai-2-2B] [--es dir <models folder>]
                var engineName = intent?.GetStringExtra("engine");
                CircleAI.Core.ModelEngine? engine = Enum.TryParse<CircleAI.Core.ModelEngine>(engineName ?? "Managed", ignoreCase: true, out var parsed)
                    ? parsed : null;
                var question = intent?.GetStringExtra("text") ?? "Hello! Who are you?";
                var model = intent?.GetStringExtra("model") ?? TwoB;
                var dir = intent?.GetStringExtra("dir");
                var max = intent?.GetIntExtra("max", 48) ?? 48;
                System.Threading.Tasks.Task.Run(() => AskThroughTheWorkerAsync(model, dir, engine, question, max));
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

    /// <summary>
    /// Loads a model in the inference process with an engine, asks it one question there and lets
    /// it go: the answer, its timing and the load's milliseconds. Set by the service's startup.
    /// </summary>
    public static Func<string, string, CircleAI.Core.ModelEngine?, string, int,
        System.Threading.Tasks.Task<(string Text, CircleAI.Assistant.Requests.TurnTiming? Timing, long LoadMs)>>? AskOnce { get; set; }

    private const string TwoB = "Ternary-Bonsai-2-2B";

    // The models folders a model is looked for in when the broadcast names none: the store, then
    // the sideload folder.
    private static string[] ModelFolders() =>
        Application.Context.GetExternalFilesDir(null)?.AbsolutePath is { } sideload
            ? [CircleAI.Assistant.Device.ModelStore.Path, System.IO.Path.Combine(sideload, "models")]
            : [CircleAI.Assistant.Device.ModelStore.Path];

    private static async System.Threading.Tasks.Task AskThroughTheWorkerAsync(
        string model, string? dir, CircleAI.Core.ModelEngine? engine, string question, int maxTokens)
    {
        const string area = "Engine";
        var name = engine?.ToString() ?? "the row's engine";
        try
        {
            if (AskOnce is not { } ask)
            {
                CircleAI.Core.Logging.CircleLog.Warn(area, "engine check: the service has not started its inference path");
                return;
            }
            var tried = dir is null ? ModelFolders() : [dir];
            dir = tried.FirstOrDefault(folder => System.IO.Directory.Exists(System.IO.Path.Combine(folder, model)));
            if (dir is null)
            {
                CircleAI.Core.Logging.CircleLog.Warn(area, $"engine check: no {model} folder in " + string.Join(" or ", tried));
                return;
            }

            CircleAI.Core.Logging.CircleLog.Info(area, $"engine check ({name}): {model} from {dir}");
            var (text, timing, loadMs) = await ask(model, dir, engine, question, maxTokens).ConfigureAwait(false);
            CircleAI.Core.Logging.CircleLog.Info(area, timing is null
                ? $"engine check ({name}): loaded in {loadMs} ms, no timing came back: {text}"
                : $"engine check ({name}): loaded in {loadMs} ms; {timing.PromptTokens} prompt tokens in {timing.PrefillMs} ms, "
                  + $"{timing.OutputTokens} tokens in {timing.DecodeMs} ms, {timing.KvPath}, {timing.Finish}: {text}");
        }
        catch (Exception ex)
        {
            CircleAI.Core.Logging.CircleLog.Error(area, $"engine check ({name}) failed", ex);
        }
        finally
        {
            CircleAI.Core.Logging.CircleLog.Info(area, "engine check done");
        }
    }
}
