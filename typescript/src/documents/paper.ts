// PDF written by hand, and a music bed synthesised on the device.
//
// WHAT A PDF ACTUALLY IS, once the mystique is gone: a list of numbered
// objects, a table of their BYTE OFFSETS, and a trailer pointing at the root.
// The table is the only hard part, and it is hard for one reason - the offsets
// must be counted against the finished file, so nothing above the table may
// change length after the table is written. That is why this builds the body
// first and measures it, rather than streaming.
//
// TWO THINGS SOUND LIKE BROKEN CODE AND ARE ARITHMETIC:
//
//   * Summing voices at full amplitude CLIPS. Four notes each at 0.8 sum to
//     3.2, wrap in 16-bit, and come out as a buzz that sounds like a crashed
//     decoder. The mix is scaled by the voice count, not hoped about.
//
//   * A note that starts or stops at a non-zero sample CLICKS. A step is
//     broadband and the ear hears it as a tick on every note boundary. Every
//     note here gets an attack and a release.

// ─────────────────────────────────────────────────────────────────────────────
// The PDF writer

/** A4 in POINTS, which is what PDF measures in: 1/72 inch, always. */
export const A4_WIDTH = 595;
export const A4_HEIGHT = 842;

/**
 * Real Helvetica advance widths, in 1/1000 em.
 *
 * Not a flat 0.5: the digits and capitals are not uniform, and a CV whose name
 * overruns the margin looks careless. Only the characters a document uses are
 * here, and anything absent falls back to 556 - the width of a digit, which is
 * close to the average.
 */
const HELVETICA: Readonly<Record<string, number>> = Object.freeze({
  " ": 278, "!": 278, '"': 355, "#": 556, $: 556, "%": 889, "&": 667,
  "'": 191, "(": 333, ")": 333, "*": 389, "+": 584, ",": 278, "-": 333,
  ".": 278, "/": 278, ":": 278, ";": 278, "<": 584, "=": 584, ">": 584,
  "?": 556, "@": 1015, "[": 278, "\\": 278, "]": 278, "^": 469, _: 556,
  "`": 333, "{": 334, "|": 260, "}": 334, "~": 584,
  ...Object.fromEntries([..."0123456789"].map((c) => [c, 556])),
  ...Object.fromEntries(
    [..."ABCDEFGHIJKLMNOPQRSTUVWXYZ"].map((c, i) => [
      c,
      [667, 667, 722, 722, 667, 611, 778, 722, 278, 500, 667, 556, 833, 722,
        778, 667, 778, 722, 667, 611, 722, 667, 944, 667, 667, 611][i],
    ]),
  ),
  ...Object.fromEntries(
    [..."abcdefghijklmnopqrstuvwxyz"].map((c, i) => [
      c,
      [556, 556, 500, 556, 556, 278, 556, 556, 222, 222, 500, 222, 833, 556,
        556, 556, 556, 333, 500, 278, 556, 500, 722, 500, 500, 500][i],
    ]),
  ),
});

/**
 * Width in points, from the real metrics.
 *
 * Bold is approximated by a flat factor rather than a second table. It is
 * within a few percent across the alphabet, which is the difference between a
 * line that wraps a word early and one that runs off the page - and only the
 * second of those is a bug.
 */
export function textWidth(text: string, size: number, bold = false): number {
  let total = 0;
  for (const c of text) total += HELVETICA[c] ?? 556;
  return (total / 1000) * size * (bold ? 1.06 : 1);
}

/**
 * Greedy wrap by MEASURED width.
 *
 * A word longer than the whole line is emitted alone and allowed to overrun
 * rather than being broken: a URL cut in half is worse than a URL that pokes
 * into the margin, because only the second can still be typed back in.
 */
export function wrapText(text: string, widthPoints: number, size: number, bold = false): string[] {
  const lines: string[] = [];
  for (const paragraph of text.split("\n")) {
    let current = "";
    for (const word of paragraph.split(/\s+/).filter(Boolean)) {
      const candidate = current ? `${current} ${word}` : word;
      if (current && textWidth(candidate, size, bold) > widthPoints) {
        lines.push(current);
        current = word;
      } else current = candidate;
    }
    lines.push(current);
  }
  return lines;
}

/**
 * Escapes a PDF literal string.
 *
 * BACKSLASH FIRST. Escaping the parentheses first would then escape the
 * backslashes it just introduced, doubling them - the classic ordering bug, and
 * it shows up as visible backslashes in the rendered document.
 */
export function escapePdf(text: string): string {
  return text.replace(/\\/g, "\\\\").replace(/\(/g, "\\(").replace(/\)/g, "\\)");
}

/** Builds a one-file PDF from content streams. */
export class PdfWriter {
  private readonly pages: string[] = [];
  private current: string[] = [];

