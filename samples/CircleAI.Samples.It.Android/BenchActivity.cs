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

        var model  = Intent?.GetStringExtra("model");
        var lean   = Intent?.GetBooleanExtra("lean", false) ?? false;
        // Room to finish thinking. The product ships a 160-token cap because a
        // spoken answer should be two sentences, but a reasoning model spends
        // its budget on a silent trace and gets cut off before it ever answers —
        // measured, three of five turns came back as 7 characters. Judging it on
        // that is judging it with a gag on.
        var maxTok = Intent?.GetIntExtra("maxtok", 512) ?? 512;
        // Delete the bundle afterwards, so a ladder can walk past what the
        // phone can store. 33 GB free does not hold 14B and 30B and 35B.
        var purge  = Intent?.GetBooleanExtra("purge", false) ?? false;

        // Clear the store and stop. The ladder needs this between phases: the
        // rungs already measured are still holding ~13 GB, and the 35B needs
        // 22.8 GB of the 29 the phone has. Without it the big models are
        // "skipped, not enough disk" — which looks like a verdict and is not.
        if (Intent?.GetBooleanExtra("purgeall", false) ?? false)
        {
            PurgeAll();
            return;
        }

        _ = RunAsync(model, lean, maxTok, purge);
    }

    void Say(string line)
    {
        Log.Info(Tag, line);
        RunOnUiThread(() => { _log.AppendLine(line); _out.Text = _log.ToString(); });
    }

    async Task RunAsync(string? model, bool lean, int maxTok, bool purge)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            Say("no model given — pass --es model <RegistryId>");
            return;
        }

        Say($"=== {model}{(lean ? "  [lean]" : "")} maxtok={maxTok} ===");
        Say($"disk:   {FreeGb():F1} GB free");
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
                    lean: lean,
                    maxTokens: maxTok);
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
        if (purge) Purge(model);
    }

    /// <summary>Empties the model store, then says what that bought.</summary>
    void PurgeAll()
    {
        try
        {
            var root = ModelDir();
            if (!System.IO.Directory.Exists(root)) { Say($"purgeall: no store at {root}"); return; }

            Say($"purgeall: {FreeGb():F1} GB free before");
            foreach (var dir in System.IO.Directory.GetDirectories(root))
            {
                try
                {
                    System.IO.Directory.Delete(dir, recursive: true);
                    Say("  removed " + System.IO.Path.GetFileName(dir));
                }
                catch (Exception ex) { Say($"  kept {System.IO.Path.GetFileName(dir)}: {Innermost(ex)}"); }
            }
            Say($"purgeall: {FreeGb():F1} GB free after");
        }
        catch (Exception ex) { Say("purgeall failed: " + Innermost(ex)); }
    }

    /// <summary>Free space on the volume holding the model store.</summary>
    double FreeGb()
    {
        try
        {
            var s = new Android.OS.StatFs(ModelDir());
            return s.AvailableBytes / 1024.0 / 1024.0 / 1024.0;
        }
        catch { return -1; }
    }

    // System.Environment, spelled out: Android.OS.Environment is also in scope
    // here and the two are not interchangeable.
    static string ModelDir() => System.IO.Path.Combine(
        System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData),
        "CircleAI", "Models");

    /// <summary>
    /// Deletes a model bundle once it has been measured.
    /// </summary>
    /// <remarks>
    /// A LADDER OUTLIVES THE DISK. Walking from 0.5B to 35B means fetching well
    /// over 60 GB, and the phone has 33 GB free — without this the run does not
    /// find the model that is too big to RUN, it finds the one that is too big
    /// to STORE, and reports a download failure as if it were a verdict.
    ///
    /// Only under an explicit flag. A person's downloaded models are theirs, and
    /// re-fetching one over a metered connection because a tool tidied up is a
    /// real cost. This is for a bench walking a ladder, nothing else.
    /// </remarks>
    void Purge(string model)
    {
        try
        {
            var dir = System.IO.Path.Combine(ModelDir(), model);
            if (System.IO.Directory.Exists(dir))
            {
                var bytes = new System.IO.DirectoryInfo(dir)
                    .EnumerateFiles("*", System.IO.SearchOption.AllDirectories)
                    .Sum(f => f.Length);
                System.IO.Directory.Delete(dir, recursive: true);
                Say($"purged {model} ({bytes / 1e9:F2} GB) - {FreeGb():F1} GB free");
            }
            else
            {
                Say($"purge: nothing at {dir}");
            }
        }
        catch (Exception ex)
        {
            Say($"purge failed: {Innermost(ex)}");
        }
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
