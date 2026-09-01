"""Charts, and the slide decks that carry them.

A CHART IS AN ARGUMENT, so the defaults here are the ones that stop it lying:

  * A bar chart's axis starts at ZERO. Cropping the baseline makes a 3% change
    look like a doubling, and it is the single most common way a true number is
    used to say something false.

  * Ticks land on 1, 2 or 5 times a power of ten. Not because it is prettier -
    because a reader estimates a value between two gridlines by dividing the gap
    in their head, and nobody divides by 7.

  * Every degenerate case has an answer: no points, one point, all values equal,
    all values zero, negatives. Each of those is a division by zero waiting in
    the obvious implementation, and each shows up in real data within a week.
"""

from __future__ import annotations

import math
from abc import ABC, abstractmethod
from dataclasses import dataclass, field
from enum import Enum
from typing import Sequence

from ..documents.pdf import (
    A4_HEIGHT, A4_WIDTH, DocumentResult, PdfWriter, text_width, wrap_text,
)


class ChartType(Enum):
    """What shape the argument takes."""

    #: Comparing separate things. Zero baseline, always.
    BAR = "bar"
    #: A quantity over a continuous axis. May be cropped, because the reader is
    #: looking at the SHAPE and a cropped line still tells the truth about it.
    LINE = "line"
    AREA = "area"
    #: Parts of one whole. Refused when the parts do not make a whole, because a
    #: pie of unrelated numbers is not a chart of anything.
    PIE = "pie"
    SCATTER = "scatter"


@dataclass(frozen=True)
class ChartDataPoint:
    """One value.

    `label` is carried on the POINT rather than in a parallel list. Parallel
    lists drift the first time a series is filtered, and the chart then labels
    the wrong bar - a mistake nobody spots because it still looks like a chart.
    """

    value: float
    label: str = ""
    #: Overrides the series colour for this point. Used to mark one bar as the
    #: subject, which is the honest way to draw attention.
    colour: str = ""


@dataclass(frozen=True)
class ChartSeries:
    """One line or set of bars."""

    name: str = ""
    points: tuple[ChartDataPoint, ...] = ()
    colour: str = "#2196F3"

    @property
    def values(self) -> tuple[float, ...]:
        return tuple(p.value for p in self.points)

    @staticmethod
    def of(values: Sequence[float], name: str = "", labels: Sequence[str] = ()) -> "ChartSeries":
        return ChartSeries(name, tuple(
            ChartDataPoint(v, labels[i] if i < len(labels) else "")
            for i, v in enumerate(values)
        ))


class ChartFonts:
    """Type sizes for a chart, in points.

    Fixed rather than scaled: a chart is read at arm's length on paper or at
    reading distance on a screen, and 7pt tick labels are the smallest that
    survive both.
    """

    TITLE = 14.0
    SUBTITLE = 10.0
    AXIS_LABEL = 9.0
    TICK = 7.5
    LEGEND = 8.5
    #: Line spacing as a MULTIPLE of size, so changing a size cannot leave the
    #: leading behind.
    LEADING = 1.35


@dataclass(frozen=True)
class ChartStyle:
    """How a chart looks."""

    #: The house blue and slate. Never orange.
    palette: tuple[str, ...] = (
        "#2196F3", "#2c3e50", "#5c9ead", "#8e9aaf", "#4a6572", "#7f8c8d")
    background: str = "#ffffff"
    grid_grey: float = 0.86
    axis_grey: float = 0.35
    ink_grey: float = 0.15
    #: Gridlines behind the data, never over it. A gridline drawn on top of a
    #: bar reads as a division in the bar.
    grid_behind: bool = True
    show_values: bool = False

    def colour_for(self, index: int) -> str:
        return self.palette[index % len(self.palette)]


