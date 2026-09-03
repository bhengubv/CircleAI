// ISpokenLanguage.cs
//
// Which language the assistant answers in.

namespace CircleAI.Samples.It;

/// <summary>Remembers the language of the conversation.</summary>
/// <remarks>
/// THE DISTINCTION BETWEEN Set AND Choose IS THE WHOLE POINT, and collapsing it
/// is a bug that has already shipped once.
/// <para>
/// Every turn reports what it HEARD, so a household that moves between languages
/// mid-conversation gets answered in the one being spoken. A person who goes to
/// the languages screen and picks one has DECIDED, and a later detection must not
/// quietly undo that. It did: Japanese was chosen, one English question was asked,
/// and the next Japanese turn resolved to English because the stored value had
/// been overwritten - while the screen still said Japanese.
/// </para>
/// </remarks>
public interface ISpokenLanguage
{
    /// <summary>The language of the last turn, or the default.</summary>
    string Current { get; }

    /// <summary>
    /// What this phone suggests, best first, for somebody who has not chosen.
    /// </summary>
    /// <remarks>
    /// SO THAT SHARED SCREENS STOP GUESSING ENGLISH. Career, Languages, Home and
    /// Translate each carried their own `?? "en"`, and the interpreter's pair was
    /// hard-coded to English-to-isiZulu in two files that had to agree. None of
    /// them can read a device locale - they are rendered by a browser as well as
    /// a phone - so the head hands them the answer instead.
    /// <para>
    /// Best first, and more than one, because a pair needs two: the person's own
    /// language and somebody else's. Always at least one entry.
    /// </para>
    /// </remarks>
    IReadOnlyList<string> Suggested { get; }

    /// <summary>The language a person picked, or null if they never did.</summary>
    string? Chosen { get; }

    /// <summary>
    /// Records an explicit choice, which detection will not overwrite.
    /// </summary>
    /// <remarks>
    /// Choose, not Set: this is a person deciding, and the next turn's detection
    /// must not quietly undo it.
    /// </remarks>
    void Choose(string tag);

    /// <summary>Forgets the choice, handing control back to detection.</summary>
    void ClearChoice();
}
