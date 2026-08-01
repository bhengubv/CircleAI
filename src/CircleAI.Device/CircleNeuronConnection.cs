// CircleNeuronConnection.cs
//
// How an app asks the device service for the brain, without learning Android's
// binder lifecycle to do it.
//
// The service is the thing that owns the models; an app that builds its own
// NeuronNode has quietly opted out of the whole arrangement and is paying the
// 122 MB load again. So the easy path has to be the shared one — one await that
// hands back the same node every caller gets.

using Android.Content;
using Android.OS;
using CircleAI.Hosting.Neuron;

namespace CircleAI.Device;

/// <summary>
/// Binds to <see cref="CircleNeuronService"/> and hands back the shared node.
/// </summary>
/// <remarks>
/// Dispose to unbind. Unbinding does NOT stop the service or unload the models —
/// that is the point of it being resident. Call
/// <see cref="CircleNeuronService.Stop"/> when the models really should go.
/// </remarks>
public sealed class CircleNeuronConnection : Java.Lang.Object, IServiceConnection, IDisposable
{
    private readonly Context _context;
    private readonly TaskCompletionSource<CircleNeuronBinder?> _bound =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _isBound;

    private CircleNeuronConnection(Context context) => _context = context;

    /// <summary>
    /// Starts the service if needed, binds to it, and waits for the node to be
    /// ready.
    /// </summary>
    /// <param name="context">Any context; the application context is used.</param>
    /// <param name="timeout">
    /// How long to wait for the model to finish loading. Cold-loading a voice on a
    /// P30 Lite measured 13-23 s, so a default of 60 s is generous rather than
    /// optimistic — but it is a TIMEOUT, not a promise: on expiry you get whatever
    /// state the service reached, and <see cref="CircleNeuronService.Status"/> says
    /// what that is.
    /// </param>
    /// <returns>The shared node, or null if it did not become ready in time.</returns>
    public static async Task<(NeuronNode? Node, CircleNeuronConnection Connection)> ConnectAsync(
        Context context,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var app = context.ApplicationContext ?? context;

        CircleNeuronService.Start(app);

        var connection = new CircleNeuronConnection(app);
        var intent = new Intent(app, typeof(CircleNeuronService));
        connection._isBound = app.BindService(intent, connection, Bind.AutoCreate);

        if (!connection._isBound) return (null, connection);

        var deadline = timeout ?? TimeSpan.FromSeconds(60);
        var binder = await WaitOrNull(connection._bound.Task, deadline, cancellationToken)
            .ConfigureAwait(false);
        if (binder is null) return (null, connection);

        // Bound is not the same as loaded. The binder arrives as soon as Android
        // has an object to hand over, which is long before a 122 MB model has been
        // read off eMMC — returning here would give the caller a node that answers
        // "not ready" to everything and look like a broken brain.
        var giveUpAt = DateTime.UtcNow + deadline;
        while (DateTime.UtcNow < giveUpAt)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var node = binder.Node;
            if (node is { IsReady: true }) return (node, connection);
            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        }

        return (binder.Node, connection);   // whatever it got to; Status explains
    }

    private static async Task<T?> WaitOrNull<T>(Task<T?> task, TimeSpan timeout, CancellationToken ct)
        where T : class
    {
        var finished = await Task.WhenAny(task, Task.Delay(timeout, ct)).ConfigureAwait(false);
        return finished == task ? await task.ConfigureAwait(false) : null;
    }

    /// <inheritdoc/>
    public void OnServiceConnected(ComponentName? name, IBinder? service) =>
        _bound.TrySetResult(service as CircleNeuronBinder);

    /// <inheritdoc/>
    /// <remarks>
    /// Fires when the service process dies — which on a 3 GB phone is a matter of
    /// when, not if. The node is gone; a caller that cached it must reconnect
    /// rather than hold a dead reference.
    /// </remarks>
    public void OnServiceDisconnected(ComponentName? name) => _bound.TrySetResult(null);

    protected override void Dispose(bool disposing)
    {
        if (disposing && _isBound)
        {
            _isBound = false;
            try { _context.UnbindService(this); }
            catch { /* already gone — unbinding twice is not worth an exception */ }
        }
        base.Dispose(disposing);
    }
}
