// MemoryWatch.cs
//
// WATCHING WHAT A MODEL ACTUALLY COSTS, WHICH NOBODY HAS EVER DONE.
//
// ModelEntry.MmapResidentGb carries its own instructions - "run the model on the
// target device, watch resident memory during generation, and record what you saw. Do
// not compute it." - and not one entry in the catalogue has one. That single empty
// field is what makes ModelFit.WillMmap false for every model, which is what makes
// the fit rule read the whole file, which is what limits a phone with 29 GB of
// storage to the models that fit in its 3.9 GB of memory.
//
// The field was never filled because nothing was watching. This watches.
//
// WORKING SET, AND WHY IT IS THE RIGHT NUMBER. Process.WorkingSet64 is RSS on Linux
// and Android: the pages of this process that are actually in physical memory right
// now. For a memory-mapped model that is exactly the part paged in - so a 22.8 GB
// file that touches 3 GB of experts reports 3 GB, which is the number the fit rule
// has always wanted and never had. For an eagerly loaded model it is the whole thing,
// which is also correct.
//
// IT IS .NET's OWN API, not /proc parsing, so it works on the desktop where the tests
// run and on the phone where the numbers matter. The managed heap is in it too, which
// is why the FLOOR is measured separately and subtracted: what is attributed to the
// model is the rise.
//
// SAMPLED, NOT READ ONCE. Weights are resident the moment they load; the cache grows
// through generation, and the peak is what has to fit. One reading taken at the wrong
// moment is how a model gets called affordable and then OOMs mid-answer - the failure
// the 15% headroom was invented to paper over.

using System.Diagnostics;

namespace CircleAI.Inference;

/// <summary>Watches what this process is actually holding, so a model's cost is a fact.</summary>
public sealed class MemoryWatch
{
    /// <summary>How often the peak is sampled while a model is working.</summary>
    /// <remarks>
    /// A quarter second. Generation on a P30 runs at 8-12 tokens a second, so this
    /// catches the cache growing without the sampler itself becoming the load.
    /// </remarks>
    public static TimeSpan SampleEvery { get; set; } = TimeSpan.FromMilliseconds(250);

    /// <summary>Bytes in a gigabyte, decimal - the catalogue's unit.</summary>
    private const double BytesPerGb = 1_000_000_000.0;

    /// <summary>What this process is holding right now, in GB. Zero when it cannot be read.</summary>
    /// <remarks>
    /// NEVER THROWS, AND ZERO MEANS UNKNOWN. A measurement that can take the brain
    /// down is worse than no measurement, and every caller treats 0 as "do not
    /// record" rather than as "it costs nothing".
    /// </remarks>
    public static double ResidentGb()
    {
        try
        {
            using var me = Process.GetCurrentProcess();
            me.Refresh();
            return me.WorkingSet64 / BytesPerGb;
        }
        catch
        {
            return 0;
        }
    }

    private readonly double _floorGb;
    private readonly string _modelId;
    private double _peakGb;
    private int _context;

    /// <summary>
    /// Starts watching, taking the CURRENT resident size as the floor.
    /// </summary>
    /// <param name="modelId">The catalogue name of the model about to load.</param>
    /// <remarks>
    /// THE FLOOR IS TAKEN BEFORE THE MODEL LOADS, which means the caller has to start
    /// this before it opens anything. Taken afterwards it would include the weights
    /// and the model would measure as free.
    /// </remarks>
    public MemoryWatch(string modelId)
    {
        _modelId = modelId ?? "";
        _floorGb = ResidentGb();
        _peakGb  = _floorGb;
    }

    /// <summary>The process's resident size with nothing loaded, in GB.</summary>
    public double FloorGb => _floorGb;

    /// <summary>The highest resident size seen since this started, in GB.</summary>
    public double PeakGb => _peakGb;

    /// <summary>Take a reading now, and remember it if it is the highest so far.</summary>
    /// <param name="contextTokens">
    /// The context the model is working at, so a cache term can be derived from two
    /// readings at different lengths.
    /// </param>
    public void Sample(int contextTokens = 0)
    {
        var now = ResidentGb();
        if (now > _peakGb) _peakGb = now;
        if (contextTokens > _context) _context = contextTokens;
    }

    /// <summary>
    /// What this model cost, or null when nothing usable was seen.
    /// </summary>
    /// <remarks>
    /// NULL RATHER THAN ZERO when the floor could not be read or the peak never rose
    /// above it: a recorded cost of nothing would tell the fit rule that this model is
    /// free, which is the most dangerous answer it could be given.
    /// </remarks>
    public ModelCost? Cost()
    {
        if (_floorGb <= 0 || _peakGb <= _floorGb) return null;

        return new ModelCost(
            ModelId: _modelId,
            ResidentGb: _peakGb - _floorGb,
            FloorGb: _floorGb,
            // ONE CONTEXT CANNOT TELL A SLOPE FROM A CONSTANT. A second measurement at
            // a different length is what produces this, and ModelCosts does that
            // arithmetic across recorded readings rather than guessing here.
            CacheGbPerThousandTokens: null,
            AtContextTokens: _context,
            When: DateTimeOffset.UtcNow);
    }
}
