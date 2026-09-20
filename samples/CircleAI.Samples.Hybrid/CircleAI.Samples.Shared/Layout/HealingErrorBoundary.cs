// HealingErrorBoundary.cs
//
// The ONE place that makes every page's failures visible. Without it, a render or
// event-handler exception in any screen leaves a blank page and reaches nothing —
// which is the app's most common silent failure. Wrapping @Body with this funnels
// every uncaught page exception through Trouble.Say (→ the self-heal loop), then
// shows a recoverable message instead of a blank screen.

using System;
using System.Threading.Tasks;
using CircleAI.Assistant;
using Microsoft.AspNetCore.Components.Web;

namespace CircleAI.Samples.Shared.Layout;

/// <summary>An <see cref="ErrorBoundary"/> that routes every caught page exception
/// into the self-heal loop via <see cref="Trouble"/>.</summary>
public sealed class HealingErrorBoundary : ErrorBoundary
{
    /// <inheritdoc />
    protected override Task OnErrorAsync(Exception exception)
    {
        // Records ONCE (Trouble.Observer → the loop). The ErrorContent shows a fixed
        // friendly line and must not call Trouble.Say again, or one failure logs twice.
        try { Trouble.Say(exception); }
        catch { /* never fail while reporting a failure */ }

        return base.OnErrorAsync(exception);
    }
}
