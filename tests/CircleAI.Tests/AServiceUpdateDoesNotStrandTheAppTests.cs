// AServiceUpdateDoesNotStrandTheAppTests.cs
//
// A PROCESS DEATH AND A PACKAGE UPDATE ARE DIFFERENT CALLBACKS, and the link client
// only handled the first.
//
// When the service process dies, Bind.AutoCreate restarts it: Android calls
// OnServiceDisconnected and then OnServiceConnected again with a fresh binder, and
// CircleAiLinkClient was already rebuilt around that (the 2026-10-09 corpse-binder
// fix). When the service PACKAGE is updated, Android calls OnBindingDied instead,
// and that binding never comes back - "the application will need to unbind and
// rebind the connection to activate it again."
//
// Measured on the P30 on 2026-10-16: the service was replaced through Visual
// Studio's Install target, the app stayed open in the foreground, and no service
// process came back, because nothing asked for one.
//
// SOURCE-LEVEL, because CircleAiLinkClient is an Android Java.Lang.Object that
// cannot be constructed off a device, and what regressed is the SHAPE: a callback
// that was never implemented.

using System;
using System.IO;
using Xunit;

namespace CircleAI.Tests;

public sealed class AServiceUpdateDoesNotStrandTheAppTests
{
    private static string Source() => File.ReadAllText(Path.Combine(
        Root(), "src", "CircleAI", "Client", "CircleAiLinkClient.cs"));

    private static string BindingDiedBody()
    {
        var source = Source();
        var start = source.IndexOf("public void OnBindingDied(ComponentName? name)", StringComparison.Ordinal);
        Assert.True(start >= 0, "CircleAiLinkClient does not implement OnBindingDied");

        // The body runs to the next member's doc comment or the next method.
        var end = source.IndexOf("protected override void Dispose", start, StringComparison.Ordinal);
        Assert.True(end > start, "could not find the end of OnBindingDied");
        return source[start..end];
    }

    [Fact]
    public void A_dead_binding_is_unbound_and_bound_again()
    {
        var body = BindingDiedBody();

        Assert.Contains("_context.UnbindService(this)", body);
        Assert.Contains("_context.BindService(intent, this, Bind.AutoCreate)", body);
    }

    [Fact]
    public void A_caller_in_flight_waits_for_the_new_binder_rather_than_failing()
    {
        // The same rule as OnServiceDisconnected: drop the dead binder and give the
        // next caller a fresh, uncompleted waiter, so it waits up to RestartGrace.
        var body = BindingDiedBody();

        Assert.Contains("_live = null;", body);
        Assert.Contains("_bound = new TaskCompletionSource<IBinder?>", body);
    }

    [Fact]
    public void It_rebinds_to_the_same_service_the_first_bind_found()
    {
        // One fact, one place: the action and package come from LinkIpc, exactly as
        // in ConnectAsync, so the rebind cannot drift to a different target.
        var body = BindingDiedBody();

        Assert.Contains("new Intent(LinkIpc.BindAction)", body);
        Assert.Contains("intent.SetPackage(LinkIpc.HostPackage)", body);
    }

    [Fact]
    public void A_rebind_that_fails_is_tried_again_by_the_next_call_that_needs_the_service()
    {
        // A bind made while the installer still holds the package can fail, and then
        // no callback ever arrives - so the call that needs the service binds again.
        var source = Source();

        Assert.Contains("if (!_isBound) Rebind();", source);
        Assert.Contains("_ = Task.Delay(RebindPause).ContinueWith(_ => Rebind(), TaskScheduler.Default);", source);
        Assert.Contains("if (_isBound || _disposed) return;", source);   // never rebinds a disposed client
    }

    [Fact]
    public void A_disconnect_never_orphans_a_caller_already_waiting()
    {
        // It replaced the waiter unconditionally, so a caller waiting on the fresh
        // one Invalidate had just made sat out RestartGrace with the service back up.
        var source = Source();
        var start  = source.IndexOf("public void OnServiceDisconnected(ComponentName? name)", StringComparison.Ordinal);
        var end    = source.IndexOf("public void OnBindingDied(ComponentName? name)", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);

        Assert.Contains("if (_bound.Task.IsCompleted)", source[start..end]);
    }

    private static string Root()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !Directory.Exists(Path.Combine(dir, "src")))
            dir = Path.GetDirectoryName(dir);

        Assert.NotNull(dir);
        return dir!;
    }
}
