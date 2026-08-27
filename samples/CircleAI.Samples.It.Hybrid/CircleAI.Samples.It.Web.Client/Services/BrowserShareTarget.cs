// BrowserShareTarget.cs
//
// Nothing shares into a browser tab.
//
// The Web Share Target API exists, but it needs an installed PWA with a
// manifest and a service worker - a different product decision from "this page
// shows you what the app does", and one nobody has asked for. Saying no is
// honest; the job screen prints the WhatsApp sentence only where it is true.

namespace CircleAI.Samples.It.Web.Client.Services;

/// <inheritdoc />
public sealed class BrowserShareTarget : IShareTarget
{
    /// <inheritdoc />
    public bool HasPending => false;

    /// <inheritdoc />
    public string? Take() => null;
}
