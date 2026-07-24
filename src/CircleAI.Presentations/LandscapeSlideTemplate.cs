#nullable enable

// LandscapeSlideTemplate.cs
//
// Turns a Deck (content) into a MigraDoc Document (layout): LANDSCAPE A4, one
// slide per page, large heading, big readable bullets. This owns LAYOUT ONLY —
// every word comes from the Deck; the template never invents content.
//
// One slide == one MigraDoc SECTION. New sections start on a new page, so a
// section-per-slide guarantees "one slide per page" without manual page breaks,
// and lets every slide carry its own footer/number. Per the house rule, page size
// and orientation are set on the SECTION's PageSetup, never on DefaultPageSetup
// (which MigraDoc forbids mutating).

using System.Collections.Generic;
using System.Globalization;
using MigraDoc.DocumentObjectModel;

namespace CircleAI.Presentations;

/// <summary>The one deck template for v1: landscape A4, one slide per page.</summary>
internal static class LandscapeSlideTemplate
{
    /// <summary>Template id, surfaced via <see cref="IDeckEngine.AvailableTemplates"/>.</summary>
    public const string Id = "landscape-slide";

    // FONT: the embedded free/OFL family served by CircleAI.Documents'
    // EmbeddedFontResolver. We cannot reference EmbeddedFontResolver.FamilyName
    // directly — that type is INTERNAL to CircleAI.Documents — so we mirror its
    // value here. If it ever changes there, change it here too. (In practice that
    // resolver ignores the requested family and always returns its embedded face,
    // so even a mismatch would still render; we keep them equal for correctness.)
    private const string FontFamily = "CircleSans";

    // Page geometry (A4 landscape). Kept as constants so the footer's right-edge
    // tab stop can be derived from the same numbers the margins use.
    private const double PageWidthCm  = 29.7; // A4 long edge, horizontal in landscape
    private const double SideMarginCm = 2.2;
    private const double TopMarginCm  = 1.8;
    private const double BotMarginCm  = 1.4;
    private static readonly Unit ContentRightEdge = Unit.FromCentimeter(PageWidthCm - 2 * SideMarginCm);

    // Brand palette only (see memory: #2196F3 / #2c3e50 / #ffffff). MigraDoc's
    // Color(uint) constructor takes 0xAARRGGBB, so the leading FF is opaque alpha.
    private static readonly Color TitleColor  = new(0xFF2C3E50); // dark slate — headings
    private static readonly Color BodyColor   = new(0xFF2C3E50); // same slate — body text
    private static readonly Color AccentColor = new(0xFF2196F3); // blue — rules/underlines
    private static readonly Color MutedColor  = new(0xFF8A9AA5); // grey — footer + notes

    public static Document Build(Deck deck)
    {
        var doc = new Document();

        // Document-wide defaults. Everything inherits from "Normal"; slides only
        // override what differs (size, weight, colour).
        var normal = doc.Styles["Normal"]!;
        normal.Font.Name  = FontFamily;
        normal.Font.Size  = 18;
        normal.Font.Color = BodyColor;

        var hasTitleSlide = !string.IsNullOrWhiteSpace(deck.Title) ||
                            !string.IsNullOrWhiteSpace(deck.Subtitle);
        if (hasTitleSlide)
            AddTitleSlide(doc, deck);

        var number = 0;
        foreach (var slide in deck.Slides)
            AddContentSlide(doc, deck, slide, ++number);

        // Never emit a zero-page PDF: an empty deck with no title still gets one
        // blank landscape page so an artifact always comes out.
        if (!hasTitleSlide && deck.Slides.Count == 0)
            NewLandscapeSection(doc);

        return doc;
    }

    // ── page setup ─────────────────────────────────────────────────────────────

    /// <summary>Adds a fresh section configured as a single landscape-A4 page.</summary>
    private static Section NewLandscapeSection(Document doc)
    {
        var section = doc.AddSection();

        // PageFormat + Orientation on the SECTION (not DefaultPageSetup). With
        // Orientation.Landscape, MigraDoc swaps A4 to 29.7cm wide x 21cm tall.
        section.PageSetup.PageFormat   = PageFormat.A4;
        section.PageSetup.Orientation  = Orientation.Landscape;
        section.PageSetup.TopMargin    = Unit.FromCentimeter(TopMarginCm);
        section.PageSetup.BottomMargin = Unit.FromCentimeter(BotMarginCm);
        section.PageSetup.LeftMargin   = Unit.FromCentimeter(SideMarginCm);
        section.PageSetup.RightMargin  = Unit.FromCentimeter(SideMarginCm);

        return section;
    }

