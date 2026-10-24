// IdleModelReview.cs
//
// THE HOURLY RE-ASK, on the phone.
//
// "Is this the best model given the resources we have?" is the one rule by which a
// model is chosen here, and until now it was answered once - at first launch, on the
// resources of that minute - and then believed for ever. ModelReview says WHEN to ask
// again and is tested; this is the Android half, which cannot be: a timer, the
// screen's state, and the existing fetch path.
//
// IT DECIDES NOTHING ITSELF. The plan is FirstRun.Plan's, computed fresh from a live
// DeviceProbe, and the fetching is ModelFetchService's - the same foreground service,
// with the same resume, the same skip-what-is-present and the same refusal to start
// on a plan it could not compute. If nothing is owed, nothing starts and nothing is
// shown; a person whose models are all down never sees that this exists.
//
// IDLE IS THE SCREEN BEING OFF, and that is the honest reading of "the person is not
// using the phone". Doze would be stricter and wrong - a phone in doze will not run
// this timer at all, so waiting for it means waiting for the phone to wake, which is
// the moment the person IS using it. isInteractive is false the moment the screen
// goes off, which is exactly when a multi-gigabyte download costs nobody anything.

using Android.App;
using Android.Content;
using Android.OS;
using CircleAI.Inference;
using CircleAI.SelfKnowledge;

namespace CircleAI.Device;

/// <summary>Asks every hour, while the phone is idle, whether a better model should be here.</summary>
public static class IdleModelReview
{
    private const string LogTag = "CircleAI.Review";

    private static readonly object Gate = new();
    private static System.Threading.Timer? _timer;
    private static DateTimeOffset _lastReview = DateTimeOffset.MinValue;

    /// <summary>
    /// The last Wolverine status line written, so the same one is not written again.
    /// </summary>
    /// <remarks>
    /// Null means nothing has been written yet, so the first reading is always announced
    /// - including the all-green one, which is the reading that proves Wolverine is
    /// wired at all.
    /// </remarks>
    private static string? _lastHealthLine;

    /// <summary>
    /// How often the timer wakes. Not how often the review runs - see
    /// <see cref="CircleAI.Assistant.ModelReview.Due"/>.
    /// </summary>
    /// <remarks>
    /// A QUARTER OF THE WINDOW, so a phone put down at any point in the hour is
    /// reviewed within fifteen minutes of becoming idle rather than at the top of the
    /// next hour. Each tick that finds the phone in use costs one boolean read.
    /// </remarks>
    private static readonly TimeSpan Tick = TimeSpan.FromMinutes(15);

    /// <summary>Starts the hourly review. Call once, from the process that owns the models.</summary>
    public static void Start(Context context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var app = context.ApplicationContext ?? context;

        lock (Gate)
        {
            if (_timer is not null) return;

            // THE FIRST REVIEW IS AN HOUR AWAY, NOT NOW. Start-up already asks what is
            // owed - ModelFetchService is launched from the same place this is - and a
            // second answer to the same question, computed seconds later from the same
            // numbers, would only walk the model store again.
            _lastReview = DateTimeOffset.UtcNow;

            _timer = new System.Threading.Timer(_ => Review(app), null, Tick, Tick);
        }

        global::Android.Util.Log.Info(LogTag,
            $"the models will be reviewed every {CircleAI.Assistant.ModelReview.Every.TotalHours:0} h, while the phone is idle");
    }

    /// <summary>Stops reviewing. For a host shutting the service down deliberately.</summary>
    public static void Stop()
    {
        lock (Gate)
        {
            _timer?.Dispose();
            _timer = null;
        }
    }