@dataclass(frozen=True)
class ChartSpec:
    """A whole chart, declaratively."""

    type: ChartType = ChartType.BAR
    title: str = ""
    subtitle: str = ""
    series: tuple[ChartSeries, ...] = ()
    x_label: str = ""
    y_label: str = ""
    style: ChartStyle = field(default_factory=ChartStyle)
    width: float = 460.0
    height: float = 260.0
    #: None means "decide from the data". An explicit value is honoured even
    #: when it crops - a caller who says so has taken responsibility for it.
    y_min: float | None = None
    y_max: float | None = None

    @property
    def is_empty(self) -> bool:
        return not any(s.points for s in self.series)

    def all_values(self) -> tuple[float, ...]:
        return tuple(v for s in self.series for v in s.values)

    def resolved_bounds(self) -> tuple[float, float]:
        """The y range actually drawn.

        A BAR CHART IS FORCED TO INCLUDE ZERO. That is the rule the type carries
        and the reason bars and lines are separate types rather than a flag.

        When every value is the same, the range is padded around it - otherwise
        the span is zero, every later division blows up, and the honest picture
        (a flat line) is the one that never renders.
        """
        values = self.all_values()
        if not values:
            return 0.0, 1.0
        low = min(values) if self.y_min is None else self.y_min
        high = max(values) if self.y_max is None else self.y_max
        if self.type in (ChartType.BAR, ChartType.AREA) and self.y_min is None:
            low = min(0.0, low)
        if self.type in (ChartType.BAR, ChartType.AREA) and self.y_max is None:
            high = max(0.0, high)
        if high == low:
            pad = abs(high) * 0.1 or 1.0
            return low - pad, high + pad
        return low, high


def nice_ticks(low: float, high: float, target: int = 5) -> list[float]:
    """Ticks on 1, 2 or 5 times a power of ten, covering [low, high].

    The step is chosen by taking the raw span over the target count, dropping to
    the power of ten below it, and rounding the leftover up to whichever of
    1, 2, 5 or 10 it first fits. Any other step gives gridlines a reader cannot
    interpolate between.
    """
    if not math.isfinite(low) or not math.isfinite(high) or high <= low:
        return [low, high] if high > low else [low, low + 1.0]
    raw = (high - low) / max(1, target)
    magnitude = 10.0 ** math.floor(math.log10(raw))
    residual = raw / magnitude
    step = magnitude * (1 if residual <= 1 else 2 if residual <= 2 else 5 if residual <= 5 else 10)

    # COUNTED IN WHOLE STEPS, not accumulated by adding.
    #
    # Adding `step` repeatedly and testing `v <= high` drifts: on [0.001, 0.009]
    # the fifth addition lands on 0.010000000000000002, which fails a `<= 0.010`
    # guard by two parts in 10^18 and drops the top tick. The top gridline then
    # sits BELOW the tallest bar, which pokes out of the plot area - found by
    # running this over a spread of ranges, not by reading it.
    first = math.floor(low / step)
    last = math.ceil(high / step)
    # A hard cap on the count, so a pathological range cannot allocate forever.
    last = min(last, first + 63)
    return [i * step for i in range(first, last + 1)]


def format_tick(value: float, step: float) -> str:
    """As many decimals as the STEP needs, not as the value has.

    Formatting each value on its own gives an axis reading 0, 0.5, 1, 1.5 - the
    whole numbers losing their decimal and the column no longer lining up.
    """
    if step <= 0 or not math.isfinite(step):
        return f"{value:g}"
    decimals = max(0, min(6, int(math.ceil(-math.log10(step))) if step < 1 else 0))
    if abs(value) >= 1e6:
        return f"{value / 1e6:.1f}M"
    if abs(value) >= 1e4:
        return f"{value / 1e3:.0f}k"
    text = f"{value:.{decimals}f}"
    # Negative zero is a real float and it prints as "-0", which reads as an
    # error on an axis.
    return "0" if text in ("-0", f"-0.{'0' * decimals}") else text


