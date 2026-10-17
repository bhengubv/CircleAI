// InFlightDownloads.cs
//
// ONE DOWNLOAD OF A MODEL AT A TIME IN THIS PROCESS, WHOEVER ASKS.
//
// Measured on the P30 on 2026-10-16. The moment Qwen3.5-0.8B became the brain and was
// missing, two paths in the service fetched it at once: the background fetch
// (ModelFetchService - "fetching Qwen3.5-0.8B-MNN" at 03:38:25.774) and the app's
// loading screen asking the service to set up ("setup: started", 134 ms later). Each
// built its own BundleModelLoader, each streamed the whole 548 MB, and both wrote the
// same llm.mnn.weight.tmp. Three minutes in, one of them renamed or deleted that file
// and the other died:
//
//   Qwen3.5-0.8B-MNN failed: FileNotFoundException ... llm.mnn.weight.tmp
//
// Twice the data on somebody's phone bill, and a download that failed for no reason of
// its own. It had always been possible; it never happened on that phone because a
// lesser model filled the brain slot, so nothing was ever missing at start-up.
//
// So the second caller for the same model JOINS the download in flight: it hears the
// same progress, and gets the same answer - the path, or the same failure. The key is
// the model's folder, so two loaders over one store agree on what "the same" means.

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using CircleAI.Core;

namespace CircleAI.Inference;

/// <summary>Process-wide de-duplication of model downloads.</summary>
internal static class InFlightDownloads
{
    private sealed class Flight
    {
        public readonly TaskCompletionSource<string> Done =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public readonly List<IProgress<DownloadProgress>> Listeners = new();
    }

    private static readonly Dictionary<string, Flight> Flights = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object Gate = new();

    /// <summary>
    /// Run <paramref name="download"/>, or join the one already running under
    /// <paramref name="key"/>.
    /// </summary>
    /// <param name="key">What makes two downloads the same - the model's folder.</param>
    /// <param name="progress">Where this caller hears progress. Null hears nothing.</param>
    /// <param name="download">
    /// The download itself, handed a reporter that reaches every caller that joins.
    /// </param>
    /// <param name="joined">Told when this caller joined rather than started.</param>
    /// <returns>The download's result, the same for every caller that shared it.</returns>
    /// <remarks>
    /// A joined caller shares the first caller's fate, cancellation included: the
    /// bytes are one stream, so there is one outcome. A caller that arrives after the
    /// download finished starts a fresh one, which finds the model already there.
    /// </remarks>
    internal static async Task<string> RunAsync(
        string key,
        IProgress<DownloadProgress>? progress,
        Func<IProgress<DownloadProgress>, Task<string>> download,
        Action<string>? joined = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(download);

        Flight? flight;
        bool mine;
        lock (Gate)
        {
            mine = !Flights.TryGetValue(key, out flight);
            if (mine)
            {
                flight = new Flight();
                Flights[key] = flight;
            }
            if (progress is not null) flight!.Listeners.Add(progress);
        }

        if (!mine)
        {
            joined?.Invoke(key);
            return await flight!.Done.Task.ConfigureAwait(false);
        }

        try
        {
            var result = await download(new FanOut(flight!)).ConfigureAwait(false);
            flight!.Done.TrySetResult(result);
            return result;
        }
        catch (Exception ex)
        {
            flight!.Done.TrySetException(ex);
            _ = flight.Done.Task.Exception;   // observed, so nobody joining is not a crash report
            throw;
        }
        finally
        {
            lock (Gate)
            {
                if (Flights.TryGetValue(key, out var current) && ReferenceEquals(current, flight))
                    Flights.Remove(key);
            }
        }
    }

    /// <summary>How many downloads are in flight right now.</summary>
    internal static int Count
    {
        get { lock (Gate) return Flights.Count; }
    }

    /// <summary>Hands every report to every caller sharing the download.</summary>
    private sealed class FanOut(Flight flight) : IProgress<DownloadProgress>
    {
        public void Report(DownloadProgress value)
        {
            IProgress<DownloadProgress>[] listeners;
            lock (Gate) listeners = flight.Listeners.ToArray();

            foreach (var listener in listeners)
            {
                try { listener.Report(value); }
                catch { /* one listener failing is not the download failing */ }
            }
        }
    }
}
