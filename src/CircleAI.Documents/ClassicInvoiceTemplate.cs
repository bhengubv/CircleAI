#nullable enable

// ClassicInvoiceTemplate.cs
//
// Turns an Invoice (content) into a MigraDoc Document (layout). Unlike the CV and
// cover letter this template is TABLE-based: line items live in a real MigraDoc
// table with aligned money columns, and the subtotal / VAT / total sit in merged
// rows of that same table so the figures line up under the "Amount" column.
//
// This owns LAYOUT ONLY, plus the presentation-level job of turning a decimal +
// an ISO currency code into a human money string. It never computes a total — the
// Invoice record derives Subtotal / VatAmount / Total itself, so the layout can
// only ever DISPLAY figures that already add up.
//
// The currency map is deliberately conservative: it only carries symbols the
// embedded DejaVu Sans renders cleanly (R, $, €, £, ¥ and plain-letter forms).
// Any unknown code falls back to the code itself (e.g. "AUD 1,234.56") rather
// than risk a missing-glyph box on a de-Googled phone with no system fonts.

using System;
using System.Collections.Generic;
using System.Globalization;
using MigraDoc.DocumentObjectModel;
using MigraDoc.DocumentObjectModel.Tables;

namespace CircleAI.Documents;

/// <summary>The one invoice template for v1: classic, table-based, VAT-aware.</summary>
internal static class ClassicInvoiceTemplate
{
    /// <summary>Template id, surfaced via <see cref="IDocumentEngine.AvailableTemplates"/>.</summary>
    public const string Id = "classic-invoice";

    // Usable width on A4 with the 1.8 cm side margins below = 21.0 − 3.6 = 17.4 cm.
    private const double UsableWidthCm = 17.4;

