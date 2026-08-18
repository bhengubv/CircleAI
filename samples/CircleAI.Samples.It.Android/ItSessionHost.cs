#nullable enable

// ItSessionHost.cs
//
// One brain per phone, not one per screen.
//
// EVERY SCREEN WAS BUILDING ITS OWN. HomeActivity created an ItSession for voice
// turns, MainActivity created another for typing, and JobSpecActivity created a
// third to tailor a CV — each calling StartAsync, each loading the model from
// storage. Caught on the P30 by opening the chat screen while the home screen
// was already warm: a second `config:` dump and a second warm-up generation, so
// two copies of a 550 MB model on a device with about 1.6 GB free.
//
// It was survivable only because the loads were lazy and most people never
// opened two screens in one session. Warming the brain at startup — which is
// what stopped the first spoken turn costing ten to twenty seconds — removed
// that accident, and turned a latent bug into one that fires on the second
// screen every time.
//
// WHY A STATIC AND NOT DEPENDENCY INJECTION. Android constructs Activities
// itself and may recreate them on a rotation or a configuration change, so
// there is no object graph that outlives them to hang this on. The same
// reasoning CircleNeuronService gives for its own static hooks.
//
// This is a stop-gap for the SAMPLE. The real home for a shared brain is the
// resident service — one process, one model, reachable from anything, including
// other apps. See ResidentAssistant.

using System;
using System.Threading.Tasks;
using Android.Content;

namespace CircleAI.Samples.It.Mobile;

/// <summary>The one brain this process owns.</summary>
public static class ItSessionHost
{
    const string Tag = "CircleAI.It";

    static readonly object Gate = new();
    static Task<CircleAI.Samples.It.ItSession>? _loading;
    static CircleAI.Samples.It.ItSession? _session;

    /// <summary>True once the brain is loaded and ready to answer.</summary>
    public static bool IsWarm => _session is not null;

    /// <summary>
    /// The shared session, loading it if this is the first caller.
    /// </summary>
    /// <remarks>
    /// Concurrent callers get the SAME load rather than one each — the failure
    /// this exists to prevent. Awaiting is safe from any screen and from the
    /// middle of a turn; a caller that arrives while it is still loading simply
    /// waits for the load already in flight.
    /// </remarks>
    public static Task<CircleAI.Samples.It.ItSession> GetAsync(Context context)
    {
        ArgumentNullException.ThrowIfNull(context);

        lock (Gate)
        {
            if (_session is not null) return Task.FromResult(_session);
            if (_loading is not null) return _loading;

            var nativeLibDir = context.ApplicationInfo?.NativeLibraryDir;
            _loading = Load(nativeLibDir);
            return _loading;
        }
    }

    static async Task<CircleAI.Samples.It.ItSession> Load(string? nativeLibDir)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var s = await Task.Run(async () =>
            {
                var made = new CircleAI.Samples.It.ItSession(
                    nativeLibDir, batteryPercent: () => 100);
                await made.StartAsync();
                return made;
            }).ConfigureAwait(false);

            lock (Gate) _session = s;
            Android.Util.Log.Info(Tag, $"brain warm in {sw.ElapsedMilliseconds} ms (shared)");
            return s;
        }
        finally
        {
            lock (Gate) _loading = null;
        }
    }

    /// <summary>
    /// Releases the brain and the memory it holds.
    /// </summary>
    /// <remarks>
    /// Not called from an Activity's OnDestroy: a screen closing is not the
    /// process ending, and dropping the model because somebody navigated back
    /// would reintroduce the load this file exists to pay only once. Left for a
    /// host that genuinely wants the memory back — a memory-pressure handler, or
    /// a deliberate "stop the assistant".
    /// </remarks>
    public static async Task ReleaseAsync()
    {
        CircleAI.Samples.It.ItSession? s;
        lock (Gate) { s = _session; _session = null; }
        if (s is not null) await s.DisposeAsync().ConfigureAwait(false);
    }
}
