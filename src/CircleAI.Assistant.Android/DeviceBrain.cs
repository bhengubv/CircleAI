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

    /// <summary>Set once teardown has begun, so nothing enters a model that is going away.</summary>
    /// <remarks>
    /// THE GATE ALONE IS NOT ENOUGH, because AskAsync releases it between
    /// FINDING the session and USING it - SessionAsync takes the gate to build
    /// one, hands it back, and AskAsync then takes the gate again to run the
    /// turn. Teardown fits in that gap: it waits for a gate nobody is holding,
    /// frees the model, and the turn resumes on a session that no longer exists.
    /// This flag is checked under the gate, which is where the decision has to
    /// be made.
    /// </remarks>
    private bool _closing;

    private static string StorageDir => ModelStore.Path;

    /// <inheritdoc />
    public Task<BrainState> StateAsync(CancellationToken ct = default)
        => Task.Run(() =>
        {
            if (_session is not null) return new BrainState(true, "Ready");

            using var registry = new ModelRegistryService();
            using var loader = new BundleModelLoader(StorageDir, registry);
            var probe = DeviceProbe.Snapshot();

            // THE SAME CHOICE THE SETTINGS SCREEN MAKES. This used to take the
            // highest-quality chat model in the catalogue without asking whether
            // the phone could run it, so Settings offered Answering at 547 MB
            // while this screen said it needed 22797 MB - forty times apart, on
            // the same handset, at the same moment.
            var chat = ModelChoice.For(ModelModality.Chat, registry, loader, probe);

            if (chat is null)
                return new BrainState(false,
                    ModelChoice.AnyCatalogued(ModelModality.Chat, registry)
                        // ABOUT THEIR PHONE, not about our catalogue. There are
                        // answering models; none of them will run here.
                        ? "Answering needs more memory than this phone has."
                        : "No answering model is catalogued yet.");

            // The SIZE, not just "not installed". It is the number that decides
            // whether somebody on a metered connection taps.
            //
            // AND NOT THE MODEL'S NAME. "Qwen3.6-35B-A3B-MNN" is our word for it;
            // nobody outside this project can act on it, and printing it turns a
            // sentence about their phone into one about our build.
            // Size, not SHA-256 - see ModelChoice.For. This is the readiness
            // gate every turn passes through, and hashing 470 MB to answer it put
            // 9.4 s in front of the first thing anybody says.
            return loader.ModelPresent(chat.Name)
                ? new BrainState(true, "Ready")
                : new BrainState(false,
                    $"Answering needs a {ModelChoice.Size(chat.TotalBytes)} download. "
                  + "Turn it on under Settings › Phone.");
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
            // CHECKED HERE, UNDER THE GATE, because this is the first moment
            // since the session was found that anything could have taken it
            // away. Entering a disposed native session is the SIGSEGV this
            // class was crashing with; a cancellation is how a caller finds
            // out, and it is the same thing every other turn already handles.
            if (_closing || _session is null)
                throw new OperationCanceledException("The model is shutting down.");

            // THE TWO CALLBACKS WERE THE WRONG WAY ROUND, and it put the app's
            // own plumbing on screen as its answer. The signature is
            // (input, emitLine, onChunk, onThinking): emitLine is ItSession's
            // CONSOLE diagnostics, onChunk is the reply arriving token by token.
            // Wired the other way, asking "how do you say hello in isiZulu"
            // rendered
            //
            //     -> concierge routes to: Generalist  [no specialist cue -> generalist]
            //
            // in the chat bubble, and the actual answer went to `_ => { }`.
            //
            // Measured on a P30, 2026-09-05. It survived because AskAsync still
            // RETURNS the right string - sb.ToString() is the real answer - so
            // every caller that reads the return value was correct, and only the
            // one screen that renders the stream showed the fault. SeeAsync two
            // methods below has always had the order right, which is what makes
            // this a slip rather than a convention.
            return await session.RunTurnStreamingAsync(
                prompt,
                _ => { },
                fragment => token?.Invoke(fragment),
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
            // Same guard as AskAsync, same reason: the gate was released between
            // finding the session and using it, and teardown fits in that gap.
            if (_closing || _session is null)
                throw new OperationCanceledException("The model is shutting down.");

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

            // BUILDING A MODEL FOR AN APP THAT IS CLOSING is hundreds of
            // megabytes and several seconds spent on something that will be
            // thrown away - and it would leave a live native session behind the
            // teardown that has already run.
            if (_closing) throw new OperationCanceledException("The model is shutting down.");

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
    /// <remarks>
    /// TAKES THE GATE, AND THAT IS THE WHOLE POINT OF THIS METHOD.
    ///
    /// It used to tear the session down without it, which meant a generation
    /// already running had the native model destroyed underneath it. MNN does
    /// not survive that: the next enqueue onto its thread pool dereferences a
    /// pointer that is now null and the process dies where it stands.
    ///
    /// Measured on a P30 on 2026-09-11, answering "my name is Thabo" typed into
    /// the chat screen:
    ///
    ///     Fatal signal 11 (SIGSEGV), code 1 (SEGV_MAPERR), fault addr 0x0
    ///     Cause: null pointer dereference
    ///     tid 11583 (.NET TP Worker)
    ///     MNN::ThreadPool::enqueue(...)+116
    ///     MNN::Transformer::Llm::generate(...)
    ///
    /// Not out of memory - 1,27 GB was free. A native crash takes the whole app
    /// with it, with no managed exception and nothing on screen: the answer
    /// simply stops and the launcher appears.
    ///
    /// EVERY OTHER PATH INTO THE MODEL ALREADY WAITED ON THIS GATE. Teardown was
    /// the one that did not, which is the one that matters most - the others
    /// interleave tokens, this one frees memory somebody is still reading.
    /// </remarks>
    public async ValueTask DisposeAsync()
    {
        // NOT WaitAsync(ct): there is no token here and a disposal that gave up
        // waiting would be back to freeing a model mid-generation. A turn is
        // bounded by its own token and its token budget, so this waits for
        // something that ends.
        await _gate.WaitAsync().ConfigureAwait(false);

        // Set UNDER the gate, so a turn that is about to start sees it and a
        // turn already running has finished before this line is reached.
        _closing = true;

        var session = _session;
        _session = null;

        try
        {
            if (session is not null) await session.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            // RELEASED BEFORE IT IS DISPOSED. Disposing a semaphore that still
            // has a waiter throws in the waiter rather than in here, and a
            // caller blocked on the gate would get an ObjectDisposedException
            // out of AskAsync instead of a clean answer.
            _gate.Release();
            _gate.Dispose();
        }
    }
}
