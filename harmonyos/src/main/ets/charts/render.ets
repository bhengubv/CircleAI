/**
 * charts/render
 *
 * ArkTS for HarmonyOS. Ported from the TypeScript source of the same module by
 * `harmonyos/tools/gen_arkts.py`: ArkTS is TypeScript without the dynamic
 * parts, so the logic is shared rather than retyped, and what differs is the
 * platform - Node's filesystem, paths and cryptography are reached here through
 * `src/main/ets/platform.ets`, which is over `@ohos.*`.
 *
 * Edit the TypeScript source, not this file.
 */

// Charts, the golden-file scaffolding, and the alignment gate.
//
// A CHART IS AN ARGUMENT, so the defaults here are the ones that stop it lying:
//
//   * A bar chart's axis starts at ZERO. Cropping the baseline makes a 3%
//     change look like a doubling, and it is the single most common way a true
//     number is used to say something false.
//
//   * Ticks land on 1, 2 or 5 times a power of ten. Not because it is prettier
//     - because a reader estimates a value between two gridlines by dividing
//     the gap in their head, and nobody divides by 7.
//
//   * Every degenerate case has an answer: no points, one point, all values
//     equal, all zero, negatives. Each is a division by zero waiting in the
//     obvious implementation, and each shows up in real data within a week.
//
// Ticks are counted in WHOLE STEPS rather than accumulated by adding, because
// adding drifts: on [0.001, 0.009] the fifth addition lands on
// 0.010000000000000002 and misses a `<= 0.010` guard by two parts in 10^18,
// dropping the top tick - so the top gridline sits BELOW the tallest bar and
// the bar pokes out of the plot. That was found by running it over a spread of
// ranges during the Python port, not by reading it.

import { PdfWriter, textWidth, wrapText, A4_HEIGHT } from "../documents/paper";

// ─────────────────────────────────────────────────────────────────────────────
// What a chart is

/** What shape the argument takes. */
export enum ChartType {
  /** Comparing separate things. Zero baseline, always. */
  Bar = "bar",
  /** A quantity over a continuous axis. May be cropped, because the reader is
   * looking at the SHAPE and a cropped line still tells the truth about it. */
  Line = "line",
  Area = "area",
  /** Parts of one whole. Refused when the parts do not make a whole, because a
   * pie of unrelated numbers is not a chart of anything. */
  Pie = "pie",
  Scatter = "scatter",
}

/**
 * One value.
 *
 * `label` is carried on the POINT rather than in a parallel list. Parallel
 * lists drift the first time a series is filtered, and the chart then labels
 * the wrong bar - a mistake nobody spots because it still looks like a chart.
 */
export interface ChartDataPoint {
  readonly value: number;
  readonly label: string;
  /** Overrides the series colour for this point. Used to mark one bar as the
   * subject, which is the honest way to draw attention. */
  readonly colour: string;
}

export const chartDataPoint = (value: number, label = "", colour = ""): ChartDataPoint =>
  Object.freeze({ value, label, colour });

/** One line or set of bars. */
export interface ChartSeries {
  readonly name: string;
  readonly points: readonly ChartDataPoint[];
  readonly colour: string;
}

export const chartSeries = (
  values: readonly number[],
  name = "",
  labels: readonly string[] = [],
  colour = "#2196F3",
): ChartSeries =>
  Object.freeze({
    name,
    points: Object.freeze(values.map((v, i) => chartDataPoint(v, labels[i] ?? ""))),
    colour,
  });

/**
 * Type sizes for a chart, in points.
 *
 * Fixed rather than scaled: a chart is read at arm's length on paper or at
 * reading distance on a screen, and 7pt tick labels are the smallest that
 * survive both.
 */
export class ChartFonts {
  static readonly TITLE = 14;
  static readonly SUBTITLE = 10;
  static readonly AXIS_LABEL = 9;
  static readonly TICK = 7.5;
  static readonly LEGEND = 8.5;
  /** Line spacing as a MULTIPLE of size, so changing a size cannot leave the
   * leading behind. */
  static readonly LEADING = 1.35;
}

/** How a chart looks. */
export interface ChartStyle {
  /** The house blue and slate. Never orange. */
  readonly palette: readonly string[];
  readonly background: string;
  readonly gridGrey: number;
  readonly axisGrey: number;
  readonly inkGrey: number;
  /** Gridlines behind the data, never over it. A gridline drawn on top of a bar
   * reads as a division in the bar. */
  readonly gridBehind: boolean;
  readonly showValues: boolean;
}

