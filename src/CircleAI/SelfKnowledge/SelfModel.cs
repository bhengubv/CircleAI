// SelfModel.cs
//
// WHAT CIRCLE AI KNOWS ABOUT ITSELF, KEPT WHERE IT WILL STILL BE TOMORROW.
//
// THE LONG-TERM MEMORY WAS BUILT AND IS USED FOR ONE HALF OF ITS JOB. MemoryService -
// atoms, subjects, recall, supersede, wear - is wired into the service and holds what
// a PERSON asked it to remember. It has never held anything the service learned about
// ITSELF. Every fact the device discovers about its own running - what a model cost,
// how fast it answered, whether it survived the load, which engine actually loaded
// here - is computed, used once, and thrown away, so the next launch starts from the
// same shipped assumptions as the first.
//
// That is the whole of why the selector answers "the best model when we released it".
// Not because the rule is wrong, but because the only inputs it has are the ones that
// shipped: a QualityRank written when the entry was written, identical on every phone
// on earth, and a declared MinRamGb nobody has checked against this handset.
//
// WHAT THIS IS NOT. It is NOT a second capability model. capabilities.json is the
// honest manifest of what this BUILD can do, it is kept current deliberately, and the
// standing decision is explicit that nothing should be built on top of it. This
// answers a different question that nothing has ever answered: not "what can this
// software do" but "what has THIS DEVICE observed about itself". A manifest cannot
// know that the 4B got the service killed here; only the phone that was killed knows.
//
// ONE STORE, TWO SUBJECTS. The facts live in the same MemoryService the person's
// memories live in, under the "self:" prefix (SelfSubjects), because a second store
// is a second thing to back up, migrate, sync and get out of step. Recall for a
// person's question must never surface "Qwen3.5-2B peaked at 1.4 GB", which is what
// the prefix is for.
//
// WRITE THROUGH, READ FROM MEMORY. The selector's question is synchronous and happens
// on the path to answering somebody, so it cannot wait on SQLite; the atoms are read
// once at start into a dictionary and every write updates both. A write that fails is
// logged and the in-process answer still stands - a measurement lost to a locked file
// is better than an answer delayed behind one.

using CircleAI.Memory;

namespace CircleAI.SelfKnowledge;

/// <summary>Circle AI's memory of itself: what this device has learned by running.</summary>
public sealed class SelfModel
{
    private readonly IMemoryService _memory;
    private readonly Action<string>? _say;
    private readonly Dictionary<string, ModelEvidence> _models = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    /// <param name="memory">The long-term memory. The same one the person's facts live in.</param>
    /// <param name="say">Where a line goes, so nothing here is ever silent.</param>
    public SelfModel(IMemoryService memory, Action<string>? say = null)
    {
        _memory = memory ?? throw new ArgumentNullException(nameof(memory));
        _say = say;
    }

    /// <summary>
    /// The module as the rest of the process sees it, or null before a host wires one.
    /// </summary>
    /// <remarks>
    /// A STATIC BECAUSE THE READERS CANNOT BE HANDED ONE. ModelFit and ModelChoice are
    /// static rules called from screens, from setup, from the selector and from the
    /// service, and threading an instance to all of them would be a refactor of
    /// everything that asks "which model" in order to change what the answer is based
    /// on. Null means no host wired it, and every reader falls back to exactly the
    /// behaviour it had before - which is what keeps a desktop test and a phone
    /// honest about the same code.
    /// </remarks>
    public static SelfModel? Current { get; set; }

    // ── reading ──────────────────────────────────────────────────────────────

