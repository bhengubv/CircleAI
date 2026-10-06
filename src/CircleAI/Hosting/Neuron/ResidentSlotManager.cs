// ResidentSlotManager.cs
//
// Owns the Neuron's single evictable specialist slot beside the always-warm
// generalist floor. Modeled on the inference server's ModelLifecycleManager
// admission gate, scaled to exactly two logical slots: the generalist (reserved,
// held by AIService — never disposed here) plus one specialist (built, held, and
// disposed here). RAM headroom is checked against the live DeviceProbe before a
// specialist is built; under memory pressure the specialist is evicted first so
// the generalist never drops.

using CircleAI.Core;
using CircleAI.Inference;

namespace CircleAI.Hosting.Neuron;

/// <summary>Outcome of a specialist-slot admission attempt.</summary>
public enum SlotOutcome
{
    /// <summary>The specialist was built and is now resident.</summary>
    Admitted = 0,

    /// <summary>The requested specialist model was already resident.</summary>
    AlreadyResident = 1,

    /// <summary>The RAM gate denied the load; the caller falls back to the generalist.</summary>
    InsufficientRam = 2,

    /// <summary>The generator factory threw; the caller falls back to the generalist.</summary>
    BuildFailed = 3,
}

/// <summary>Result of <see cref="ResidentSlotManager.EnsureSpecialistAsync"/>.</summary>
/// <param name="Outcome">What happened.</param>
/// <param name="Generator">The resident specialist when admitted / already-resident; else <c>null</c>.</param>
/// <param name="Message">Human-readable detail (telemetry / debugging).</param>
public sealed record SlotAdmission(SlotOutcome Outcome, IChatGenerator? Generator, string Message);

/// <summary>
/// Manages the Neuron's one evictable specialist slot. The generalist floor is
/// never held here — the manager only reserves accounting for it so the RAM gate
/// keeps room for both organs.
/// </summary>
public sealed class ResidentSlotManager : IAsyncDisposable
{
    private readonly long _generalistReservedBytes;
    private readonly Func<DeviceProbe> _probe;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private string? _specialistModelId;
    private long _specialistBytes;
    private IChatGenerator? _specialist;
    private bool _disposed;

    /// <param name="generalistReservedBytes">
    /// Footprint of the always-warm generalist, reserved from the RAM ceiling so
    /// a specialist is only admitted when both organs fit.
    /// </param>
    /// <param name="probe">
    /// Live device snapshot source. Defaults to <see cref="DeviceProbe.Snapshot"/>.
    /// </param>
    public ResidentSlotManager(long generalistReservedBytes, Func<DeviceProbe>? probe = null)
    {
        _generalistReservedBytes = Math.Max(0L, generalistReservedBytes);
        _probe = probe ?? (() => DeviceProbe.Snapshot());
    }

    /// <summary>The model id of the resident specialist, or <c>null</c> when the slot is empty.</summary>
    public string? ResidentSpecialistModelId => _specialistModelId;

    /// <summary>The resident specialist generator, or <c>null</c> when the slot is empty.</summary>
    public IChatGenerator? ResidentSpecialist => _specialist;

    /// <summary>
    /// Ensure a specialist for <paramref name="selection"/> is resident, building
    /// it via <paramref name="buildAsync"/> when needed. Admission gate: the
    /// generalist floor plus the specialist footprint must fit under the device
    /// RAM ceiling. On denial or build failure the slot is left empty and the
    /// caller answers from the generalist.
    /// </summary>
    public async Task<SlotAdmission> EnsureSpecialistAsync(
        ModelSelection selection,
        Func<string, CancellationToken, Task<IChatGenerator>> buildAsync,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(buildAsync);
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Same specialist already hot — reuse it.
            if (_specialist is not null &&
                string.Equals(_specialistModelId, selection.ModelId, StringComparison.OrdinalIgnoreCase))
            {
                return new SlotAdmission(SlotOutcome.AlreadyResident, _specialist,
                    $"Specialist '{selection.ModelId}' already resident.");
            }

            // Admission gate — generalist floor + specialist must fit the ceiling.
            var ceiling = Math.Max(0L, _probe().RamAvailableBytes);
            var needed = _generalistReservedBytes + Math.Max(0L, selection.EstimatedBytes);
            if (ceiling > 0 && needed > ceiling)
            {
                return new SlotAdmission(SlotOutcome.InsufficientRam, null,
                    $"Specialist '{selection.ModelId}' needs {needed >> 20} MiB " +
                    $"(generalist {_generalistReservedBytes >> 20} + specialist {selection.EstimatedBytes >> 20}); " +
                    $"device ceiling {ceiling >> 20} MiB.");
            }

            // Only one specialist slot — evict the incumbent before building the new one.
            await DisposeSpecialistLockedAsync().ConfigureAwait(false);

            IChatGenerator built;
            try
            {
                built = await buildAsync(selection.ModelId, ct).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("Specialist build returned null.");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                return new SlotAdmission(SlotOutcome.BuildFailed, null,
                    $"Specialist '{selection.ModelId}' build failed: {ex.Message}");
            }

            _specialist        = built;
            _specialistModelId = selection.ModelId;
            _specialistBytes   = Math.Max(0L, selection.EstimatedBytes);
            return new SlotAdmission(SlotOutcome.Admitted, built,
                $"Specialist '{selection.ModelId}' resident ({_specialistBytes >> 20} MiB).");
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Evict the specialist (the generalist floor is never touched). No-op when
    /// the slot is already empty. Safe to call under memory pressure.
    /// </summary>
    public async Task EvictSpecialistAsync()
    {
        if (_disposed) return;
        await _gate.WaitAsync().ConfigureAwait(false);
        try { await DisposeSpecialistLockedAsync().ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    private async Task DisposeSpecialistLockedAsync()
    {
        var gen = _specialist;
        _specialist        = null;
        _specialistModelId = null;
        _specialistBytes   = 0L;
        if (gen is IAsyncDisposable ad)
        {
            try { await ad.DisposeAsync().ConfigureAwait(false); } catch { /* swallow */ }
        }
        else
        {
            try { gen?.Dispose(); } catch { /* swallow */ }
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await EvictSpecialistAsync().ConfigureAwait(false);
        _gate.Dispose();
    }
}