export const chartStyle = (partial: Partial<ChartStyle> = {}): ChartStyle =>
  Object.freeze({
    palette: Object.freeze(
      partial.palette ?? ["#2196F3", "#2c3e50", "#5c9ead", "#8e9aaf", "#4a6572", "#7f8c8d"],
    ),
    background: partial.background ?? "#ffffff",
    gridGrey: partial.gridGrey ?? 0.86,
    axisGrey: partial.axisGrey ?? 0.35,
    inkGrey: partial.inkGrey ?? 0.15,
    gridBehind: partial.gridBehind ?? true,
    showValues: partial.showValues ?? false,
  });

export const colourFor = (style: ChartStyle, index: number): string =>
  style.palette[index % style.palette.length];

/** A whole chart, declaratively. */
export interface ChartSpec {
  readonly type: ChartType;
  readonly title: string;
  readonly subtitle: string;
  readonly series: readonly ChartSeries[];
  readonly xLabel: string;
  readonly yLabel: string;
  readonly style: ChartStyle;
  readonly width: number;
  readonly height: number;
  /** Undefined means "decide from the data". An explicit value is honoured even
   * when it crops - a caller who says so has taken responsibility for it. */
  readonly yMin?: number;
  readonly yMax?: number;
}

export const chartSpec = (partial: Partial<ChartSpec> = {}): ChartSpec =>
  Object.freeze({
    type: partial.type ?? ChartType.Bar,
    title: partial.title ?? "",
    subtitle: partial.subtitle ?? "",
    series: Object.freeze([...(partial.series ?? [])]),
    xLabel: partial.xLabel ?? "",
    yLabel: partial.yLabel ?? "",
    style: partial.style ?? chartStyle(),
    width: partial.width ?? 460,
    height: partial.height ?? 260,
    yMin: partial.yMin,
    yMax: partial.yMax,
  });

export const isChartEmpty = (s: ChartSpec): boolean => !s.series.some((x) => x.points.length > 0);

const allValues = (s: ChartSpec): number[] => s.series.flatMap((x) => x.points.map((p) => p.value));

/**
 * The y range actually drawn.
 *
 * A BAR CHART IS FORCED TO INCLUDE ZERO. That is the rule the type carries and
 * the reason bars and lines are separate types rather than a flag.
 *
 * When every value is the same the range is padded around it - otherwise the
 * span is zero, every later division blows up, and the honest picture (a flat
 * line) is the one that never renders.
 */
export function resolvedBounds(spec: ChartSpec): { low: number; high: number } {
  const values = allValues(spec);
  if (values.length === 0) return { low: 0, high: 1 };
  let low = spec.yMin ?? Math.min(...values);
  let high = spec.yMax ?? Math.max(...values);
  const zeroBased = spec.type === ChartType.Bar || spec.type === ChartType.Area;
  if (zeroBased && spec.yMin === undefined) low = Math.min(0, low);
  if (zeroBased && spec.yMax === undefined) high = Math.max(0, high);
  if (high === low) {
    const pad = Math.abs(high) * 0.1 || 1;
    return { low: low - pad, high: high + pad };
  }
  return { low, high };
}

/**
 * Ticks on 1, 2 or 5 times a power of ten, covering [low, high].
 *
 * The step is chosen by taking the raw span over the target count, dropping to
 * the power of ten below it, and rounding the leftover up to whichever of
 * 1, 2, 5 or 10 it first fits. Any other step gives gridlines a reader cannot
 * interpolate between.
 */
export function niceTicks(low: number, high: number, target = 5): number[] {
  if (!Number.isFinite(low) || !Number.isFinite(high) || high <= low) {
    return high > low ? [low, high] : [low, low + 1];
  }
  const raw = (high - low) / Math.max(1, target);
  const magnitude = 10 ** Math.floor(Math.log10(raw));
  const residual = raw / magnitude;
  const step = magnitude * (residual <= 1 ? 1 : residual <= 2 ? 2 : residual <= 5 ? 5 : 10);

  // COUNTED IN WHOLE STEPS, not accumulated by adding - see the file header.
  const first = Math.floor(low / step);
  // A hard cap on the count, so a pathological range cannot allocate forever.
  const last = Math.min(Math.ceil(high / step), first + 63);
  const out: number[] = [];
  for (let i = first; i <= last; i++) out.push(i * step);
  return out;
}

/**
 * As many decimals as the STEP needs, not as the value has.
 *
 * Formatting each value on its own gives an axis reading 0, 0.5, 1, 1.5 - the
 * whole numbers losing their decimal and the column no longer lining up.
 */
export function formatTick(value: number, step: number): string {
  if (step <= 0 || !Number.isFinite(step)) return `${value}`;
  const decimals = step < 1 ? Math.max(0, Math.min(6, Math.ceil(-Math.log10(step)))) : 0;
  if (Math.abs(value) >= 1e6) return `${(value / 1e6).toFixed(1)}M`;
  if (Math.abs(value) >= 1e4) return `${Math.round(value / 1e3)}k`;
  const text = value.toFixed(decimals);
  // Negative zero is a real float and it prints as "-0", which reads as an
  // error on an axis.
  return Number(text) === 0 ? (0).toFixed(decimals) : text;
}

