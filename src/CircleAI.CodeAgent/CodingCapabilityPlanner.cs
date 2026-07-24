// CodingCapabilityPlanner.cs
//
// The hardware-tier gate for on-device coding, expressed in the repo's existing
// PlanFor pattern: it returns a ModalityPlan whose SelectionQuality is the
// honest verdict. Unavailable on a low-end device (hardware floor) OR when no
// real coding model is installed (empty catalogue). Good only when a capable
// device AND a hash-verified model line up.
//
// Mirrors ISpeechModelSelector.PlanFor / IAIService.PlanFor so callers branch on
// the same SelectionQuality they already understand.

using System.Linq;
using CircleAI.Core;       // DeviceProbe, DeviceTier
using CircleAI.Inference;  // ModalityPlan, SelectionQuality, ModelSelection

namespace CircleAI.CodeAgent;

/// <summary>
/// Decides whether "code from my mobile" can run on THIS device, and how. The
/// single decision point the agent loop consults before doing anything.
/// </summary>
public interface ICodingCapabilityPlanner
{
    /// <summary>
    /// Plan on-device coding for <paramref name="probe"/> (or a fresh
    /// <see cref="DeviceProbe.Snapshot"/> when null). The returned
    /// <see cref="ModalityPlan.Quality"/> is:
    /// <list type="bullet">
    ///   <item><see cref="SelectionQuality.Unavailable"/> — device below the
    ///   hardware floor, or no coding model installed. Decline.</item>
    ///   <item><see cref="SelectionQuality.NothingFits"/> — models are
    ///   catalogued but none fits this device.</item>
    ///   <item><see cref="SelectionQuality.Good"/> — a real model fits;
    ///   <see cref="ModalityPlan.Model"/> names it.</item>
    /// </list>
    /// </summary>
    ModalityPlan PlanForCoding(DeviceProbe? probe = null);
}

/// <summary>
/// Default <see cref="ICodingCapabilityPlanner"/>: hardware floor first, then
/// the coding-model catalogue. Honest by construction — it declines a weak phone
/// on hardware alone, and declines a capable phone that has no real model.
/// </summary>
public sealed class CodingCapabilityPlanner : ICodingCapabilityPlanner
{
    private readonly ICodingModelCatalog _catalog;
    private readonly CodingModelRequirements _req;

    /// <summary>
    /// Construct with a catalogue (default: <see cref="EmptyCodingModelCatalog"/>)
    /// and a floor (default: <see cref="CodingModelRequirements.Default"/>).
    /// </summary>
    public CodingCapabilityPlanner(
        ICodingModelCatalog? catalog = null,
        CodingModelRequirements? requirements = null)
    {
        _catalog = catalog ?? EmptyCodingModelCatalog.Instance;
        _req     = requirements ?? CodingModelRequirements.Default;
    }

    /// <inheritdoc/>
    public ModalityPlan PlanForCoding(DeviceProbe? probe = null)
    {
        probe ??= DeviceProbe.Snapshot();

        var tier      = probe.Classify();
        var ramGb     = probe.RamAvailableBytes / (1024.0 * 1024 * 1024);
        var storageGb = probe.StorageFreeBytes  / (1024.0 * 1024 * 1024);

        // 1. HARDWARE FLOOR. A real 3-7B coding model cannot run in a weak
        //    phone's RAM budget. Below floor => Unavailable BY DESIGN. This is
        //    the P30 Lite path and it does not depend on the catalogue at all.
        if (tier < _req.MinDeviceTier || ramGb + 0.0001 < _req.MinRamGb)
        {
            return new ModalityPlan(
                SelectionQuality.Unavailable, null,
                $"on-device coding needs ~{_req.MinRamGb:0.#} GB RAM and tier >= {_req.MinDeviceTier}; " +
                $"this device reports {ramGb:0.#} GB and tier {tier}. Unavailable by design.");
        }

        // Storage floor is advisory — skip it when the host could not read free
        // space (StorageFreeBytes == 0), exactly as the chat selector does.
        if (storageGb > 0 && storageGb + 0.0001 < _req.MinFreeStorageGb)
        {
            return new ModalityPlan(
                SelectionQuality.Unavailable, null,
                $"a {_req.MinParametersBillion}B+ coding model needs ~{_req.MinFreeStorageGb:0.#} GB free storage; " +
                $"only {storageGb:0.#} GB available.");
        }

        // 2. CATALOGUE. Even a capable phone cannot code without a real,
        //    hash-verified coding-model bundle. We ship none (no hash), so this
        //    is Unavailable today — for the right reason, stated plainly.
        if (_catalog.Available.Count == 0)
        {
            return new ModalityPlan(
                SelectionQuality.Unavailable, null,
                "device is capable, but no on-device coding model is installed. A real 3-7B coding " +
                "model requires a downloaded, SHA-256-verified bundle this build does not carry. " +
                "Register one via ICodingModelCatalog to enable.");
        }

        // 3. FIT. Filter the catalogue by capability flags + device fit, then
        //    take the largest model that fits (more parameters = better coder).
        var fits = _catalog.Available
            .Where(m => (m.Capabilities & _req.RequiredCapabilities) == _req.RequiredCapabilities)
            .Where(m => m.ParametersBillion >= _req.MinParametersBillion)
            .Where(m => m.MinRamGb <= ramGb + 0.0001 &&
                        (storageGb <= 0 || m.MinFreeStorageGb <= storageGb + 0.0001))
            .OrderByDescending(m => m.ParametersBillion)
            .ToList();

        if (fits.Count == 0)
        {
            return new ModalityPlan(
                SelectionQuality.NothingFits, null,
                "coding models are catalogued but none clears this device's RAM / storage / capability floor.");
        }

        var winner = fits[0];
        var selection = new ModelSelection(
            ModelId:          winner.ModelId,
            RequiresDownload: true, // the catalogue does not track on-disk cache; the caller checks
            EstimatedBytes:   winner.TotalBytes,
            Tier:             tier,
            Quality:          SelectionQuality.Good);

        return new ModalityPlan(
            SelectionQuality.Good, selection,
            $"{winner.ModelId} ({winner.ParametersBillion}B) fits this device.");
    }
}
