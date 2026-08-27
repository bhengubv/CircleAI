// Situation.cs
//
// What is about to happen, described well enough to look it up.
//
// THIS IS THE WHOLE DIFFERENCE between a memory that helps and one that does
// not. Loading everything at the start of a conversation puts the rules
// furthest from the moment they apply: an hour and forty tool calls later,
// nothing read at the greeting is meaningfully present, and no amount of
// emphasis in the file changes that.
//
// So recall is keyed on the action rather than the session. Before a deploy,
// ask what is known about deploying. The subject of the action is matched
// against the subject of the atom, which is a lookup rather than a guess.

using System;
using System.Collections.Generic;
using System.Linq;

namespace CircleAI.Memory;

/// <summary>The action about to be taken, as something the store can answer.</summary>
/// <param name="Verb">What is being done - "deploy", "install", "translate".</param>
/// <param name="Target">What it is being done to - "android/p30", "settings-screen".</param>
/// <param name="Tool">The mechanism, when it matters - "shell", "adb", "dotnet".</param>
/// <param name="Text">
/// Anything else worth searching on: the command, the question, the file. Used
/// for keyword matching when the subject alone is too coarse.
/// </param>
public sealed record Situation(
    string? Verb = null,
    string? Target = null,
    string? Tool = null,
    string? Text = null)
{
    /// <summary>
    /// The subject key an atom is filed under.
    /// </summary>
    /// <remarks>
    /// Deliberately coarse - "deploy:android" rather than the exact command.
    /// A key that includes every argument matches nothing twice, which is the
    /// same as having no index at all.
    /// </remarks>
    public string Key => string.Join(":", new[] { Verb, Target }
        .Where(p => !string.IsNullOrWhiteSpace(p))
        .Select(p => p!.Trim().ToLowerInvariant()));

    /// <summary>
    /// Progressively shorter keys, most specific first.
    /// </summary>
    /// <remarks>
    /// "deploy:android/p30" should also find atoms filed under "deploy:android"
    /// and under "deploy". Without this an atom filed one level up is invisible
    /// to the situation it was written for, which is how a store ends up full of
    /// things nobody ever sees again.
    /// </remarks>
    public IReadOnlyList<string> Keys
    {
        get
        {
            var keys = new List<string>();
            var verb = Verb?.Trim().ToLowerInvariant();
            var target = Target?.Trim().ToLowerInvariant();

            if (!string.IsNullOrEmpty(verb) && !string.IsNullOrEmpty(target))
            {
                keys.Add($"{verb}:{target}");

                // Walk up a slash-delimited target: android/p30 -> android.
                var cut = target.LastIndexOf('/');
                while (cut > 0)
                {
                    target = target[..cut];
                    keys.Add($"{verb}:{target}");
                    cut = target.LastIndexOf('/');
                }
            }

            if (!string.IsNullOrEmpty(verb)) keys.Add(verb);
            return keys;
        }
    }

    /// <summary>Everything worth matching on, as one search string.</summary>
    public string Query => string.Join(" ", new[] { Verb, Target, Tool, Text }
        .Where(p => !string.IsNullOrWhiteSpace(p))
        .Select(p => p!.Trim()));

    /// <summary>Whether there is anything here to look up at all.</summary>
    public bool IsEmpty => Keys.Count == 0 && string.IsNullOrWhiteSpace(Text);
}

/// <summary>What recall found, already inside its budget.</summary>
/// <param name="Atoms">
/// What to put in front of the agent, best first. Rulings, facts and
/// preferences - the things that can be quoted.
/// </param>
/// <param name="Tone">
/// Relationship atoms. Deliberately separate, because these shape how to
/// answer rather than what to say, and quoting somebody's own manner back at
/// them is not recall, it is impertinence.
/// </param>
/// <param name="Considered">How many current atoms were looked at, for tracing.</param>
public sealed record RecallResult(
    IReadOnlyList<MemoryAtom> Atoms,
    IReadOnlyList<MemoryAtom> Tone,
    int Considered)
{
    /// <summary>Nothing known about this.</summary>
    public static RecallResult Empty { get; } = new(Array.Empty<MemoryAtom>(), Array.Empty<MemoryAtom>(), 0);

    /// <summary>Whether anything came back worth showing.</summary>
    public bool Any => Atoms.Count > 0;
}

/// <summary>How much recall is allowed to cost.</summary>
/// <param name="MaxAtoms">Hard cap on items returned.</param>
/// <param name="MaxCharacters">
/// Hard cap on total text. On a phone the context window is the scarcest thing
/// in the building, and a memory that floods it defeats its own purpose.
/// </param>
public sealed record RecallBudget(int MaxAtoms = 5, int MaxCharacters = 600)
{
    /// <summary>The default: small enough to sit in front of every action.</summary>
    public static RecallBudget Default { get; } = new();
}
