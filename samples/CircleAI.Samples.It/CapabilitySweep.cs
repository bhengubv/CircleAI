// CapabilitySweep.cs
//
// The end-to-end capability probe the sample runs ON the phone. Two halves:
//
//   1. BuildModelReport  — asks the REAL device + the REAL embedded registry
//      what each modality can do here. This is the on-device proof that the
//      catalogued models (the vision VLM, the Piper voices, Whisper) resolve
//      and that device-fit gating is honest: on a 3 GB phone vision comes back
//      NothingFits (the 3B VLM is too big), TTS/ASR come back Good. No
//      download happens — selection is a metadata decision.
//
//   2. RenderDocumentSuiteAsync — renders every document KIND (CV, cover
//      letter, invoice, report) through the pure-managed PDFsharp engine, with
//      no model and no network. Proves the whole document surface runs on
//      ARM64/EMUI, not just the CV.
//
// Deliberately self-contained (builds its own selector + sample data) so it
// runs the instant the app opens — before, and independent of, the ~433 MB
// chat model load.

using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CircleAI.Core;
using CircleAI.Core.Models;
using CircleAI.Documents;
using CircleAI.Inference;

namespace CircleAI.Samples.It;

public static class CapabilitySweep
{
    // ── 1. what can this phone actually run ──────────────────────────────────

    /// <summary>
    /// Per-modality verdict for THIS device, read from the embedded registry.
    /// Vision / Asr / Tts / Vad / WakeWord / Music / Video / Coding — each with
    /// its <see cref="SelectionQuality"/>, the model that would load (and its
    /// size), and the plain-language reason the selector gives.
    /// </summary>
    public static string BuildModelReport()
    {
        var probe    = DeviceProbe.Snapshot();
        var selector = new SpeechModelSelector(new ModelRegistryService());

        var ramGb = probe.RamAvailableBytes / (1024.0 * 1024 * 1024);
        var stoGb = probe.StorageFreeBytes  / (1024.0 * 1024 * 1024);

        var sb = new StringBuilder();
        sb.Append($"device: {ramGb:F1} GB RAM free, {stoGb:F0} GB storage free, tier {probe.Classify()}\n\n");

        // Chat is excluded on purpose: it selects through IModelSelector.BestFit,
        // not the speech selector, and PlanFor throws for it by contract.
        foreach (var m in new[]
        {
            ModelModality.Vision, ModelModality.Asr, ModelModality.Tts, ModelModality.Vad,
            ModelModality.WakeWord, ModelModality.Music, ModelModality.Video, ModelModality.Coding,
        })
        {
            var plan = selector.PlanFor(probe, m);
            var pick = plan.Model is not null
                ? $"{plan.Model.ModelId} (~{plan.Model.EstimatedBytes / 1_000_000} MB)"
                : plan.UsesBuiltIn ? "built-in, no model" : "-";

            sb.Append($"  {m,-9} {plan.Quality,-18} {pick}\n");
            sb.Append($"      {plan.Reason}\n");
        }
        return sb.ToString();
    }

    // ── 2. render every document kind, offline ───────────────────────────────

    /// <summary>
    /// Renders CV, cover letter, invoice and report through the offline engine.
    /// Returns each artifact's bytes + suggested file name; the host writes them
    /// wherever it wants (the Android head drops them in FilesDir).
    /// </summary>
    public static async Task<IReadOnlyList<(string Label, DocumentResult Doc)>> RenderDocumentSuiteAsync(
        CancellationToken ct = default)
    {
        var engine = new PdfSharpDocumentEngine();

        return new List<(string, DocumentResult)>
        {
            ("CV",           await ItSession.GenerateSampleCvAsync(ct).ConfigureAwait(false)),
            ("Cover letter", await engine.RenderAsync(new DocumentRequest(DocumentKind.CoverLetter, SampleCoverLetter()), ct).ConfigureAwait(false)),
            ("Invoice",      await engine.RenderAsync(new DocumentRequest(DocumentKind.Invoice,      SampleInvoice()),     ct).ConfigureAwait(false)),
            ("Report",       await engine.RenderAsync(new DocumentRequest(DocumentKind.Report,       SampleReport()),      ct).ConfigureAwait(false)),
        };
    }

    // ── sample content (same fictional person as the CV, so the suite reads as
    //    one applicant's paperwork) ────────────────────────────────────────────

    private static CvContact Contact() => new(
        Email: "thabo.mokoena@example.co.za",
        Phone: "+27 82 555 0142",
        Location: "Soweto, Johannesburg",
        Links: new[] { "github.com/thabomokoena" });

    private static CoverLetter SampleCoverLetter() => new(
        SenderName:       "Thabo Mokoena",
        SenderContact:    Contact(),
        Date:             "24 July 2026",
        RecipientName:    "Ms Naidoo",
        RecipientTitle:   "Engineering Manager",
        RecipientCompany: "Yoco",
        RecipientAddress: "Cape Town",
        Subject:          "Application: Junior Software Developer",
        Greeting:         null,
        Body: new[]
        {
            "I am applying for the Junior Software Developer role. I build offline-first Android apps in C# and enjoy shipping small, reliable tools that solve real problems.",
            "In my internship I cut ticket turnaround from three days to one and automated a monthly report that saved the team about six hours a month. I would bring the same bias for practical impact to your team.",
            "I would welcome the chance to discuss how I can contribute. Thank you for your time and consideration.",
        });

    private static Invoice SampleInvoice() => new(
        From: new InvoiceParty("Thabo Mokoena", "Soweto, Johannesburg",
                               "thabo.mokoena@example.co.za", "+27 82 555 0142", "TAX-0123456789"),
        To:   new InvoiceParty("Gauteng Community Hub", "Johannesburg", "accounts@gch.example.co.za"),
        InvoiceNumber: "INV-2026-014",
        IssueDate:     "24 July 2026",
        DueDate:       "23 August 2026",
        LineItems: new[]
        {
            new InvoiceLineItem("Website design — 3 pages", 1m, 4500m),
            new InvoiceLineItem("Content updates",          6m,  350m),
            new InvoiceLineItem("Hosting setup",            1m,  800m),
        },
        VatPercent:   15m,
        CurrencyCode: "ZAR",
        PaymentNote:  "Bank Zero\nAccount 1234567890\nReference INV-2026-014");

    private static ReportDocument SampleReport() => new(
        Title:    "On-device capability report",
        Subtitle: "CircleAI Neuron — generated on the phone",
        Author:   "IT! sample",
        Date:     "24 July 2026",
        Sections: new[]
        {
            new ReportSection("Summary", new[]
            {
                "This report was generated fully offline on the device by the pure-managed document engine — no model, no network. If you are reading it as a PDF pulled off the phone, the render path works on ARM64.",
            }),
            new ReportSection("What ran", Bullets: new[]
            {
                "Document engine (PDFsharp + embedded DejaVu) on ARM64/EMUI.",
                "Model-selector verdict for every modality, from the embedded registry.",
                "All four document kinds: CV, cover letter, invoice, report.",
            }),
            new ReportSection("What the models need", new[]
            {
                "Speech (Whisper ASR, Piper TTS) fits this phone. Vision needs a 4 GB+ device — the 3B VLM is catalogued but gated off smaller phones, which is the honest verdict, not a crash.",
            }),
        });
}
