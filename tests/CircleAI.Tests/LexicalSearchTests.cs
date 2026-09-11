// LexicalSearchTests.cs
//
// Search across everything, proved without a model.
//
// TWO TESTS HERE ARE EVIDENCE AND THE REST ARE PROPERTIES, and the difference
// was established by running both scorers over the same documents rather than by
// reasoning about them. SearchScoring.SimpleRelevance sums a term-frequency
// RATE - occurrences over document length - and the first draft of these tests
// claimed it had three faults. It has two:
//
//   no idf              -> naive picks the "the"-heavy document, BM25 the rare
//                          word. DISCRIMINATING.
//   length over-divided -> naive picks a two-word note over a 700-word
//                          transcript that actually discusses the subject.
//                          DISCRIMINATING.
//   saturation          -> naive is FINE. A rate saturates on its own. The
//                          claim that ten occurrences scored ten times one was
//                          simply wrong: it scores 1.00x.
//
// The two discriminating cases are marked. A test that passes under both
// scorers proves the property, not the improvement, and saying otherwise
// dresses up decoration as evidence.
//
// AND NONE OF THIS NEEDS A MODEL. The semantic path waits on an embedding model
// nobody has catalogued; this is arithmetic and runs on a handset with nothing
// downloaded.

using System;
using System.Collections.Generic;
using System.Linq;
using CircleAI.Search;
using Xunit;

namespace CircleAI.Tests;

public class LexicalSearchTests
{
    // ── The two real faults, and one property that is not one ──────────

    [Fact]
    public void A_common_word_does_not_outvote_the_rare_one_that_answers_the_question()
    {
        // DISCRIMINATING - the first and larger of the two real faults. Every
        // document contains "the"; only one contains "invoice". Measured: the
        // naive scorer picks the document with the most "the"s.
        var index = new LexicalIndex();
        index.Add(Doc("a", "the the the the meeting about the the schedule"));
        index.Add(Doc("b", "the the the the the the the notes"));
        index.Add(Doc("c", "the invoice for the work"));

        var hits = index.Search("the invoice");

        Assert.Equal("c", hits[0].Document.Origin.Id);
    }

    [Fact]
    public void Repeating_a_word_ten_times_does_not_score_ten_times()
    {
        // A PROPERTY, NOT EVIDENCE, and it was labelled as evidence at first.
        // The naive scorer passes this too - a term-frequency RATE saturates on
        // its own, scoring 1.00x where BM25 scores 1.23x. Kept because the
        // property must hold; relabelled because claiming it as an improvement
        // over the old scorer was untrue.
        var index = new LexicalIndex();
        index.Add(Doc("once", "invoice"));
        index.Add(Doc("tenfold", string.Join(' ', Enumerable.Repeat("invoice", 10))));

        var hits = index.Search("invoice");

        var once = hits.Single(h => h.Document.Origin.Id == "once").Score;
        var ten = hits.Single(h => h.Document.Origin.Id == "tenfold").Score;

        Assert.True(ten < once * 10,
            $"ten occurrences scored {ten}, which is not below ten times {once}");
    }

    [Fact]
    public void A_long_document_that_really_discusses_it_beats_a_short_one_that_mentions_it()
    {
        // DISCRIMINATING - the second of the two real faults. SimpleRelevance
        // divides by length outright (b = 1.0), so a seven-hundred-word meeting
        // transcript discussing the clinic appointment five times loses to the
        // two-word note "clinic elsewhere". Measured: naive picks the note,
        // BM25 picks the meeting.
        //
        // This is the failure that matters on a phone, because the long
        // documents are the transcripts and the short ones are stray notes.
        var index = new LexicalIndex();
        index.Add(Doc("meeting",
            string.Join(' ', Enumerable.Repeat("we discussed the clinic appointment at length today", 5))
            + " " + string.Join(' ', Enumerable.Repeat("and other matters were raised", 120))));
        index.Add(Doc("note", "clinic elsewhere"));

        var hits = index.Search("clinic appointment");

        Assert.Equal("meeting", hits[0].Document.Origin.Id);
    }

    // ── The scoring pieces on their own ─────────────────────────────────

    [Fact]
    public void A_term_in_every_document_tells_you_nothing_and_scores_about_nothing()
    {
        // And must not go NEGATIVE - the classic idf does, for a term in more
        // than half the corpus, which would push a document DOWN for containing
        // a common word. Worse than ignoring it.
        var idf = Bm25.InverseDocumentFrequency(documentsWithTerm: 100, totalDocuments: 100);

        Assert.True(idf >= 0, $"idf went negative: {idf}");
        Assert.True(idf < 0.1, $"a term in every document should contribute ~0, got {idf}");
    }

    [Fact]
    public void A_rarer_term_always_scores_higher_than_a_commoner_one()
    {
        var rare = Bm25.InverseDocumentFrequency(1, 1000);
        var middling = Bm25.InverseDocumentFrequency(100, 1000);
        var common = Bm25.InverseDocumentFrequency(900, 1000);

        Assert.True(rare > middling && middling > common,
            $"idf did not decrease with frequency: {rare}, {middling}, {common}");
    }

    [Fact]
    public void An_empty_corpus_scores_zero_rather_than_dividing_by_it()
    {
        Assert.Equal(0, Bm25.InverseDocumentFrequency(0, 0));
        Assert.Equal(0, Bm25.Score(termCount: 3, documentLength: 10, averageLength: 0, idf: 0));
    }

