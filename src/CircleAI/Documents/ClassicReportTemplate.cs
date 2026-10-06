#nullable enable

// ClassicReportTemplate.cs
//
// Turns a ReportDocument (content) into a MigraDoc Document (layout). A plain,
// classic report: a masthead (title, subtitle, author • date, a rule) followed by
// ordered sections. Each section is a bordered heading, then any prose paragraphs,
// then any bullet points, then an optional simple table — the parts that are
// present, in that order. Real selectable text on A4, the same honest, parseable
// output as the CV, letter and invoice.
//
// This owns LAYOUT ONLY. Every word here came from the user or the model; the
// template never invents content. Absent parts (no subtitle, no bullets, no
// table) simply do not render — no blank lines left behind.
//
// Text-only for v1: see the charts NOTE in ReportDocument.cs for why a chart is
// not embedded inline here.

using System;
using System.Collections.Generic;
using MigraDoc.DocumentObjectModel;
using MigraDoc.DocumentObjectModel.Tables;

namespace CircleAI.Documents;

/// <summary>The one report template for v1: classic masthead + ordered sections.</summary>
internal static class ClassicReportTemplate
{
    /// <summary>Template id, surfaced via <see cref="IDocumentEngine.AvailableTemplates"/>.</summary>
    public const string Id = "classic-report";

    // Usable width on A4 with the 2.0 cm side margins below = 21.0 − 4.0 = 17.0 cm.
    private const double UsableWidthCm = 17.0;

    public static Document Build(ReportDocument report)
    {
        var doc = new Document();

        var normal = doc.Styles["Normal"]!;
        normal.Font.Name = EmbeddedFontResolver.FamilyName;
        normal.Font.Size = 10.5; // a comfortable reading size for continuous report prose
        normal.ParagraphFormat.SpaceAfter = Unit.FromPoint(2);

        // MigraDoc forbids mutating DefaultPageSetup — page size and margins go on
        // the SECTION's own PageSetup (which starts as a copy of the default).
        var section = doc.AddSection();
        section.PageSetup.PageFormat   = PageFormat.A4;
        section.PageSetup.TopMargin    = Unit.FromCentimeter(2.0);
        section.PageSetup.BottomMargin = Unit.FromCentimeter(2.0);
        section.PageSetup.LeftMargin   = Unit.FromCentimeter(2.0);
        section.PageSetup.RightMargin  = Unit.FromCentimeter(2.0);

        AddMasthead(section, report);

        var sections = report.Sections ?? Array.Empty<ReportSection>();
        foreach (var s in sections)
            AddSection(section, s);

        return doc;
    }

    // ── masthead ───────────────────────────────────────────────────────────────

    private static void AddMasthead(Section section, ReportDocument report)
    {
        var title = section.AddParagraph(report.Title ?? "");
        title.Format.Font.Size = 24;
        title.Format.Font.Bold = true;
        title.Format.SpaceAfter = Unit.FromPoint(2);
        Paragraph last = title;

        if (!string.IsNullOrWhiteSpace(report.Subtitle))
        {
            var sub = section.AddParagraph(report.Subtitle!.Trim());
            sub.Format.Font.Size = 13;
            sub.Format.SpaceAfter = Unit.FromPoint(3);
            last = sub;
        }

        var metaBits = new List<string>();
        if (!string.IsNullOrWhiteSpace(report.Author)) metaBits.Add(report.Author!);
        if (!string.IsNullOrWhiteSpace(report.Date))   metaBits.Add(report.Date!);
        if (metaBits.Count > 0)
        {
            var meta = section.AddParagraph(string.Join("  •  ", metaBits));
            meta.Format.Font.Size = 9.5;
            meta.Format.Font.Color = Colors.Gray;
            last = meta;
        }

        // Close the masthead with a full-width rule + a gap before the first section.
        // A paragraph border spans the text column regardless of the line's length,
        // so this reads as a clean divider under whatever the last header line was.
        last.Format.SpaceAfter = Unit.FromPoint(6);
        last.Format.Borders.Bottom = new Border { Width = Unit.FromPoint(0.75) };
    }

    // ── sections ───────────────────────────────────────────────────────────────

