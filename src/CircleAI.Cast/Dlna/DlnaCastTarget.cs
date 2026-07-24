// DlnaCastTarget.cs — (3.5.0) ICastTarget over a resolved UPnP MediaRenderer. Holds the
// parsed description and a factory that mints a control session (so the target itself
// stays free of HttpClient / media-host wiring).

using System;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.Cast.Dlna;

/// <summary>A discovered DLNA renderer (smart TV) on the LAN.</summary>
public sealed class DlnaCastTarget : ICastTarget
{
    private readonly Func<DlnaCastTarget, ICastSession> _sessionFactory;

    internal DlnaCastTarget(RendererDescription description, Func<DlnaCastTarget, ICastSession> sessionFactory)
    {
        Description = description ?? throw new ArgumentNullException(nameof(description));
        _sessionFactory = sessionFactory ?? throw new ArgumentNullException(nameof(sessionFactory));
    }

    /// <summary>The full parsed UPnP description (control URL, icon, etc.).</summary>
    public RendererDescription Description { get; }

    public CastTargetId Id => new(Description.Udn);
    public string FriendlyName => Description.FriendlyName;
    public string Manufacturer => Description.Manufacturer;
    public string Model => Description.ModelName;
    public CastProtocol Protocol => CastProtocol.Dlna;
    public Uri Location => Description.Location;
    public Uri? IconUri => Description.IconUrl;

    public ValueTask<ICastSession> ConnectAsync(CancellationToken ct = default)
        => ValueTask.FromResult(_sessionFactory(this));
}
