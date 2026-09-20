// LinkVerbs.cs
//
// The structured verbs a linked app can call beyond chat, and the scope each one
// needs. Kept next to the grant + turn types, portable and desktop-tested, so the
// client library and the CircleAI service share ONE definition of what a verb is
// and what it costs in trust.
//
// CHAT IS THE FLOOR; THESE ARE OPT-IN. Recall and remember reach the person's
// long-term memory (LinkScope.Memory); skills reach the full library
// (LinkScope.Skills). Discovery is the exception — it reads the honest capability
// manifest, which is the same for everyone and carries nothing private, so it
// rides the chat floor. The scope a verb needs is declared HERE, in one place, so
// the service can never under-ask by accident.

using System;
using System.Collections.Generic;

namespace CircleAI.Linking;

/// <summary>A structured request a linked app makes of the shared brain, beyond a chat turn.</summary>
public enum LinkVerb
{
    /// <summary>Read the person's long-term memory for a situation. Needs <see cref="LinkScope.Memory"/>.</summary>
    Recall,

    /// <summary>Write a fact into the person's long-term memory. Needs <see cref="LinkScope.Memory"/>.</summary>
    Remember,

    /// <summary>Search or list the skill library. Needs <see cref="LinkScope.Skills"/>.</summary>
    Skills,

    /// <summary>List what Circle AI can do (the honest manifest). The chat floor is enough.</summary>
    Capabilities,
}

/// <summary>One structured call across the link. Flat and string-shaped for the Bundle hop.</summary>
/// <param name="Verb">Which verb.</param>
/// <param name="Query">Recall: the situation text. Skills: the search text (null lists all).</param>
/// <param name="Text">Remember: the fact to store.</param>
/// <param name="Subject">Remember: the situation key to file it under (optional).</param>
/// <param name="Limit">Recall: the most atoms to return.</param>
public sealed record LinkVerbRequest(
    LinkVerb Verb,
    string? Query = null,
    string? Text = null,
    string? Subject = null,
    int Limit = 5);

/// <summary>A row-shaped answer: zero or more rows, each a few string fields.</summary>
/// <remarks>
/// One reply type for every verb, so the wire has one shape to marshal and test.
/// The columns depend on the verb: recall = [text]; skills = [id, name];
/// capabilities = [id, status, summary]; remember returns Ok with no rows.
/// </remarks>
public sealed record LinkRowsReply(bool Ok, IReadOnlyList<IReadOnlyList<string>> Rows, string? Error)
{
    /// <summary>A successful reply carrying <paramref name="rows"/> (possibly empty).</summary>
    public static LinkRowsReply Success(IReadOnlyList<IReadOnlyList<string>> rows) => new(true, rows, null);

    /// <summary>A failed reply. <paramref name="error"/> says why.</summary>
    public static LinkRowsReply Failure(string error) =>
        new(false, Array.Empty<IReadOnlyList<string>>(), error);
}

/// <summary>Where a verb's required scope is decided — once, so the service can't under-ask.</summary>
public static class LinkVerbs
{
    /// <summary>The scope a caller must hold for <paramref name="verb"/>.</summary>
    public static LinkScope RequiredScope(LinkVerb verb) => verb switch
    {
        LinkVerb.Recall       => LinkScope.Memory,
        LinkVerb.Remember     => LinkScope.Memory,
        LinkVerb.Skills       => LinkScope.Skills,
        LinkVerb.Capabilities => LinkScope.Chat,   // the manifest is not private
        _                     => LinkScope.Chat,
    };
}