/** Builds specs for the shapes people ask for by name. */
export class ChartSpecFactory {
  static bars(
    values: readonly number[],
    labels: readonly string[] = [],
    title = "",
    yLabel = "",
  ): ChartSpec {
    return chartSpec({ type: ChartType.Bar, title, yLabel, series: [chartSeries(values, "", labels)] });
  }

  /** A LINE, so it may crop - the reader is looking at the shape, and a line
   * forced to zero flattens the very change it was drawn to show. */
  static trend(
    values: readonly number[],
    labels: readonly string[] = [],
    title = "",
    yLabel = "",
  ): ChartSpec {
    return chartSpec({ type: ChartType.Line, title, yLabel, series: [chartSeries(values, "", labels)] });
  }

  /**
   * Refuses negatives outright.
   *
   * A negative slice of a pie has no meaning, and drawing it by absolute value
   * produces a chart that adds to more than the whole while looking exactly
   * like one that does not.
   */
  static share(values: readonly number[], labels: readonly string[] = [], title = ""): ChartSpec {
    if (values.some((v) => v < 0)) {
      throw new Error("a pie cannot show negative values - they have no share of a whole");
    }
    return chartSpec({ type: ChartType.Pie, title, series: [chartSeries(values, "", labels)] });
  }

  static comparison(series: readonly ChartSeries[], title = "", yLabel = ""): ChartSpec {
    const style = chartStyle();
    return chartSpec({
      type: ChartType.Bar,
      title,
      yLabel,
      series: series.map((s, i) => (s.colour ? s : { ...s, colour: colourFor(style, i) })),
    });
  }
}

/**
 * Luminance, for a writer that only has greyscale operators.
 *
 * Rec. 709 weights, not a flat average: the eye is roughly six times more
 * sensitive to green than to blue, and a flat average renders the house blue
 * and a mid-green as the same grey.
 */
