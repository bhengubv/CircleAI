"""PDF, written by hand, and the documents a person actually asks for.

WHY BY HAND. The same reason the pixels are: every PDF library is a native blob
per architecture or a licence to argue about, and a CV that only generates on a
device with the right shared object is not a feature.

WHAT A PDF ACTUALLY IS, once the mystique is gone: a list of numbered objects,
a table of their BYTE OFFSETS, and a trailer pointing at the root. The table is
the only hard part, and it is hard for one reason - the offsets must be counted
against the finished file, so nothing above the table may change length after
the table is written. That is why this builds the body first and measures it,
rather than streaming.

THE PDF NAME IS THE C# TYPE'S. The C# used PdfSharp; nothing here does. The
names are kept so the two trees line up and a reader looking for the same class
finds it - the implementation underneath is ours, and says so.
"""

from __future__ import annotations

import re
from abc import ABC, abstractmethod
from dataclasses import dataclass, field
from datetime import date
from enum import Enum
from typing import Sequence


# ─────────────────────────────────────────────────────────────────────────────
# The writer

#: A4 in POINTS, which is what PDF measures in: 1/72 inch, always, regardless of
#: any device's pixels. 210mm x 297mm to the nearest point.
A4_WIDTH = 595
A4_HEIGHT = 842

#: Widths of the 14 standard fonts are in 1/1000 em. Helvetica is close enough
#: to uniform for our purposes at 0.5 em average, but the digits and caps are
#: not, and a CV whose name overruns the margin looks careless. These are the
#: real Helvetica advance widths for the characters a document uses.
_HELVETICA_WIDTHS: dict[str, int] = {
    " ": 278, "!": 278, '"': 355, "#": 556, "$": 556, "%": 889, "&": 667,
    "'": 191, "(": 333, ")": 333, "*": 389, "+": 584, ",": 278, "-": 333,
    ".": 278, "/": 278, ":": 278, ";": 278, "<": 584, "=": 584, ">": 584,
    "?": 556, "@": 1015, "[": 278, "\\": 278, "]": 278, "^": 469, "_": 556,
    "`": 333, "{": 334, "|": 260, "}": 334, "~": 584,
}
for _c in "0123456789":
    _HELVETICA_WIDTHS[_c] = 556
for _c, _w in zip(
    "ABCDEFGHIJKLMNOPQRSTUVWXYZ",
    (667, 667, 722, 722, 667, 611, 778, 722, 278, 500, 667, 556, 833, 722,
     778, 667, 778, 722, 667, 611, 722, 667, 944, 667, 667, 611),
):
    _HELVETICA_WIDTHS[_c] = _w
for _c, _w in zip(
    "abcdefghijklmnopqrstuvwxyz",
    (556, 556, 500, 556, 556, 278, 556, 556, 222, 222, 500, 222, 833, 556,
     556, 556, 556, 333, 500, 278, 556, 500, 722, 500, 500, 500),
):
    _HELVETICA_WIDTHS[_c] = _w


def text_width(text: str, size: float, bold: bool = False) -> float:
    """Width in points, from the real Helvetica metrics.

    Bold is approximated by a flat factor rather than a second table. It is
    within a few percent across the alphabet, which is the difference between a
    line that wraps a word early and one that runs off the page - and only the
    second of those is a bug.
    """
    total = sum(_HELVETICA_WIDTHS.get(c, 556) for c in text)
    return total / 1000.0 * size * (1.06 if bold else 1.0)


def wrap_text(
    text: str, width_points: float, size: float, bold: bool = False
) -> list[str]:
    """Greedy wrap by MEASURED width.

    A word longer than the whole line is emitted alone and allowed to overrun,
    rather than being broken: a URL cut in half is worse than a URL that pokes
    into the margin, because only one of the two can still be typed back in.
    """
    lines: list[str] = []
    for paragraph in text.split("\n"):
        current = ""
        for word in paragraph.split():
            candidate = f"{current} {word}".strip()
            if current and text_width(candidate, size, bold) > width_points:
                lines.append(current)
                current = word
            else:
                current = candidate
        lines.append(current)
    return lines


