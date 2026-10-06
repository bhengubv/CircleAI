#nullable enable

// ClassicCoverLetterTemplate.cs
//
// Turns a CoverLetter (content) into a MigraDoc Document (layout). A plain,
// classic business-letter block: sender header, date, recipient block, subject
// line, greeting, body paragraphs, sign-off and a typed signature. Real
// selectable text on A4 — the same honest, parseable output as the CV.
//
// This owns LAYOUT ONLY. Every word here came from the user or the model; the
// template never invents content — the only "defaults" it prints (greeting,
// closing, signature) are resolved on the CoverLetter model itself, not here.

using System.Collections.Generic;
using MigraDoc.DocumentObjectModel;

namespace CircleAI.Documents;

/// <summary>The one cover-letter template for v1: classic single-block business letter.</summary>
internal static class ClassicCoverLetterTemplate
{
    /// <summary>Template id, surfaced via <see cref="IDocumentEngine.AvailableTemplates"/>.</summary>
    public const string Id = "classic-letter";

    public static Document Build(CoverLetter letter)
    {
        var doc = new Document();

        var normal = doc.Styles["Normal"]!;
        normal.Font.Name = EmbeddedFontResolver.FamilyName;
        normal.Font.Size = 11; // a touch larger than the CV — correspondence reads better at 11pt
        normal.ParagraphFormat.SpaceAfter = Unit.FromPoint(2);

        // MigraDoc forbids mutating DefaultPageSetup — page size and margins go on
        // the SECTION's own PageSetup (which starts as a copy of the default). A
        // letter carries slightly wider margins than the CV for a formal feel.
        var section = doc.AddSection();
        section.PageSetup.PageFormat   = PageFormat.A4;
        section.PageSetup.TopMargin    = Unit.FromCentimeter(2.0);
        section.PageSetup.BottomMargin = Unit.FromCentimeter(2.0);
        section.PageSetup.LeftMargin   = Unit.FromCentimeter(2.2);
        section.PageSetup.RightMargin  = Unit.FromCentimeter(2.2);

        AddSenderHeader(section, letter);
        AddDate(section, letter);
        AddRecipient(section, letter);
        AddSubject(section, letter);
        AddGreeting(section, letter);
        AddBody(section, letter);
        AddSignature(section, letter);

        return doc;
    }

    // ── sender header ──────────────────────────────────────────────────────────

    private static void AddSenderHeader(Section section, CoverLetter letter)
    {
        var name = section.AddParagraph(letter.SenderName);
        name.Format.Font.Size = 15;
        name.Format.Font.Bold = true;
        name.Format.SpaceAfter = Unit.FromPoint(1);

        var contactBits = new List<string>();
        var c = letter.SenderContact;
        if (!string.IsNullOrWhiteSpace(c.Location)) contactBits.Add(c.Location!);
        if (!string.IsNullOrWhiteSpace(c.Phone))    contactBits.Add(c.Phone!);
        if (!string.IsNullOrWhiteSpace(c.Email))    contactBits.Add(c.Email!);
        if (c.Links is { Count: > 0 }) contactBits.AddRange(c.Links);

        if (contactBits.Count > 0)
        {
            var contact = section.AddParagraph(string.Join("  •  ", contactBits));
            contact.Format.Font.Size = 9.5;
            contact.Format.SpaceAfter = Unit.FromPoint(14);
        }
        else
        {
            name.Format.SpaceAfter = Unit.FromPoint(14);
        }
    }

    // ── date ───────────────────────────────────────────────────────────────────

    private static void AddDate(Section section, CoverLetter letter)
    {
        if (string.IsNullOrWhiteSpace(letter.Date)) return;
        var date = section.AddParagraph(letter.Date);
        date.Format.SpaceAfter = Unit.FromPoint(12);
    }

    // ── recipient block ──────────────────────────────────────────────────────────

    private static void AddRecipient(Section section, CoverLetter letter)
    {
        // Only the lines that exist are printed — a missing recipient name or
        // address never leaves a blank line behind. We track the last line added
        // so the block can end with a gap without leaning on Section.LastParagraph.
        Paragraph? last = null;

        void Emit(string text, bool bold = false)
        {
            var p = section.AddParagraph();
            if (bold) p.AddFormattedText(text, TextFormat.Bold);
            else      p.AddText(text);
            last = p;
        }

        if (!string.IsNullOrWhiteSpace(letter.RecipientName))    Emit(letter.RecipientName!, bold: true);
        if (!string.IsNullOrWhiteSpace(letter.RecipientTitle))   Emit(letter.RecipientTitle!);
        if (!string.IsNullOrWhiteSpace(letter.RecipientCompany)) Emit(letter.RecipientCompany);
        if (!string.IsNullOrWhiteSpace(letter.RecipientAddress)) Emit(letter.RecipientAddress!);

        if (last is not null)
            last.Format.SpaceAfter = Unit.FromPoint(14);
    }

    // ── subject / greeting ───────────────────────────────────────────────────────

    private static void AddSubject(Section section, CoverLetter letter)
    {
        if (string.IsNullOrWhiteSpace(letter.Subject)) return;
        var subject = section.AddParagraph();
        subject.AddFormattedText($"Re: {letter.Subject}", TextFormat.Bold);
        subject.Format.SpaceAfter = Unit.FromPoint(12);
    }

    private static void AddGreeting(Section section, CoverLetter letter)
    {
        var greeting = section.AddParagraph(letter.EffectiveGreeting);
        greeting.Format.SpaceAfter = Unit.FromPoint(8);
    }

    // ── body ─────────────────────────────────────────────────────────────────────

    private static void AddBody(Section section, CoverLetter letter)
    {
        if (letter.Body is not { Count: > 0 }) return;

        foreach (var paragraph in letter.Body)
        {
            if (string.IsNullOrWhiteSpace(paragraph)) continue;
            var p = section.AddParagraph(paragraph.Trim());
            p.Format.SpaceAfter = Unit.FromPoint(8);
        }
    }

    // ── sign-off ─────────────────────────────────────────────────────────────────

    private static void AddSignature(Section section, CoverLetter letter)
    {
        var closing = section.AddParagraph(letter.EffectiveClosing);
        closing.Format.SpaceBefore = Unit.FromPoint(6);
        // Leave vertical room for a handwritten/scanned signature above the name.
        closing.Format.SpaceAfter = Unit.FromPoint(34);

        var sign = section.AddParagraph();
        sign.AddFormattedText(letter.EffectiveSignature, TextFormat.Bold);
    }
}
