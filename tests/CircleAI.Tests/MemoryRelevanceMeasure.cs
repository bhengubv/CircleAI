// MemoryRelevanceMeasure.cs
//
// THE RULER FOR RECALL, BUILT BEFORE THE FIX.
//
// A P30 running v15 answered "What is the capital of France" and the log said
// "recall: 1 offered, 1 kept" - a memory atom was handed to the model for a
// geography question. On a 0.6B with a 4096-token window that noise is what
// makes the answer ramble, and the user reported accuracy as very low.
//
// WHAT THIS MEASURES, AND WHAT IT DELIBERATELY DOES NOT ASSUME. I do not know
// which atom the phone offered - reasoning from the source said the deploy note
// shares no word with "capital of France", so it should NOT have matched, yet
// something did. This repo's own history (OPEN-GAPS E15) is a scar from
// changing a ranker on an assumption about content. So this reproduces the
// phone's EXACT call shape - Situation(Text: question), the way DeviceMemory
// builds it, with no Verb/Target - and prints what comes back.
//
// The measurement runs against the store's own behaviour. No fix is applied
// here; the numbers this prints are the "before".

using CircleAI.Memory;
using Xunit;
using Xunit.Abstractions;

namespace CircleAI.Tests;

public class MemoryRelevanceMeasure
{
    private readonly ITestOutputHelper _out;
    public MemoryRelevanceMeasure(ITestOutputHelper output) => _out = output;

    private static SqliteAtomStore NewStore() => new("Data Source=:memory:");

    private static MemoryAtom Atom(AtomKind kind, string text, string? subject = null) =>
        new() { Kind = kind, Text = text, Subject = subject };

    // The atom the phone had, reproduced verbatim from the device log's query
    // line. A single fact, filed the way LearnAsync files what it hears: with a
    // subject derived from the turn, not from the questions asked later.
    private const string DeployNote =
        "Never deploy with -t:Install on this phone, it wipes the models every time";

    // Questions a person asks that have NOTHING to do with that note.
    public static TheoryData<string> UnrelatedQuestions() =>
    [
        "What is the capital of France",
        "Name three colours",
        "Tell me a joke",
        "How do I plant maize",
        "What is 12 times 8",
    ];

    [Theory]
    [MemberData(nameof(UnrelatedQuestions))]
    public async Task What_a_lone_fact_is_offered_for(string question)
    {
        using var store = NewStore();
        await store.AddAsync(Atom(AtomKind.Fact, DeployNote, subject: "deploy:android"));

        // EXACTLY how DeviceMemory asks: the raw question as Text, nothing else.
        var recall = new Recall(store);
        var result = await recall.ForAsync(new Situation(Text: question));

        _out.WriteLine($"\"{question}\" -> {result.Atoms.Count} offered, "
                     + $"{result.Considered} considered");
        foreach (var a in result.Atoms)
            _out.WriteLine($"    [{a.Kind}] subj={a.Subject ?? "(none)"}  {a.Text}");

        // No assertion on the count yet - this is the ruler. It records what the
        // store does so a fix can be measured against it. The one thing worth
        // failing on is a crash.
        Assert.True(result.Atoms.Count >= 0);
    }

    [Fact]
    public async Task Does_the_store_even_return_the_note_for_an_unrelated_query()
    {
        // THE SHARPEST QUESTION: does MatchAsync hand back an atom that shares no
        // word with the query? If it does, the fix is at the match. If it does
        // not, then the phone had a DIFFERENT atom and I must go and read it,
        // not fix the wrong thing.
        using var store = NewStore();
        await store.AddAsync(Atom(AtomKind.Fact, DeployNote, subject: "deploy:android"));

        var matched = await store.MatchAsync(new Situation(Text: "What is the capital of France"));

        _out.WriteLine($"MatchAsync returned {matched.Count} for 'capital of France'");
        foreach (var a in matched)
            _out.WriteLine($"    {a.Text}");

        Assert.True(matched.Count >= 0);
    }
}
