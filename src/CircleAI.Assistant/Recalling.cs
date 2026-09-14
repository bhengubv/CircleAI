// Recalling.cs
//
// Asking long-term memory what it knows, and putting it in front of the question.
//
// ONE OWNER, BECAUSE THERE ARE TWO DOORS. Somebody can say something to this app
// or type it, and both are the person talking. The WRITE side already knows that
// - Chat's own comment says "a memory wired only to the microphone remembers
// half a person" - but the READ side was wired only to the microphone, which is
// exactly the half it warned about. Typed "my name is Thabo" was learned and
// then typed "what is my name?" was asked cold.
//
// The recall and the preamble therefore live here rather than inline in the
// turn, so the two paths cannot drift into two different answers. The
// composition is pure and testable without a phone, a microphone or a model,
// which is the whole reason it is a separate function.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.Assistant;

/// <summary>Long-term memory, on the way in to an answer.</summary>
public static class Recalling
{
    /// <summary>How long recall may take before the answer goes on without it.</summary>
    /// <remarks>
    /// A BUDGET, NOT A TIMEOUT ON THE STORE. The store may be perfectly healthy
    /// and simply slower than the moment allows. Measured against the rest of a
    /// turn - seconds of transcription and thinking - a quarter of a second is
    /// affordable and a second is not. A remembered name is worth having and
    /// never worth making somebody wait for.
    /// </remarks>
    public static readonly TimeSpan Budget = TimeSpan.FromMilliseconds(250);

    /// <summary>How many facts may ride along. Small on purpose.</summary>
    /// <remarks>
    /// Every one of these is tokens the model must read before it reaches the
    /// actual question, on a phone that decodes about seven tokens a second. The
    /// session's history is bounded for the same reason - see CircleAISession - and a
    /// preamble that grows without bound would undo it.
    /// </remarks>
    public const int Keep = 4;

    /// <summary>
    /// What the store knows about this, or nothing — never an exception, never a wait.
    /// </summary>
    /// <remarks>
    /// SWALLOWS EVERYTHING, INCLUDING THE TIMEOUT. This sits directly in front of
    /// an answer somebody is waiting for; a store that could not answer must not
    /// fail a turn that otherwise works.
    /// </remarks>
    public static async Task<IReadOnlyList<Remembered>> AboutAsync(
        IRemembers? memory, string heard, CancellationToken ct = default)
    {
        if (memory is null || string.IsNullOrWhiteSpace(heard)) return [];

        try
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
            budget.CancelAfter(Budget);
            return await memory.RecallAsync(heard, Keep, budget.Token).ConfigureAwait(false);
        }
        catch
        {
            // Nothing remembered; the answer still happens.
            return [];
        }
    }

    /// <summary>The question, with what is already known put in front of it.</summary>
    /// <remarks>
    /// IN THE USER TURN, NOT THE SYSTEM PROMPT. The system prompt is cached
    /// across turns — see UsePrefixCache — and rewriting it every turn would
    /// throw that cache away, which is the 13 seconds of cold prefill this app
    /// spent a day removing. Carried on the user turn it costs only its own
    /// tokens.
    /// <para>
    /// Returns <paramref name="heard"/> UNCHANGED when nothing is known, rather
    /// than an empty heading. "Things you already know about them:" followed by
    /// nothing invites a small model to fill the gap, and inventing a fact about
    /// somebody is the one failure this whole feature must not cause.
    /// </para>
    /// </remarks>
    /// <summary>The question again, with the remembered facts taken back off.</summary>
    /// <remarks>
    /// RETRIEVAL IS NOT PROMPTING, AND ONE STRING WAS DOING BOTH. What
    /// <see cref="Ask"/> builds is what the MODEL should read. It was also being
    /// handed to the skill store and the RAG index as the thing to search for -
    /// so a phone that remembered "Never deploy with -t:Install on this phone,
    /// it wipes the models every time" searched 1,378 skills for those words
    /// along with the actual question. Measured on a P30 on 2026-09-13.
    /// <para>
    /// The facts are the least relevant possible search terms: they are about
    /// the PERSON, and what is being looked for is about the SUBJECT.
    /// </para>
    /// <para>
    /// HERE RATHER THAN IN THE HOSTING LAYER, because this file owns the format.
    /// CircleAI.Hosting cannot reference this assembly - the dependency runs the
    /// other way - and a layer that reconstructed this heading by hand would be
    /// a second owner of it, which is how the two drift.
    /// </para>
    /// <para>
    /// Anything that is not one of ours passes through unchanged, so a caller
    /// that never used <see cref="Ask"/> is unaffected.
    /// </para>
    /// </remarks>
    public static string Question(string? asked)
    {
        if (string.IsNullOrWhiteSpace(asked)) return asked ?? string.Empty;
        if (!asked.StartsWith(Heading, StringComparison.Ordinal)) return asked;

        // The question is the last paragraph - Ask separates it with a blank
        // line, which is the only blank line the composed form contains.
        var split = asked.IndexOf(Environment.NewLine + Environment.NewLine, StringComparison.Ordinal);
        if (split < 0) return asked;

        var question = asked[(split + (Environment.NewLine.Length * 2))..].Trim();

        // A composed prompt with nothing after the facts should not retrieve on
        // the facts; it should retrieve on nothing.
        return question;
    }

    /// <summary>The one line that marks a composed prompt, written once.</summary>
    private const string Heading = "Things you already know about them:";

    public static string Ask(string heard, IReadOnlyList<Remembered>? known)
    {
        if (known is null || known.Count == 0) return heard;

        var facts = known
            .Where(k => !string.IsNullOrWhiteSpace(k.Text))
            .Take(Keep)
            .Select(k => "- " + k.Text.Trim())
            .ToList();

        if (facts.Count == 0) return heard;

        return Heading + Environment.NewLine
             + string.Join(Environment.NewLine, facts)
             + Environment.NewLine + Environment.NewLine
             + heard;
    }
}
