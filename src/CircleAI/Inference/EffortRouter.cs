// EffortRouter.cs
//
// HOW HARD TO THINK ABOUT THIS QUESTION, ON THIS DEVICE, FOR THIS PERSON.
//
// The plan's Step 1 names it: "a per-turn effort router decides quick (thinking off) or hard
// (medium with a budget), and learns from the person cancelling or asking again." A brain that
// thinks before every answer makes "what time is it" wait for nothing - on the P30 one factual
// question spent its whole budget thinking and never answered - and one that never thinks
// answers "why is the sky blue" off the top of its head. So each question is weighed, quick or
// hard, and the device decides what hard is worth:
//
//   quick                    thinking off, everywhere
//   hard, on a phone         medium, with a budget
//   hard, on a computer      xhigh, where a template takes reasoning_effort
//
// THE BUDGET IS TIME, MEASURED. Effort names how long a device will wait for a thought; the
// brain's own measured speed (BrainSpeed) turns that into tokens. The 2B on the P30 answers at
// 1.47 tokens a second, so a medium thought there is about 66 tokens - where a desktop at forty a
// second thinks for over a thousand. With no measurement yet the token ceiling stands alone.
//
// IT LEARNS, PER DEVICE, IN SELFMODEL. Cancelling an answer while it is being written says it
// took too long; asking the same thing again after an answer says the answer was not enough.
// Two of either, net, move that kind of question one step - fewer thoughts after cancels, more
// after re-asks - within what the device allows, and the record outlives the process.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using CircleAI.Core;

namespace CircleAI.Inference;

/// <summary>What a question is, for deciding how hard to think about it.</summary>
public enum TurnWeight
{
    /// <summary>Answered at once: a fact, a greeting, an instruction.</summary>
    Quick = 0,

    /// <summary>Worth thinking about first: a why, a comparison, a plan, a problem to work.</summary>
    Hard = 1,
}

/// <summary>What the person did with an answer that teaches the router.</summary>
public enum EffortSignal
{
    /// <summary>They withdrew the question while it was being answered: it took too long.</summary>
    Cancelled = 0,

    /// <summary>They asked the same thing again after it was answered: the answer was not enough.</summary>
    AskedAgain = 1,
}

/// <summary>The effort one question gets, and why, in a sentence that names no text.</summary>
/// <param name="Weight">Quick or hard.</param>
/// <param name="Effort">How hard the model thinks first.</param>
/// <param name="ThinkingBudget">The most tokens the thought may take; 0 when it does not think.</param>
/// <param name="Why">Why - for the log, never the question itself.</param>
public sealed record EffortChoice(TurnWeight Weight, ReasoningEffort Effort, int ThinkingBudget, string Why);

/// <summary>Chooses each question's reasoning effort, and learns from how the person answers it.</summary>
public sealed class EffortRouter
{
    private readonly Func<FormFactor> _form;
    private readonly Func<string?, double?> _speed;
    private readonly Func<CircleAI.SelfKnowledge.SelfModel?> _self;
    private FormFactor? _formRead;

    /// <param name="form">What kind of device this is; null reads it from the device once (DeviceContext).</param>
    /// <param name="speed">A model's measured decode speed here; null reads BrainSpeed.</param>
    /// <param name="self">The device's memory of itself; null reads SelfModel.Current each time.</param>
    public EffortRouter(Func<FormFactor>? form = null, Func<string?, double?>? speed = null,
        Func<CircleAI.SelfKnowledge.SelfModel?>? self = null)
    {
        _form = form ?? (() => DeviceContext.Read().FormFactor);
        _speed = speed ?? (model => string.IsNullOrWhiteSpace(model) ? null : BrainSpeed.MeasuredOf(model!));
        _self = self ?? (() => CircleAI.SelfKnowledge.SelfModel.Current);
    }

    /// <summary>The words that make a question hard, beside the reasoning cues the concierge already routes on.</summary>
    /// <remarks>
    /// CONSERVATIVE ON PURPOSE. A question weighed hard by mistake costs a phone the length of a
    /// thought before the first word - most of a minute on the P30 - and a quick one weighed
    /// quick by mistake costs a plainer answer, which a re-ask then teaches the router about.
    /// </remarks>
    private static readonly string[] HardCues =
    [
        "why ", "why?", "explain", "how does", "how do ", "how would", "compare", "difference between",
        "pros and cons", "work out", "figure out", "plan ", "a plan", "analyse", "analyze", "puzzle",
        "riddle", "what would happen", "what if", "should i", "reason through", "think through",
    ];

    /// <summary>Weighs a question: hard when it asks for reasoning, quick otherwise.</summary>
    /// <remarks>
    /// THE CONCIERGE'S REASONING CUES FIRST (HeuristicNeuronRouter): one list of what "wants
    /// reasoning" means, read by both, so the router that chooses a brain and the router that
    /// chooses its effort cannot disagree about the same sentence. Then its own cues, a long
    /// question (thirty words and more), or more than one question at once.
    /// </remarks>
    public static TurnWeight Weigh(string? question)
    {
        if (string.IsNullOrWhiteSpace(question)) return TurnWeight.Quick;
        var text = question.ToLowerInvariant();
        if (CircleAI.Hosting.Neuron.HeuristicNeuronRouter.ReasoningCues.Any(c => text.Contains(c, StringComparison.Ordinal)))
            return TurnWeight.Hard;
        if (HardCues.Any(c => text.Contains(c, StringComparison.Ordinal))) return TurnWeight.Hard;
        if (Words(text).Count >= 30) return TurnWeight.Hard;
        if (text.Count(c => c == '?') >= 2) return TurnWeight.Hard;
        return TurnWeight.Quick;
    }

