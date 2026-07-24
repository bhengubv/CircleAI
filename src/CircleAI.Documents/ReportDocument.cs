#nullable enable

// ReportDocument.cs
//
// The report schema — the fourth document KIND. Same double duty as the CV,
// cover letter and invoice:
//
//   1. The typed input a template lays out into a PDF.
//   2. The JSON shape the on-device model fills in: the Neuron is asked to emit
//      JSON matching THIS schema, with IncludeReasoning=false, and the result is
//      deserialized straight into a ReportDocument. Model owns the words;
//      template owns the layout.
//
// A report is a masthead (title, subtitle, author, date) followed by an ORDERED
// list of sections. Each section is a heading plus any of: prose paragraphs,
// bullet points, and one simple table — "and/or", so a section can be all prose,
// all bullets, a table with a lead-in paragraph, or any mix. The template renders
// only the parts that are present, exactly like the CV skips absent blocks.
//
// Dates are STRINGS on purpose, exactly as in CvDocument / Invoice: a report
// prints "23 July 2026", a small model emits text, and parsing to DateTime buys
// no layout benefit while being brittle.
//
// NOTE (charts): a report section is text-only for v1. CircleAI.Charts draws onto
// PDFsharp XGraphics (per-page, absolute) while this library flows content through
// MigraDoc, and Charts emits no raster an AddImage could ingest — so embedding a
// chart inline is not the "simple" path. The schema is deliberately open to a
// future chart member without disturbing existing callers.

using System;
using System.Collections.Generic;

namespace CircleAI.Documents;

/// <summary>A complete report, ready to lay out. Also the model's JSON target.</summary>
/// <param name="Title">The report title, e.g. "Township Wi-Fi Pilot — Six-Month Report". Heads the document and drives the file name.</param>
/// <param name="Subtitle">Optional strap-line under the title, e.g. a one-line scope. Omitted cleanly when absent.</param>
/// <param name="Author">Optional author / issuing person or team. Omitted cleanly when absent.</param>
/// <param name="Date">Optional free-text date, e.g. "23 July 2026". Omitted cleanly when absent.</param>
/// <param name="Sections">The body, in reading order. May be empty (an empty report still renders a real masthead).</param>
public sealed record ReportDocument(
    string                        Title,
    string?                       Subtitle,
    string?                       Author,
    string?                       Date,
    IReadOnlyList<ReportSection>  Sections)
{
    /// <summary>
    /// A safe, non-null report built from only a title. This is the DETERMINISTIC
    /// FALLBACK, mirroring <see cref="CvDocument.Minimal"/>: if the model returns
    /// unparseable JSON, the host still produces a real PDF (a titled, empty
    /// report) rather than nothing. An artifact always comes out.
    /// </summary>
    public static ReportDocument Minimal(string title) =>
        new(title,
            Subtitle: null,
            Author:   null,
            Date:     null,
            Sections: Array.Empty<ReportSection>());

    /// <summary>
    /// A fully-populated example, for previews, tests and the "show me what this
    /// looks like" path. Deliberately in the same South African key as the other
    /// samples, and exercises every section shape: prose-only, bullets-only,
    /// prose + a table, and a bullets + prose mix.
    /// </summary>
    public static ReportDocument Sample() =>
        new(
            Title:    "Township Wi-Fi Pilot — Six-Month Report",
            Subtitle: "Offline-first community connectivity across three sites in Gauteng",
            Author:   "Thabo Mokoena",
            Date:     "23 July 2026",
            Sections: new[]
            {
                new ReportSection(
                    "Executive Summary",
                    Paragraphs: new[]
                    {
                        "Between January and June 2026 we ran a low-cost community Wi-Fi pilot across three "
                        + "township sites, each served by a single solar-backed node. The goal was to test whether "
                        + "an offline-first design — local caching, on-device tools and no reliance on a metered "
                        + "uplink — could deliver useful connectivity to households on entry-level Android phones.",

                        "The pilot reached 3,208 unique devices and sustained 94% median uptime on constrained "
                        + "hardware. Usage concentrated on messaging, document tools and educational content that "
                        + "worked without a live connection, confirming that the offline-first approach fits how "
                        + "people in these communities actually use their phones.",
                    }),

                new ReportSection(
                    "Objectives",
                    Bullets: new[]
                    {
                        "Prove a single solar-backed node can serve a township block reliably for six months.",
                        "Measure real usage on entry-level (Huawei P30 Lite-class) devices, not lab hardware.",
                        "Validate that offline-first tools carry the load when the uplink is slow or absent.",
                        "Keep the whole stack free, open-source and de-Googled end to end.",
                    }),

                new ReportSection(
                    "Coverage and Usage",
                    Paragraphs: new[]
                    {
                        "The table below summarises the three sites over the full pilot window. \"Uptime\" is the "
                        + "share of the six months the node served traffic; \"Avg. daily users\" counts distinct "
                        + "devices seen per day.",
                    },
                    Table: new ReportTable(
                        Columns: new[] { "Site", "Households", "Avg. daily users", "Uptime" },
                        Rows: new IReadOnlyList<string>[]
                        {
                            new[] { "Kliptown",  "180", "612", "96%" },
                            new[] { "Diepsloot", "240", "884", "93%" },
                            new[] { "Tembisa",   "150", "477", "91%" },
                        },
                        Caption: "Table 1 — Reach and reliability by pilot site (Jan–Jun 2026).")),

                new ReportSection(
                    "Findings",
                    Paragraphs: new[]
                    {
                        "Three patterns held across every site and are the basis for the recommendations that follow.",
                    },
                    Bullets: new[]
                    {
                        "Offline-first tools accounted for 71% of active time — connectivity was a convenience, not a prerequisite.",
                        "Solar backing kept nodes alive through load-shedding; the two outages traced to hardware, not power.",
                        "Entry-level phones handled the on-device workload without noticeable lag once assets were cached locally.",
                    }),

                new ReportSection(
                    "Recommendations",
                    Paragraphs: new[]
                    {
                        "Scale to a further six sites using the same single-node, solar-backed template, prioritising "
                        + "blocks with the highest household density to maximise reach per node.",

                        "Invest the next engineering cycle in the offline-first tools themselves rather than in the "
                        + "network: the pilot shows the connection is rarely the bottleneck, the software is.",
                    }),
            });
}

