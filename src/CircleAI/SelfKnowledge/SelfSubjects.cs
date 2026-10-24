// SelfSubjects.cs
//
// THE KEYS CIRCLE AI FILES ITS OWN FACTS UNDER.
//
// One owner for every subject string, because this repository's most expensive and
// most repeated defect is one fact with two owners: the language count, the wake
// phrase, the model choice and "is the bundle downloaded" each ended up with two
// answers that disagreed, and every time it was two places writing the same string.
// A subject key is a string two halves have to agree on - the half that remembers and
// the half that recalls - so it gets a constant rather than a literal.
//
// COARSE ON PURPOSE. MemoryAtom.Subject's own remark says it: "Deliberately coarse -
// 'deploy:android' rather than the exact command. A key that includes every argument
// matches nothing twice, which is the same as having no index at all." So a model's
// cost is filed under the model, not under the model and the context and the hour.

namespace CircleAI.SelfKnowledge;

/// <summary>What Circle AI files its own facts under, in its long-term memory.</summary>
public static class SelfSubjects
{
    /// <summary>Everything the service knows about itself begins with this.</summary>
    /// <remarks>
    /// SEPARATE FROM THE PERSON'S MEMORY, IN THE SAME STORE. The memory already holds
    /// what somebody asked it to remember; this is what it learned about ITSELF, and
    /// the two must never be confused - recall for a person's question must not
    /// surface "Qwen3.5-2B peaked at 1.4 GB", and the model selector must not be
    /// swayed by somebody's shopping list. One prefix keeps them apart in one store,
    /// which is better than two stores that drift.
    /// </remarks>
    public const string Prefix = "self:";

    /// <summary>What a model actually cost in memory here. One per model.</summary>
    public static string ModelCost(string modelId) => $"{Prefix}model:cost:{Normalise(modelId)}";

    /// <summary>How fast a model actually answered here. One per model.</summary>
    public static string ModelSpeed(string modelId) => $"{Prefix}model:speed:{Normalise(modelId)}";

    /// <summary>How a load of this model turned out - it ran, or it did not.</summary>
    /// <remarks>
    /// A DECISION, NOT A FACT, and the distinction is the memory's own: a decision
    /// records "what came up, what was chosen, how it turned out", and the failures
    /// are worth as much as the successes. "Loaded Qwen3.5-4B, the service was killed"
    /// is the atom that stops it being chosen again, and it is the one thing a shipped
    /// quality rank can never know.
    /// </remarks>
    public static string ModelOutcome(string modelId) => $"{Prefix}model:outcome:{Normalise(modelId)}";

    /// <summary>What this device is - memory, storage, the ABI, what it will run.</summary>
    public const string Device = Prefix + "device";

    /// <summary>Which inference engines actually loaded here, and which build of each.</summary>
    /// <remarks>
    /// NOT A BUILD-TIME FACT. Whether llama.cpp is present is decided by which native
    /// library is in the APK for THIS ABI, and which llama.cpp it is - stock or the
    /// PrismML fork, which is the difference between reading Bonsai's ternary packs
    /// and refusing them - is a string read out of the loaded library at runtime.
    /// A device that has it and a device that does not are the same build.
    /// </remarks>
    public const string Engines = Prefix + "engines";

    /// <summary>What the vendor's firmware does to us, learned the hard way.</summary>
    /// <remarks>
    /// EMUI hibernated the always-listening service while its owner spoke to the phone
    /// for eleven minutes. That is a fact about this device which no catalogue, no
    /// manifest and no build knows, and which the next decision about background work
    /// ought to be able to read.
    /// </remarks>
    public const string Vendor = Prefix + "vendor";

    /// <summary>Something the software switched off here because it tripped. One per switch.</summary>
    /// <remarks>
    /// APPENDED, NOT SUPERSEDED, WHICH IS WHY IT IS A SUBJECT OF ITS OWN. Every other
    /// self subject holds the LATEST reading and replaces the one before it, because a
    /// memory figure from six months ago is worth less than one from yesterday. A trip
    /// is the opposite: the third time mmap SIGSEGVs is a different and much more
    /// important fact than the first, so the atoms accumulate and the COUNT of them is
    /// the thing worth knowing. That also keeps the numbers out of the prose - trips
    /// are counted, not parsed - which is the rule LoadAsync states for itself.
    /// </remarks>
    public static string Healed(string name) => $"{Prefix}healed:{Normalise(name)}";

    /// <summary>Every subject that is a switch the software turned off, for a sweep.</summary>
    public static bool IsAboutHealing(string? subject) =>
        subject is not null && subject.StartsWith(Prefix + "healed:", StringComparison.Ordinal);

    /// <summary>The switch name out of a healing subject, or null.</summary>
    public static string? HealedOf(string? subject) =>
        IsAboutHealing(subject) ? subject![(Prefix.Length + "healed:".Length)..] : null;

    /// <summary>Every subject that is about one model, for a sweep.</summary>
    public static bool IsAboutModel(string? subject) =>
        subject is not null && subject.StartsWith(Prefix + "model:", StringComparison.Ordinal);

    /// <summary>The model id out of one of the model subjects, or null.</summary>
    public static string? ModelOf(string? subject)
    {
        if (!IsAboutModel(subject)) return null;
        var last = subject!.LastIndexOf(':');
        return last >= 0 && last < subject.Length - 1 ? subject[(last + 1)..] : null;
    }

    /// <summary>
    /// A model id as it appears in a subject key.
    /// </summary>
    /// <remarks>
    /// LOWERCASED, because the registry and the folder on disk do not always agree on
    /// case and a subject that differs only in case is a second fact about the same
    /// model - which is the defect this file exists to prevent, committed by the file
    /// that exists to prevent it.
    /// </remarks>
    private static string Normalise(string modelId) =>
        (modelId ?? string.Empty).Trim().ToLowerInvariant();
}
