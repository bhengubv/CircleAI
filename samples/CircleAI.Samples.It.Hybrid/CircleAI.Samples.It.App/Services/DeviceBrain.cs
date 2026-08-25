// DeviceBrain.cs
//
// The chat model on this phone, loaded once and kept.

using CircleAI.Core;
using CircleAI.Core.Models;
using CircleAI.Inference;

namespace CircleAI.Samples.It.App.Services;

/// <inheritdoc />
/// <remarks>
/// ONE SESSION FOR THE WHOLE APP. Registered as a singleton and guarded by a
/// semaphore, because loading a chat model is seconds and hundreds of megabytes:
/// a screen that builds its own and disposes it afterwards pays that twice per
/// question. The job-spec screen used to do exactly that.
/// </remarks>
public sealed class DeviceBrain : IBrain, IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ItSession? _session;

    private static string StorageDir
        => Path.Combine(FileSystem.AppDataDirectory, "CircleAI", "Models");

    /// <inheritdoc />
    public Task<BrainState> StateAsync(CancellationToken ct = default)
        => Task.Run(() =>
        {
            if (_session is not null) return new BrainState(true, "Ready");

            using var registry = new ModelRegistryService();
            using var loader = new BundleModelLoader(StorageDir, registry);

            var chat = registry.AllModels
                .Where(m => m.Modality == ModelModality.Chat)
                .OrderByDescending(m => loader.ModelExists(m.Name))
                .ThenByDescending(m => m.QualityRank)
                .FirstOrDefault();

            if (chat is null)
                return new BrainState(false, "No answering model is catalogued.");

            // The SIZE, not just "not installed". It is the number that decides
            // whether somebody on a metered connection taps.
            return loader.ModelExists(chat.Name)
                ? new BrainState(true, "Ready")
                : new BrainState(false,
                    $"Answering needs {chat.Name} — {chat.TotalBytes / 1_000_000} MB. "
                  + "Turn it on under \"What it can do\".");
        }, ct);

    /// <inheritdoc />
    public async Task<string> AskAsync(
        string prompt, Action<string>? token = null, CancellationToken ct = default)
    {
        var session = await SessionAsync(ct).ConfigureAwait(false);

        // Serialised: one model, one turn at a time. Two overlapping turns
        // interleave their tokens into one unreadable answer.
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await session.RunTurnStreamingAsync(
                prompt,
                fragment => token?.Invoke(fragment),
                _ => { },
                _ => { }).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<string> SeeAsync(
        string question, byte[] image,
        Action<string>? token = null, CancellationToken ct = default)
    {
        var session = await SessionAsync(ct).ConfigureAwait(false);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // The session asks the selector whether this device can see BEFORE it
            // tries, so "no vision model" comes back as a sentence rather than as
            // an exception from somewhere deep inside.
            return await session.RunImageTurnAsync(
                question, image, _ => { }, fragment => token?.Invoke(fragment))
                .ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<ItSession> SessionAsync(CancellationToken ct)
    {
        if (_session is not null) return _session;

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_session is not null) return _session;

            // THE FIRST ARGUMENT IS THE NATIVE LIBRARY DIRECTORY, not the model
            // store - the session finds its own models but has to be told where
            // the .so files were unpacked. Passing the model path here loads no
            // native backend at all.
            var nativeLibDir =
#if ANDROID
                Android.App.Application.Context.ApplicationInfo?.NativeLibraryDir;
#else
                null;
#endif
            var session = new ItSession(nativeLibDir, batteryPercent: () => 100);
            await session.StartAsync().ConfigureAwait(false);
            _session = session;
            return session;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_session is not null) await _session.DisposeAsync().ConfigureAwait(false);
        _gate.Dispose();
    }
}