    // ── title slide ────────────────────────────────────────────────────────────

    private static void AddTitleSlide(Document doc, Deck deck)
    {
        var section = NewLandscapeSection(doc);

        // MigraDoc has no simple vertical centring, so we push the title down with
        // an empty spacer paragraph. ~5.5cm lands it a little above the middle.
        var spacer = section.AddParagraph();
        spacer.Format.SpaceBefore = Unit.FromCentimeter(5.5);

        if (!string.IsNullOrWhiteSpace(deck.Title))
        {
            var title = section.AddParagraph(deck.Title.Trim());
            title.Format.Font.Size  = 40;
            title.Format.Font.Bold  = true;
            title.Format.Font.Color = TitleColor;
            title.Format.Alignment  = ParagraphAlignment.Center;
            title.Format.SpaceAfter = Unit.FromPoint(8);
        }

        if (!string.IsNullOrWhiteSpace(deck.Subtitle))
        {
            var sub = section.AddParagraph(deck.Subtitle!.Trim());
            sub.Format.Font.Size  = 20;
            sub.Format.Font.Color = AccentColor;
            sub.Format.Alignment  = ParagraphAlignment.Center;
            sub.Format.SpaceAfter = Unit.FromPoint(14);
        }

        if (!string.IsNullOrWhiteSpace(deck.Author))
        {
            var author = section.AddParagraph(deck.Author!.Trim());
            author.Format.Font.Size  = 14;
            author.Format.Font.Color = MutedColor;
            author.Format.Alignment  = ParagraphAlignment.Center;
        }
    }

    // ── content slide ──────────────────────────────────────────────────────────

    private static void AddContentSlide(Document doc, Deck deck, Slide slide, int number)
    {
        var section = NewLandscapeSection(doc);

        // Heading: large, bold, with a full-width accent rule beneath it.
        var title = section.AddParagraph((slide.Title ?? string.Empty).Trim());
        title.Format.Font.Size  = 30;
        title.Format.Font.Bold  = true;
        title.Format.Font.Color = TitleColor;
        title.Format.SpaceAfter = Unit.FromPoint(14);
        title.Format.Borders.Bottom            = new Border { Width = Unit.FromPoint(2), Color = AccentColor };
        title.Format.Borders.DistanceFromBottom = Unit.FromPoint(6);

        // Bullets: big and airy so they read from across a room.
        foreach (var bullet in slide.Bullets)
        {
            if (string.IsNullOrWhiteSpace(bullet)) continue;

            var p = section.AddParagraph(bullet.Trim());
            p.Format.Font.Size            = 18;
            p.Format.ListInfo.ListType    = ListType.BulletList1;
            p.Format.LeftIndent           = Unit.FromCentimeter(0.9);
            p.Format.SpaceBefore          = Unit.FromPoint(7);
            p.Format.SpaceAfter           = Unit.FromPoint(7);
        }

        // Speaker notes: surfaced subtly at the bottom of the slide body.
        if (!string.IsNullOrWhiteSpace(slide.Notes))
            AddNotes(section, slide.Notes!.Trim());

        // Running footer: optional text on the left, slide number on the right.
        AddFooter(section, slide.Footer ?? deck.Footer, number);
    }

    // ── primitives ─────────────────────────────────────────────────────────────

    private static void AddNotes(Section section, string notes)
    {
        var label = section.AddParagraph("Notes");
        label.Format.Font.Size            = 9;
        label.Format.Font.Bold            = true;
        label.Format.Font.Color           = MutedColor;
        label.Format.SpaceBefore          = Unit.FromPoint(16);
        label.Format.Borders.Top            = new Border { Width = Unit.FromPoint(0.5), Color = MutedColor };
        label.Format.Borders.DistanceFromTop = Unit.FromPoint(8);

        var body = section.AddParagraph(notes);
        body.Format.Font.Size   = 10;
        body.Format.Font.Italic = true;
        body.Format.Font.Color  = MutedColor;
    }

    private static void AddFooter(Section section, string? text, int number)
    {
        var footer = section.Footers.Primary.AddParagraph();
        footer.Format.Font.Size  = 9;
        footer.Format.Font.Color = MutedColor;

        // A right-aligned tab stop at the content's right edge lets the optional
        // footer text sit left while the slide number sits hard right, on one line.
        footer.Format.AddTabStop(ContentRightEdge, TabAlignment.Right);

        if (!string.IsNullOrWhiteSpace(text))
            footer.AddText(text.Trim());

        footer.AddTab();
        footer.AddText(number.ToString(CultureInfo.InvariantCulture));
    }
}
