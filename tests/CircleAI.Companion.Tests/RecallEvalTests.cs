// RecallEvalTests.cs
//
// (M1) THE NUMBER — honest edition. Made-up people talk over several turns. The
// memory-web is built AUTOMATICALLY from their own words by one general connector
// (HeuristicKnowledgeGraphExtractor) applied to every turn — NOT hand-wired per
// case. Then each person asks a new question whose words do NOT overlap the target
// memory; the only way to reach it is a bridge that must emerge on its own through
// a word the person happened to mention in two different turns.
//
//   Baseline B0 = flat episodic (same-words recall).  Candidate B2 = fused.
//   Ship gate (M1): B2 beats B0 on the associative slice by >= 15 pts Recall@5,
//   and does not regress the surface slice.
//
// Everything is deterministic (a lexical embedder, no model). Honest caveats,
// stated plainly: the conversations are synthetic, and the connector is the
// model-free one — the real LLM connector and real users are the next gaps.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using CircleAI.Domain;
using CircleAI.Memory;
using Xunit;
using Xunit.Abstractions;

namespace CircleAI.Companion.Tests;

public class RecallEvalTests
{
    private readonly ITestOutputHelper _out;
    public RecallEvalTests(ITestOutputHelper output) => _out = output;

    private enum Slice { Associative, Surface }

    // Turns = everything this person said (each a memory). One of them is the
    // target (contains Signature). The graph is built from Turns by the connector.
    private sealed record Persona(
        string Id, Slice Slice, string Query, string Signature, IReadOnlyList<string> Turns);

    private sealed record SliceMetrics(double AssocRecall, double SurfaceRecall, double P50, double P95);

    // ── the people ───────────────────────────────────────────────────────────
    // Associative: the query shares NO word with the target memory. A bridge turn
    // shares a word with the query AND a word with the target — so the connection
    // has to be found across two turns, not by matching words.

    private static IReadOnlyList<Persona> People() => new[]
    {
        new Persona("assoc.cardiac", Slice.Associative,
            "why does my chest feel tight", "cardiac",
            new[] { "my fathers heart failed from cardiac arrest",
                    "the doctor checked my heart and my chest last week" }),

        new Persona("assoc.mortgage", Slice.Associative,
            "can i afford a holiday", "mortgage",
            new[] { "my mortgage eats most of my salary",
                    "i keep worrying about salary and holiday plans" }),

        new Persona("assoc.pollen", Slice.Associative,
            "why are my eyes itchy", "pollen",
            new[] { "i am allergic to pollen every spring",
                    "my eyes always react to the spring air" }),

        new Persona("assoc.isizulu", Slice.Associative,
            "which greeting suits durban", "isizulu",
            new[] { "my grandmother speaks only isizulu",
                    "in durban my grandmother taught me respect" }),

        new Persona("assoc.diabetes", Slice.Associative,
            "how much cake at the party", "diabetes",
            new[] { "my sugar trouble is called diabetes",
                    "the nurse linked my sugar to what i eat at a party" }),

        new Persona("assoc.rent", Slice.Associative,
            "should i buy a new couch", "rent",
            new[] { "my rent is due before payday",
                    "i plan my payday around the couch and groceries" }),

        // Surface: the query overlaps the target lexically → same-words recall finds it.
        new Persona("surf.dentist", Slice.Surface,
            "when is my dentist appointment", "tuesday",
            new[] { "my dentist appointment is on tuesday" }),

        new Persona("surf.medication", Slice.Surface,
            "what medication do i take each morning", "metformin",
            new[] { "i take metformin as medication every morning" }),

        new Persona("surf.car", Slice.Surface,
            "where did i park my car today", "level",
            new[] { "i parked my car on level three today" }),

        new Persona("surf.flight", Slice.Surface,
            "what time is my flight today", "noon",
            new[] { "my flight today leaves around noon" }),

        new Persona("surf.doctor", Slice.Surface,
            "who is my doctor", "naidoo",
            new[] { "my doctor is doctor naidoo" }),

        new Persona("surf.wifi", Slice.Surface,
            "what is the wifi password", "bluesky",
            new[] { "the wifi password is bluesky" }),
    };

    // Mundane distractors — deliberately different vocabulary, so they add realistic
    // noise (each is also encoded as a memory) without secretly bridging any target.
    private static IReadOnlyList<string> Distractors() => new[]
    {
        "the garden needs weeding this weekend", "we watched the clouds drift past",
        "the kettle is boiling loudly again", "she repainted the wooden bench",
        "the bakery smelled of warm cinnamon", "our neighbour mows on sundays",
        "the puppy chewed a slipper", "the market sells fresh mangoes",
        "the bus was crowded near the mall", "he strummed an old guitar",
        "the lamp flickers in the hallway", "we folded the clean laundry",
        "the kite dipped in the breeze", "the soup wanted a pinch of salt",
        "the ferry crossed the calm bay", "she sketched the harbour boats",
        "the printer jammed on page four", "the orchard was heavy with plums",
        "we swept the dusty veranda", "the choir practised on thursdays",
        "the river rose after heavy showers", "he waxed the surfboard carefully",
        "the bookshop stayed open late", "the campfire crackled softly",
    };