  constructor(
    readonly width = A4_WIDTH,
    readonly height = A4_HEIGHT,
  ) {}

  /**
   * `y` is measured from the TOP, converted here.
   *
   * PDF's origin is bottom-left and every other coordinate system in this
   * codebase is top-left. Converting once, at the boundary, is why nothing
   * above this line has to remember that.
   */
  text(text: string, x: number, y: number, size = 11, bold = false, grey = 0): void {
    this.current.push(
      `BT /${bold ? "F2" : "F1"} ${size.toFixed(2)} Tf ${grey.toFixed(3)} g ` +
        `${x.toFixed(2)} ${(this.height - y).toFixed(2)} Td (${escapePdf(text)}) Tj ET`,
    );
  }

  line(x0: number, y0: number, x1: number, y1: number, width = 0.5, grey = 0): void {
    this.current.push(
      `${grey.toFixed(3)} G ${width.toFixed(2)} w ${x0.toFixed(2)} ${(this.height - y0).toFixed(2)} m ` +
        `${x1.toFixed(2)} ${(this.height - y1).toFixed(2)} l S`,
    );
  }

  rect(x: number, y: number, w: number, h: number, grey = 0.9, fill = true): void {
    this.current.push(
      `${grey.toFixed(3)} ${fill ? "g" : "G"} ${x.toFixed(2)} ${(this.height - y - h).toFixed(2)} ` +
        `${w.toFixed(2)} ${h.toFixed(2)} re ${fill ? "f" : "S"}`,
    );
  }

  /**
   * A page with nothing on it is STILL A PAGE.
   *
   * A document that silently drops an empty page renumbers everything after it,
   * and a reader who was told "see page 4" finds page 5.
   */
  endPage(): void {
    this.pages.push(this.current.join("\n"));
    this.current = [];
  }

  get pageCount(): number {
    return this.pages.length + (this.current.length > 0 ? 1 : 0);
  }