class ChartSpecFactory:
    """Builds specs for the shapes people ask for by name."""

    @staticmethod
    def bars(
        values: Sequence[float], labels: Sequence[str] = (),
        title: str = "", y_label: str = "",
    ) -> ChartSpec:
        return ChartSpec(
            ChartType.BAR, title, series=(ChartSeries.of(values, "", labels),),
            y_label=y_label)

    @staticmethod
    def trend(
        values: Sequence[float], labels: Sequence[str] = (),
        title: str = "", y_label: str = "",
    ) -> ChartSpec:
        """A LINE, so it may crop - the reader is looking at the shape, and a
        line forced to zero flattens the very change it was drawn to show."""
        return ChartSpec(
            ChartType.LINE, title, series=(ChartSeries.of(values, "", labels),),
            y_label=y_label)

    @staticmethod
    def share(
        values: Sequence[float], labels: Sequence[str] = (), title: str = "",
    ) -> ChartSpec:
        """Refuses negatives outright.

        A negative slice of a pie has no meaning, and drawing it by absolute
        value produces a chart that adds to more than the whole while looking
        exactly like one that does not.
        """
        if any(v < 0 for v in values):
            raise ValueError(
                "a pie cannot show negative values - they have no share of a whole")
        return ChartSpec(
            ChartType.PIE, title, series=(ChartSeries.of(values, "", labels),))

    @staticmethod
    def comparison(
        series: Sequence[ChartSeries], title: str = "", y_label: str = "",
    ) -> ChartSpec:
        style = ChartStyle()
        return ChartSpec(
            ChartType.BAR, title, y_label=y_label,
            series=tuple(
                s if s.colour else ChartSeries(s.name, s.points, style.colour_for(i))
                for i, s in enumerate(series)
            ),
        )


class IChartRenderer(ABC):
    """Draws a chart."""

    @abstractmethod
    def render(self, spec: ChartSpec) -> DocumentResult: ...

    @abstractmethod
    def draw_into(
        self, pdf: PdfWriter, spec: ChartSpec, x: float, y: float
    ) -> float:
        """Draws at (x, y) top-left and returns the baseline below it, so a
        report can flow text after a chart without knowing its height."""


def _hex_to_grey(hex_colour: str) -> float:
    """Luminance, for a writer that only has greyscale operators.

    Rec. 709 weights, not a flat average: the eye is roughly six times more
    sensitive to green than to blue, and a flat average renders the house blue
    and a mid-green as the same grey.
    """
    s = hex_colour.lstrip("#")
    if len(s) == 3:
        s = "".join(c * 2 for c in s)
    if len(s) < 6:
        return 0.5
    r, g, b = (int(s[i:i + 2], 16) / 255.0 for i in (0, 2, 4))
    return 0.2126 * r + 0.7152 * g + 0.0722 * b