    private const int Dim = 128;

    private static float[] Embed(string text)
    {
        var v = new float[Dim];
        foreach (var tok in text.ToLowerInvariant().Split(
                     new[] { ' ', '\t', '\n', '.', ',', '?', '!', ';', ':', '\'', '"', '(', ')' },
                     StringSplitOptions.RemoveEmptyEntries))
        {
            uint h = 2166136261u;
            foreach (var c in tok) { h ^= c; h *= 16777619u; }
            v[h % Dim] += 1f;
        }
        double mag = 0;
        for (var i = 0; i < Dim; i++) mag += (double)v[i] * v[i];
        mag = Math.Sqrt(mag);
        if (mag > 0) for (var i = 0; i < Dim; i++) v[i] = (float)(v[i] / mag);
        return v;
    }

    private static async Task<SliceMetrics> Measure(IRecall recall, IReadOnlyList<Persona> people)
    {
        int aHit = 0, aTot = 0, sHit = 0, sTot = 0;
        var lat = new List<double>();
        foreach (var p in people)
        {
            var sw = Stopwatch.StartNew();
            var hits = await recall.RecallAsync(p.Query, Embed(p.Query), topK: 5);
            sw.Stop();
            lat.Add(sw.Elapsed.TotalMilliseconds);

            // "Found" = the target MEMORY (a sentence — has a space) surfaced, not a
            // bare concept word. The signature appears only in the target sentence.
            var found = hits.Any(h =>
                h.Item.Text.Contains(' ') &&
                h.Item.Text.Contains(p.Signature, StringComparison.OrdinalIgnoreCase));

            if (p.Slice == Slice.Associative) { aTot++; if (found) aHit++; }
            else { sTot++; if (found) sHit++; }
        }
        lat.Sort();
        double Pctl(double q) => lat.Count == 0 ? 0 : lat[Math.Clamp((int)Math.Floor(q * (lat.Count - 1)), 0, lat.Count - 1)];
        return new SliceMetrics(
            aTot > 0 ? (double)aHit / aTot : 0, sTot > 0 ? (double)sHit / sTot : 0, Pctl(0.50), Pctl(0.95));
    }

    [Fact]
    public async Task FusedRecall_ClearsTheShipGate_GraphBuiltFromConversation()
    {
        var people = People();
        var extractor = new HeuristicKnowledgeGraphExtractor();

        // Every memory (persona turns + distractors) goes into BOTH stores, exactly
        // as it would in the live loop — episodic by embedding, graph via the connector.
        var memories = people.SelectMany(p => p.Turns).Concat(Distractors()).ToList();

        using var episodic = new SqliteEpisodicStore("Data Source=:memory:");
        using var kg = new SqliteKnowledgeGraph("Data Source=:memory:");
        var baseTime = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        for (var i = 0; i < memories.Count; i++)
        {
            var text = memories[i];
            await episodic.AddAsync(new EpisodicMemoryEntry
            {
                UserText = text, Embedding = Embed(text), RecordedAtUtc = baseTime.AddMinutes(i),
            });
            foreach (var t in await extractor.ExtractFromTurnAsync(text, string.Empty, sourceEpisodeId: text))
                kg.AddTriple(t.Subject, t.Predicate, t.Object, t.Source, t.Confidence);
        }

        var hippo = new SqliteHippoRagStore(kg);
        var b0 = new FusedRecall(episodic, graph: null);
        var b2 = new FusedRecall(episodic, hippo);

        var m0 = await Measure(b0, people);
        var m2 = await Measure(b2, people);

        var gap = m2.AssocRecall - m0.AssocRecall;
        var report =
            $"[recall-eval] associative: B0={m0.AssocRecall:P0} B2={m2.AssocRecall:P0} (gap {gap:P0}); " +
            $"surface: B0={m0.SurfaceRecall:P0} B2={m2.SurfaceRecall:P0}; " +
            $"recall p95: B0={m0.P95:F2}ms B2={m2.P95:F2}ms";
        _out.WriteLine(report);

        Assert.True(gap >= 0.15,
            $"associative recall gap {gap:P0} is below the 15-pt ship gate. {report}");
        Assert.True(m2.SurfaceRecall >= m0.SurfaceRecall - 1e-9,
            $"surface slice regressed under fused recall. {report}");
    }
}
