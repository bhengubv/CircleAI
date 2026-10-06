#nullable enable

// SingleColumnCvTemplate.cs
//
// Turns a CvDocument (content) into a MigraDoc Document (layout). Single-column,
// standard headings, real selectable text — deliberately ATS-friendly (the free
// resume-maker lesson): the machines that scan CVs before a human ever sees them
// choke on multi-column and text-in-images. Plain and parseable beats pretty.
//
// This owns LAYOUT ONLY. Every word here came from the user or the model; the
// template never invents content.

using System.Collections.Generic;
using MigraDoc.DocumentObjectModel;

namespace CircleAI.Documents;

/// <summary>The one CV template for v1: single column, ATS-friendly.</summary>
internal static class SingleColumnCvTemplate
{
    /// <summary>Template id, surfaced via <see cref="IDocumentEngine.AvailableTemplates"/>.</summary>
    public const string Id = "single-column";

    public static Document Build(CvDocument cv)
    {
        var doc = new Document();

        var normal = doc.Styles["Normal"]!;
        normal.Font.Name = EmbeddedFontResolver.FamilyName;
        normal.Font.Size = 10;
        normal.ParagraphFormat.SpaceAfter = Unit.FromPoint(2);

        // MigraDoc forbids mutating DefaultPageSetup — page size and margins go
        // on the SECTION's own PageSetup (which starts as a copy of the default).
        var section = doc.AddSection();
        section.PageSetup.PageFormat   = PageFormat.A4;
        section.PageSetup.TopMargin    = Unit.FromCentimeter(1.6);
        section.PageSetup.BottomMargin = Unit.FromCentimeter(1.6);
        section.PageSetup.LeftMargin   = Unit.FromCentimeter(1.8);
        section.PageSetup.RightMargin  = Unit.FromCentimeter(1.8);

        AddHeader(section, cv);
        AddSummary(section, cv);
        AddExperience(section, cv);
        AddEducation(section, cv);
        AddSkills(section, cv);
        AddCertifications(section, cv);
        AddLanguages(section, cv);

        return doc;
    }

    // ── header ───────────────────────────────────────────────────────────────

    private static void AddHeader(Section section, CvDocument cv)
    {
        var name = section.AddParagraph(cv.FullName);
        name.Format.Font.Size = 22;
        name.Format.Font.Bold = true;
        name.Format.SpaceAfter = Unit.FromPoint(1);

        if (!string.IsNullOrWhiteSpace(cv.Headline))
        {
            var headline = section.AddParagraph(cv.Headline);
            headline.Format.Font.Size = 12;
            headline.Format.SpaceAfter = Unit.FromPoint(4);
        }

        var contactBits = new List<string>();
        if (!string.IsNullOrWhiteSpace(cv.Contact.Location)) contactBits.Add(cv.Contact.Location!);
        if (!string.IsNullOrWhiteSpace(cv.Contact.Phone))    contactBits.Add(cv.Contact.Phone!);
        if (!string.IsNullOrWhiteSpace(cv.Contact.Email))    contactBits.Add(cv.Contact.Email!);
        if (cv.Contact.Links is { Count: > 0 }) contactBits.AddRange(cv.Contact.Links);

        if (contactBits.Count > 0)
        {
            var contact = section.AddParagraph(string.Join("  •  ", contactBits));
            contact.Format.Font.Size = 9;
            contact.Format.SpaceAfter = Unit.FromPoint(8);
        }
    }

    // ── sections ───────────────────────────────────────────────────────────────

    private static void AddSummary(Section section, CvDocument cv)
    {
        if (string.IsNullOrWhiteSpace(cv.Summary)) return;
        Heading(section, "Summary");
        section.AddParagraph(cv.Summary!.Trim());
    }