class PdfSharpChartRenderer(IChartRenderer):
    """Draws charts into the hand-written PDF writer.

    Named for the C# class it mirrors; nothing here uses PdfSharp.
    """

    PAD_LEFT = 46.0
    PAD_BOTTOM = 30.0
    PAD_TOP = 8.0
    PAD_RIGHT = 10.0

    def render(self, spec: ChartSpec) -> DocumentResult:
        pdf = PdfWriter(int(spec.width) + 60, int(spec.height) + 90)
        self.draw_into(pdf, spec, 30, 40)
        return DocumentResult(pdf.build(), "application/pdf", 1)

    def draw_into(
        self, pdf: PdfWriter, spec: ChartSpec, x: float, y: float
    ) -> float:
        top = y
        if spec.title:
            pdf.text(spec.title, x, y, ChartFonts.TITLE, True, spec.style.ink_grey)
            y += ChartFonts.TITLE * ChartFonts.LEADING
        if spec.subtitle:
            pdf.text(spec.subtitle, x, y, ChartFonts.SUBTITLE, False, 0.4)
            y += ChartFonts.SUBTITLE * ChartFonts.LEADING

        if spec.is_empty:
            # An empty chart says so IN THE FRAME. A blank rectangle is read as
            # a rendering failure, and somebody goes looking for a bug that is
            # actually an empty dataset.
            pdf.rect(x, y, spec.width, spec.height, 0.97)
            pdf.text("no data", x + spec.width / 2 - 18, y + spec.height / 2,
                     ChartFonts.AXIS_LABEL, False, 0.55)
            return y + spec.height + 10

        if spec.type is ChartType.PIE:
            return self._draw_pie(pdf, spec, x, y)

        plot_x = x + self.PAD_LEFT
        plot_y = y + self.PAD_TOP
        plot_w = spec.width - self.PAD_LEFT - self.PAD_RIGHT
        plot_h = spec.height - self.PAD_TOP - self.PAD_BOTTOM
        low, high = spec.resolved_bounds()
        ticks = nice_ticks(low, high)
        # The ticks may reach past the data; the axis follows the TICKS so the
        # top gridline is not floating above the plot area.
        low, high = min(low, ticks[0]), max(high, ticks[-1])
        span = high - low or 1.0
        step = ticks[1] - ticks[0] if len(ticks) > 1 else span

        def to_y(value: float) -> float:
            return plot_y + plot_h - (value - low) / span * plot_h

        for tick in ticks:
            ty = to_y(tick)
            pdf.line(plot_x, ty, plot_x + plot_w, ty, 0.4, spec.style.grid_grey)
            label = format_tick(tick, step)
            pdf.text(
                label, plot_x - 5 - text_width(label, ChartFonts.TICK),
                ty + ChartFonts.TICK * 0.35, ChartFonts.TICK, False, 0.45)

        zero_y = to_y(0.0)
        if low < 0 < high:
            # The zero line is darker than a gridline. Where a chart crosses
            # zero, that crossing is the most important thing in it.
            pdf.line(plot_x, zero_y, plot_x + plot_w, zero_y, 0.8,
                     spec.style.axis_grey)

        if spec.type is ChartType.BAR:
            self._draw_bars(pdf, spec, plot_x, plot_w, plot_h, to_y, low, high)
        else:
            self._draw_lines(pdf, spec, plot_x, plot_w, to_y)

        pdf.line(plot_x, plot_y + plot_h, plot_x + plot_w, plot_y + plot_h,
                 0.7, spec.style.axis_grey)

        first = spec.series[0]
        if first.points and any(p.label for p in first.points):
            slot = plot_w / len(first.points)
            for i, point in enumerate(first.points):
                if not point.label:
                    continue
                label = point.label
                # Labels are TRUNCATED, never rotated. A rotated label needs a
                # text matrix, and an unreadable angled label is not better than
                # a shortened flat one.
                while label and text_width(label, ChartFonts.TICK) > slot - 2:
                    label = label[:-1]
                pdf.text(
                    label,
                    plot_x + slot * i + (slot - text_width(label, ChartFonts.TICK)) / 2,
                    plot_y + plot_h + 12, ChartFonts.TICK, False, 0.45)

        if spec.y_label:
            pdf.text(spec.y_label, x, plot_y - 2, ChartFonts.AXIS_LABEL, False, 0.4)
        if spec.x_label:
            pdf.text(spec.x_label, plot_x + plot_w / 2, plot_y + plot_h + 24,
                     ChartFonts.AXIS_LABEL, False, 0.4)

        y = plot_y + plot_h + (26 if spec.x_label else 16)
        named = [s for s in spec.series if s.name]
        if len(named) > 1:
            pen = plot_x
            for i, s in enumerate(named):
                pdf.rect(pen, y - 6, 8, 8, _hex_to_grey(s.colour or spec.style.colour_for(i)))
                pdf.text(s.name, pen + 12, y, ChartFonts.LEGEND, False, 0.3)
                pen += 12 + text_width(s.name, ChartFonts.LEGEND) + 16
            y += 14
        return y

    def _draw_bars(
        self, pdf: PdfWriter, spec: ChartSpec, plot_x: float, plot_w: float,
        plot_h: float, to_y, low: float, high: float,
    ) -> None:
        groups = max(len(s.points) for s in spec.series)
        if groups == 0:
            return
        slot = plot_w / groups
        count = len(spec.series)
        # A tenth of the slot on each side, so neighbouring groups do not touch
        # - touching bars read as one wide bar.
        bar_w = slot * 0.8 / count
        base = to_y(max(low, min(0.0, high)) if low < 0 < high else max(low, 0.0))
        for si, series in enumerate(spec.series):
            for i, point in enumerate(series.points):
                top = to_y(point.value)
                left = plot_x + slot * i + slot * 0.1 + bar_w * si
                # Height from the ABSOLUTE difference, so a negative value draws
                # downward from the baseline instead of a zero-height bar.
                height = abs(base - top)
                grey = _hex_to_grey(
                    point.colour or series.colour or spec.style.colour_for(si))
                pdf.rect(left, min(base, top), bar_w, height, grey)
                if spec.style.show_values:
                    label = format_tick(point.value, abs(high - low) / 100 or 1)
                    pdf.text(
                        label,
                        left + (bar_w - text_width(label, ChartFonts.TICK)) / 2,
                        min(base, top) - 3, ChartFonts.TICK, False, 0.3)

    def _draw_lines(
        self, pdf: PdfWriter, spec: ChartSpec, plot_x: float, plot_w: float, to_y,
    ) -> None:
        for si, series in enumerate(spec.series):
            n = len(series.points)
            if n == 0:
                continue
            grey = _hex_to_grey(series.colour or spec.style.colour_for(si))
            if n == 1:
                # ONE point is a dot, not a line. Drawing a line from a point to
                # itself emits a degenerate path that some readers refuse.
                yv = to_y(series.points[0].value)
                pdf.rect(plot_x + plot_w / 2 - 2, yv - 2, 4, 4, grey)
                continue
            gap = plot_w / (n - 1)
            for i in range(n - 1):
                pdf.line(
                    plot_x + gap * i, to_y(series.points[i].value),
                    plot_x + gap * (i + 1), to_y(series.points[i + 1].value),
                    1.4, grey)

    def _draw_pie(
        self, pdf: PdfWriter, spec: ChartSpec, x: float, y: float
    ) -> float:
        """Drawn as a stacked bar, not a circle.

        The writer has no arc operator, and a pie approximated by line segments
        looks like a bad pie. A single stacked bar carries the same information
        and is EASIER to read - which is the actual finding about pie charts.
        """
        points = spec.series[0].points
        total = sum(max(0.0, p.value) for p in points)
        if total <= 0:
            pdf.text("no share to show", x, y + 12, ChartFonts.AXIS_LABEL, False, 0.5)
            return y + 24
        bar_h = 26.0
        pen = x
        for i, point in enumerate(points):
            w = max(0.0, point.value) / total * spec.width
            pdf.rect(pen, y, w, bar_h,
                     _hex_to_grey(point.colour or spec.style.colour_for(i)))
            pen += w
        y += bar_h + 12
        for i, point in enumerate(points):
            share = max(0.0, point.value) / total * 100
            pdf.rect(x, y - 6, 8, 8, _hex_to_grey(point.colour or spec.style.colour_for(i)))
            pdf.text(f"{point.label or f'item {i + 1}'}  {share:.1f}%",
                     x + 12, y, ChartFonts.LEGEND, False, 0.25)
            y += 13
        return y + 4


