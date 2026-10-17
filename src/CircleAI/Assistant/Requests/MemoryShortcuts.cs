// MemoryShortcuts.cs
//
// "REMEMBER THAT..." IS AN INSTRUCTION, AND SAVING IS NOT SOMETHING A MODEL CAN SAY IT DID.
//
// On the P30 on 2026-10-17 "Remember that my clinic appointment moved to Friday" went to
// the 0.8B, which answered "I can't remember specific appointments, but I can tell you
// what happened: it was a Friday" - and nothing anywhere wrote it down. The phone has had
// one memory for every app since the service took it over (CircleNeuronLinkService.Memory);
// the request path simply never used it. These write it down, say so only once it is
// written, and answer from it when asked.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using CircleAI.Memory;

namespace CircleAI.Assistant.Requests;

/// <summary>Writes down what somebody asks it to remember.</summary>
/// <param name="memoryFor">The memory for a caller key: the device's, or null when there is none.</param>
/// <param name="wakePhrase">The phrase the phone listens for, so "Hey B, remember..." is still an instruction.</param>
public sealed partial class RememberShortcut(Func<string, IMemoryService?> memoryFor, Func<string?>? wakePhrase = null) : IAskShortcut
{
    /// <inheritdoc />
    public string Name => "remember";

    // "Remember that X", "Please remember X", "Don't forget that X", "Note that X".
    // "Note" needs its "that": "note to self" is not a fact.
    [GeneratedRegex(@"^(?:please\s+)?(?:(?:remember|don['’]?t\s+forget|do\s+not\s+forget)(?:\s+that)?|(?:note|make\s+a\s+note)\s+that)\s+(?<fact>.+?)[\s.!]*$",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex Instruction();

    // "Remember when we..." is somebody reminiscing, and "remember to..." asks for a
    // reminder, which this is not: neither is a fact to write down.
    private static readonly string[] NotAFact = ["to", "when", "how", "what", "where", "who", "why", "if", "whether"];

    /// <summary>The fact in an instruction to remember it, as it was said, or null.</summary>
    public static string? FactIn(string? said)
    {
        var text = said?.Trim() ?? string.Empty;
        if (text.Length == 0 || text.EndsWith('?')) return null;   // "do you remember...?" is a question

        var m = Instruction().Match(text);
        if (!m.Success) return null;

        var fact = m.Groups["fact"].Value.Trim();
        var words = Said.Words(fact);
        if (words.Length < 2 || fact.Length > 300) return null;
        if (NotAFact.Contains(words[0], StringComparer.OrdinalIgnoreCase)) return null;
        return fact;
    }

    /// <inheritdoc />
    public async ValueTask<string?> TryAnswerAsync(AskRecord ask, CancellationToken ct)
    {
        string? phrase = null;
        try { phrase = wakePhrase?.Invoke(); } catch { /* the shortcut still works without it */ }

        var fact = FactIn(Said.WithoutWakePhrase(ask.Asked, phrase));
        if (fact is null) return null;

        IMemoryService? memory = null;
        try { memory = memoryFor(ask.CallerKey); } catch { /* answered below */ }
        if (memory is null) return AssistantPersona.CouldNotRemember;

        try
        {
            // AS IT WAS SAID, in the first person: it is the person's own sentence, and
            // RecallShortcut turns it round again when it is read back.
            var written = char.ToUpperInvariant(fact[0]) + fact[1..].TrimEnd('.', '!', ' ') + ".";
            await memory.RememberAsync(new MemoryAtom { Kind = AtomKind.Fact, Text = written }, ct: ct).ConfigureAwait(false);
        }
        catch (System.OperationCanceledException) { throw; }
        catch { return AssistantPersona.CouldNotRemember; }

        return AssistantPersona.WillRemember(Said.Lead(Said.ToSecondPerson(fact.TrimEnd('.', '!', ' '))));
    }
}

/// <summary>Answers a question about something it was told, from what it wrote down.</summary>
/// <remarks>
/// NARROW ON PURPOSE. It answers only a question about the person ("my", "did I tell
/// you") that shares its subject with one remembered fact - two words of it, or every word
/// when the question has one. Everything else, "How do I plant maize?" included, goes to
/// the model as before.
/// </remarks>
/// <param name="memoryFor">The memory for a caller key: the device's, or null when there is none.</param>
/// <param name="wakePhrase">The phrase the phone listens for.</param>
public sealed partial class RecallShortcut(Func<string, IMemoryService?> memoryFor, Func<string?>? wakePhrase = null) : IAskShortcut
{
    /// <inheritdoc />
    public string Name => "recall";

    [GeneratedRegex(@"\b(?:my|mine)\b|\b(?:did|have)\s+i\s+(?:tell|told|say|said|mention|mentioned)\b|\babout\s+me\b",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AboutThem();

    [GeneratedRegex(@"^(?:how\s+(?:do|can|should|would|could|will)\s+i\b|how\s+to\b)",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HowTo();

    [GeneratedRegex(@"^(?:what|what's|when|where|who|whom|whose|which|why|how|is|are|was|were|do|does|did|can|could|will|would|should|have|has|had)\b",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Asking();

    /// <inheritdoc />
    public async ValueTask<string?> TryAnswerAsync(AskRecord ask, CancellationToken ct)
    {
        string? phrase = null;
        try { phrase = wakePhrase?.Invoke(); } catch { /* the shortcut still works without it */ }

        var said = Said.WithoutWakePhrase(ask.Asked, phrase);
        if (said.Length == 0 || RememberShortcut.FactIn(said) is not null) return null;
        if (!said.EndsWith('?') && !Asking().IsMatch(said)) return null;
        if (!AboutThem().IsMatch(said) || HowTo().IsMatch(said)) return null;

        var terms = CircleAI.Core.SearchTerms.Significant(said)
            .Select(t => t.ToLowerInvariant()).Distinct().ToList();
        if (terms.Count == 0) return null;

        IMemoryService? memory = null;
        try { memory = memoryFor(ask.CallerKey); } catch { /* the model answers instead */ }
        if (memory is null) return null;

        RecallResult found;
        try
        {
            found = await memory.RecallAsync(new Situation(Text: said), new RecallBudget(MaxAtoms: 5), ct).ConfigureAwait(false);
        }
        catch (System.OperationCanceledException) { throw; }
        catch { return null; }   // a memory that cannot be read costs this shortcut, not the answer

        var need = Math.Min(2, terms.Count);
        MemoryAtom? best = null;
        var bestShared = 0;
        foreach (var atom in found.Atoms)
        {
            if (!atom.IsCurrent || string.IsNullOrWhiteSpace(atom.Text)) continue;
            var words = Said.Words(atom.Text);
            var shared = terms.Count(t => words.Any(w => Said.SameWord(w, t)));
            if (shared >= need && shared > bestShared) { best = atom; bestShared = shared; }
        }

        return best is null
            ? null
            : AssistantPersona.YouToldMe(Said.Lead(Said.ToSecondPerson(best.Text.Trim().TrimEnd('.', '!', ' '))));
    }
}
