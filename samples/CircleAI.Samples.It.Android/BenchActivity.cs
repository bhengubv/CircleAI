#nullable enable

// BenchActivity.cs
//
// How fast, how right, and what it costs the phone — for ONE pinned model.
//
// THE BAR IS NOT "DOES IT FIT". A model that loads and then makes somebody wait
// seventy seconds for a wrong answer has failed, and it failed at the only thing
// that matters: a person asked for help and did not get it. So this measures the
// three things a person actually experiences —
//
//   how long until it starts answering   (time to first token)
//   how fast the answer arrives          (tokens per second)
//   whether the answer is any good       (a fixed question set, kept verbatim)
//
// — plus the one thing they experience without knowing it: whether their OTHER
// apps survived. A brain that evicts the messaging app to answer a question has
// taken more than it gave.
//
// TIME TO FIRST TOKEN IS THE HEADLINE, not total time. Speech starts at the first
// sentence, so the wait a person feels is the wait before anything happens; what
// comes after is reading speed. A model with a 3 s first token and a slow tail
// feels alive. One with a 30 s first token and an instant tail feels broken, and
// no throughput number rescues it.
//
// HEADLESS AND DRIVEN FROM adb, one model per run, because the selector is the
// variable being removed. The same handset picked a 0.6B model at 73% battery
// and a 1.5B at 100%; timings taken across that are not comparable to anything.
//
//   adb shell am start -n <pkg>/crc64….BenchActivity --es model Qwen3-1.7B-MNN
//
// Results go to logcat under CircleAI.Bench, one line per question plus a summary
// line, so a run can be collected without touching the screen.

using System;
using System.Threading.Tasks;
using Stopwatch = System.Diagnostics.Stopwatch;   // System.Diagnostics.Activity collides with Android's
using Android.App;
using Android.Content;
using Android.OS;
using Android.Util;
using Android.Views;
using Android.Widget;

namespace CircleAI.Samples.It.Mobile;

[Activity(Label = "Bench", Exported = true)]
public class BenchActivity : Activity
{
    const string Tag = "CircleAI.Bench";

    /// <summary>
    /// What a person actually opens this for, in the order they would ask.
    /// </summary>
    /// <remarks>
    /// NOT A BENCHMARK SUITE. No MMLU, no reasoning puzzles. Somebody holding a
    /// R1,500 phone at a taxi rank wants a fact, a direction, some arithmetic, or
    /// company — and wants it in their own language. Every question here has a
    /// checkable answer or an obvious failure mode, so "smartest" can be judged by
    /// reading the log rather than by trusting a score.
    ///
    /// The last one is isiZulu on purpose. A model that answers the first four and
    /// then echoes the fifth back is not usable here, whatever it scores.
    /// </remarks>
    static readonly string[] Questions =
    {
        "What is the capital of France?",
        "What is 17 times 4?",
        "How do I find the nearest hospital?",
        "I am feeling lonely today.",
        "Ngicela ungisize ngingedwa namuhla.",
    };

    TextView _out = null!;
    readonly System.Text.StringBuilder _log = new();

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        ActionBar?.Hide();

        var scroll = new ScrollView(this);
        _out = Ui.Label(this, "", 12f, Ui.Ink);
        _out.SetPadding(Ui.Dp(this, 12), Ui.Dp(this, 12), Ui.Dp(this, 12), Ui.Dp(this, 12));
        scroll.AddView(_out);
        scroll.SetBackgroundColor(Ui.Bg);
        SetContentView(scroll);

        // KEEP THE SCREEN ON. A run takes minutes and the phone sleeping mid-run
        // throttles the CPU, which quietly turns a timing test into a test of
        // Huawei's power manager.
        Window?.AddFlags(WindowManagerFlags.KeepScreenOn);

