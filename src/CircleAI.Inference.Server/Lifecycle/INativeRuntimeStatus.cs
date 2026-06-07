// INativeRuntimeStatus.cs
//
// Last-known native-runtime paths produced by NativeRuntimePrep.PrepareForLoad,
// surfaced through /v1/diagnostics so DLL-not-found failures are debuggable
// from the wire. Updated each time the bridge factory materialises a model.

using CircleAI.Inference;

namespace CircleAI.Inference.Server.Lifecycle;

/// <summary>
/// Singleton holder of the last-known <see cref="NativeRuntimePrep.NativeRuntimePaths"/>.
/// Written by <see cref="MnnInferenceBridgeFactory"/> after every successful
/// <see cref="NativeRuntimePrep.PrepareForLoad"/>, read by the diagnostics
/// endpoint.
/// </summary>
public interface INativeRuntimeStatus
{
    /// <summary>Most recent prep result, or <c>null</c> before the first model load.</summary>
    NativeRuntimePrep.NativeRuntimePaths? Latest { get; }

    /// <summary>Record the result of a successful prep run.</summary>
    void Update(NativeRuntimePrep.NativeRuntimePaths paths);
}

/// <inheritdoc cref="INativeRuntimeStatus"/>
public sealed class NativeRuntimeStatus : INativeRuntimeStatus
{
    private readonly object _lock = new();
    private NativeRuntimePrep.NativeRuntimePaths? _latest;

    public NativeRuntimePrep.NativeRuntimePaths? Latest
    {
        get { lock (_lock) return _latest; }
    }

    public void Update(NativeRuntimePrep.NativeRuntimePaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        lock (_lock) _latest = paths;
    }
}