def _escape(text: str) -> str:
    r"""Escapes a PDF literal string.

    Backslash FIRST. Escaping the parentheses first would then escape the
    backslashes it just introduced, doubling them - the classic ordering bug,
    and it shows up as visible backslashes in the rendered document.
    """
    return text.replace("\\", r"\\").replace("(", r"\(").replace(")", r"\)")


class PdfWriter:
    """Builds a one-file PDF from content streams."""

    def __init__(self, width: int = A4_WIDTH, height: int = A4_HEIGHT) -> None:
        self.width = width
        self.height = height
        self._pages: list[str] = []
        self._current: list[str] = []

    # ── drawing ──────────────────────────────────────────────────────────────

    def text(
        self, text: str, x: float, y: float, size: float = 11,
        bold: bool = False, grey: float = 0.0,
    ) -> None:
        """`y` is measured from the TOP, converted here.

        PDF's origin is bottom-left and every other coordinate system in this
        codebase is top-left. Converting once, at the boundary, is why nothing
        above this line has to remember that.
        """
        font = "F2" if bold else "F1"
        self._current.append(
            f"BT /{font} {size:.2f} Tf {grey:.3f} g "
            f"{x:.2f} {self.height - y:.2f} Td ({_escape(text)}) Tj ET"
        )

    def line(
        self, x0: float, y0: float, x1: float, y1: float,
        width: float = 0.5, grey: float = 0.0,
    ) -> None:
        self._current.append(
            f"{grey:.3f} G {width:.2f} w {x0:.2f} {self.height - y0:.2f} m "
            f"{x1:.2f} {self.height - y1:.2f} l S"
        )

    def rect(
        self, x: float, y: float, w: float, h: float,
        grey: float = 0.9, fill: bool = True,
    ) -> None:
        op = "f" if fill else "S"
        setter = "g" if fill else "G"
        self._current.append(
            f"{grey:.3f} {setter} {x:.2f} {self.height - y - h:.2f} "
            f"{w:.2f} {h:.2f} re {op}"
        )

    def end_page(self) -> None:
        """A page with nothing on it is still a page.

        A document that silently drops an empty page renumbers everything after
        it, and a reader who was told "see page 4" finds page 5.
        """
        self._pages.append("\n".join(self._current))
        self._current = []

    @property
    def page_count(self) -> int:
        return len(self._pages) + (1 if self._current else 0)

    # ── assembly ─────────────────────────────────────────────────────────────

    def build(self) -> bytes:
        """Objects, then the xref table, then the trailer.

        Offsets are counted as the body is assembled, so nothing above the table
        can change length afterwards. Building the body into a list and joining
        it at the end would give the right bytes and the WRONG offsets, and the
        file would open in one reader and fail in another - which is how this
        bug survives a casual check.
        """
        if self._current:
            self.end_page()
        if not self._pages:
            self.end_page()

        n = len(self._pages)
        # 1 catalog, 2 pages tree, 3 F1, 4 F2, then a page and a stream each.
        first_page_obj = 5
        kids = " ".join(f"{first_page_obj + i * 2} 0 R" for i in range(n))

        objects: list[bytes] = [
            b"<< /Type /Catalog /Pages 2 0 R >>",
            f"<< /Type /Pages /Count {n} /Kids [{kids}] >>".encode(),
            b"<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica "
            b"/Encoding /WinAnsiEncoding >>",
            b"<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold "
            b"/Encoding /WinAnsiEncoding >>",
        ]
        for i, content in enumerate(self._pages):
            stream = content.encode("latin-1", "replace")
            objects.append(
                f"<< /Type /Page /Parent 2 0 R /MediaBox "
                f"[0 0 {self.width} {self.height}] /Resources << /Font "
                f"<< /F1 3 0 R /F2 4 0 R >> >> /Contents "
                f"{first_page_obj + i * 2 + 1} 0 R >>".encode()
            )
            objects.append(
                f"<< /Length {len(stream)} >>\nstream\n".encode()
                + stream + b"\nendstream"
            )

        out = bytearray(b"%PDF-1.4\n")
        offsets: list[int] = []
        for i, body in enumerate(objects, start=1):
            offsets.append(len(out))
            out += f"{i} 0 obj\n".encode() + body + b"\nendobj\n"

        xref_at = len(out)
        out += f"xref\n0 {len(objects) + 1}\n".encode()
        # Every entry is EXACTLY 20 bytes including the line ending. A reader
        # seeks by multiplying, so one short entry misreads every object after
        # it.
        out += b"0000000000 65535 f \n"
        for off in offsets:
            out += f"{off:010d} 00000 n \n".encode()
        out += (
            f"trailer\n<< /Size {len(objects) + 1} /Root 1 0 R >>\n"
            f"startxref\n{xref_at}\n%%EOF\n"
        ).encode()
        return bytes(out)


