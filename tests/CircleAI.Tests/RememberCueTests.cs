// RememberCueTests.cs
//
// Being asked outright to remember something.
//
// THE FIRST THING EVER SAID TO THE DEPLOYED APP WAS "Remember that my clinic
// appointment moved to Friday", and the phone stored nothing. A search for
// "clinic" a minute later came back empty, and that was CueExtractor behaving
// exactly as written: forty-three cues for rules, preferences, decisions and
// failures, and not one for being asked outright. A bare "use " counted as a
// decision while "remember that" counted as nothing at all.
//
// An explicit request is the only case where the person is not being
// interpreted, so these cues outrank every other. Matching is by confidence
// descending, so that ordering is a property of the numbers rather than of
// where the rows sit in the array - which is why the precedence test below is
// worth having.
//
// AND THE FALSE POSITIVES ARE THE POINT. "I don't remember where I put it" is
// not an instruction to store anything. The cost of getting that wrong is
// somebody's idle sentence kept for ever in a memory they never asked for, so
// there is deliberately no bare "remember " cue and these tests pin that.

using System.Linq;
using CircleAI.Memory;
using Xunit;

namespace CircleAI.Tests;

public sealed class RememberCueTests
{
    private static MemoryAtom[] Extract(string said) =>
        [.. new CueExtractor()
            .Extract(new EpisodicMemoryEntry { UserText = said })
            .Select(c => c.Atom)];

    [Fact]
    public void The_sentence_the_phone_ignored_is_now_remembered()
    {
        // THE REGRESSION, verbatim from the P30.
        var atoms = Extract("Remember that my clinic appointment moved to Friday");

        var atom = Assert.Single(atoms);
        Assert.Equal(AtomKind.Fact, atom.Kind);
        Assert.Contains("clinic", atom.Text);
    }

    [Theory]
    [InlineData("Remember that the gate code is 4417")]
    [InlineData("Remember to call the clinic on Friday")]
    [InlineData("Don't forget the gate code is 4417")]
    [InlineData("Dont forget the gate code is 4417")]
    [InlineData("Keep in mind the clinic closes at four")]
    [InlineData("Note that the gate code changed")]
    public void Every_explicit_request_is_kept_as_a_fact(string said)
    {
        var atom = Assert.Single(Extract(said));
        Assert.Equal(AtomKind.Fact, atom.Kind);
    }

    [Fact]
    public void An_explicit_request_is_not_a_ruling()
    {
        // "my appointment moved to Friday" is something that is TRUE, not
        // something the person wants done differently in future. Filing it as a
        // Ruling would put it in front of the model as a standing instruction.
        var atom = Assert.Single(Extract("Remember that I moved to Cape Town"));

        Assert.Equal(AtomKind.Fact, atom.Kind);
        Assert.NotEqual(AtomKind.Ruling, atom.Kind);
    }

    [Fact]
    public void An_explicit_request_outranks_a_cue_that_also_matches()
    {
        // "don't " is a Ruling cue at sentence start, and would otherwise claim
        // this sentence at 0.88. The explicit form must win - and it wins on
        // CONFIDENCE, not on where it sits in the array, which is the part worth
        // pinning because a reordering would not break it and a renumbering
        // would.
        var atom = Assert.Single(Extract("Don't forget the clinic moved to Friday"));

        Assert.Equal(AtomKind.Fact, atom.Kind);
    }

    [Theory]
    [InlineData("I don't remember where I put it")]
    [InlineData("Do you remember the film we watched")]
    [InlineData("I can never remember her birthday")]
    public void Idle_talk_about_remembering_is_not_an_instruction(string said)
    {
        // THE REASON THERE IS NO BARE "remember " CUE. None of these asks for
        // anything to be stored, and storing them would mean a private sentence
        // kept for ever because it happened to contain a word.
        var atoms = Extract(said);

        Assert.DoesNotContain(atoms, a => a.Kind == AtomKind.Fact);
    }

    [Fact]
    public void The_existing_cues_still_behave_as_they_did()
    {
        // The new rows sit above everything at higher confidence, so the guard
        // is that they have not quietly captured the sentences that already had
        // an owner.
        Assert.Equal(AtomKind.Preference, Assert.Single(Extract("I prefer tea to coffee")).Kind);
        Assert.Equal(AtomKind.Ruling,     Assert.Single(Extract("Never restart a device")).Kind);
    }
}
