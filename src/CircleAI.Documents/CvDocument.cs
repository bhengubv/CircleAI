#nullable enable

// CvDocument.cs
//
// The canonical CV schema — and the single most important type in this library,
// because it serves DOUBLE duty:
//
//   1. The typed input a template lays out into a PDF.
//   2. The JSON shape the on-device model fills in (Slice 1b): the Neuron is
//      asked to emit JSON matching THIS schema, with IncludeReasoning=false, and
//      the result is deserialized straight into a CvDocument. Model owns the
//      words; template owns the layout.
//
// One schema for both directions means a future "import my old CV" parse path
// (docling-style) and the generation path share the same model, not two that
// drift apart.
//
// Dates are STRINGS on purpose. A CV shows "Jan 2023 – Present", not a DateTime,
// and a 0.6B model emits text; parsing every date into DateTime would be brittle
// for zero layout benefit. EndDate == null means "Present".

using System.Collections.Generic;

namespace CircleAI.Documents;

/// <summary>A complete CV, ready to lay out. Also the model's JSON target.</summary>
/// <param name="FullName">e.g. "Thabo Mokoena".</param>
/// <param name="Headline">Target role / professional title, e.g. "Junior Software Developer".</param>
/// <param name="Contact">How to reach the person.</param>
/// <param name="Summary">A short professional summary. Optional — omitted cleanly when absent.</param>
/// <param name="Experience">Work history, most recent first.</param>
/// <param name="Education">Qualifications, most recent first.</param>
/// <param name="Skills">Flat skill list; the template groups/wraps them.</param>
/// <param name="Certifications">Optional certifications.</param>
/// <param name="Languages">Optional spoken languages (relevant in a multilingual market).</param>
public sealed record CvDocument(
    string                          FullName,
    string                          Headline,
    CvContact                       Contact,
    string?                         Summary,
    IReadOnlyList<CvExperience>     Experience,
    IReadOnlyList<CvEducation>      Education,
    IReadOnlyList<string>           Skills,
    IReadOnlyList<CvCertification>? Certifications = null,
    IReadOnlyList<string>?          Languages      = null)
{
    /// <summary>
    /// A safe, non-null CV built from only the essentials. This is the
    /// DETERMINISTIC FALLBACK: if the model returns unparseable JSON, the host
    /// still produces a real PDF from the user's raw input rather than nothing.
    /// "Produces and does" — an artifact always comes out.
    /// </summary>
    public static CvDocument Minimal(string fullName, string headline, CvContact contact) =>
        new(fullName, headline, contact,
            Summary: null,
            Experience: System.Array.Empty<CvExperience>(),
            Education:  System.Array.Empty<CvEducation>(),
            Skills:     System.Array.Empty<string>());
}

/// <summary>Contact block for the CV header.</summary>
/// <param name="Email">Optional.</param>
/// <param name="Phone">Optional.</param>
/// <param name="Location">e.g. "Soweto, Johannesburg". Optional.</param>
/// <param name="Links">Optional profile/portfolio links (LinkedIn, GitHub, …).</param>
public sealed record CvContact(
    string?                Email    = null,
    string?                Phone    = null,
    string?                Location = null,
    IReadOnlyList<string>? Links    = null);

/// <summary>One role in the work history.</summary>
/// <param name="Title">Job title, e.g. "Retail Assistant".</param>
/// <param name="Organisation">Employer.</param>
/// <param name="Location">Optional.</param>
/// <param name="StartDate">Free text, e.g. "Feb 2022".</param>
/// <param name="EndDate">Free text, or <c>null</c> for "Present".</param>
/// <param name="Highlights">Bullet points — what was done/achieved, ideally quantified.</param>
public sealed record CvExperience(
    string                Title,
    string                Organisation,
    string?               Location,
    string                StartDate,
    string?               EndDate,
    IReadOnlyList<string> Highlights);

/// <summary>One qualification.</summary>
/// <param name="Qualification">e.g. "National Senior Certificate", "BSc Computer Science".</param>
/// <param name="Institution">School / university / provider.</param>
/// <param name="Location">Optional.</param>
/// <param name="StartDate">Optional free text.</param>
/// <param name="EndDate">Optional free text, or <c>null</c> for in-progress.</param>
/// <param name="Notes">Optional — distinctions, relevant modules, etc.</param>
public sealed record CvEducation(
    string  Qualification,
    string  Institution,
    string? Location  = null,
    string? StartDate = null,
    string? EndDate   = null,
    string? Notes     = null);

/// <summary>One certification.</summary>
/// <param name="Name">e.g. "Microsoft Certified: Azure Fundamentals".</param>
/// <param name="Issuer">Optional.</param>
/// <param name="Year">Optional free text.</param>
public sealed record CvCertification(
    string  Name,
    string? Issuer = null,
    string? Year   = null);