    // Symbols known to render in the embedded font. Everything else → ISO code.
    private static readonly IReadOnlyDictionary<string, string> CurrencySymbols =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["ZAR"] = "R",   // South African rand — the home currency
            ["USD"] = "$",
            ["EUR"] = "€",
            ["GBP"] = "£",
            ["JPY"] = "¥",
            ["NAD"] = "N$",  // Namibian dollar
            ["BWP"] = "P",   // Botswana pula
            ["KES"] = "KSh", // Kenyan shilling
            ["ZMW"] = "K",   // Zambian kwacha
        };

    public static Document Build(Invoice invoice)
    {
        var doc = new Document();

        var normal = doc.Styles["Normal"]!;
        normal.Font.Name = EmbeddedFontResolver.FamilyName;
        normal.Font.Size = 10;
        normal.ParagraphFormat.SpaceAfter = Unit.FromPoint(2);

        // Page size + margins go on the SECTION's PageSetup, never DefaultPageSetup
        // (mutating the default throws in MigraDoc).
        var section = doc.AddSection();
        section.PageSetup.PageFormat   = PageFormat.A4;
        section.PageSetup.TopMargin    = Unit.FromCentimeter(1.6);
        section.PageSetup.BottomMargin = Unit.FromCentimeter(1.6);
        section.PageSetup.LeftMargin   = Unit.FromCentimeter(1.8);
        section.PageSetup.RightMargin  = Unit.FromCentimeter(1.8);

        AddTitleAndHeader(section, invoice);
        AddBillTo(section, invoice);
        AddLineItems(section, invoice);
        AddPaymentDetails(section, invoice);

        return doc;
    }

    // ── title + issuer / meta band ───────────────────────────────────────────────

    private static void AddTitleAndHeader(Section section, Invoice invoice)
    {
        // "TAX INVOICE" is the legally correct heading in South Africa when VAT is
        // charged; a plain "INVOICE" when it is not.
        var title = section.AddParagraph(invoice.VatPercent > 0 ? "TAX INVOICE" : "INVOICE");
        title.Format.Font.Size = 20;
        title.Format.Font.Bold = true;
        title.Format.SpaceAfter = Unit.FromPoint(8);

        // Borderless two-column band: issuer on the left, invoice meta on the right.
        var head = section.AddTable();
        head.AddColumn(Unit.FromCentimeter(9.4));
        var right = head.AddColumn(Unit.FromCentimeter(UsableWidthCm - 9.4));
        right.Format.Alignment = ParagraphAlignment.Right;

        var row = head.AddRow();
        AddPartyBlock(row.Cells[0].Elements, "From", invoice.From);

        var meta = row.Cells[1].Elements;
        AddMetaLine(meta, "Invoice No", invoice.InvoiceNumber);
        AddMetaLine(meta, "Issue Date", invoice.IssueDate);
        AddMetaLine(meta, "Due Date",   invoice.DueDate);
    }

    private static void AddBillTo(Section section, Invoice invoice)
    {
        var last = AddPartyBlock(section.Elements, "Bill To", invoice.To, spaceBeforePt: 16);
        // End the block with a gap before the line-items table.
        last.Format.SpaceAfter = Unit.FromPoint(12);
    }

    // ── line items + totals ──────────────────────────────────────────────────────

    private static void AddLineItems(Section section, Invoice invoice)
    {
        var table = section.AddTable();
        // Widths sum to the full usable width (8.8 + 2.0 + 3.2 + 3.4 = 17.4).
        table.AddColumn(Unit.FromCentimeter(8.8));                     // Description (left)
        RightColumn(table.AddColumn(Unit.FromCentimeter(2.0)));        // Qty
        RightColumn(table.AddColumn(Unit.FromCentimeter(3.2)));        // Unit price
        RightColumn(table.AddColumn(Unit.FromCentimeter(3.4)));        // Amount

        // Header row: bold, lightly shaded, underlined.
        var header = table.AddRow();
        header.Format.Font.Bold = true;
        header.Shading.Color = Colors.Gainsboro;
        header.TopPadding    = Unit.FromPoint(3);
        header.BottomPadding = Unit.FromPoint(3);
        header.Borders.Bottom.Width = Unit.FromPoint(0.75);
        header.Cells[0].AddParagraph("Description");
        header.Cells[1].AddParagraph("Qty");
        header.Cells[2].AddParagraph("Unit Price");
        header.Cells[3].AddParagraph("Amount");

        var items = invoice.LineItems ?? Array.Empty<InvoiceLineItem>();
        foreach (var item in items)
        {
            var row = table.AddRow();
            row.TopPadding    = Unit.FromPoint(2.5);
            row.BottomPadding = Unit.FromPoint(2.5);
            row.Cells[0].AddParagraph(item.Description ?? "");
            row.Cells[1].AddParagraph(Qty(item.Quantity));
            row.Cells[2].AddParagraph(Money(item.UnitPrice, invoice.CurrencyCode));
            row.Cells[3].AddParagraph(Money(item.LineTotal, invoice.CurrencyCode));
        }

        // Totals: merged label cell spans the first three columns, amount sits under
        // "Amount". VAT line is skipped entirely when no VAT is charged.
        AddTotalRow(table, "Subtotal", Money(invoice.Subtotal, invoice.CurrencyCode), bold: false, topRule: true);
        if (invoice.VatPercent > 0)
            AddTotalRow(table, $"VAT ({Pct(invoice.VatPercent)}%)", Money(invoice.VatAmount, invoice.CurrencyCode), bold: false, topRule: false);
        AddTotalRow(table, "Total", Money(invoice.Total, invoice.CurrencyCode), bold: true, topRule: true);

        var currencyNote = section.AddParagraph($"All amounts in {(invoice.CurrencyCode ?? "").ToUpperInvariant()}.");
        currencyNote.Format.Font.Size = 8.5;
        currencyNote.Format.Font.Color = Colors.Gray;
        currencyNote.Format.SpaceBefore = Unit.FromPoint(4);
    }

    private static void AddTotalRow(Table table, string label, string amount, bool bold, bool topRule)
    {
        var row = table.AddRow();
        row.TopPadding    = Unit.FromPoint(2.5);
        row.BottomPadding = Unit.FromPoint(2.5);

        var labelCell = row.Cells[0];
        labelCell.MergeRight = 2; // covers Description + Qty + Unit Price
        var lp = labelCell.AddParagraph(label);
        lp.Format.Alignment = ParagraphAlignment.Right;
        lp.Format.Font.Bold = bold;

        var ap = row.Cells[3].AddParagraph(amount);
        ap.Format.Font.Bold = bold;

        if (topRule)
        {
            labelCell.Borders.Top.Width   = Unit.FromPoint(0.75);
            row.Cells[3].Borders.Top.Width = Unit.FromPoint(0.75);
        }
    }

    // ── payment details ──────────────────────────────────────────────────────────

    private static void AddPaymentDetails(Section section, Invoice invoice)
    {
        if (string.IsNullOrWhiteSpace(invoice.PaymentNote)) return;

        Label(section.Elements, "Payment Details", spaceBeforePt: 16);

        // Newlines in the note become separate lines (Bank / Account / Reference …).
        var lines = invoice.PaymentNote!.Replace("\r\n", "\n").Split('\n');
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var p = section.AddParagraph(line.Trim());
            p.Format.Font.Size = 9.5;
        }
    }

    // ── shared primitives ────────────────────────────────────────────────────────

    /// <summary>
    /// Adds a labelled party block (label, bold name, then any present detail
    /// lines) to <paramref name="host"/>, which may be a section or a table cell.
    /// Returns the LAST paragraph added so a caller can hang trailing space off it.
    /// </summary>
    private static Paragraph AddPartyBlock(DocumentElements host, string label, InvoiceParty party, double spaceBeforePt = 0)
    {
        Label(host, label, spaceBeforePt);

        var name = host.AddParagraph();
        name.AddFormattedText(party.Name, TextFormat.Bold);
        Paragraph last = name;

        void Line(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            var p = host.AddParagraph(text!);
            p.Format.Font.Size = 9.5;
            last = p;
        }

        Line(party.Address);
        Line(party.Email);
        Line(party.Phone);
        if (!string.IsNullOrWhiteSpace(party.TaxNumber))
            Line($"VAT No: {party.TaxNumber}");

        return last;
    }

    private static void AddMetaLine(DocumentElements host, string label, string value)
    {
        var p = host.AddParagraph();
        p.AddFormattedText($"{label}: ", TextFormat.Bold);
        p.AddText(value ?? "");
        p.Format.Font.Size = 10;
    }

    /// <summary>A small, grey, upper-case section label (e.g. "FROM", "BILL TO").</summary>
    private static void Label(DocumentElements host, string text, double spaceBeforePt = 0)
    {
        var p = host.AddParagraph(text.ToUpperInvariant());
        p.Format.Font.Size = 8;
        p.Format.Font.Color = Colors.Gray;
        p.Format.SpaceAfter = Unit.FromPoint(1);
        if (spaceBeforePt > 0) p.Format.SpaceBefore = Unit.FromPoint(spaceBeforePt);
    }

    private static void RightColumn(Column column) => column.Format.Alignment = ParagraphAlignment.Right;

    // ── money / number formatting ────────────────────────────────────────────────

    /// <summary>Formats a decimal as a currency string, e.g. 6500m + "ZAR" → "R6,500.00".</summary>
    private static string Money(decimal amount, string? currencyCode)
    {
        var code = (currencyCode ?? "").Trim();
        var symbol = CurrencySymbols.TryGetValue(code, out var s) ? s : code.ToUpperInvariant();
        var number = Invoice.Round2(amount).ToString("N2", CultureInfo.InvariantCulture);
        if (symbol.Length == 0) return number;
        // No gap after a single-char symbol ("R6,500.00"); a gap after letters ("KSh 6,500.00").
        var gap = symbol.Length > 1 ? " " : "";
        return $"{symbol}{gap}{number}";
    }

    /// <summary>Quantity without noise: "2", "1.5", "0.75".</summary>
    private static string Qty(decimal quantity) => quantity.ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>VAT percent without noise: "15", "14.5".</summary>
    private static string Pct(decimal percent) => percent.ToString("0.##", CultureInfo.InvariantCulture);
}