# ─────────────────────────────────────────────────────────────────────────────
# What a document is


class DocumentKind(Enum):
    """What is being made."""

    CV = "cv"
    COVER_LETTER = "cover-letter"
    REPORT = "report"
    INVOICE = "invoice"
    LETTER = "letter"


class DocumentFormat(Enum):
    """How it comes out."""

    PDF = "pdf"
    #: Plain text, always available. The fallback that means a device with no
    #: renderer still produces the document rather than an apology.
    TEXT = "text"
    MARKDOWN = "markdown"
    HTML = "html"


@dataclass(frozen=True)
class DocumentRequest:
    """A request to make one."""

    kind: DocumentKind
    format: DocumentFormat = DocumentFormat.PDF
    title: str = ""
    #: The typed document - a `CvDocument`, `ReportDocument` and so on. Untyped
    #: here because the engine dispatches on `kind`, and threading a generic
    #: through every caller buys nothing a runtime check does not.
    payload: object = None


@dataclass(frozen=True)
class DocumentResult:
    """What came back."""

    data: bytes = b""
    media_type: str = "application/pdf"
    page_count: int = 0
    #: Set when the request could not be met AT ALL. A result with no bytes and
    #: no error is a bug, and this makes that shape impossible to read as
    #: success.
    error: str = ""

    @property
    def succeeded(self) -> bool:
        return not self.error and bool(self.data)


# ─────────────────────────────────────────────────────────────────────────────
# CV


@dataclass(frozen=True)
class CvContact:
    """How to reach somebody.

    Every field optional and NOTHING inferred. A CV generator that guesses an
    email from a name gets it wrong in public, on the document a person is
    judged by.
    """

    name: str = ""
    email: str = ""
    phone: str = ""
    location: str = ""
    website: str = ""

    def lines(self) -> list[str]:
        """Only what was given. An empty field prints nothing rather than a
        placeholder - a CV with "Phone: -" reads as unfinished."""
        return [v for v in (self.email, self.phone, self.location, self.website) if v]


@dataclass(frozen=True)
class CvExperience:
    """One job."""

    role: str
    organisation: str
    start: str = ""
    end: str = ""
    bullets: tuple[str, ...] = ()
    location: str = ""

    @property
    def period(self) -> str:
        """A missing end reads as "present", which is what it means on a CV and
        is the one place an assumption here is safe."""
        if not self.start:
            return self.end
        return f"{self.start} - {self.end or 'present'}"


@dataclass(frozen=True)
class CvEducation:
    """One qualification."""

    qualification: str
    institution: str
    year: str = ""
    detail: str = ""


@dataclass(frozen=True)
class CvCertification:
    """One certificate."""

    name: str
    issuer: str = ""
    year: str = ""
    #: Deliberately not validated or fetched. Checking a credential against an
    #: issuer means telling that issuer somebody is applying for a job.
    reference: str = ""


@dataclass(frozen=True)
class CvDocument:
    """A whole CV."""

    contact: CvContact = field(default_factory=CvContact)
    headline: str = ""
    summary: str = ""
    experience: tuple[CvExperience, ...] = ()
    education: tuple[CvEducation, ...] = ()
    certifications: tuple[CvCertification, ...] = ()
    skills: tuple[str, ...] = ()

    def to_text(self) -> str:
        """The format that always works, and the one that survives a paste into
        an application form that strips everything else."""
        out: list[str] = []
        if self.contact.name:
            out += [self.contact.name.upper(), ""]
        if self.headline:
            out += [self.headline, ""]
        for line in self.contact.lines():
            out.append(line)
        if self.summary:
            out += ["", "SUMMARY", self.summary]
        if self.experience:
            out += ["", "EXPERIENCE"]
            for job in self.experience:
                out.append(f"{job.role}, {job.organisation}  ({job.period})")
                out += [f"  - {b}" for b in job.bullets]
        if self.education:
            out += ["", "EDUCATION"]
            out += [
                f"{e.qualification}, {e.institution}"
                + (f" ({e.year})" if e.year else "")
                for e in self.education
            ]
        if self.certifications:
            out += ["", "CERTIFICATIONS"]
            out += [
                c.name + (f" - {c.issuer}" if c.issuer else "")
                + (f" ({c.year})" if c.year else "")
                for c in self.certifications
            ]
        if self.skills:
            out += ["", "SKILLS", ", ".join(self.skills)]
        return "\n".join(out)