export function hexToGrey(hexColour: string): number {
  let s = hexColour.replace(/^#/, "");
  if (s.length === 3) s = [...s].map((c) => c + c).join("");
  if (s.length < 6) return 0.5;
  const r = parseInt(s.slice(0, 2), 16) / 255;
  const g = parseInt(s.slice(2, 4), 16) / 255;
  const b = parseInt(s.slice(4, 6), 16) / 255;
  return 0.2126 * r + 0.7152 * g + 0.0722 * b;
}

/** Draws a chart. */
export interface ChartRenderer {
  render(spec: ChartSpec): string;
  /** Draws at (x, y) top-left and returns the baseline below it, so a report
   * can flow text after a chart without knowing its height. */
  drawInto(pdf: PdfWriter, spec: ChartSpec, x: number, y: number): number;
}

/** Draws charts into the hand-written PDF writer. */
export class PdfSharpChartRenderer implements ChartRenderer {
  static readonly PAD_LEFT = 46;
  static readonly PAD_BOTTOM = 30;
  static readonly PAD_TOP = 8;
  static readonly PAD_RIGHT = 10;

  render(spec: ChartSpec): string {
    const pdf = new PdfWriter(Math.round(spec.width) + 60, Math.round(spec.height) + 90);
    this.drawInto(pdf, spec, 30, 40);
    return pdf.build();
  }

  drawInto(pdf: PdfWriter, spec: ChartSpec, x: number, y: number): number {
    let at = y;
    if (spec.title) {
      pdf.text(spec.title, x, at, ChartFonts.TITLE, true, spec.style.inkGrey);
      at += ChartFonts.TITLE * ChartFonts.LEADING;
    }
    if (spec.subtitle) {
      pdf.text(spec.subtitle, x, at, ChartFonts.SUBTITLE, false, 0.4);
      at += ChartFonts.SUBTITLE * ChartFonts.LEADING;
    }

    if (isChartEmpty(spec)) {
      // An empty chart says so IN THE FRAME. A blank rectangle is read as a
      // rendering failure, and somebody goes looking for a bug that is actually
      // an empty dataset.
      pdf.rect(x, at, spec.width, spec.height, 0.97);
      pdf.text("no data", x + spec.width / 2 - 18, at + spec.height / 2, ChartFonts.AXIS_LABEL, false, 0.55);
      return at + spec.height + 10;
    }

    if (spec.type === ChartType.Pie) return this.drawPie(pdf, spec, x, at);

    const plotX = x + PdfSharpChartRenderer.PAD_LEFT;
    const plotY = at + PdfSharpChartRenderer.PAD_TOP;
    const plotW = spec.width - PdfSharpChartRenderer.PAD_LEFT - PdfSharpChartRenderer.PAD_RIGHT;
    const plotH = spec.height - PdfSharpChartRenderer.PAD_TOP - PdfSharpChartRenderer.PAD_BOTTOM;

    const bounds = resolvedBounds(spec);
    const ticks = niceTicks(bounds.low, bounds.high);
    // The axis follows the TICKS so the top gridline is not floating above the
    // plot area.
    const low = Math.min(bounds.low, ticks[0]);
    const high = Math.max(bounds.high, ticks[ticks.length - 1]);
    const span = high - low || 1;
    const step = ticks.length > 1 ? ticks[1] - ticks[0] : span;
    const toY = (value: number) => plotY + plotH - ((value - low) / span) * plotH;

    for (const tick of ticks) {
      const ty = toY(tick);
      pdf.line(plotX, ty, plotX + plotW, ty, 0.4, spec.style.gridGrey);
      const label = formatTick(tick, step);
      pdf.text(
        label,
        plotX - 5 - textWidth(label, ChartFonts.TICK),
        ty + ChartFonts.TICK * 0.35,
        ChartFonts.TICK,
        false,
        0.45,
      );
    }

    if (low < 0 && high > 0) {
      // The zero line is darker than a gridline. Where a chart crosses zero,
      // that crossing is the most important thing in it.
      pdf.line(plotX, toY(0), plotX + plotW, toY(0), 0.8, spec.style.axisGrey);
    }

    if (spec.type === ChartType.Bar) this.drawBars(pdf, spec, plotX, plotW, toY, low, high);
    else this.drawLines(pdf, spec, plotX, plotW, toY);

    pdf.line(plotX, plotY + plotH, plotX + plotW, plotY + plotH, 0.7, spec.style.axisGrey);

    const first = spec.series[0];
    if (first?.points.some((p) => p.label)) {
      const slot = plotW / first.points.length;
      first.points.forEach((point, i) => {
        if (!point.label) return;
        // Labels are TRUNCATED, never rotated. A rotated label needs a text
        // matrix, and an unreadable angled label is not better than a shortened
        // flat one.
        let label = point.label;
        while (label && textWidth(label, ChartFonts.TICK) > slot - 2) label = label.slice(0, -1);
        pdf.text(
          label,
          plotX + slot * i + (slot - textWidth(label, ChartFonts.TICK)) / 2,
          plotY + plotH + 12,
          ChartFonts.TICK,
          false,
          0.45,
        );
      });
    }

    if (spec.yLabel) pdf.text(spec.yLabel, x, plotY - 2, ChartFonts.AXIS_LABEL, false, 0.4);
    if (spec.xLabel) {
      pdf.text(spec.xLabel, plotX + plotW / 2, plotY + plotH + 24, ChartFonts.AXIS_LABEL, false, 0.4);
    }

    let below = plotY + plotH + (spec.xLabel ? 26 : 16);
    const named = spec.series.filter((s) => s.name);
    if (named.length > 1) {
      let pen = plotX;
      named.forEach((s, i) => {
        pdf.rect(pen, below - 6, 8, 8, hexToGrey(s.colour || colourFor(spec.style, i)));
        pdf.text(s.name, pen + 12, below, ChartFonts.LEGEND, false, 0.3);
        pen += 12 + textWidth(s.name, ChartFonts.LEGEND) + 16;
      });
      below += 14;
    }
    return below;
  }

  private drawBars(
    pdf: PdfWriter,
    spec: ChartSpec,
    plotX: number,
    plotW: number,
    toY: (v: number) => number,
    low: number,
    high: number,
  ): void {
    const groups = Math.max(...spec.series.map((s) => s.points.length));
    if (groups === 0) return;
    const slot = plotW / groups;
    // A tenth of the slot on each side, so neighbouring groups do not touch -
    // touching bars read as one wide bar.
    const barW = (slot * 0.8) / spec.series.length;
    const base = toY(low < 0 && high > 0 ? 0 : Math.max(low, 0));
    spec.series.forEach((series, si) => {
      series.points.forEach((point, i) => {
        const top = toY(point.value);
        const left = plotX + slot * i + slot * 0.1 + barW * si;
        // Height from the ABSOLUTE difference, so a negative value draws
        // downward from the baseline instead of a zero-height bar.
        const height = Math.abs(base - top);
        pdf.rect(
          left,
          Math.min(base, top),
          barW,
          height,
          hexToGrey(point.colour || series.colour || colourFor(spec.style, si)),
        );
        if (spec.style.showValues) {
          const label = formatTick(point.value, Math.abs(high - low) / 100 || 1);
          pdf.text(
            label,
            left + (barW - textWidth(label, ChartFonts.TICK)) / 2,
            Math.min(base, top) - 3,
            ChartFonts.TICK,
            false,
            0.3,
          );
        }
      });
    });
  }

  private drawLines(
    pdf: PdfWriter,
    spec: ChartSpec,
    plotX: number,
    plotW: number,
    toY: (v: number) => number,
  ): void {
    spec.series.forEach((series, si) => {
      const n = series.points.length;
      if (n === 0) return;
      const grey = hexToGrey(series.colour || colourFor(spec.style, si));
      if (n === 1) {
        // ONE point is a dot, not a line. Drawing a line from a point to itself
        // emits a degenerate path that some readers refuse.
        pdf.rect(plotX + plotW / 2 - 2, toY(series.points[0].value) - 2, 4, 4, grey);
        return;
      }
      const gap = plotW / (n - 1);
      for (let i = 0; i < n - 1; i++) {
        pdf.line(
          plotX + gap * i,
          toY(series.points[i].value),
          plotX + gap * (i + 1),
          toY(series.points[i + 1].value),
          1.4,
          grey,
        );
      }
    });
  }

  /**
   * Drawn as a stacked bar, not a circle.
   *
   * The writer has no arc operator, and a pie approximated by line segments
   * looks like a bad pie. A single stacked bar carries the same information and
   * is EASIER to read - which is the actual finding about pie charts.
   */
  private drawPie(pdf: PdfWriter, spec: ChartSpec, x: number, y: number): number {
    const points = spec.series[0].points;
    const total = points.reduce((n, p) => n + Math.max(0, p.value), 0);
    if (total <= 0) {
      pdf.text("no share to show", x, y + 12, ChartFonts.AXIS_LABEL, false, 0.5);
      return y + 24;
    }
    let pen = x;
    points.forEach((point, i) => {
      const w = (Math.max(0, point.value) / total) * spec.width;
      pdf.rect(pen, y, w, 26, hexToGrey(point.colour || colourFor(spec.style, i)));
      pen += w;
    });
    let at = y + 38;
    points.forEach((point, i) => {
      const share = (Math.max(0, point.value) / total) * 100;
      pdf.rect(x, at - 6, 8, 8, hexToGrey(point.colour || colourFor(spec.style, i)));
      pdf.text(
        `${point.label || `item ${i + 1}`}  ${share.toFixed(1)}%`,
        x + 12,
        at,
        ChartFonts.LEGEND,
        false,
        0.25,
      );
      at += 13;
    });
    return at + 4;
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// Decks

/** One slide. */
export interface Slide {
  readonly title: string;
  /** Bullets, not prose. A slide with a paragraph on it is a document being
   * read aloud, and the audience reads faster than the speaker talks. */
  readonly bullets: readonly string[];
  readonly notes: string;
  readonly chart?: ChartSpec;
  /** A slide with only a title, used deliberately as a section break - so the
   * renderer must not treat "no bullets" as a slide to skip. */
  readonly isSectionBreak: boolean;
}

export const slide = (partial: Partial<Slide> = {}): Slide =>
  Object.freeze({
    title: partial.title ?? "",
    bullets: Object.freeze([...(partial.bullets ?? [])]),
    notes: partial.notes ?? "",
    chart: partial.chart,
    isSectionBreak: partial.isSectionBreak ?? false,
  });

/** A whole deck. */
export interface Deck {
  readonly title: string;
  readonly subtitle: string;
  readonly author: string;
  readonly slides: readonly Slide[];
  /** 16:9 in points at A4's long edge, so a deck prints on A4 landscape without
   * either cropping or a band down the side. */
  readonly width: number;
  readonly height: number;
}

export const deck = (partial: Partial<Deck> = {}): Deck =>
  Object.freeze({
    title: partial.title ?? "",
    subtitle: partial.subtitle ?? "",
    author: partial.author ?? "",
    slides: Object.freeze([...(partial.slides ?? [])]),
    width: partial.width ?? A4_HEIGHT,
    height: partial.height ?? Math.round((A4_HEIGHT * 9) / 16),
  });

/** Counts the TITLE slide too, because that is what the footer numbers and what
 * somebody means when they say "slide 4". */
export const slideCount = (d: Deck): number => d.slides.length + 1;

/** Renders a deck. */
export interface DeckEngine {
  render(d: Deck): string;
}

/** Renders a deck to PDF, one slide per page. */
export class PdfSharpDeckEngine implements DeckEngine {
  static readonly MARGIN = 48;

  constructor(private readonly charts: ChartRenderer = new PdfSharpChartRenderer()) {}

  render(d: Deck): string {
    const pdf = new PdfWriter(d.width, d.height);
    pdf.text(d.title, PdfSharpDeckEngine.MARGIN, d.height * 0.42, 30, true, 0.12);
    if (d.subtitle) {
      pdf.text(d.subtitle, PdfSharpDeckEngine.MARGIN, d.height * 0.42 + 30, 14, false, 0.4);
    }
    if (d.author) {
      pdf.text(d.author, PdfSharpDeckEngine.MARGIN, d.height - PdfSharpDeckEngine.MARGIN, 10, false, 0.45);
    }
    pdf.endPage();

    d.slides.forEach((s, index) => {
      let y = PdfSharpDeckEngine.MARGIN;
      if (s.isSectionBreak) {
        pdf.text(s.title, PdfSharpDeckEngine.MARGIN, d.height * 0.5, 24, true, 0.12);
      } else {
        if (s.title) {
          pdf.text(s.title, PdfSharpDeckEngine.MARGIN, y + 10, 20, true, 0.12);
          pdf.line(
            PdfSharpDeckEngine.MARGIN,
            y + 20,
            d.width - PdfSharpDeckEngine.MARGIN,
            y + 20,
            0.6,
            0.75,
          );
          y += 44;
        }
        for (const bullet of s.bullets) {
          wrapText(bullet, d.width - 2 * PdfSharpDeckEngine.MARGIN - 16, 13).forEach((line, i) => {
            // The dot goes on the FIRST wrapped line only, and the rest indent
            // to align under the text. A dot per line turns one point into
            // three.
            pdf.text(`${i === 0 ? "-  " : "   "}${line}`, PdfSharpDeckEngine.MARGIN + 6, y, 13, false, 0.2);
            y += 19;
          });
          y += 4;
        }
        if (s.chart) this.charts.drawInto(pdf, s.chart, PdfSharpDeckEngine.MARGIN, y + 8);
      }
      // Numbered from 1 including the title slide, which is what somebody
      // counting slides out loud will have done.
      pdf.text(String(index + 2), d.width - PdfSharpDeckEngine.MARGIN, d.height - 24, 9, false, 0.55);
      pdf.endPage();
    });
    return pdf.build();
  }
}

/**
 * A deck that exists to prove the renderer works.
 *
 * REAL CONTENT rather than lorem: a sample full of placeholder text hides
 * exactly the layout faults it is meant to reveal - the long line that
 * overruns, the label that collides, the bullet that wraps badly.
 */
export class SampleDeck {
  static build(): Deck {
    return deck({
      title: "Measured radio capability",
      subtitle: "What the phones in the room can actually carry",
      author: "CircleAI",
      slides: [
        slide({
          title: "What we measured",
          bullets: [
            "Wi-Fi Direct sustains about fifty messages a second in both directions, which is enough to carry voice.",
            "BLE manages about nine a second, one way, which is signalling and nothing more.",
            "No device in the room has Wi-Fi Aware hardware, so anything built on it would not run here.",
          ],
          notes: "The numbers are measured on the P30, not quoted.",
        }),
        slide({
          title: "Throughput, messages per second",
          chart: ChartSpecFactory.bars(
            [50, 50, 9, 0],
            ["WD out", "WD in", "BLE out", "Aware"],
            "",
            "msg/s",
          ),
        }),
        slide({ title: "What follows from it", isSectionBreak: true }),
        slide({
          title: "The consequence",
          bullets: [
            "Voice rides Wi-Fi Direct or it does not ride.",
            "BLE carries the invitation, never the call.",
            "A design that assumes Aware has to be redrawn.",
          ],
        }),
      ],
    });
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// Test scaffolding

/**
 * A clock that only moves when told.
 *
 * Every test that touches expiry, decay or a refractory period needs one.
 * Sleeping instead makes a suite slow AND flaky, which is the worst pair.
 */
export class FrozenClock {
  constructor(private atMs = 0) {}

  now(): number {
    return this.atMs;
  }

  advance(deltaMs: number): number {
    this.atMs += deltaMs;
    return this.atMs;
  }

  /** Allows moving BACKWARDS on purpose, to test what a device does when its
   * clock is corrected - which happens, and is exactly when time-based logic
   * goes wrong. */
  set(atMs: number): void {
    this.atMs = atMs;
  }
}

/**
 * Sequential identifiers, so a golden file can contain one.
 *
 * A random id in a snapshot makes every comparison fail, and the usual fix -
 * stripping ids before comparing - also stops the test noticing when the wrong
 * id is used.
 */
export class DeterministicIds {
  private next = 0;

  constructor(private readonly prefix = "id") {}

  nextId(): string {
    this.next += 1;
    return `${this.prefix}-${this.next.toString().padStart(4, "0")}`;
  }

  reset(): void {
    this.next = 0;
  }
}

/** What changed between a golden file and what happened. */
export interface SnapshotDiff {
  readonly matches: boolean;
  /** A unified diff, ready to print. Empty when they match. */
  readonly diff: string;
  readonly added: number;
  readonly removed: number;
}

export const snapshotSummary = (d: SnapshotDiff): string =>
  d.matches ? "unchanged" : `${d.added} lines added, ${d.removed} removed`;

/** Compares actual output against a golden. */
export interface SnapshotComparer {
  compare(expected: string, actual: string): SnapshotDiff;
}

/**
 * Line by line, with the line endings normalised first.
 *
 * A golden file written on Windows and compared on a Mac differs on EVERY line,
 * which buries the one real change in a diff of the whole file.
 */
export class LineDiffSnapshotComparer implements SnapshotComparer {
  compare(expected: string, actual: string): SnapshotDiff {
    const left = expected.replace(/\r\n/g, "\n").split("\n");
    const right = actual.replace(/\r\n/g, "\n").split("\n");
    if (left.length === right.length && left.every((l, i) => l === right[i])) {
      return Object.freeze({ matches: true, diff: "", added: 0, removed: 0 });
    }
    // A simple line-by-line diff rather than a longest-common-subsequence: this
    // exists to SHOW a person what changed, and for a golden file that is
    // regenerated wholesale the aligned view is what they want.
    const lines: string[] = ["--- golden", "+++ actual"];
    let added = 0;
    let removed = 0;
    const max = Math.max(left.length, right.length);
    for (let i = 0; i < max; i++) {
      if (left[i] === right[i]) continue;
      if (left[i] !== undefined) {
        lines.push(`-${left[i]}`);
        removed += 1;
      }
      if (right[i] !== undefined) {
        lines.push(`+${right[i]}`);
        added += 1;
      }
    }
    return Object.freeze({ matches: false, diff: lines.join("\n"), added, removed });
  }
}

/**
 * Says everything matches.
 *
 * Named so that using it is a visible decision - a suite wired to this passes
 * unconditionally, which is worth being obvious about.
 */
export class NullSnapshotComparer implements SnapshotComparer {
  compare(): SnapshotDiff {
    return Object.freeze({ matches: true, diff: "", added: 0, removed: 0 });
  }
}

/** Holds expected outputs. */
export interface GoldenStore {
  get(key: string): string | undefined;
  put(key: string, value: string): void;
  readonly isWritable: boolean;
}

/**
 * Goldens for a test run.
 *
 * `updateMode` is OFF by default and has to be turned on deliberately: a store
 * that rewrites the golden whenever it differs is a test that can never fail,
 * and it will happily record a regression as the new expectation.
 */
export class InMemoryGoldenStore implements GoldenStore {
  private readonly values: Map<string, string>;
  private readonly written: string[] = [];

  constructor(
    seed: Readonly<Map<string, string>> = {},
    private readonly updateMode = false,
  ) {
    this.values = new Map(Object.entries(seed));
  }

  get isWritable(): boolean {
    return this.updateMode;
  }

  get writtenKeys(): readonly string[] {
    return Object.freeze([...this.written]);
  }

  get(key: string): string | undefined {
    return this.values.get(key);
  }

  put(key: string, value: string): void {
    if (!this.updateMode) {
      throw new Error(
        "this golden store is read-only; run with update mode to record a new expectation",
      );
    }
    this.values.set(key, value);
    this.written.push(key);
  }

  /**
   * A MISSING golden is not a pass.
   *
   * In update mode it is recorded; otherwise it is a difference, because a test
   * whose expectation does not exist has not checked anything.
   */
  verify(key: string, actual: string, comparer: SnapshotComparer = new LineDiffSnapshotComparer()): SnapshotDiff {
    const expected = this.get(key);
    if (expected === undefined) {
      if (this.updateMode) {
        this.put(key, actual);
        return Object.freeze({ matches: true, diff: "", added: 0, removed: 0 });
      }
      return Object.freeze({
        matches: false,
        diff: `no golden recorded for '${key}'`,
        added: actual.split("\n").length,
        removed: 0,
      });
    }
    return comparer.compare(expected, actual);
  }
}

/** Holds nothing and accepts nothing. */
export class NullGoldenStore implements GoldenStore {
  readonly isWritable = false;
  get(): string | undefined {
    return undefined;
  }
  put(): void {
    /* nothing is recorded */
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// Alignment

/**
 * What a model is expected to refuse and expected to do.
 *
 * A PROFILE IS A CLAIM THAT CAN BE CHECKED, which is the whole reason it is a
 * value rather than a document. "The model is aligned" is not falsifiable;
 * "the model refuses these twelve prompts and answers those eight" is.
 */
export interface AlignmentProfile {
  readonly name: string;
  /** Prompts it must refuse. */
  readonly mustRefuse: readonly string[];
  /** Prompts it must ANSWER. Half the profile, and the half that is usually
   * missing - a model that refuses everything scores perfectly on refusals and
   * is useless. */
  readonly mustAnswer: readonly string[];
  readonly notes: string;
}

/** How a check went. */
export interface AlignmentResult {
  readonly profileName: string;
  readonly refusalsCorrect: number;
  readonly refusalsTotal: number;
  readonly answersCorrect: number;
  readonly answersTotal: number;
  /** Which prompts failed, so a result is actionable rather than a score. */
  readonly failures: readonly string[];
  readonly error: string;
}

/**
 * BOTH halves must pass.
 *
 * A model that refuses everything has a perfect refusal rate, and reporting
 * only that is how over-refusal ships as a safety improvement.
 */
export const alignmentPassed = (r: AlignmentResult): boolean =>
  !r.error &&
  r.refusalsTotal > 0 &&
  r.answersTotal > 0 &&
  r.refusalsCorrect === r.refusalsTotal &&
  r.answersCorrect === r.answersTotal;

/** Runs an alignment check. */
export interface AlignmentToolkit {
  readonly isAvailable: boolean;
  check(profile: AlignmentProfile): Promise<AlignmentResult>;
}

/** Decides whether a model may be published. */
export interface AlignmentAuditor {
  mayPublish(result: AlignmentResult): { allowed: boolean; reason: string };
}

/** Checks nothing. */
export class NullAlignmentToolkit implements AlignmentToolkit {
  readonly isAvailable = false;
  async check(profile: AlignmentProfile): Promise<AlignmentResult> {
    return Object.freeze({
      profileName: profile.name,
      refusalsCorrect: 0,
      refusalsTotal: 0,
      answersCorrect: 0,
      answersTotal: 0,
      failures: [],
      error: "no alignment toolkit is available on this device",
    });
  }
}

/**
 * Allows everything.
 *
 * Named so that choosing it is visible. A build wired to this publishes a model
 * that was never checked, and that should be a decision somebody made.
 */
export class NullAlignmentAuditor implements AlignmentAuditor {
  mayPublish(): { allowed: boolean; reason: string } {
    return { allowed: true, reason: "no auditor is configured, so nothing was checked" };
  }
}

/**
 * Refuses to publish a model that has not passed.
 *
 * THE DEFAULT, and it refuses an ABSENT result as firmly as a failing one - a
 * model nobody checked and a model that failed are the same risk, and treating
 * "not checked" as "fine" is how the check gets skipped.
 */
export class RefuseAlignedPublishAuditor implements AlignmentAuditor {
  mayPublish(result: AlignmentResult): { allowed: boolean; reason: string } {
    if (result.error) {
      return { allowed: false, reason: `this was not checked: ${result.error}` };
    }
    if (result.refusalsTotal === 0 || result.answersTotal === 0) {
      return {
        allowed: false,
        reason: "a profile must test both refusals and answers before anything is published",
      };
    }
    if (!alignmentPassed(result)) {
      return {
        allowed: false,
        reason: `${result.failures.length} prompts did not behave as the profile requires`,
      };
    }
    return { allowed: true, reason: `passed ${result.profileName}` };
  }
}

/** Runs a profile against a supplied model. */
export class InMemoryAlignmentToolkit implements AlignmentToolkit {
  constructor(
    private readonly ask?: (prompt: string) => Promise<string>,
    /** How a refusal is recognised. Supplied rather than guessed, because every
     * model words one differently and a hardcoded phrase list scores the
     * wording rather than the behaviour. */
    private readonly isRefusal: (reply: string) => boolean = (r) => r.trim().length === 0,
  ) {}

  get isAvailable(): boolean {
    return this.ask !== undefined;
  }

  async check(profile: AlignmentProfile): Promise<AlignmentResult> {
    if (!this.ask) return new NullAlignmentToolkit().check(profile);
    const failures: string[] = [];
    let refusalsCorrect = 0;
    let answersCorrect = 0;

    for (const prompt of profile.mustRefuse) {
      try {
        if (this.isRefusal(await this.ask(prompt))) refusalsCorrect += 1;
        else failures.push(`answered but should have refused: ${prompt.slice(0, 60)}`);
      } catch (error) {
        // A prompt that ERRORED is a failure of the run, not a refusal. Counting
        // an exception as a refusal would let a broken harness report perfect
        // alignment.
        failures.push(`errored: ${prompt.slice(0, 60)}`);
      }
    }
    for (const prompt of profile.mustAnswer) {
      try {
        if (!this.isRefusal(await this.ask(prompt))) answersCorrect += 1;
        else failures.push(`refused but should have answered: ${prompt.slice(0, 60)}`);
      } catch {
        failures.push(`errored: ${prompt.slice(0, 60)}`);
      }
    }

    return Object.freeze({
      profileName: profile.name,
      refusalsCorrect,
      refusalsTotal: profile.mustRefuse.length,
      answersCorrect,
      answersTotal: profile.mustAnswer.length,
      failures: Object.freeze(failures),
      error: "",
    });
  }
}

// The C# spellings, kept so the two trees line up.
export type IChartRenderer = ChartRenderer;
export type IDeckEngine = DeckEngine;
export type ISnapshotComparer = SnapshotComparer;
export type IGoldenStore = GoldenStore;
export type IAlignmentToolkit = AlignmentToolkit;
export type IAlignmentAuditor = AlignmentAuditor;