    private static void AddExperience(Section section, CvDocument cv)
    {
        if (cv.Experience is not { Count: > 0 }) return;
        Heading(section, "Experience");

        foreach (var role in cv.Experience)
        {
            var line = section.AddParagraph();
            line.Format.SpaceBefore = Unit.FromPoint(4);
            line.AddFormattedText(role.Title, TextFormat.Bold);
            if (!string.IsNullOrWhiteSpace(role.Organisation))
                line.AddText($" — {role.Organisation}");

            var period = $"{role.StartDate} – {(string.IsNullOrWhiteSpace(role.EndDate) ? "Present" : role.EndDate)}";
            if (!string.IsNullOrWhiteSpace(role.Location)) period += $"  •  {role.Location}";
            var meta = section.AddParagraph(period);
            meta.Format.Font.Size = 9;
            meta.Format.Font.Italic = true;
            meta.Format.SpaceAfter = Unit.FromPoint(2);

            foreach (var bullet in role.Highlights)
                Bullet(section, bullet);
        }
    }

    private static void AddEducation(Section section, CvDocument cv)
    {
        if (cv.Education is not { Count: > 0 }) return;
        Heading(section, "Education");

        foreach (var ed in cv.Education)
        {
            var line = section.AddParagraph();
            line.Format.SpaceBefore = Unit.FromPoint(3);
            line.AddFormattedText(ed.Qualification, TextFormat.Bold);
            if (!string.IsNullOrWhiteSpace(ed.Institution))
                line.AddText($" — {ed.Institution}");

            var bits = new List<string>();
            if (!string.IsNullOrWhiteSpace(ed.StartDate) || !string.IsNullOrWhiteSpace(ed.EndDate))
                bits.Add($"{ed.StartDate} – {(string.IsNullOrWhiteSpace(ed.EndDate) ? "in progress" : ed.EndDate)}".Trim(' ', '–'));
            if (!string.IsNullOrWhiteSpace(ed.Location)) bits.Add(ed.Location!);
            if (bits.Count > 0)
            {
                var meta = section.AddParagraph(string.Join("  •  ", bits));
                meta.Format.Font.Size = 9;
                meta.Format.Font.Italic = true;
            }

            if (!string.IsNullOrWhiteSpace(ed.Notes))
                section.AddParagraph(ed.Notes!.Trim());
        }
    }

    private static void AddSkills(Section section, CvDocument cv)
    {
        if (cv.Skills is not { Count: > 0 }) return;
        Heading(section, "Skills");
        section.AddParagraph(string.Join("  •  ", cv.Skills));
    }

    private static void AddCertifications(Section section, CvDocument cv)
    {
        if (cv.Certifications is not { Count: > 0 }) return;
        Heading(section, "Certifications");
        foreach (var cert in cv.Certifications)
        {
            var bits = new List<string> { cert.Name };
            if (!string.IsNullOrWhiteSpace(cert.Issuer)) bits.Add(cert.Issuer!);
            if (!string.IsNullOrWhiteSpace(cert.Year))   bits.Add(cert.Year!);
            Bullet(section, string.Join(" — ", bits));
        }
    }

    private static void AddLanguages(Section section, CvDocument cv)
    {
        if (cv.Languages is not { Count: > 0 }) return;
        Heading(section, "Languages");
        section.AddParagraph(string.Join("  •  ", cv.Languages));
    }

    // ── primitives ───────────────────────────────────────────────────────────

    private static void Heading(Section section, string text)
    {
        var h = section.AddParagraph(text.ToUpperInvariant());
        h.Format.Font.Size = 11;
        h.Format.Font.Bold = true;
        h.Format.SpaceBefore = Unit.FromPoint(10);
        h.Format.SpaceAfter  = Unit.FromPoint(3);
        h.Format.Borders.Bottom = new Border { Width = Unit.FromPoint(0.5) };
    }

    private static void Bullet(Section section, string text)
    {
        var p = section.AddParagraph(text.Trim());
        p.Format.ListInfo.ListType = ListType.BulletList1;
        p.Format.LeftIndent = Unit.FromCentimeter(0.5);
        p.Format.SpaceAfter = Unit.FromPoint(1);
    }
}