@dataclass(frozen=True)
class CoverLetter:
    """A letter to go with it."""

    sender: CvContact = field(default_factory=CvContact)
    recipient: str = ""
    organisation: str = ""
    subject: str = ""
    body: str = ""
    #: A real date, not a rendered string, so the document formats it to the
    #: reader's convention rather than baking in one country's order.
    written_on: date | None = None

    def to_text(self) -> str:
        out: list[str] = []
        if self.sender.name:
            out.append(self.sender.name)
        out += self.sender.lines()
        if self.written_on:
            out += ["", self.written_on.isoformat()]
        if self.organisation or self.recipient:
            out += ["", *(x for x in (self.recipient, self.organisation) if x)]
        if self.subject:
            out += ["", self.subject]
        out += ["", self.body]
        if self.sender.name:
            out += ["", "Yours sincerely,", self.sender.name]
        return "\n".join(out)


# ─────────────────────────────────────────────────────────────────────────────
# Report


@dataclass(frozen=True)
class ReportTable:
    """A table in a report."""

    headers: tuple[str, ...] = ()
    rows: tuple[tuple[str, ...], ...] = ()
    caption: str = ""

    def column_count(self) -> int:
        """The WIDEST row, not the header count.

        A row with an extra cell would otherwise be silently truncated, which
        loses data in a document somebody is about to act on.
        """
        return max(
            [len(self.headers)] + [len(r) for r in self.rows] or [0]
        ) if (self.headers or self.rows) else 0


@dataclass(frozen=True)
class ReportSection:
    """One section."""

    heading: str = ""
    body: str = ""
    tables: tuple[ReportTable, ...] = ()
    #: Sections nest. Depth is computed on render rather than stored, so moving
    #: a section cannot leave it labelled with its old level.
    subsections: tuple["ReportSection", ...] = ()


@dataclass(frozen=True)
class ReportDocument:
    """A whole report."""

    title: str = ""
    subtitle: str = ""
    author: str = ""
    written_on: date | None = None
    sections: tuple[ReportSection, ...] = ()

    def numbered(self) -> list[tuple[str, int, ReportSection]]:
        """Flattens to (number, depth, section) in reading order.

        Numbers are DERIVED, so inserting a section renumbers everything after
        it automatically - a stored number is a cross-reference that silently
        goes wrong.
        """
        out: list[tuple[str, int, ReportSection]] = []

        def walk(sections: Sequence[ReportSection], prefix: str, depth: int) -> None:
            for i, section in enumerate(sections, start=1):
                number = f"{prefix}{i}"
                out.append((number, depth, section))
                walk(section.subsections, number + ".", depth + 1)

        walk(self.sections, "", 0)
        return out

    def to_text(self) -> str:
        out: list[str] = []
        if self.title:
            out += [self.title.upper()]
        if self.subtitle:
            out += [self.subtitle]
        if self.author or self.written_on:
            out += [
                " - ".join(
                    x for x in (self.author,
                                self.written_on.isoformat() if self.written_on else "")
                    if x)
            ]
        for number, depth, section in self.numbered():
            out += ["", f"{'  ' * depth}{number} {section.heading}"]
            if section.body:
                out.append(f"{'  ' * depth}{section.body}")
            for table in section.tables:
                if table.caption:
                    out.append(f"{'  ' * depth}[{table.caption}]")
                if table.headers:
                    out.append(f"{'  ' * depth}{' | '.join(table.headers)}")
                out += [f"{'  ' * depth}{' | '.join(r)}" for r in table.rows]
        return "\n".join(out)