/// <summary>
/// One section of a report: a heading, then any of prose paragraphs, bullet
/// points and a single table. All body parts are optional — a section may be
/// prose-only, bullets-only, a table with a lead-in, or a mix. The template
/// renders the parts that are present, in the order paragraphs → bullets → table.
/// </summary>
/// <param name="Heading">The section heading, e.g. "Executive Summary".</param>
/// <param name="Paragraphs">Optional prose paragraphs, in order. Each entry is one paragraph.</param>
/// <param name="Bullets">Optional bullet points, in order.</param>
/// <param name="Table">Optional simple table for this section.</param>
public sealed record ReportSection(
    string                  Heading,
    IReadOnlyList<string>?  Paragraphs = null,
    IReadOnlyList<string>?  Bullets    = null,
    ReportTable?            Table      = null);

/// <summary>
/// A simple report table: a header row of column names and zero or more data
/// rows, each a list of cell strings read left to right. Rows shorter than the
/// header are padded with blanks and longer rows are clipped, so a ragged table
/// still lays out cleanly rather than throwing. Everything is text — no derived
/// figures — because a report table displays what the model wrote, it does not
/// compute (that distinction is the invoice's job, not the report's).
/// </summary>
/// <param name="Columns">Header cells, left to right. Their count sets the table width.</param>
/// <param name="Rows">Data rows; each is a list of cell strings aligned to <paramref name="Columns"/>.</param>
/// <param name="Caption">Optional caption printed above the table.</param>
public sealed record ReportTable(
    IReadOnlyList<string>               Columns,
    IReadOnlyList<IReadOnlyList<string>> Rows,
    string?                            Caption = null);
