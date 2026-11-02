// SeeingSelector.cs
//
// A PICTURE GOES TO THE BRAIN THAT ACTUALLY SEES ON THIS PHONE.
//
// The inference process's vision specialist was whatever the catalogue's selector best-fitted for
// Vision: on the P30 that is SmolVLM on MNN, and S9 found MNN's vision call returns -6 for it on
// every picture (OPEN-GAPS E32) - while the C# 2B with its tower beside it described the same
// picture right, on the same phone, in the same run. The catalogue cannot say so - a Bonsai row
// does not promise sight, because its tower may not be there (EngineChoice.Delivers) - but the store
// can: when a C# brain and its tower are both installed, that pair answers the picture. Everything
// else is the catalogue selector's, unchanged. The pair is still admitted beside the brain only if
// it fits; when it does not, AIService says it cannot see rather than guess.

using CircleAI.Core;
using CircleAI.Core.Models;
using CircleAI.Inference;

namespace CircleAI.Service.Droid.Inference;

/// <summary>An <see cref="IModelSelector"/> that answers a Vision request with an installed C# brain and its tower first.</summary>
internal sealed class SeeingSelector : IModelSelector
{
    private readonly IModelSelector _inner;
    private readonly ModelRegistryService _registry;
    private readonly BundleModelLoader _store;

    public SeeingSelector(IModelSelector inner, ModelRegistryService registry, BundleModelLoader store)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    /// <inheritdoc />
    public ModelSelection BestFit(DeviceProbe probe, ChatCapability required) =>
        required.HasFlag(ChatCapability.Vision) && SeeingPair(probe) is { } pair ? pair : _inner.BestFit(probe, required);

    /// <inheritdoc />
    public ModelSelection BestFit(DeviceProbe probe, ChatCapability required, int minQualityRank) =>
        required.HasFlag(ChatCapability.Vision) && SeeingPair(probe) is { } pair ? pair : _inner.BestFit(probe, required, minQualityRank);

    // EVERYTHING ELSE IS THE CATALOGUE SELECTOR'S - its fallback chain (the brownout swap walks it)
    // and its candidates - passed through, not re-derived.

    /// <inheritdoc />
    public ModelSelection? LoadableNow(DeviceProbe probe, ChatCapability required) => _inner.LoadableNow(probe, required);

    /// <inheritdoc />
    public IReadOnlyList<ModelSelection> AllCandidates(DeviceProbe probe) => _inner.AllCandidates(probe);

    /// <inheritdoc />
    public IReadOnlyList<string> ChainFor(string headModelId) => _inner.ChainFor(headModelId);

    // The smallest C# brain in the store whose vision tower is in the store too; null when there is none,
    // or when this device's C# kernels are too slow for a picture turn to finish.
    private ModelSelection? SeeingPair(DeviceProbe probe)
    {
        try
        {
            // TOO SLOW HERE TO ANSWER A PICTURE THROUGH THE QUEUE (S9, 2026-11-02). On the P30 the pair
            // took the tower's 25 s and then read the turn at about two tokens a second with nothing
            // written, so the supervisor's 90 s watchdog ended it twice - and Wolverine, reading a
            // timeout, shortened every answer's context for hours. The rule chat already keeps
            // (BrainSpeed: below four tokens a second is too slow to talk with) holds for a picture
            // too; the kernels' own rate here says it before anything is loaded.
            if (CircleAI.Inference.Managed.Kernels.KernelThreads.Remembered() is { TokensPerSecond: > 0 } kernels
                && kernels.TokensPerSecond < BrainSpeed.InteractiveTokensPerSecond)
            {
                global::Android.Util.Log.Info("CircleAI.Supervisor", string.Create(System.Globalization.CultureInfo.InvariantCulture,
                    $"a picture does not go to a C# brain here: its kernels run at {kernels.TokensPerSecond:F2} tokens a second, below the {BrainSpeed.InteractiveTokensPerSecond:F0} a turn needs"));
                return null;
            }

            var towers = _registry.AllModels.Where(m => BrainCompanions.IsTower(m) && _store.ModelPresent(m.Name)).ToList();
            var pair = _registry.AllModels
                .Where(b => b.Modality == ModelModality.Chat && !b.Retired && b.Engine == ModelEngine.Managed && _store.ModelPresent(b.Name))
                .Select(b => (Brain: b, Tower: towers.FirstOrDefault(t => BrainCompanions.Belongs(t, b))))
                .Where(p => p.Tower is not null)
                .OrderBy(p => p.Brain.TotalBytes + p.Tower!.TotalBytes)
                .FirstOrDefault();
            if (pair.Brain is null) return null;

            global::Android.Util.Log.Info("CircleAI.Supervisor",
                $"a picture goes to {pair.Brain.Name} with its tower {pair.Tower!.Name}: the pair that sees on this phone");
            return new ModelSelection(pair.Brain.Name, RequiresDownload: false, pair.Brain.TotalBytes + pair.Tower.TotalBytes, probe.Classify());
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn("CircleAI.Supervisor", "the seeing pair could not be read: " + ex.GetType().Name);
            return null;
        }
    }
}