    private static void AddSection(Section section, ReportSection s)
    {
        if (s is null) return;

        if (!string.IsNullOrWhiteSpace(s.Heading))
            Heading(section, s.Heading.Trim());

        if (s.Paragraphs is { Count: > 0 })
        {
            foreach (var para in s.Paragraphs)
            {
                if (string.IsNullOrWhiteSpace(para)) continue;
                var p = section.AddParagraph(para.Trim());
                p.Format.SpaceAfter = Unit.FromPoint(6);
            }
        }

        if (s.Bullets is { Count: > 0 })
        {
            foreach (var bullet in s.Bullets)
            {
                if (string.IsNullOrWhiteSpace(bullet)) continue;
                Bullet(section, bullet);
            }
        }

        if (s.Table is not null)
            AddGrid(section, s.Table);
    }

    // ── table ──────────────────────────────────────────────────────────────────

    private static void AddGrid(Section section, ReportTable table)
    {
        var columns = table.Columns ?? Array.Empty<string>();
        if (columns.Count == 0) return; // no columns → nothing meaningful to draw

        if (!string.IsNullOrWhiteSpace(table.Caption))
        {
            var cap = section.AddParagraph(table.Caption!.Trim());
            cap.Format.Font.Size   = 9;
            cap.Format.Font.Italic = true;
            cap.Format.Font.Color  = Colors.Gray;
            cap.Format.SpaceBefore = Unit.FromPoint(6);
            cap.Format.SpaceAfter  = Unit.FromPoint(2);
        }

        var grid = section.AddTable();
        // Light full grid so a report table reads as tabular data (unlike the
        // invoice, which uses only rule lines under money columns).
        grid.Borders.Color = Colors.LightGray;
        grid.Borders.Width = Unit.FromPoint(0.25);

        // Even column widths across the usable page width.
        var colWidth = Unit.FromCentimeter(UsableWidthCm / columns.Count);
        for (var i = 0; i < columns.Count; i++)
            grid.AddColumn(colWidth);

        // Header row: bold, lightly shaded, underlined — matches the invoice.
        var header = grid.AddRow();
        header.Format.Font.Bold = true;
        header.Shading.Color = Colors.Gainsboro;
        header.TopPadding    = Unit.FromPoint(3);
        header.BottomPadding = Unit.FromPoint(3);
        header.Borders.Bottom.Width = Unit.FromPoint(0.75);
        for (var i = 0; i < columns.Count; i++)
            header.Cells[i].AddParagraph(columns[i] ?? "");

        // Data rows. Ragged rows are tolerated: short rows pad with blanks, long
        // rows are clipped to the column count, so layout never throws on bad data.
        var rows = table.Rows ?? Array.Empty<IReadOnlyList<string>>();
        foreach (var r in rows)
        {
            var row = grid.AddRow();
            row.TopPadding    = Unit.FromPoint(2.5);
            row.BottomPadding = Unit.FromPoint(2.5);
            for (var i = 0; i < columns.Count; i++)
            {
                var value = (r is not null && i < r.Count) ? (r[i] ?? "") : "";
                row.Cells[i].AddParagraph(value);
            }
        }

        // A small gap after the table before the next section's heading.
        var spacer = section.AddParagraph();
        spacer.Format.SpaceAfter = Unit.FromPoint(4);
    }

    // ── primitives ───────────────────────────────────────────────────────────────

    private static void Heading(Section section, string text)
    {
        var h = section.AddParagraph(text);
        h.Format.Font.Size = 13;
        h.Format.Font.Bold = true;
        h.Format.SpaceBefore = Unit.FromPoint(12);
        h.Format.SpaceAfter  = Unit.FromPoint(3);
        h.Format.Borders.Bottom = new Border { Width = Unit.FromPoint(0.5) };
    }

    private static void Bullet(Section section, string text)
    {
        var p = section.AddParagraph(text.Trim());
        p.Format.ListInfo.ListType = ListType.BulletList1;
        p.Format.LeftIndent = Unit.FromCentimeter(0.5);
        p.Format.SpaceAfter = Unit.FromPoint(2);
    }
}