    [Fact]
    public void A_term_the_document_does_not_contain_contributes_nothing()
    {
        Assert.Equal(0, Bm25.Score(termCount: 0, documentLength: 10, averageLength: 10, idf: 5));
    }

    [Fact]
    public void A_query_term_typed_three_times_counts_once()
    {
        // "invoice invoice invoice" is somebody being emphatic, not a request to
        // treble that term's weight.
        var index = new LexicalIndex();
        index.Add(Doc("a", "invoice for the work"));
        index.Add(Doc("b", "unrelated notes"));

        var once = index.Search("invoice")[0].Score;
        var thrice = index.Search("invoice invoice invoice")[0].Score;

        Assert.Equal(once, thrice, 10);
    }

    // ── Across everything ───────────────────────────────────────────────

    [Fact]
    public void One_query_reaches_memory_transcripts_and_documents_together()
    {
        // THE POINT OF THE FEATURE. Three stores, one question, one ranked list -
        // rather than three lists a caller merges by hand, differently each time.
        var index = new LexicalIndex();
        index.Add(new SearchDocument(new SearchOrigin("memory", "m1"), "Thabo asked about the clinic"));
        index.Add(new SearchDocument(new SearchOrigin("transcript", "t1"), "the clinic appointment moved to Friday"));
        index.Add(new SearchDocument(new SearchOrigin("document", "d1"), "invoice for the clinic visit"));

        var hits = index.Search("clinic");

        Assert.Equal(3, hits.Count);
        Assert.Equal(
            new[] { "document", "memory", "transcript" },
            hits.Select(h => h.Document.Origin.Kind).OrderBy(k => k, StringComparer.Ordinal));
    }

    [Fact]
    public void A_search_can_be_limited_to_one_kind_of_source()
    {
        var index = new LexicalIndex();
        index.Add(new SearchDocument(new SearchOrigin("memory", "m1"), "clinic"));
        index.Add(new SearchDocument(new SearchOrigin("transcript", "t1"), "clinic"));

        var hits = index.Search("clinic", kinds: ["transcript"]);

        Assert.Equal("transcript", Assert.Single(hits).Document.Origin.Kind);
    }

    [Fact]
    public void A_hit_says_which_of_the_query_terms_it_actually_contained()
    {
        // So a screen can show why a result is there - and a person can see that
        // the one word they cared about was the one that did not match.
        var index = new LexicalIndex();
        index.Add(Doc("a", "the clinic appointment"));

        var hit = Assert.Single(index.Search("clinic pharmacy"));

        Assert.Contains("clinic", hit.Matched);
        Assert.DoesNotContain("pharmacy", hit.Matched);
    }

    [Fact]
    public void Something_matching_nothing_is_not_a_result()
    {
        // A list that always has ten rows teaches people the bottom rows are
        // noise, and then they stop reading the top ones.
        var index = new LexicalIndex();
        index.Add(Doc("a", "the clinic appointment"));

        Assert.Empty(index.Search("helicopter"));
    }

    [Fact]
    public void Ties_are_broken_by_recency()
    {
        var index = new LexicalIndex();
        index.Add(new SearchDocument(new SearchOrigin("memory", "old"), "clinic",
            new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero)));
        index.Add(new SearchDocument(new SearchOrigin("memory", "new"), "clinic",
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)));

        Assert.Equal("new", index.Search("clinic")[0].Document.Origin.Id);
    }

    // ── The awkward inputs ──────────────────────────────────────────────

    [Fact]
    public void An_empty_query_or_an_empty_index_returns_nothing_rather_than_throwing()
    {
        var empty = new LexicalIndex();
        Assert.Empty(empty.Search("anything"));

        var index = new LexicalIndex();
        index.Add(Doc("a", "something"));

        Assert.Empty(index.Search(""));
        Assert.Empty(index.Search(null));
        Assert.Empty(index.Search("   "));
        Assert.Empty(index.Search("anything", take: 0));
    }

    [Fact]
    public void A_document_with_no_words_in_it_is_not_indexed()
    {
        var index = new LexicalIndex();

        Assert.False(index.Add(Doc("blank", "")));
        Assert.False(index.Add(Doc("spaces", "   ")));
        Assert.False(index.Add(Doc("punctuation", "... ,,, ;;;")));
        Assert.Equal(0, index.Count);
    }

    [Fact]
    public void Matching_ignores_case_because_people_do_not_type_it()
    {
        var index = new LexicalIndex();
        index.Add(Doc("a", "The Clinic Appointment"));

        Assert.Single(index.Search("CLINIC"));
        Assert.Single(index.Search("clinic"));
    }

    [Fact]
    public void Take_bounds_the_result_list()
    {
        var index = new LexicalIndex();
        for (var i = 0; i < 20; i++) index.Add(Doc($"d{i}", "clinic appointment"));

        Assert.Equal(5, index.Search("clinic", take: 5).Count);
    }

    [Fact]
    public void Clearing_it_forgets_everything_including_the_term_statistics()
    {
        // A cleared index that kept its document frequencies would rank the next
        // corpus by the last one's vocabulary.
        var index = new LexicalIndex();
        index.Add(Doc("a", "clinic"));
        index.Clear();

        Assert.Equal(0, index.Count);
        Assert.Empty(index.Search("clinic"));

        index.Add(Doc("b", "clinic"));
        Assert.Single(index.Search("clinic"));
    }

    private static SearchDocument Doc(string id, string text)
        => new(new SearchOrigin("test", id), text);
}