# ─────────────────────────────────────────────────────────────────────────────
# Decks


@dataclass(frozen=True)
class Slide:
    """One slide."""

    title: str = ""
    #: Bullets, not prose. A slide with a paragraph on it is a document being
    #: read aloud, and the audience reads faster than the speaker talks.
    bullets: tuple[str, ...] = ()
    notes: str = ""
    chart: ChartSpec | None = None
    #: A slide with only a title. Used deliberately as a section break, so the
    #: renderer must not treat "no bullets" as a slide to skip.
    is_section_break: bool = False


@dataclass(frozen=True)
class Deck:
    """A whole deck."""

    title: str = ""
    subtitle: str = ""
    author: str = ""
    slides: tuple[Slide, ...] = ()
    #: 16:9 in points at A4's long edge, so a deck prints on A4 landscape
    #: without either cropping or a band down the side.
    width: int = A4_HEIGHT
    height: int = int(A4_HEIGHT * 9 / 16)

    @property
    def slide_count(self) -> int:
        """Counts the TITLE slide too, because that is what the footer numbers
        and what somebody means when they say "slide 4"."""
        return len(self.slides) + 1


class IDeckEngine(ABC):
    """Renders a deck."""

    @abstractmethod
    def render(self, deck: Deck) -> DocumentResult: ...