# ─────────────────────────────────────────────────────────────────────────────
# The engine


class IDocumentEngine(ABC):
    """Renders a document request."""

    @abstractmethod
    def render(self, request: DocumentRequest) -> DocumentResult: ...

    @abstractmethod
    def supports(self, format: DocumentFormat) -> bool: ...


class PdfSharpDocumentEngine(IDocumentEngine):
    """The default engine: PDF and plain text, both ours.

    Named for the C# class it mirrors. Nothing here uses PdfSharp - the writer
    above is the whole implementation.
    """

    MARGIN = 56.0
    LEADING = 15.0

    def supports(self, format: DocumentFormat) -> bool:
        return format in (DocumentFormat.PDF, DocumentFormat.TEXT)

    def render(self, request: DocumentRequest) -> DocumentResult:
        if request.format is DocumentFormat.TEXT:
            payload = request.payload
            text = payload.to_text() if hasattr(payload, "to_text") else str(payload)
            return DocumentResult(text.encode("utf-8"), "text/plain", 1)
        if request.format is not DocumentFormat.PDF:
            return DocumentResult(
                error=f"this engine renders PDF and plain text, not "
                      f"{request.format.value}")

        if request.kind is DocumentKind.CV and isinstance(request.payload, CvDocument):
            return self._render_cv(request.payload)
        if isinstance(request.payload, CoverLetter):
            return self._render_letter(request.payload)
        if isinstance(request.payload, ReportDocument):
            return self._render_report(request.payload)
        return DocumentResult(error=f"nothing here can render a {request.kind.value}")

    # ── layout helpers ───────────────────────────────────────────────────────

    def _flow(
        self, pdf: PdfWriter, y: float, text: str, size: float,
        bold: bool = False, indent: float = 0.0, grey: float = 0.0,
    ) -> float:
        """Writes wrapped text and returns the new baseline, breaking pages.

        The page break happens BEFORE the line is drawn, not after. Drawing
        first and checking after puts one line past the bottom edge of every
        page it fills - the classic off-by-one that only shows on long
        documents.
        """
        width = pdf.width - 2 * self.MARGIN - indent
        for line in wrap_text(text, width, size, bold):
            if y > pdf.height - self.MARGIN:
                pdf.end_page()
                y = self.MARGIN + size
            pdf.text(line, self.MARGIN + indent, y, size, bold, grey)
            y += self.LEADING * (size / 11.0)
        return y

    def _rule(self, pdf: PdfWriter, y: float) -> float:
        pdf.line(self.MARGIN, y, pdf.width - self.MARGIN, y, 0.5, 0.6)
        return y + 8

    def _render_cv(self, cv: CvDocument) -> DocumentResult:
        pdf = PdfWriter()
        y = self.MARGIN + 18
        if cv.contact.name:
            y = self._flow(pdf, y, cv.contact.name, 20, True)
        if cv.headline:
            y = self._flow(pdf, y + 2, cv.headline, 11, False, 0, 0.35)
        contact = "  ".join(cv.contact.lines())
        if contact:
            y = self._flow(pdf, y + 2, contact, 9, False, 0, 0.35)
        y = self._rule(pdf, y + 6)

        def heading(label: str, at: float) -> float:
            at = self._flow(pdf, at + 6, label.upper(), 10, True, 0, 0.25)
            return at + 2

        if cv.summary:
            y = heading("Summary", y)
            y = self._flow(pdf, y, cv.summary, 10)
        if cv.experience:
            y = heading("Experience", y)
            for job in cv.experience:
                y = self._flow(pdf, y, f"{job.role}, {job.organisation}", 11, True)
                meta = "  ".join(x for x in (job.period, job.location) if x)
                if meta:
                    y = self._flow(pdf, y, meta, 9, False, 0, 0.4)
                for bullet in job.bullets:
                    y = self._flow(pdf, y, f"- {bullet}", 10, False, 12)
                y += 4
        if cv.education:
            y = heading("Education", y)
            for e in cv.education:
                line = f"{e.qualification}, {e.institution}"
                if e.year:
                    line += f" ({e.year})"
                y = self._flow(pdf, y, line, 10)
        if cv.certifications:
            y = heading("Certifications", y)
            for c in cv.certifications:
                bits = [c.name] + [x for x in (c.issuer, c.year) if x]
                y = self._flow(pdf, y, " - ".join(bits), 10)
        if cv.skills:
            y = heading("Skills", y)
            y = self._flow(pdf, y, ", ".join(cv.skills), 10)

        return DocumentResult(pdf.build(), "application/pdf", pdf.page_count)

    def _render_letter(self, letter: CoverLetter) -> DocumentResult:
        pdf = PdfWriter()
        y = self.MARGIN + 14
        if letter.sender.name:
            y = self._flow(pdf, y, letter.sender.name, 13, True)
        for line in letter.sender.lines():
            y = self._flow(pdf, y, line, 9, False, 0, 0.4)
        if letter.written_on:
            y = self._flow(pdf, y + 14, letter.written_on.isoformat(), 10)
        for line in (letter.recipient, letter.organisation):
            if line:
                y = self._flow(pdf, y, line, 10)
        if letter.subject:
            y = self._flow(pdf, y + 12, letter.subject, 11, True)
        y = self._flow(pdf, y + 8, letter.body, 10.5)
        if letter.sender.name:
            y = self._flow(pdf, y + 16, "Yours sincerely,", 10.5)
            y = self._flow(pdf, y + 10, letter.sender.name, 10.5)
        return DocumentResult(pdf.build(), "application/pdf", pdf.page_count)

    def _render_report(self, report: ReportDocument) -> DocumentResult:
        pdf = PdfWriter()
        y = self.MARGIN + 20
        if report.title:
            y = self._flow(pdf, y, report.title, 22, True)
        if report.subtitle:
            y = self._flow(pdf, y + 2, report.subtitle, 12, False, 0, 0.35)
        meta = " - ".join(
            x for x in (report.author,
                        report.written_on.isoformat() if report.written_on else "")
            if x)
        if meta:
            y = self._flow(pdf, y + 2, meta, 9, False, 0, 0.4)
        y = self._rule(pdf, y + 8)

        for number, depth, section in report.numbered():
            size = max(10.0, 15.0 - depth * 2)
            y = self._flow(
                pdf, y + 8, f"{number}  {section.heading}", size, True, depth * 10)
            if section.body:
                y = self._flow(pdf, y + 2, section.body, 10, False, depth * 10)
            for table in section.tables:
                y = self._render_table(pdf, y + 6, table, depth * 10)
        return DocumentResult(pdf.build(), "application/pdf", pdf.page_count)

    def _render_table(
        self, pdf: PdfWriter, y: float, table: ReportTable, indent: float
    ) -> float:
        columns = table.column_count()
        if columns == 0:
            return y
        available = pdf.width - 2 * self.MARGIN - indent
        col = available / columns
        if table.caption:
            y = self._flow(pdf, y, table.caption, 9, True, indent, 0.3)
        if table.headers:
            pdf.rect(self.MARGIN + indent, y - 10, available, 14, 0.92)
            for i, head in enumerate(table.headers):
                pdf.text(head, self.MARGIN + indent + i * col + 3, y, 9, True)
            y += self.LEADING
        for row in table.rows:
            if y > pdf.height - self.MARGIN:
                pdf.end_page()
                y = self.MARGIN + 10
            for i, cell in enumerate(row):
                # Cells are CLIPPED by measurement, not wrapped: a table that
                # reflows mid-column stops lining up, and a table that does not
                # line up is unreadable in a way a truncated cell is not.
                text = cell
                while text and text_width(text, 9) > col - 6:
                    text = text[:-1]
                if text != cell and len(text) > 1:
                    text = text[:-1] + "…"
                pdf.text(text, self.MARGIN + indent + i * col + 3, y, 9)
            y += self.LEADING
        return y


class NullDocumentEngine(IDocumentEngine):
    """Renders plain text and nothing else.

    Not a raise: a CV as text is still a CV, and a person who needs one today
    can paste it into a form.
    """

    def supports(self, format: DocumentFormat) -> bool:
        return format is DocumentFormat.TEXT

    def render(self, request: DocumentRequest) -> DocumentResult:
        payload = request.payload
        text = payload.to_text() if hasattr(payload, "to_text") else str(payload)
        return DocumentResult(text.encode("utf-8"), "text/plain", 1)
