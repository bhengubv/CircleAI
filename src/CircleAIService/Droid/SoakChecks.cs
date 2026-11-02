// SoakChecks.cs
//
// THE S9 CHECKS A BROADCAST CAN DRIVE, BEHIND SoakReceiver - EACH THROUGH THE PRODUCT'S OWN PATH.
//
//   see    a picture asked about as the link asks: held in the link's picture store under the
//          request id its ask carries, then submitted to the queue - answered by whichever brain
//          on the phone can see, exactly as an app's picture is (CircleNeuronLinkService).
//   logs   the last lines of each process's log file, written to logcat: a Release build cannot be
//          read with run-as, and the file outlives logcat's two minutes on the P30 (S1).
//   fetch  one catalogued model fetched as setup fetches it: on the setup board, checked against
//          its SHA-256 by the download, primed when it lands.
//
// Nothing here is reachable but through the DUMP-protected receiver, and every line it writes is
// safe to read: a file name, a size, a count - never a question or an answer.

using CircleAI.Assistant.Device;
using CircleAI.Device;
using CircleAI.Inference;

namespace CircleAI.Service.Android;

public sealed partial class ServiceApplication
{
    private const string SoakTag = "CircleAI.Soak";

    // A picture asked about as the link asks (S9), and what came back - the answer of a developer's
    // own test question, which the product's text-free turn line never carries.
    private static async Task SeeForTestAsync(CircleAI.Assistant.Requests.RequestCoordinator coordinator, string file, string? question)
    {
        try
        {
            if (CircleNeuronLinkService.Images is not { } images)
            {
                global::Android.Util.Log.Warn(SoakTag, "see: pictures are not taken here yet");
                return;
            }
            if (!System.IO.File.Exists(file))
            {
                global::Android.Util.Log.Warn(SoakTag, "see: no picture at " + file);
                return;
            }

            var image = System.IO.File.ReadAllBytes(file);
            if (ImageBudget.NeedsShrinking(image, ImageBudget.VisionMaxEdge) && CircleNeuronLinkService.Shrink is { } shrink)
                image = shrink(image);

            // THE PICTURE IS HELD BEFORE THE ASK EXISTS, under the request id the ask carries - the
            // queue may start answering the moment it accepts.
            const string caller = "internal:adb";
            var requestId = "picture-" + Guid.NewGuid().ToString("N");
            var held = images.Hold(image, caller, requestId);
            var admission = coordinator.Submit(new CircleAI.Assistant.Requests.AskSubmission(
                caller, requestId, "adb", CircleAI.Assistant.Requests.AskLane.Foreground,
                string.IsNullOrWhiteSpace(question) ? "What is in this picture?" : question));
            if (!admission.Accepted || admission.Record is null)
            {
                images.Drop(held);
                global::Android.Util.Log.Warn(SoakTag, "see: refused: " + admission.Refusal);
                return;
            }
            global::Android.Util.Log.Info(SoakTag,
                $"see: ask {admission.Record.Id} accepted with a {image.Length} byte picture ({ImageBudget.Describe(image)})");
            await AnsweredForTestAsync(coordinator, caller, admission.Record.Id, "see").ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn(SoakTag, $"see: {ex.GetType().Name}: {ex.Message}");
        }
    }

    // What an adb test question came back with: its state, the brain that answered, and the answer.
    internal static async Task AnsweredForTestAsync(CircleAI.Assistant.Requests.RequestCoordinator coordinator,
                                                    string caller, string askId, string what)
    {
        try
        {
            var done = await coordinator.WaitAsync(caller, askId, TimeSpan.FromMinutes(8), CancellationToken.None).ConfigureAwait(false);
            global::Android.Util.Log.Info(SoakTag, done is null
                ? $"{what}: ask {askId} not found"
                : $"{what}: ask {askId} {done.State} by {done.ModelId ?? "-"}: {done.Text ?? done.Sentence ?? string.Empty}");
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn(SoakTag, $"{what}: ask {askId} not followed: {ex.GetType().Name}");
        }
    }

    // The last lines of each process's log file - those matching grep, when it is given - in logcat
    // (S9), written with Android's own log so they are not written into the file again.
    private void LogFilesForTest(int lines, string? grep)
    {
        try
        {
            var dir = System.IO.Path.Combine(FilesDir!.AbsolutePath, "logs");
            if (!System.IO.Directory.Exists(dir))
            {
                global::Android.Util.Log.Warn(SoakTag, "logs: no log folder at " + dir);
                return;
            }
            var match = string.IsNullOrWhiteSpace(grep)
                ? null
                : new System.Text.RegularExpressions.Regex(grep, System.Text.RegularExpressions.RegexOptions.IgnoreCase,
                                                           TimeSpan.FromMilliseconds(200));
            foreach (var file in System.IO.Directory.GetFiles(dir).OrderBy(f => f, StringComparer.Ordinal))
            {
                var info = new System.IO.FileInfo(file);
                global::Android.Util.Log.Info(SoakTag,
                    $"logs: {info.Name}, {info.Length / 1024} kB, last written {info.LastWriteTimeUtc:yyyy-MM-ddTHH:mm:ssZ}");
                foreach (var line in Tail(file, lines, match)) global::Android.Util.Log.Info(SoakTag, $"logs: {info.Name} | {line}");
            }
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn(SoakTag, $"logs: {ex.GetType().Name}: {ex.Message}");
        }
    }

    // The last lines of a file another thread may be writing - only those that match, when asked: read
    // shared, only the tail kept.
    private static IReadOnlyList<string> Tail(string file, int lines, System.Text.RegularExpressions.Regex? match = null)
    {
        using var stream = new System.IO.FileStream(file, System.IO.FileMode.Open, System.IO.FileAccess.Read,
            System.IO.FileShare.ReadWrite | System.IO.FileShare.Delete);
        using var reader = new System.IO.StreamReader(stream);
        var kept = new Queue<string>(lines);
        while (reader.ReadLine() is { } line)
        {
            if (match is not null && !Matches(match, line)) continue;
            if (kept.Count == lines) kept.Dequeue();
            kept.Enqueue(line);
        }
        return kept.ToArray();
    }

    // A pattern that runs too long on one line does not stop the read.
    private static bool Matches(System.Text.RegularExpressions.Regex match, string line)
    {
        try { return match.IsMatch(line); }
        catch (System.Text.RegularExpressions.RegexMatchTimeoutException) { return false; }
    }

    // One catalogued model fetched as setup fetches it (S9).
    private static async Task FetchForTestAsync(string model)
    {
        try
        {
            using var registry = new CircleAI.Core.Models.ModelRegistryService();
            if (registry.GetLatestModel(model) is not { } entry)
            {
                global::Android.Util.Log.Warn(SoakTag, "fetch: no catalogue row named " + model);
                return;
            }
            using var loader = new BundleModelLoader(ModelStore.Path, registry);
            if (loader.ModelPresent(model))
            {
                global::Android.Util.Log.Info(SoakTag, $"fetch: {model} is already here");
                return;
            }

            SetupBoard.Queue(model, null, entry.TotalBytes);
            global::Android.Util.Log.Info(SoakTag, $"fetch: {model}, {entry.TotalBytes / 1_000_000} MB");
            await loader.DownloadModelAsync(model, SetupBoard.Relay(model, null), CancellationToken.None).ConfigureAwait(false);
            global::Android.Util.Log.Info(SoakTag, $"fetch: {model} {(loader.ModelPresent(model) ? "landed" : "did not land")}");
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn(SoakTag, $"fetch: {model} failed: {ex.GetType().Name}: {ex.Message}");
        }
    }
}
