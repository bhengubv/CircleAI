#nullable enable

// IChartRenderer.cs
//
// The seam. A ChartSpec goes in; pixels land on a PDF surface. NOTHING here
// exposes which drawing library is used beyond the XGraphics/PdfPage types that
// ARE the integration point with CircleAI.Documents' PDFsharp pipeline. Swapping
// the concrete renderer is a one-class change no caller sees.
//
// Three entry points, cheapest coupling first:
//   * Render(...) onto a caller-owned XGraphics — THE embed path. A report
//     pipeline that already has an XGraphics for a page region calls this and the
//     chart is drawn inline, as vector, into that page.
//   * RenderToPage(...) onto a caller-owned PdfPage — same, when the caller has a
//     page but not yet an XGraphics.
//   * RenderToPdf(...) to a standalone one-page PDF (bytes) — for a preview, a
//     merge step, or a chart that is its own artifact.

using PdfSharp.Drawing;
using PdfSharp.Pdf;

namespace CircleAI.Charts;

/// <summary>Draws a <see cref="ChartSpec"/> onto a PDF surface, fully offline and on-device.</summary>
public interface IChartRenderer
{
    /// <summary>
    /// Draws the chart onto an existing <see cref="XGraphics"/> surface, confined
    /// to <paramref name="bounds"/> (in points, in the surface's coordinate
    /// space). This is the primitive the other two methods build on and the way
    /// to embed a chart into a page the host already owns.
    /// </summary>
    /// <param name="spec">What to draw.</param>
    /// <param name="gfx">The target surface (e.g. from <c>XGraphics.FromPdfPage</c>).</param>
    /// <param name="bounds">The rectangle to draw within.</param>
    /// <param name="style">Visual options; null uses <see cref="ChartStyle.Default"/>.</param>
    void Render(ChartSpec spec, XGraphics gfx, XRect bounds, ChartStyle? style = null);

    /// <summary>
    /// Draws the chart onto <paramref name="page"/>. When <paramref name="bounds"/>
    /// is null the chart fills the page (minus a default margin).
    /// </summary>
    void RenderToPage(ChartSpec spec, PdfPage page, XRect? bounds = null, ChartStyle? style = null);

    /// <summary>
    /// Renders the chart to a self-contained one-page PDF and returns the bytes.
    /// The page is sized to <paramref name="widthPoints"/> x
    /// <paramref name="heightPoints"/> (72 points = 1 inch).
    /// </summary>
    byte[] RenderToPdf(ChartSpec spec, double widthPoints = 480, double heightPoints = 320, ChartStyle? style = null);
}
