#nullable enable

// CoverLetter.cs
//
// The cover-letter schema — the second document KIND, and like CvDocument it
// serves DOUBLE duty:
//
//   1. The typed input a template lays out into a PDF.
//   2. The JSON shape the on-device model fills in: the Neuron is asked to emit
//      JSON matching THIS schema, with IncludeReasoning=false, and the result is
//      deserialized straight into a CoverLetter. Model owns the words; template
//      owns the layout.
//
// A cover letter almost always travels WITH a CV, so the sender's contact block
// is deliberately the SAME CvContact type — one person, one contact shape,
// whether it heads a CV or signs a letter. Reusing it means the model fills one
// contact schema, not two that drift apart.
//
// Dates are STRINGS on purpose, exactly as in CvDocument: a letter prints
// "23 July 2026", a 0.6B model emits text, and parsing to DateTime would be
// brittle for zero layout benefit.

using System.Collections.Generic;

namespace CircleAI.Documents;

/// <summary>A complete cover letter, ready to lay out. Also the model's JSON target.</summary>
/// <param name="SenderName">The applicant's name, e.g. "Thabo Mokoena". Heads the letter and signs it.</param>
/// <param name="SenderContact">How to reach the sender — reuses the CV contact block on purpose.</param>
/// <param name="Date">Free-text date the letter is written, e.g. "23 July 2026".</param>
/// <param name="RecipientName">Person addressed, e.g. "Ms Nomsa Dlamini". Optional — omitted cleanly when absent.</param>
/// <param name="RecipientTitle">Recipient's role, e.g. "Hiring Manager". Optional.</param>
/// <param name="RecipientCompany">Company / organisation the letter is sent to.</param>
/// <param name="RecipientAddress">Optional postal / location line for the recipient block.</param>
/// <param name="Subject">Subject / role applied for, e.g. "Application for Junior Software Developer". Printed as the "Re:" line.</param>
/// <param name="Greeting">Salutation, e.g. "Dear Ms Dlamini,". Optional — a sensible default is derived when absent.</param>
/// <param name="Body">The letter's paragraphs, in order. Each entry is one paragraph.</param>
/// <param name="Closing">Sign-off, e.g. "Yours sincerely,". Optional — defaults to "Yours sincerely,".</param>
/// <param name="SignatureName">Typed signature under the sign-off. Optional — defaults to <see cref="SenderName"/>.</param>
public sealed record CoverLetter(
    string                SenderName,
    CvContact             SenderContact,
    string                Date,
    string?               RecipientName,
    string?               RecipientTitle,
    string                RecipientCompany,
    string?               RecipientAddress,
    string                Subject,
    string?               Greeting,
    IReadOnlyList<string> Body,
    string?               Closing       = null,
    string?               SignatureName = null)
{
    /// <summary>
    /// The greeting actually printed. If <see cref="Greeting"/> is set it wins;
    /// otherwise we address the named recipient, falling back to the neutral
    /// "Dear Sir or Madam," when no name is known. Kept on the model (not the
    /// template) so the default is one decision, testable in isolation.
    /// </summary>
    public string EffectiveGreeting =>
        !string.IsNullOrWhiteSpace(Greeting) ? Greeting!
        : !string.IsNullOrWhiteSpace(RecipientName) ? $"Dear {RecipientName},"
        : "Dear Sir or Madam,";

    /// <summary>The sign-off actually printed; defaults to "Yours sincerely,".</summary>
    public string EffectiveClosing =>
        string.IsNullOrWhiteSpace(Closing) ? "Yours sincerely," : Closing!;

    /// <summary>The typed signature actually printed; defaults to the sender's name.</summary>
    public string EffectiveSignature =>
        string.IsNullOrWhiteSpace(SignatureName) ? SenderName : SignatureName!;

    /// <summary>
    /// A safe, non-null cover letter built from only the essentials. This is the
    /// DETERMINISTIC FALLBACK, mirroring <see cref="CvDocument.Minimal"/>: if the
    /// model returns unparseable JSON, the host still produces a real PDF from the
    /// user's raw input rather than nothing. An artifact always comes out.
    /// </summary>
    public static CoverLetter Minimal(
        string sender, CvContact contact, string date, string company, string subject) =>
        new(sender, contact, date,
            RecipientName:    null,
            RecipientTitle:   null,
            RecipientCompany: company,
            RecipientAddress: null,
            Subject:          subject,
            Greeting:         null,
            Body:             System.Array.Empty<string>());

    /// <summary>
    /// A fully-populated example, for previews, tests and the "show me what this
    /// looks like" path. Deliberately in the same South African key as the CV
    /// sample so the two documents read as one applicant's pack.
    /// </summary>
    public static CoverLetter Sample() =>
        new(
            SenderName: "Thabo Mokoena",
            SenderContact: new CvContact(
                Email:    "thabo.mokoena@example.co.za",
                Phone:    "+27 82 555 0142",
                Location: "Soweto, Johannesburg"),
            Date: "23 July 2026",
            RecipientName:    "Ms Nomsa Dlamini",
            RecipientTitle:   "Hiring Manager",
            RecipientCompany: "Aurora Digital (Pty) Ltd",
            RecipientAddress: "12 Rivonia Road, Sandton, Johannesburg",
            Subject: "Application for Junior Software Developer",
            Greeting: null, // derived → "Dear Ms Dlamini,"
            Body: new[]
            {
                "I am writing to apply for the Junior Software Developer position advertised on your careers page. "
                + "Having recently completed my studies in software development and built several small applications "
                + "in my own time, I am eager to contribute to a team that ships real products to real people.",

                "During my studies I taught myself C# and built an offline note-taking app that runs entirely on a "
                + "low-end Android phone, without any cloud service. That project taught me to care about performance, "
                + "battery use and people who do not always have data — constraints I understand your team takes seriously.",

                "I would welcome the opportunity to discuss how I can add value to Aurora Digital. Thank you for "
                + "considering my application; I have attached my CV and am available for an interview at your convenience.",
            },
            Closing: "Yours sincerely,",
            SignatureName: "Thabo Mokoena");
}