    private static void Review(Context app)
    {
        try
        {
            var idle = ScreenIsOff(app);
            var busy = ModelFetchService.Fetching;
            var since = DateTimeOffset.UtcNow - _lastReview;

            // WHAT THE SOFTWARE SWITCHED OFF AND IS NOW TRYING AGAIN. This is the
            // half-open tick of the API side's circuit breaker: nothing has to flip
            // anything, because SelfHeal answers by the clock, but a phone that quietly
            // stopped mapping weights three weeks ago and quietly started again is a
            // phone nobody can debug. Only while idle - a switch coming back on takes
            // effect at the next load, and the next load should not be in the middle of
            // somebody's sentence.
            if (idle)
                foreach (var again in SelfHeal.TakeDueToRetry())
                    global::Android.Util.Log.Info(LogTag,
                        $"trying {again.Name} again (it tripped {again.Trips}x, last on "
                      + $"{again.SinceUtc:yyyy-MM-dd}: {again.Why})");

            // AND THE GREEN/RED READING, WHENEVER IT CHANGES.
            //
            // ONLY ON CHANGE, because this ticks every few minutes and SelfHeal's own
            // note is the rule here: nothing is silent, but that is not the same as
            // everything being said forever. A status line every tick is a status line
            // nobody reads, and it would bury the trip lines above it.
            //
            // The comparison is on the rendered line rather than the overall light, so a
            // SECOND switch going red while one already is still gets announced - the
            // light would not have moved.
            if (idle)
            {
                var now = SelfHeal.Status().OneLine;
                if (!string.Equals(now, _lastHealthLine, StringComparison.Ordinal))
                {
                    _lastHealthLine = now;
                    global::Android.Util.Log.Info(LogTag, now);
                }
            }

            if (!CircleAI.Assistant.ModelReview.Due(idle, busy, since)) return;
            _lastReview = DateTimeOffset.UtcNow;

            var root = ModelFetchService.StorageDirectory;
            if (string.IsNullOrWhiteSpace(root))
            {
                // SAID, NOT SKIPPED. A review that cannot find the model store is a
                // phone that will never improve, and the first version of this would
                // have been silent about it for the life of the device.
                global::Android.Util.Log.Warn(LogTag, "no model store set; nothing to review");
                return;
            }

            global::Android.Util.Log.Info(LogTag, "reviewing: is this the best we can hold?");

            using var registry = new CircleAI.Core.Models.ModelRegistryService();
            using var loader = new BundleModelLoader(root, registry);

            // THE SAME CALL START-UP MAKES. It computes the plan from a live probe,
            // starts only if something is owed, and shows nothing otherwise - so a
            // phone with everything it can hold is reviewed in milliseconds and
            // nobody ever knows it happened.
            ModelFetchService.StartIfAnythingIsOwed(app, loader);
        }
        catch (Exception ex)
        {
            // NEVER FATAL, AND NEVER SILENT. This runs on a timer in the service
            // process; an exception escaping it would take the process with it, and a
            // review that quietly stopped happening is indistinguishable from a phone
            // that has everything.
            global::Android.Util.Log.Warn(LogTag, "the review failed: " + ex.Message);
        }
    }

    /// <summary>Whether the person is not using the phone right now.</summary>
    /// <remarks>
    /// isInteractive, NOT isDeviceIdleMode. Doze is the stricter state and the wrong
    /// one: a phone in doze does not run this timer, so waiting for doze means waiting
    /// for the phone to come out of it - which is the moment somebody picks it up.
    /// <para>
    /// A phone that will not answer is treated as IN USE. The cost of being wrong that
    /// way is one skipped hour; the other way it is a download starting under
    /// somebody's hands.
    /// </para>
    /// </remarks>
    private static bool ScreenIsOff(Context app)
    {
        try
        {
            var power = (PowerManager?)app.GetSystemService(Context.PowerService);
            if (power is null) return false;

            return Build.VERSION.SdkInt >= BuildVersionCodes.KitkatWatch
                ? !power.IsInteractive
                : !power.IsScreenOn;
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Info(LogTag, "could not read whether the screen is on: " + ex.Message);
            return false;
        }
    }
}
