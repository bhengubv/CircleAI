// OverviewQuestion.cs
//
// "What can you do?" - the one question an index cannot answer.
//
// IT HAS NO SUBJECT. Every word of it is a function word, so SearchTerms
// .Significant returns an empty list and there is nothing to look up. CircleAI's
// capability manifest only ever answered it by ACCIDENT: one of its nineteen
// entries contains the phrase word for word in its description and the store
// matched the whole query as a substring. A phone keyboard autocorrecting "do"
// to "doo" dropped it to nothing, and the model fell back to introducing itself
// as somebody else's product.
//
// AND THE MODEL WILL NOT RECITE IT EITHER. Told in its system prompt what this
// phone can do, a 0.8B on a P30 answered "I am here to help you! What can I do
// for you today?" - right identity, no false claims, and not one of the things
// it can actually do. The same prompt tells it to answer in one or two short
// sentences, so it gives a short one. The sentence was in front of it rather
// than said by it.
//
// So the host answers this one itself. The precedent is arithmetic, which this
// product already made deterministic in core for the same reason: a question
// with a known answer should not be a dice roll. Here the answer is a fact about
// the app, which the app owns and the model is only guessing at.
//
// WHAT THIS IS NOT. It is not a general intent classifier and must never grow
// into one. It is the narrowest rule that covers the question people actually
// ask, and everything else goes to the model as before.

namespace CircleAI.Core;

/// <summary>Recognises "what can you do?" and its close relatives.</summary>
public static class OverviewQuestion
{
    /// <summary>
    /// Words that make a subjectless question a question about CAPABILITY.
    /// </summary>
    /// <remarks>
    /// THE GUARD AGAINST ANSWERING THE WRONG QUESTION. "How are you" and "who
    /// are you" also reduce to zero significant terms, and replying to either
    /// with a list of features would be worse than the model's own answer. The
    /// persona covers who it is; this covers what it does.
    /// </remarks>
    private static readonly string[] Capability = ["do", "can"];

    /// <summary>Is this a question about what the assistant can do?</summary>
    /// <param name="question">The person's message, as typed or transcribed.</param>
    /// <remarks>
    /// TWO TESTS, BOTH REQUIRED. No significant terms, so there is genuinely
    /// nothing to retrieve on - that is what makes a canned answer the right
    /// answer rather than a shortcut. And a capability word, so the subjectless
    /// questions that are NOT about features keep going to the model.
    /// </remarks>
    public static bool Matches(string? question)
    {
        if (string.IsNullOrWhiteSpace(question)) return false;
        if (SearchTerms.Significant(question).Count > 0) return false;

        var words = question.Split(SearchTerms.Separators, StringSplitOptions.RemoveEmptyEntries);
        foreach (var word in words)
            foreach (var cue in Capability)
                if (string.Equals(word.Trim(), cue, StringComparison.OrdinalIgnoreCase))
                    return true;

        return false;
    }
}