        var model = Intent?.GetStringExtra("model");
        var lean  = Intent?.GetBooleanExtra("lean", false) ?? false;
        _ = RunAsync(model, lean);
    }

    void Say(string line)
    {
        Log.Info(Tag, line);
        RunOnUiThread(() => { _log.AppendLine(line); _out.Text = _log.ToString(); });
    }

    async Task RunAsync(string? model, bool lean)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            Say("no model given — pass --es model <RegistryId>");
            return;
        }

        Say($"=== {model}{(lean ? "  [lean]" : "")} ===");
        Say($"before: {MemLine()}");

        ItSession session;
        var load = Stopwatch.StartNew();
        try
        {
            session = await Task.Run(async () =>
            {
                var s = new ItSession(
                    ApplicationInfo?.NativeLibraryDir,
                    batteryPercent: () => 100,
                    pinModelId: model,
                    lean: lean);
                await s.StartAsync();
                return s;
            });
        }
        catch (Exception ex)
        {
            // THE FAILURES ARE THE POINT. Walking up the ladder until something
            // breaks is how the ceiling gets found, so a model that will not load
            // is a result and gets recorded like one — not a crash.
            load.Stop();
            Say($"LOAD FAILED after {load.ElapsedMilliseconds} ms: {Innermost(ex)}");
            Say($"after:  {MemLine()}");
            Say($"RESULT {model} load=FAIL");
            return;
        }
        load.Stop();

        Say($"loaded in {load.ElapsedMilliseconds / 1000.0:F1} s — {session.StatusLine}");
        Say($"after:  {MemLine()}");
        Say($"self:   {SelfPssMb()} MB");

        double ttftTotal = 0, charsTotal = 0, wallTotal = 0;
        var answered = 0;

        foreach (var q in Questions)
        {
            var sw = Stopwatch.StartNew();
            long ttft = -1, tlast = -1;
            var chars = 0;
            var chunks = 0;

            try
            {
                await session.RunTurnStreamingAsync(
                    q,
                    _ => { },
                    chunk =>
                    {
                        // First chunk with actual content — whitespace-only chunks
                        // are real and must not be counted as the answer starting.
                        if (ttft < 0 && !string.IsNullOrWhiteSpace(chunk))
                            ttft = sw.ElapsedMilliseconds;
                        // WHEN each chunk lands, and HOW MANY. ttft has equalled
                        // total on every measurement so far, which either means
                        // the answer arrives as one lump at the end, or that it
                        // arrives in pieces that are all bunched at the end. The
                        // gap between the first and last chunk tells them apart.
                        tlast = sw.ElapsedMilliseconds;
                        chunks++;
                        chars += chunk?.Length ?? 0;
                    },
                    _ => { });
            }
            catch (Exception ex)
            {
                Say($"Q  {q}");
                Say($"   TURN FAILED: {Innermost(ex)}");
                continue;
            }
            sw.Stop();

            // ~4 characters per token is close enough for a rate; the exact
            // tokeniser count is not what is being compared here.
            var tokens = chars / 4.0;
            var secs   = sw.ElapsedMilliseconds / 1000.0;
            var rate   = secs > 0 ? tokens / secs : 0;

            Say($"Q  {q}");
            Say($"   ttft {ttft} ms | last {tlast} ms | total {secs:F1} s | " +
                $"~{rate:F1} tok/s | {chars} chars in {chunks} chunks");

            if (ttft >= 0) { ttftTotal += ttft; answered++; }
            charsTotal += chars;
            wallTotal  += secs;
        }

        var meanTtft = answered > 0 ? ttftTotal / answered : -1;
        var meanRate = wallTotal > 0 ? (charsTotal / 4.0) / wallTotal : 0;

        Say($"after all: {MemLine()}");
        Say($"self:      {SelfPssMb()} MB");
        Say($"RESULT {model} load=OK loadms={load.ElapsedMilliseconds} " +
            $"ttft={meanTtft:F0} tokps={meanRate:F1} pss={SelfPssMb()}");

        await session.DisposeAsync();
    }

    /// <summary>Free memory as the OS sees it — the "is the phone still usable" number.</summary>
    /// <remarks>
    /// MemAvailable rather than MemFree. MemFree counts only untouched pages and
    /// reads near zero on any Android device that has been on for an hour, which
    /// makes it useless for deciding whether another app can still start.
    /// </remarks>
    string MemLine()
    {
        try
        {
            var mi = new ActivityManager.MemoryInfo();
            var am = (ActivityManager?)GetSystemService(ActivityService);
            am?.GetMemoryInfo(mi);
            return $"avail {mi.AvailMem / 1048576} MB of {mi.TotalMem / 1048576} MB" +
                   (mi.LowMemory ? "  (LOW)" : "");
        }
        catch { return "mem unknown"; }
    }

    long SelfPssMb()
    {
        try
        {
            var am = (ActivityManager?)GetSystemService(ActivityService);
            var info = am?.GetProcessMemoryInfo(new[] { Android.OS.Process.MyPid() });
            return info is { Length: > 0 } ? info[0].TotalPss / 1024 : -1;
        }
        catch { return -1; }
    }

    static string Innermost(Exception ex)
    {
        var e = ex;
        while (e.InnerException is { } inner) e = inner;
        return e.Message.Trim();
    }
}