    /// <summary>What this device has observed about a model. Never null; may be empty.</summary>
    public ModelEvidence About(string modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId)) return new ModelEvidence("");
        lock (_gate)
            return _models.TryGetValue(modelId, out var known) ? known : new ModelEvidence(modelId);
    }

    /// <summary>Every model this device has anything to say about.</summary>
    public IReadOnlyList<ModelEvidence> Models()
    {
        lock (_gate) return _models.Values.OrderBy(m => m.ModelId, StringComparer.Ordinal).ToList();
    }

    // ── learning ─────────────────────────────────────────────────────────────

    /// <summary>Records what a model cost in memory here.</summary>
    /// <remarks>
    /// A FACT, which is the kind the memory re-checks before relying on. Memory costs
    /// move: another app's footprint changes what is free, an update changes the
    /// runtime, and a figure from six months ago on a phone whose other software has
    /// turned over is worth less than a figure from yesterday. Superseding rather than
    /// appending means the newest measurement is the answer, which is the whole point.
    /// </remarks>
    public void LearnCost(CircleAI.Inference.ModelCost cost)
    {
        if (cost is null || string.IsNullOrWhiteSpace(cost.ModelId)) return;

        Update(cost.ModelId, was => was with
        {
            ResidentGb = cost.ResidentGb,
            FloorGb = cost.FloorGb,
            CacheGbPerThousandTokens = cost.CacheGbPerThousandTokens ?? was.CacheGbPerThousandTokens,
            LastSeenUtc = cost.When,
        });

        Write(new MemoryAtom
        {
            Kind = AtomKind.Fact,
            Subject = SelfSubjects.ModelCost(cost.ModelId),
            Text = Invariant(
                $"{cost.ModelId} held {cost.ResidentGb:F2} GB over a {cost.FloorGb:F2} GB floor on this device"),
        });
    }

    /// <summary>Records how fast a model answered here.</summary>
    public void LearnSpeed(string modelId, double tokensPerSecond)
    {
        if (string.IsNullOrWhiteSpace(modelId) || tokensPerSecond <= 0) return;

        Update(modelId, was => was with
        {
            TokensPerSecond = tokensPerSecond,
            LastSeenUtc = DateTimeOffset.UtcNow,
        });

        Write(new MemoryAtom
        {
            Kind = AtomKind.Fact,
            Subject = SelfSubjects.ModelSpeed(modelId),
            Text = Invariant($"{modelId} answered at {tokensPerSecond:F1} tokens a second on this device"),
        });
    }

    /// <summary>Records that a model loaded and worked, or did not.</summary>
    /// <remarks>
    /// A DECISION, WITH ITS OUTCOME, because that is what the memory's own remarks
    /// call this: "what came up, what was chosen, whether it worked", and "the
    /// failures are worth as much as the fixes". A shipped quality rank can never know
    /// that this handset was killed loading a particular model; this is the only place
    /// that fact can live.
    /// </remarks>
    public void LearnOutcome(string modelId, bool worked, string? detail = null)
    {
        if (string.IsNullOrWhiteSpace(modelId)) return;

        Update(modelId, was => was with
        {
            Loads = was.Loads + (worked ? 1 : 0),
            Failures = was.Failures + (worked ? 0 : 1),
            LastSeenUtc = DateTimeOffset.UtcNow,
        });

        Write(new MemoryAtom
        {
            Kind = AtomKind.Decision,
            Subject = SelfSubjects.ModelOutcome(modelId),
            Challenge = $"run {modelId} on this device",
            Text = worked
                ? $"{modelId} loaded and answered here"
                : $"{modelId} did not: {detail ?? "no reason recorded"}",
        });
    }

    /// <summary>Records something about the device itself.</summary>
    public void LearnAboutDevice(string subject, string what)
    {
        if (string.IsNullOrWhiteSpace(subject) || string.IsNullOrWhiteSpace(what)) return;
        Write(new MemoryAtom { Kind = AtomKind.Fact, Subject = subject, Text = what });
    }

    /// <summary>Records that the software switched something off here because it tripped.</summary>
    /// <remarks>
    /// APPENDED, SO THE COUNT SURVIVES. Every other write here supersedes the last one
    /// on its subject; this one must not, because the number of times a thing has
    /// tripped is exactly what decides how long it stays off, and superseding would
    /// reset that to one on every launch - a phone that crashes on mmap every morning
    /// would try it again every morning, forever.
    /// </remarks>
    public void LearnTrip(string name, string why)
    {
        if (string.IsNullOrWhiteSpace(name)) return;

        Append(new MemoryAtom
        {
            Kind = AtomKind.Decision,
            Subject = SelfSubjects.Healed(name),
            Challenge = $"{name} tripped on this device",
            Text = string.IsNullOrWhiteSpace(why) ? $"{name} was switched off here" : why,
        });
    }

    // ── starting up ──────────────────────────────────────────────────────────

    /// <summary>
    /// Reads back everything this device had already learned about itself.
    /// </summary>
    /// <remarks>
    /// THE LINE THAT MAKES IT LONG-TERM. Without this the module is a cache that dies
    /// with the process, and the next launch is as ignorant as the first - which is
    /// precisely the state the product has been in. Called once, at start, before
    /// anything asks which model to use.
    /// <para>
    /// PARSED FROM THE SUBJECT, NOT FROM THE PROSE. The text is written for a person
    /// to read back; the structure is the subject key and the kind. Reconstructing
    /// numbers by parsing sentences would make the wording load-bearing, which is how
    /// a log line becomes an API by accident.
    /// </para>
    /// </remarks>
    public async Task LoadAsync(CancellationToken ct = default)
    {
        try
        {
            var all = await _memory.AllAsync(limit: 2000, ct).ConfigureAwait(false);

            var counted = 0;
            foreach (var atom in all.Where(a => a.IsCurrent && SelfSubjects.IsAboutModel(a.Subject)))
            {
                var id = SelfSubjects.ModelOf(atom.Subject);
                if (string.IsNullOrWhiteSpace(id)) continue;

                // The OUTCOME subject is the one whose history is the fact: how many
                // times it worked and how many it did not. Costs and speeds are
                // superseded, so only the current atom matters and the numbers come
                // back with it when the host records them again.
                if (atom.Subject!.Contains(":outcome:", StringComparison.Ordinal))
                {
                    var worked = atom.Text.Contains("loaded and answered", StringComparison.OrdinalIgnoreCase);
                    Update(id!, was => was with
                    {
                        Loads = was.Loads + (worked ? 1 : 0),
                        Failures = was.Failures + (worked ? 0 : 1),
                        LastSeenUtc = atom.RecordedAtUtc,
                    });
                    counted++;
                }
            }

            // AND WHAT IT SWITCHED OFF, which is the half a launch cannot do without.
            // These accumulate rather than supersede, so the COUNT is the history, and
            // how many times a thing has tripped is what decides how long it stays off.
            // A start that did not read them back would turn every switch on again into
            // the same crash that turned it off - and would do that every single launch.
            var switches = 0;
            foreach (var trips in all
                .Where(a => SelfSubjects.IsAboutHealing(a.Subject))
                .GroupBy(a => SelfSubjects.HealedOf(a.Subject)!, StringComparer.OrdinalIgnoreCase))
            {
                SelfHeal.Restore(trips.Key, trips.Count(), trips.Max(a => a.RecordedAtUtc));
                switches++;
            }

            _say?.Invoke($"what this device knows about itself: {counted} record(s) over "
                       + $"{_models.Count} model(s), and {switches} switch(es) it turned off");
        }
        catch (Exception ex)
        {
            // A memory that will not open is a device that starts from the shipped
            // assumptions, which is where it was before any of this. Never fatal.
            _say?.Invoke("could not read back what this device knows about itself: " + ex.Message);
        }
    }

    // ── internals ────────────────────────────────────────────────────────────

    private void Update(string modelId, Func<ModelEvidence, ModelEvidence> change)
    {
        lock (_gate)
        {
            var was = _models.TryGetValue(modelId, out var known) ? known : new ModelEvidence(modelId);
            _models[modelId] = change(was);
        }
    }

    /// <summary>Writes an atom, without making the caller wait or fail for it.</summary>
    /// <remarks>
    /// FIRE AND FORGET, AND SAID WHEN IT FAILS. These are written from the load path
    /// and from Dispose; awaiting SQLite there would put a file lock between somebody
    /// and their answer. A write that is lost costs one measurement, and the next run
    /// takes it again.
    /// </remarks>
    private void Write(MemoryAtom atom)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                // SUPERSEDES THE LAST ONE ON THE SAME SUBJECT, so the newest reading is
                // the answer rather than one of fifty. The memory does the linking; this
                // only has to hand it the subject it is replacing.
                var current = await _memory.AllAsync(limit: 500).ConfigureAwait(false);
                var previous = current.FirstOrDefault(a =>
                    a.IsCurrent && string.Equals(a.Subject, atom.Subject, StringComparison.Ordinal));

                await _memory.RememberAsync(atom, previous?.Id).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _say?.Invoke($"could not write down what it learned about {atom.Subject}: {ex.Message}");
            }
        });
    }

    /// <summary>Writes an atom without replacing the one before it on the same subject.</summary>
    /// <remarks>
    /// THE OTHER HALF OF <see cref="Write"/>, and the difference is whether history
    /// matters. A cost is a reading and only the newest one is useful; a trip is an
    /// event and the third one means something the first did not.
    /// </remarks>
    private void Append(MemoryAtom atom)
    {
        _ = Task.Run(async () =>
        {
            try { await _memory.RememberAsync(atom).ConfigureAwait(false); }
            catch (Exception ex)
            {
                _say?.Invoke($"could not write down what tripped on {atom.Subject}: {ex.Message}");
            }
        });
    }

    private static string Invariant(FormattableString s) =>
        s.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