  /**
   * Objects, then the xref table, then the trailer.
   *
   * Offsets are counted as the body is assembled, so nothing above the table
   * can change length afterwards. Building the body into a list and joining it
   * at the end would give the right bytes and the WRONG offsets, and the file
   * would open in one reader and fail in another - which is how this bug
   * survives a casual check.
   */
  build(): string {
    if (this.current.length > 0) this.endPage();
    if (this.pages.length === 0) this.endPage();

    const n = this.pages.length;
    // 1 catalog, 2 pages tree, 3 F1, 4 F2, then a page and a stream each.
    const firstPageObj = 5;
    const kids = this.pages.map((_, i) => `${firstPageObj + i * 2} 0 R`).join(" ");

    const objects: string[] = [
      "<< /Type /Catalog /Pages 2 0 R >>",
      `<< /Type /Pages /Count ${n} /Kids [${kids}] >>`,
      "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
      "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold /Encoding /WinAnsiEncoding >>",
    ];
    this.pages.forEach((content, i) => {
      objects.push(
        `<< /Type /Page /Parent 2 0 R /MediaBox [0 0 ${this.width} ${this.height}] ` +
          `/Resources << /Font << /F1 3 0 R /F2 4 0 R >> >> /Contents ${firstPageObj + i * 2 + 1} 0 R >>`,
      );
      objects.push(`<< /Length ${content.length} >>\nstream\n${content}\nendstream`);
    });

    let out = "%PDF-1.4\n";
    const offsets: number[] = [];
    objects.forEach((body, i) => {
      offsets.push(out.length);
      out += `${i + 1} 0 obj\n${body}\nendobj\n`;
    });

    const xrefAt = out.length;
    out += `xref\n0 ${objects.length + 1}\n`;
    // Every entry is EXACTLY 20 bytes including the line ending. A reader seeks
    // by multiplying, so one short entry misreads every object after it.
    out += "0000000000 65535 f \n";
    for (const offset of offsets) out += `${offset.toString().padStart(10, "0")} 00000 n \n`;
    out += `trailer\n<< /Size ${objects.length + 1} /Root 1 0 R >>\nstartxref\n${xrefAt}\n%%EOF\n`;
    return out;
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// What a document is

/** What is being made. */
export enum DocumentKind {
  Cv = "cv",
  CoverLetter = "cover-letter",
  Report = "report",
  Invoice = "invoice",
  Letter = "letter",
}

/** How it comes out. */
export enum DocumentFormat {
  Pdf = "pdf",
  /** Plain text, always available. The fallback that means a device with no
   * renderer still produces the document rather than an apology. */
  Text = "text",
  Markdown = "markdown",
  Html = "html",
}

/** A request to make one. */
export interface DocumentRequest {
  readonly kind: DocumentKind;
  readonly format: DocumentFormat;
  readonly title: string;
  /** The typed document. Untyped here because the engine dispatches on `kind`,
   * and threading a generic through every caller buys nothing a runtime check
   * does not. */
  readonly payload: unknown;
}

/** What came back. */
export interface DocumentResult {
  readonly text: string;
  readonly mediaType: string;
  readonly pageCount: number;
  /** Set when the request could not be met AT ALL. A result with no content and
   * no error is a bug, and this makes that shape impossible to read as
   * success. */
  readonly error: string;
}

export const documentResult = (partial: Partial<DocumentResult> = {}): DocumentResult =>
  Object.freeze({
    text: partial.text ?? "",
    mediaType: partial.mediaType ?? "application/pdf",
    pageCount: partial.pageCount ?? 0,
    error: partial.error ?? "",
  });

export const documentSucceeded = (r: DocumentResult): boolean => !r.error && r.text.length > 0;

// ─────────────────────────────────────────────────────────────────────────────
// CV

/**
 * How to reach somebody.
 *
 * Every field optional and NOTHING inferred. A CV generator that guesses an
 * email from a name gets it wrong in public, on the document a person is judged
 * by.
 */
export interface CvContact {
  readonly name: string;
  readonly email: string;
  readonly phone: string;
  readonly location: string;
  readonly website: string;
}

export const cvContact = (partial: Partial<CvContact> = {}): CvContact =>
  Object.freeze({
    name: partial.name ?? "",
    email: partial.email ?? "",
    phone: partial.phone ?? "",
    location: partial.location ?? "",
    website: partial.website ?? "",
  });

/** Only what was given. An empty field prints nothing rather than a
 * placeholder - a CV with "Phone: -" reads as unfinished. */
export const contactLines = (c: CvContact): string[] =>
  [c.email, c.phone, c.location, c.website].filter(Boolean);

/** One job. */
export interface CvExperience {
  readonly role: string;
  readonly organisation: string;
  readonly start: string;
  readonly end: string;
  readonly bullets: readonly string[];
  readonly location: string;
}

/** A missing end reads as "present", which is what it means on a CV and is the
 * one place an assumption here is safe. */
export const experiencePeriod = (e: CvExperience): string =>
  !e.start ? e.end : `${e.start} - ${e.end || "present"}`;

/** One qualification. */
export interface CvEducation {
  readonly qualification: string;
  readonly institution: string;
  readonly year: string;
  readonly detail: string;
}

/** One certificate. */
export interface CvCertification {
  readonly name: string;
  readonly issuer: string;
  readonly year: string;
  /** Deliberately not validated or fetched. Checking a credential against an
   * issuer means telling that issuer somebody is applying for a job. */
  readonly reference: string;
}

/** A whole CV. */
export interface CvDocument {
  readonly contact: CvContact;
  readonly headline: string;
  readonly summary: string;
  readonly experience: readonly CvExperience[];
  readonly education: readonly CvEducation[];
  readonly certifications: readonly CvCertification[];
  readonly skills: readonly string[];
}

/** The format that always works, and the one that survives a paste into an
 * application form that strips everything else. */
export function cvToText(cv: CvDocument): string {
  const out: string[] = [];
  if (cv.contact.name) out.push(cv.contact.name.toUpperCase(), "");
  if (cv.headline) out.push(cv.headline, "");
  out.push(...contactLines(cv.contact));
  if (cv.summary) out.push("", "SUMMARY", cv.summary);
  if (cv.experience.length) {
    out.push("", "EXPERIENCE");
    for (const job of cv.experience) {
      out.push(`${job.role}, ${job.organisation}  (${experiencePeriod(job)})`);
      out.push(...job.bullets.map((b) => `  - ${b}`));
    }
  }
  if (cv.education.length) {
    out.push("", "EDUCATION");
    out.push(
      ...cv.education.map(
        (e) => `${e.qualification}, ${e.institution}${e.year ? ` (${e.year})` : ""}`,
      ),
    );
  }
  if (cv.certifications.length) {
    out.push("", "CERTIFICATIONS");
    out.push(...cv.certifications.map((c) => [c.name, c.issuer, c.year].filter(Boolean).join(" - ")));
  }
  if (cv.skills.length) out.push("", "SKILLS", cv.skills.join(", "));
  return out.join("\n");
}

/** A letter to go with it. */
export interface CoverLetter {
  readonly sender: CvContact;
  readonly recipient: string;
  readonly organisation: string;
  readonly subject: string;
  readonly body: string;
  /** An ISO date, so the document formats it to the reader's convention rather
   * than baking in one country's order. */
  readonly writtenOnIso: string;
}

// ─────────────────────────────────────────────────────────────────────────────
// Invoice and report

/** Who an invoice is from or to. */
export interface InvoiceParty {
  readonly name: string;
  readonly addressLines: readonly string[];
  readonly vatNumber: string;
  readonly email: string;
}

/** One line on a printed invoice. */
export interface InvoiceLineItem {
  readonly description: string;
  readonly quantity: number;
  /** In MINOR UNITS. The document formats it; it never does arithmetic on a
   * decimal. */
  readonly unitPriceMinor: number;
  readonly taxBasisPoints: number;
  readonly currency: string;
}

/** A table in a report. */
export interface ReportTable {
  readonly headers: readonly string[];
  readonly rows: readonly (readonly string[])[];
  readonly caption: string;
}

/**
 * The WIDEST row, not the header count.
 *
 * A row with an extra cell would otherwise be silently truncated, which loses
 * data in a document somebody is about to act on.
 */
export const tableColumnCount = (t: ReportTable): number =>
  Math.max(t.headers.length, ...t.rows.map((r) => r.length), 0);

/** One section. */
export interface ReportSection {
  readonly heading: string;
  readonly body: string;
  readonly tables: readonly ReportTable[];
  /** Sections nest. Depth is computed on render rather than stored, so moving a
   * section cannot leave it labelled with its old level. */
  readonly subsections: readonly ReportSection[];
}

/** A whole report. */
export interface ReportDocument {
  readonly title: string;
  readonly subtitle: string;
  readonly author: string;
  readonly writtenOnIso: string;
  readonly sections: readonly ReportSection[];
}

/**
 * Flattens to (number, depth, section) in reading order.
 *
 * Numbers are DERIVED, so inserting a section renumbers everything after it
 * automatically - a stored number is a cross-reference that silently goes
 * wrong.
 */
export function numberedSections(
  report: ReportDocument,
): { number: string; depth: number; section: ReportSection }[] {
  const out: { number: string; depth: number; section: ReportSection }[] = [];
  const walk = (sections: readonly ReportSection[], prefix: string, depth: number): void => {
    sections.forEach((section, i) => {
      const number = `${prefix}${i + 1}`;
      out.push({ number, depth, section });
      walk(section.subsections, `${number}.`, depth + 1);
    });
  };
  walk(report.sections, "", 0);
  return out;
}

// ─────────────────────────────────────────────────────────────────────────────
// The engine

/** Renders a document request. */
export interface DocumentEngine {
  render(request: DocumentRequest): DocumentResult;
  supports(format: DocumentFormat): boolean;
}

/**
 * The default engine: PDF and plain text, both ours.
 *
 * Named for the C# class it mirrors. Nothing here uses PdfSharp - the writer
 * above is the whole implementation.
 */
export class PdfSharpDocumentEngine implements DocumentEngine {
  static readonly MARGIN = 56;
  static readonly LEADING = 15;

  supports(format: DocumentFormat): boolean {
    return format === DocumentFormat.Pdf || format === DocumentFormat.Text;
  }

  render(request: DocumentRequest): DocumentResult {
    if (request.format === DocumentFormat.Text) {
      const payload = request.payload;
      const text =
        payload && typeof payload === "object" && "contact" in payload
          ? cvToText(payload as CvDocument)
          : String(payload ?? "");
      return documentResult({ text, mediaType: "text/plain", pageCount: 1 });
    }
    if (request.format !== DocumentFormat.Pdf) {
      return documentResult({
        error: `this engine renders PDF and plain text, not ${request.format}`,
      });
    }
    if (request.kind === DocumentKind.Cv && request.payload && typeof request.payload === "object") {
      return this.renderCv(request.payload as CvDocument);
    }
    if (request.kind === DocumentKind.Report && request.payload && typeof request.payload === "object") {
      return this.renderReport(request.payload as ReportDocument);
    }
    return documentResult({ error: `nothing here can render a ${request.kind}` });
  }

  /**
   * Writes wrapped text and returns the new baseline, breaking pages.
   *
   * THE PAGE BREAK HAPPENS BEFORE THE LINE IS DRAWN, not after. Drawing first
   * and checking after puts one line past the bottom edge of every page it
   * fills - the classic off-by-one that only shows on long documents.
   */
  private flow(
    pdf: PdfWriter,
    y: number,
    text: string,
    size: number,
    bold = false,
    indent = 0,
    grey = 0,
  ): number {
    const width = pdf.width - 2 * PdfSharpDocumentEngine.MARGIN - indent;
    let at = y;
    for (const line of wrapText(text, width, size, bold)) {
      if (at > pdf.height - PdfSharpDocumentEngine.MARGIN) {
        pdf.endPage();
        at = PdfSharpDocumentEngine.MARGIN + size;
      }
      pdf.text(line, PdfSharpDocumentEngine.MARGIN + indent, at, size, bold, grey);
      at += PdfSharpDocumentEngine.LEADING * (size / 11);
    }
    return at;
  }

  private renderCv(cv: CvDocument): DocumentResult {
    const pdf = new PdfWriter();
    let y = PdfSharpDocumentEngine.MARGIN + 18;
    if (cv.contact.name) y = this.flow(pdf, y, cv.contact.name, 20, true);
    if (cv.headline) y = this.flow(pdf, y + 2, cv.headline, 11, false, 0, 0.35);
    const contact = contactLines(cv.contact).join("  ");
    if (contact) y = this.flow(pdf, y + 2, contact, 9, false, 0, 0.35);
    pdf.line(
      PdfSharpDocumentEngine.MARGIN,
      y + 6,
      pdf.width - PdfSharpDocumentEngine.MARGIN,
      y + 6,
      0.5,
      0.6,
    );
    y += 14;

    const heading = (label: string, at: number): number =>
      this.flow(pdf, at + 6, label.toUpperCase(), 10, true, 0, 0.25) + 2;

    if (cv.summary) y = this.flow(pdf, heading("Summary", y), cv.summary, 10);
    if (cv.experience.length) {
      y = heading("Experience", y);
      for (const job of cv.experience) {
        y = this.flow(pdf, y, `${job.role}, ${job.organisation}`, 11, true);
        const meta = [experiencePeriod(job), job.location].filter(Boolean).join("  ");
        if (meta) y = this.flow(pdf, y, meta, 9, false, 0, 0.4);
        for (const bullet of job.bullets) y = this.flow(pdf, y, `- ${bullet}`, 10, false, 12);
        y += 4;
      }
    }
    if (cv.education.length) {
      y = heading("Education", y);
      for (const e of cv.education) {
        y = this.flow(pdf, y, `${e.qualification}, ${e.institution}${e.year ? ` (${e.year})` : ""}`, 10);
      }
    }
    if (cv.certifications.length) {
      y = heading("Certifications", y);
      for (const c of cv.certifications) {
        y = this.flow(pdf, y, [c.name, c.issuer, c.year].filter(Boolean).join(" - "), 10);
      }
    }
    if (cv.skills.length) y = this.flow(pdf, heading("Skills", y), cv.skills.join(", "), 10);

    return documentResult({ text: pdf.build(), pageCount: pdf.pageCount });
  }

  private renderReport(report: ReportDocument): DocumentResult {
    const pdf = new PdfWriter();
    let y = PdfSharpDocumentEngine.MARGIN + 20;
    if (report.title) y = this.flow(pdf, y, report.title, 22, true);
    if (report.subtitle) y = this.flow(pdf, y + 2, report.subtitle, 12, false, 0, 0.35);
    const meta = [report.author, report.writtenOnIso].filter(Boolean).join(" - ");
    if (meta) y = this.flow(pdf, y + 2, meta, 9, false, 0, 0.4);

    for (const { number, depth, section } of numberedSections(report)) {
      const size = Math.max(10, 15 - depth * 2);
      y = this.flow(pdf, y + 8, `${number}  ${section.heading}`, size, true, depth * 10);
      if (section.body) y = this.flow(pdf, y + 2, section.body, 10, false, depth * 10);
      for (const table of section.tables) y = this.renderTable(pdf, y + 6, table, depth * 10);
    }
    return documentResult({ text: pdf.build(), pageCount: pdf.pageCount });
  }

  private renderTable(pdf: PdfWriter, y: number, table: ReportTable, indent: number): number {
    const columns = tableColumnCount(table);
    if (columns === 0) return y;
    const available = pdf.width - 2 * PdfSharpDocumentEngine.MARGIN - indent;
    const col = available / columns;
    let at = y;
    if (table.caption) at = this.flow(pdf, at, table.caption, 9, true, indent, 0.3);
    if (table.headers.length) {
      pdf.rect(PdfSharpDocumentEngine.MARGIN + indent, at - 10, available, 14, 0.92);
      table.headers.forEach((head, i) => {
        pdf.text(head, PdfSharpDocumentEngine.MARGIN + indent + i * col + 3, at, 9, true);
      });
      at += PdfSharpDocumentEngine.LEADING;
    }
    for (const row of table.rows) {
      if (at > pdf.height - PdfSharpDocumentEngine.MARGIN) {
        pdf.endPage();
        at = PdfSharpDocumentEngine.MARGIN + 10;
      }
      row.forEach((cell, i) => {
        // Cells are CLIPPED by measurement, not wrapped: a table that reflows
        // mid-column stops lining up, and a table that does not line up is
        // unreadable in a way a truncated cell is not.
        let text = cell;
        while (text && textWidth(text, 9) > col - 6) text = text.slice(0, -1);
        if (text !== cell && text.length > 1) text = `${text.slice(0, -1)}…`;
        pdf.text(text, PdfSharpDocumentEngine.MARGIN + indent + i * col + 3, at, 9);
      });
      at += PdfSharpDocumentEngine.LEADING;
    }
    return at;
  }
}

/**
 * Renders plain text and nothing else.
 *
 * Not a throw: a CV as text is still a CV, and a person who needs one today can
 * paste it into a form.
 */
export class NullDocumentEngine implements DocumentEngine {
  supports(format: DocumentFormat): boolean {
    return format === DocumentFormat.Text;
  }
  render(request: DocumentRequest): DocumentResult {
    const payload = request.payload;
    const text =
      payload && typeof payload === "object" && "contact" in payload
        ? cvToText(payload as CvDocument)
        : String(payload ?? "");
    return documentResult({ text, mediaType: "text/plain", pageCount: 1 });
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// Music

/** The shape of a block of PCM. */
export interface AudioPcmFormat {
  readonly sampleRateHz: number;
  readonly channels: number;
  readonly bitsPerSample: number;
}

export const musicFormat = (): AudioPcmFormat =>
  Object.freeze({ sampleRateHz: 22050, channels: 1, bitsPerSample: 16 });

/**
 * Writes a RIFF/WAVE file.
 *
 * THE TWO SIZE FIELDS ARE DIFFERENT: the RIFF size is the whole file minus 8,
 * and the data size is the PCM bytes only. Getting either wrong produces a file
 * that plays in one program and not another - the worst kind of wrong, because
 * the first program you test in is usually the forgiving one.
 */
export class WavWriter {
  static header(format: AudioPcmFormat, dataBytes: number): Uint8Array {
    const out = new Uint8Array(44);
    const view = new DataView(out.buffer);
    const ascii = (at: number, s: string) => {
      for (let i = 0; i < s.length; i++) out[at + i] = s.charCodeAt(i);
    };
    const blockAlign = (format.channels * format.bitsPerSample) / 8;
    ascii(0, "RIFF");
    view.setUint32(4, 36 + dataBytes, true);
    ascii(8, "WAVEfmt ");
    view.setUint32(16, 16, true);
    view.setUint16(20, 1, true);
    view.setUint16(22, format.channels, true);
    view.setUint32(24, format.sampleRateHz, true);
    view.setUint32(28, format.sampleRateHz * blockAlign, true);
    view.setUint16(32, blockAlign, true);
    view.setUint16(34, format.bitsPerSample, true);
    ascii(36, "data");
    view.setUint32(40, dataBytes, true);
    return out;
  }

  /**
   * Floats in -1..1 to 16-bit, CLAMPED not wrapped.
   *
   * A sample of 1.2 that wraps becomes a large negative number - a click at
   * full scale, louder than anything else in the file. Scaled by 32767 rather
   * than 32768 so +1.0 is representable and does not become the one value that
   * wraps.
   */
  static pcmFrom(samples: readonly number[]): Uint8Array {
    const out = new Uint8Array(samples.length * 2);
    const view = new DataView(out.buffer);
    for (let i = 0; i < samples.length; i++) {
      const s = samples[i] < -1 ? -1 : samples[i] > 1 ? 1 : samples[i];
      view.setInt16(i * 2, Math.round(s * 32767), true);
    }
    return out;
  }

  static write(format: AudioPcmFormat, pcm: Uint8Array): Uint8Array {
    const head = WavWriter.header(format, pcm.length);
    const out = new Uint8Array(head.length + pcm.length);
    out.set(head, 0);
    out.set(pcm, head.length);
    return out;
  }
}

/**
 * The twelve, as semitones above C.
 *
 * Sharps only. A separate flat spelling would be musically correct and would
 * double every lookup table for no audible difference - E flat and D sharp are
 * the same frequency.
 */
export enum PitchClass {
  C = 0,
  CSharp = 1,
  D = 2,
  DSharp = 3,
  E = 4,
  F = 5,
  FSharp = 6,
  G = 7,
  GSharp = 8,
  A = 9,
  ASharp = 10,
  B = 11,
}

/** Which notes are in play. */
export enum Scale {
  Major = "major",
  /** Natural minor. The one people mean by "sad". */
  Minor = "minor",
  /** Five notes, no semitone clashes. ANY two notes in it sound fine together,
   * which makes it the safe default for a generated bed - a bad random choice
   * is still consonant. */
  Pentatonic = "pentatonic",
  Dorian = "dorian",
  /** Whole tones only. Deliberately unresolved; used for tension, never for a
   * bed somebody has to listen to for four minutes. */
  WholeTone = "whole-tone",
}

const SCALE_INTERVALS: Readonly<Record<string, readonly number[]>> = Object.freeze({
  [Scale.Major]: Object.freeze([0, 2, 4, 5, 7, 9, 11]),
  [Scale.Minor]: Object.freeze([0, 2, 3, 5, 7, 8, 10]),
  [Scale.Pentatonic]: Object.freeze([0, 2, 4, 7, 9]),
  [Scale.Dorian]: Object.freeze([0, 2, 3, 5, 7, 9, 10]),
  [Scale.WholeTone]: Object.freeze([0, 2, 4, 6, 8, 10]),
});

/** A tonic and a scale. */
export class MusicalKey {
  /** A4 = 440 Hz is MIDI note 69. Equal temperament, so every semitone is the
   * twelfth root of two - the formula rather than a table, because a table has
   * to stop somewhere and this does not. */
  static readonly A4_MIDI = 69;
  static readonly A4_HZ = 440;

  constructor(
    readonly tonic: PitchClass = PitchClass.C,
    readonly scale: Scale = Scale.Pentatonic,
  ) {}

  static frequencyOf(midiNote: number): number {
    return MusicalKey.A4_HZ * 2 ** ((midiNote - MusicalKey.A4_MIDI) / 12);
  }

  /**
   * MIDI notes of the scale, ascending, wrapping into higher octaves.
   *
   * C4 is MIDI 60, so an octave number maps to (octave + 1) * 12. Getting that
   * offset wrong transposes everything by an octave, which sounds fine and is
   * wrong - the bed ends up under or over the voice it should sit with.
   */
  degrees(octave = 4, count = 0): number[] {
    const intervals = SCALE_INTERVALS[this.scale];
    const wanted = count || intervals.length;
    const base = (octave + 1) * 12 + this.tonic;
    return Array.from(
      { length: wanted },
      (_, i) => base + 12 * Math.floor(i / intervals.length) + intervals[i % intervals.length],
    );
  }

  frequencies(octave = 4, count = 0): number[] {
    return this.degrees(octave, count).map(MusicalKey.frequencyOf);
  }
}

/** Where a bed comes from. */
export enum MusicBedBackend {
  /** Sine tones from a scale. Always available, ours, free. */
  Procedural = "procedural",
  /** A model. Only when one has been downloaded. */
  Neural = "neural",
  /** A file the person supplied. Their licence, their decision. */
  SampleLibrary = "sample-library",
  None = "none",
}

/** What kind of bed is wanted. */
export interface MusicSpec {
  readonly key: MusicalKey;
  /** Under a voice, slower is better - a bed that competes for attention with
   * the words is a bed that failed. */
  readonly tempoBpm: number;
  readonly durationSeconds: number;
  /** 0..1, and the default is deliberately low. A bed at conversational level
   * is not a bed. */
  readonly level: number;
  readonly voices: number;
  readonly format: AudioPcmFormat;
  readonly seed: number;
}

export const musicSpec = (partial: Partial<MusicSpec> = {}): MusicSpec =>
  Object.freeze({
    key: partial.key ?? new MusicalKey(),
    tempoBpm: partial.tempoBpm ?? 72,
    durationSeconds: partial.durationSeconds ?? 8,
    level: partial.level ?? 0.18,
    voices: partial.voices ?? 3,
    format: partial.format ?? musicFormat(),
    seed: partial.seed ?? 0,
  });

/** The rendered result. */
export interface MusicBed {
  readonly pcm: Uint8Array;
  readonly format: AudioPcmFormat;
  readonly backend: MusicBedBackend;
  readonly durationSeconds: number;
  /** Set when nothing could be made. Empty PCM with no reason is a bug that
   * reads as silence. */
  readonly error: string;
}

export const bedToWav = (bed: MusicBed): Uint8Array => WavWriter.write(bed.format, bed.pcm);

/** Makes a bed. */
export interface MusicBedGenerator {
  readonly backend: MusicBedBackend;
  readonly isAvailable: boolean;
  generate(spec: MusicSpec): MusicBed;
}

/**
 * Makes silence, and says so.
 *
 * Returns a bed with an error rather than throwing: a clip with no music is
 * still a clip, and failing the whole render because the bed could not be made
 * is the wrong trade.
 */
export class NullMusicBedGenerator implements MusicBedGenerator {
  readonly backend = MusicBedBackend.None;
  readonly isAvailable = true;
  generate(spec: MusicSpec): MusicBed {
    return Object.freeze({
      pcm: new Uint8Array(0),
      format: spec.format,
      backend: MusicBedBackend.None,
      durationSeconds: 0,
      error: "no music generator is configured on this device",
    });
  }
}

/**
 * Sine tones from a scale, mixed and enveloped.
 *
 * DETERMINISTIC from the spec's seed, so the same spec makes the same bed -
 * which matters because a person who liked yesterday's clip should be able to
 * make it again.
 */
export class ProceduralMusicBedGenerator implements MusicBedGenerator {
  readonly backend = MusicBedBackend.Procedural;
  readonly isAvailable = true;

  /**
   * Attack and release, in seconds. Short enough to be inaudible as a fade and
   * long enough to remove the click: a step at 22050 Hz is broadband and the
   * ear hears it as a tick, which is the single most common defect in generated
   * audio.
   */
  static readonly ENVELOPE_SECONDS = 0.02;

  /**
   * A tiny LCG, so the bed does not depend on the platform's generator.
   *
   * `Math.random` is shared and unseedable; a bed that used it would change
   * because something unrelated drew a number first.
   */
  private static nextRandom(state: number): { state: number; value: number } {
    const next = (Math.imul(state, 1103515245) + 12345) & 0x7fffffff;
    return { state: next, value: next / 0x7fffffff };
  }

  private envelope(index: number, total: number, rate: number): number {
    const ramp = Math.max(1, Math.round(ProceduralMusicBedGenerator.ENVELOPE_SECONDS * rate));
    if (index < ramp) return index / ramp;
    if (index >= total - ramp) return Math.max(0, (total - index) / ramp);
    return 1;
  }

  generate(spec: MusicSpec): MusicBed {
    const rate = spec.format.sampleRateHz;
    const total = Math.floor(spec.durationSeconds * rate);
    if (total <= 0) {
      return Object.freeze({
        pcm: new Uint8Array(0),
        format: spec.format,
        backend: this.backend,
        durationSeconds: 0,
        error: "a bed needs a duration",
      });
    }

    const voices = Math.max(1, spec.voices);
    const pool = spec.key.frequencies(3, Math.max(5, voices * 2));
    let state = spec.seed || 1;
    const samples = new Float64Array(total);
    const secondsPerBeat = 60 / Math.max(1, spec.tempoBpm);
    const noteFrames = Math.max(1, Math.floor(secondsPerBeat * 2 * rate));

    for (let voice = 0; voice < voices; voice++) {
      // Each voice starts at a different point so they do not all change note
      // together - simultaneous changes sound like a chord machine rather than
      // a bed.
      let position = -Math.floor((voice * noteFrames) / voices);
      while (position < total) {
        const drawn = ProceduralMusicBedGenerator.nextRandom(state);
        state = drawn.state;
        const frequency = pool[Math.floor(drawn.value * pool.length) % pool.length];
        const length = Math.min(noteFrames, total - Math.max(0, position));
        if (length <= 0) break;
        for (let i = 0; i < length; i++) {
          const n = position + i;
          if (n < 0 || n >= total) continue;
          samples[n] += Math.sin((2 * Math.PI * frequency * n) / rate) * this.envelope(i, length, rate);
        }
        position += noteFrames;
      }
    }

    // SCALED BY THE VOICE COUNT. Without this, three voices each reaching 1.0
    // sum to 3.0, wrap in 16-bit and come out as a buzz that sounds exactly
    // like a broken decoder.
    const scale = spec.level / voices;
    const scaled = Array.from(samples, (s) => s * scale);
    return Object.freeze({
      pcm: WavWriter.pcmFrom(scaled),
      format: spec.format,
      backend: this.backend,
      durationSeconds: total / rate,
      error: "",
    });
  }
}

/**
 * Picks a generator, preferring the one that is actually there.
 *
 * PROCEDURAL IS THE FLOOR and never absent. A resolver that could return
 * nothing would make every caller handle a case that need not exist.
 */
export class MusicBedGeneratorResolver {
  private readonly fallback = new ProceduralMusicBedGenerator();

  constructor(private readonly generators: readonly MusicBedGenerator[] = []) {}

  resolve(preferred?: MusicBedBackend): MusicBedGenerator {
    if (preferred) {
      const wanted = this.generators.find((g) => g.backend === preferred && g.isAvailable);
      if (wanted) return wanted;
    }
    return (
      this.generators.find((g) => g.isAvailable && g.backend !== MusicBedBackend.None) ??
      this.fallback
    );
  }

  availableBackends(): readonly MusicBedBackend[] {
    const found = new Set(this.generators.filter((g) => g.isAvailable).map((g) => g.backend));
    found.add(MusicBedBackend.Procedural);
    return Object.freeze([...found].sort());
  }
}

// The C# spellings, kept so the two trees line up.
export type IDocumentEngine = DocumentEngine;
export type IMusicBedGenerator = MusicBedGenerator;