class PdfSharpDeckEngine(IDeckEngine):
    """Renders a deck to PDF, one slide per page."""

    MARGIN = 48.0

    def __init__(self, charts: IChartRenderer | None = None) -> None:
        self._charts = charts or PdfSharpChartRenderer()

    def render(self, deck: Deck) -> DocumentResult:
        pdf = PdfWriter(deck.width, deck.height)

        pdf.text(deck.title, self.MARGIN, deck.height * 0.42, 30, True, 0.12)
        if deck.subtitle:
            pdf.text(deck.subtitle, self.MARGIN, deck.height * 0.42 + 30, 14, False, 0.4)
        if deck.author:
            pdf.text(deck.author, self.MARGIN, deck.height - self.MARGIN, 10, False, 0.45)
        pdf.end_page()

        for index, slide in enumerate(deck.slides, start=2):
            y = self.MARGIN
            if slide.is_section_break:
                pdf.text(slide.title, self.MARGIN, deck.height * 0.5, 24, True, 0.12)
            else:
                if slide.title:
                    pdf.text(slide.title, self.MARGIN, y + 10, 20, True, 0.12)
                    pdf.line(self.MARGIN, y + 20, deck.width - self.MARGIN, y + 20,
                             0.6, 0.75)
                    y += 44
                for bullet in slide.bullets:
                    for i, line in enumerate(wrap_text(
                        bullet, deck.width - 2 * self.MARGIN - 16, 13
                    )):
                        # The dot goes on the FIRST wrapped line only, and the
                        # rest indent to align under the text. A dot per line
                        # turns one point into three.
                        prefix = "-  " if i == 0 else "   "
                        pdf.text(prefix + line, self.MARGIN + 6, y, 13, False, 0.2)
                        y += 19
                    y += 4
                if slide.chart is not None:
                    self._charts.draw_into(pdf, slide.chart, self.MARGIN, y + 8)
            # Numbered from 1 including the title slide, which is what somebody
            # counting slides out loud will have done.
            pdf.text(str(index), deck.width - self.MARGIN, deck.height - 24, 9,
                     False, 0.55)
            pdf.end_page()
        return DocumentResult(pdf.build(), "application/pdf", pdf.page_count)


class SampleDeck:
    """A deck that exists to prove the renderer works.

    Real content rather than lorem: a sample full of placeholder text hides
    exactly the layout faults it is meant to reveal - the long line that
    overruns, the label that collides, the bullet that wraps badly.
    """

    @staticmethod
    def build() -> Deck:
        return Deck(
            title="Measured radio capability",
            subtitle="What the phones in the room can actually carry",
            author="CircleAI",
            slides=(
                Slide(
                    "What we measured",
                    (
                        "Wi-Fi Direct sustains about fifty messages a second in "
                        "both directions, which is enough to carry voice.",
                        "BLE manages about nine a second, one way, which is "
                        "signalling and nothing more.",
                        "No device in the room has Wi-Fi Aware hardware, so "
                        "anything built on it would not run here.",
                    ),
                    notes="The numbers are measured on the P30, not quoted.",
                ),
                Slide(
                    "Throughput, messages per second",
                    chart=ChartSpecFactory.bars(
                        [50.0, 50.0, 9.0, 0.0],
                        ["WD out", "WD in", "BLE out", "Aware"],
                        y_label="msg/s"),
                ),
                Slide("What follows from it", is_section_break=True),
                Slide(
                    "The consequence",
                    (
                        "Voice rides Wi-Fi Direct or it does not ride.",
                        "BLE carries the invitation, never the call.",
                        "A design that assumes Aware has to be redrawn.",
                    ),
                ),
            ),
        )
