// ICareerInterview.cs
//
// The CV interview: one question at a time, and the document it builds.

namespace CircleAI.Samples.It;

/// <summary>Where the interview has got to.</summary>
/// <param name="Question">What to ask now, or the closing line when done.</param>
/// <param name="Why">
/// Why it is being asked. Anything that asks about your employment deserves a
/// reason.
/// </param>
/// <param name="Verify">
/// Whether the answer must be read back before it is kept - names, numbers and
/// places, where mishearing does real damage. A mis-heard surname is worse than a
/// blank one.
/// </param>
/// <param name="Done">
/// True when the script is finished. NOT the end of the screen: the document is
/// the point, so the screen stays on it and offers what comes next.
/// </param>
public sealed record CareerStep(string Question, string Why, bool Verify, bool Done);

/// <summary>How a line of the rendered CV should be set.</summary>
public enum CvLineKind
{
    /// <summary>The person's name, at the top.</summary>
    Name,

    /// <summary>The one-line headline under the name.</summary>
    Headline,

    /// <summary>Contact details, or any small print.</summary>
    Small,

    /// <summary>A section heading - WORK, SKILLS, EDUCATION.</summary>
    Section,

    /// <summary>A job title or qualification.</summary>
    Entry,

    /// <summary>Ordinary text.</summary>
    Body,

    /// <summary>Vertical space between sections.</summary>
    Gap,
}

/// <summary>One line of the CV preview.</summary>
public sealed record CvLine(string Text, CvLineKind Kind);

/// <summary>Runs the CV interview and renders the document it produces.</summary>
public interface ICareerInterview
{
    /// <summary>The question to ask now.</summary>
    Task<CareerStep> StepAsync(CancellationToken ct = default);

    /// <summary>
    /// Store an answer and move on. A declined answer advances without storing.
    /// </summary>
    Task AnswerAsync(string text, CancellationToken ct = default);

    /// <summary>The CV as it currently stands, ready to lay out.</summary>
    Task<IReadOnlyList<CvLine>> PreviewAsync(CancellationToken ct = default);

    /// <summary>How far along the CV is, in the screen's own words.</summary>
    Task<string> ProgressAsync(CancellationToken ct = default);

    /// <summary>Write the CV out, and say where it went.</summary>
    Task<string> SaveAsync(CancellationToken ct = default);
}
