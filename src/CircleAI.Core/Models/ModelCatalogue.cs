// ModelCatalogue.cs
//
// Keeping the model options current, once, for the whole process.
//
// EVERY PIECE OF THIS EXISTED AND NOTHING CALLED IT. ModelScopeCatalogClient
// fetches, caches to disk, checks a signature and honours a cadence.
// ModelRegistryService has a constructor that takes one and a
// PrimeFromCatalogAsync that uses it. AIOptions has a CatalogClient property and
// ServiceCollectionExtensions reads it. The catalogue that reaches the phone was
// still whatever was compiled into the APK, because:
//
//   * nothing ever set AIOptions.CatalogClient, so the registry's client was
//     always null;
//   * nothing ever called PrimeFromCatalogAsync, so it would not have mattered;
//   * and about twenty places construct `new ModelRegistryService()` directly,
//     bypassing dependency injection entirely - so fixing the first two would
//     still not have reached DeviceBrain, DeviceSetup, CircleAISession,
//     CircleAIListener, the abilities screen or the sweep.
//
// This file is the missing caller. It owns one client, refreshes on a cadence,
// and publishes the result where every registry in the process can see it.
//
// WHAT IT WILL NOT DO. It will not block a turn, it will not throw, and it will
// not make the app worse offline: the curated catalogue is the spine and a
// refresh only ever ADDS to it (see CatalogueMerge). A phone that never reaches
// the network behaves exactly as it does today, which is the behaviour most of
// these phones will actually have.

using System;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.Core.Models;

/// <summary>Keeps the model catalogue current for the whole process.</summary>
public static class ModelCatalogue
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static ModelScopeCatalogClient? _client;
    private static DateTimeOffset _lastAttempt = DateTimeOffset.MinValue;

    /// <summary>
    /// How long after a failed attempt before another is worth making.
    /// </summary>
    /// <remarks>
    /// A phone with no signal would otherwise retry on every screen that
    /// constructs a registry, which on this app is most of them - a spinning
    /// radio and a flat battery for a catalogue that is not going to arrive.
    /// <c>reachability-beats-battery</c> says the radios stay up; it does not
    /// say to hammer them.
    /// </remarks>
    public static readonly TimeSpan Backoff = TimeSpan.FromMinutes(30);

    /// <summary>Whether a live catalogue has been fetched this process.</summary>
    public static bool HasLive => ModelRegistryService.Live is not null;

    /// <summary>How many models the live catalogue added, or zero.</summary>
    public static int Discovered => ModelRegistryService.Live?.Models?.Count ?? 0;

    /// <summary>
    /// Bring the catalogue up to date, if it is due and the network allows.
    /// </summary>
    /// <param name="options">
    /// Overrides the defaults - cadence, cache directory, the filter applied to
    /// the query. <c>null</c> takes <see cref="ModelScopeCatalogOptions"/>'s own
    /// defaults except for the cadence, which is lowered to
    /// <see cref="CatalogRefreshCadence.Daily"/>.
    /// </param>
    /// <param name="ct">Cancels the fetch.</param>
    /// <returns>True when a live catalogue is now in effect.</returns>
    /// <remarks>
    /// DAILY, NOT ON EVERY STARTUP, and that is a deliberate departure from the
    /// client's own default. OnStartup is right for a desktop SDK that starts
    /// once; this app's process is killed and restarted by the system all day,
    /// so "on startup" on a phone means "several times an hour". The set of
    /// published models does not change that fast.
    /// <para>
    /// NEVER THROWS. Every caller of this is a screen or a warm-up path, and a
    /// catalogue refresh is the least important thing either of them is doing.
    /// A failure leaves the curated catalogue in place, which is a working app.
    /// </para>
    /// </remarks>
    public static async Task<bool> RefreshAsync(
        ModelScopeCatalogOptions? options = null, CancellationToken ct = default)
    {
        // Cheap exits before taking the gate: this is called from screens.
        if (DateTimeOffset.UtcNow - _lastAttempt < Backoff && !HasLive) return false;

        if (!await Gate.WaitAsync(TimeSpan.Zero, ct).ConfigureAwait(false))
            return HasLive;                 // another caller is already doing it

        try
        {
            if (DateTimeOffset.UtcNow - _lastAttempt < Backoff && !HasLive) return false;
            _lastAttempt = DateTimeOffset.UtcNow;

            _client ??= new ModelScopeCatalogClient(
                options ?? new ModelScopeCatalogOptions
                {
                    Cadence = CatalogRefreshCadence.Daily,
                },
                httpClient: null, verifier: null, deviceContext: null);

            // GetCachedCatalogAsync, not RefreshAsync: it honours the cadence,
            // serves the disk cache when one is fresh, and accepts a stale cache
            // when the network fails. Calling RefreshAsync here would fetch on
            // every invocation and throw when offline.
            var fetched = await _client
                .GetCachedCatalogAsync(acceptStaleOnError: true, ct)
                .ConfigureAwait(false);

            ModelRegistryService.Publish(fetched);
            return HasLive;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Offline, rate-limited, an API that changed shape, a signature that
            // did not verify. All of them mean the same thing to this app: keep
            // the catalogue that shipped.
            return HasLive;
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>
    /// Start a refresh and do not wait for it.
    /// </summary>
    /// <remarks>
    /// FOR WARM-UP PATHS, which is where this belongs. Loading a model already
    /// costs seconds on a P30 and the catalogue has nothing to do with it, so a
    /// refresh must never be something a person waits through. Fire-and-forget
    /// is safe here only because <see cref="RefreshAsync"/> cannot throw.
    /// </remarks>
    public static void RefreshInBackground(ModelScopeCatalogOptions? options = null)
        => _ = Task.Run(() => RefreshAsync(options, CancellationToken.None));

    /// <summary>
    /// Publish a catalogue this code did not fetch.
    /// </summary>
    /// <param name="registry">
    /// The catalogue. Ignored when null or empty — an empty one is what a
    /// partial fetch or a rate-limit page parses to, and accepting it would be a
    /// silent lie about what arrived.
    /// </param>
    /// <remarks>
    /// SO NOBODY IS FORCED THROUGH ONE HOST. <see cref="RefreshAsync"/> asks
    /// ModelScope because that is where the MNN bundles are published, and a
    /// product whose model list depends on ONE company's endpoint being
    /// reachable has a single point of failure that is political as much as
    /// technical - <c>sanction-proof-is-the-rule</c> is about exactly this.
    /// <para>
    /// This is the door for a host that mirrors the catalogue itself, serves it
    /// over a mesh, ships it on a card, or reads it off a device that already
    /// has it. The merge is the same either way: curated stays the spine and
    /// what arrives can only add.
    /// </para>
    /// </remarks>
    public static void Offer(ModelRegistry? registry) => ModelRegistryService.Publish(registry);

    /// <summary>
    /// Drop the live catalogue and go back to what shipped.
    /// </summary>
    /// <remarks>
    /// For a host that wants the shipped catalogue and nothing else - an
    /// air-gapped build, or somebody who does not want the phone asking a server
    /// what models exist - and for tests, which must not leak a catalogue from
    /// one case into the next.
    /// </remarks>
    public static void Forget()
    {
        ModelRegistryService.ForgetLive();
        _lastAttempt = DateTimeOffset.MinValue;
        _client?.Dispose();
        _client = null;
    }
}