    /// <summary>The effort for one question, on the brain <paramref name="modelId"/>.</summary>
    /// <param name="question">The question alone, as the person asked it.</param>
    /// <param name="modelId">The brain that will answer, for its measured speed; null when unknown.</param>
    public EffortChoice Choose(string? question, string? modelId = null)
    {
        var weight = Weigh(question);
        var form = Form();
        var basis = Basis(weight, form);
        var shift = 0;
        try { shift = _self()?.EffortShift(weight) ?? 0; }
        catch { /* a memory that will not answer leaves the default */ }

        var effort = (ReasoningEffort)Math.Clamp((int)basis + shift, (int)ReasoningEffort.Off, (int)Ceiling(form));
        double? speed = null;
        try { speed = _speed(modelId); }
        catch { /* unknown speed: the token ceiling stands alone */ }
        var budget = Budget(effort, speed);

        // Numbers in the invariant culture: this PC writes a decimal comma otherwise.
        var thinkingPart = budget <= 0 ? string.Empty
            : string.Create(CultureInfo.InvariantCulture, $", up to {budget} thinking tokens")
              + (speed is > 0 ? string.Create(CultureInfo.InvariantCulture, $" at {speed:F1} tok/s") : string.Empty);
        var learnedPart = shift == 0 ? string.Empty
            : string.Create(CultureInfo.InvariantCulture, $" ({(shift > 0 ? "+" : string.Empty)}{shift} learned here)");
        var why = $"{weight.ToString().ToLowerInvariant()} on a {form.ToString().ToLowerInvariant()}: {effort.TemplateName()}"
                  + thinkingPart + learnedPart;
        return new EffortChoice(weight, effort, budget, why);
    }

    /// <summary>Records what the person did with an answer given <paramref name="choice"/>, on this device.</summary>
    public void Learn(EffortChoice choice, EffortSignal signal)
    {
        ArgumentNullException.ThrowIfNull(choice);
        try { _self()?.LearnEffort(choice.Weight, signal, choice.Effort); }
        catch { /* a lost lesson costs one step of learning, never the answer */ }
    }

    /// <summary>
    /// Whether <paramref name="next"/> asks the same thing again as <paramref name="previous"/>:
    /// the same words once case and punctuation are gone, or most of them in common.
    /// </summary>
    /// <remarks>
    /// SAID AGAIN, NOT MERELY ON THE SAME SUBJECT. Two thirds of the words in common, over the
    /// longer of the two, is a repetition with a word changed ("why is the sky blue" -> "but why
    /// is the sky blue"), where a follow-up shares a subject and little else.
    /// </remarks>
    public static bool Again(string? previous, string? next)
    {
        var a = Words(previous);
        var b = Words(next);
        if (a.Count == 0 || b.Count == 0) return false;
        if (a.SequenceEqual(b)) return true;
        if (Math.Min(a.Count, b.Count) < 3) return false;
        var shared = a.Intersect(b, StringComparer.Ordinal).Count();
        return shared * 3 >= Math.Max(a.Distinct().Count(), b.Distinct().Count()) * 2;
    }

    /// <summary>The effort a device gives a question before anything has been learned.</summary>
    public static ReasoningEffort Basis(TurnWeight weight, FormFactor form) =>
        weight == TurnWeight.Quick ? ReasoningEffort.Off
        : IsComputer(form) ? ReasoningEffort.XHigh
        : form == FormFactor.Watch ? ReasoningEffort.Low
        : ReasoningEffort.Medium;

    /// <summary>The most a device's learning may raise an effort to.</summary>
    public static ReasoningEffort Ceiling(FormFactor form) =>
        IsComputer(form) ? ReasoningEffort.XHigh
        : form == FormFactor.Watch ? ReasoningEffort.Low
        : ReasoningEffort.High;

    /// <summary>
    /// How many tokens a thought at <paramref name="effort"/> may take, at the brain's measured
    /// <paramref name="tokensPerSecond"/> when it is known.
    /// </summary>
    /// <remarks>
    /// EACH LEVEL IS A WAIT: a quarter of a minute, three quarters, two minutes, five - and a
    /// ceiling in tokens for when the speed is unknown or high. Never fewer than 32, so a slow
    /// device still gets a thought worth having.
    /// </remarks>
    public static int Budget(ReasoningEffort effort, double? tokensPerSecond)
    {
        var (seconds, ceiling) = effort switch
        {
            ReasoningEffort.Low    => (15, 256),
            ReasoningEffort.Medium => (45, 1024),
            ReasoningEffort.High   => (120, 4096),
            ReasoningEffort.XHigh  => (300, 16384),
            _                      => (0, 0),
        };
        if (ceiling == 0) return 0;
        if (tokensPerSecond is not > 0) return ceiling;
        return Math.Clamp((int)(seconds * tokensPerSecond.Value), 32, ceiling);
    }

    private static bool IsComputer(FormFactor form) => form is FormFactor.Laptop or FormFactor.Desktop or FormFactor.Server;

    private FormFactor Form()
    {
        if (_formRead is { } known) return known;
        FormFactor form;
        try { form = _form(); }
        catch { form = FormFactor.Unknown; }
        _formRead = form;
        return form;
    }

    private static List<string> Words(string? text) =>
        string.IsNullOrWhiteSpace(text)
            ? []
            : text.ToLowerInvariant()
                  .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                  .Select(w => new string(w.Where(char.IsLetterOrDigit).ToArray()))
                  .Where(w => w.Length > 0)
                  .ToList();
}
