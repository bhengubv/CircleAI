#nullable enable

// ChartSpecFactory.cs
//
// Ready-made sample specs — for a "does it render?" smoke test, a template
// preview thumbnail, or as copy-paste starting points. The data is generic and
// self-contained (no external source, nothing Google-shaped), so it is safe to
// show to any user.

using System.Collections.Generic;

namespace CircleAI.Charts;

/// <summary>Sample <see cref="ChartSpec"/> builders covering each chart type.</summary>
public static class ChartSpecFactory
{
    /// <summary>A single-series bar chart with value labels.</summary>
    public static ChartSpec SampleBar() => new(
        ChartType.Bar,
        "Monthly Active Users",
        new[]
        {
            new ChartSeries("Users", new[]
            {
                new ChartDataPoint("Jan", 1200),
                new ChartDataPoint("Feb", 1580),
                new ChartDataPoint("Mar", 1490),
                new ChartDataPoint("Apr", 2100),
                new ChartDataPoint("May", 2460),
                new ChartDataPoint("Jun", 2890),
            }),
        },
        ValueAxisLabel: "users",
        ShowValueLabels: true);

    /// <summary>A clustered bar chart comparing two series over the same categories.</summary>
    public static ChartSpec SampleGroupedBar() => new(
        ChartType.Bar,
        "Revenue vs Cost by Quarter",
        new[]
        {
            new ChartSeries("Revenue", new[]
            {
                new ChartDataPoint("Q1", 42000),
                new ChartDataPoint("Q2", 51000),
                new ChartDataPoint("Q3", 47500),
                new ChartDataPoint("Q4", 63000),
            }),
            new ChartSeries("Cost", new[]
            {
                new ChartDataPoint("Q1", 31000),
                new ChartDataPoint("Q2", 34000),
                new ChartDataPoint("Q3", 33500),
                new ChartDataPoint("Q4", 39000),
            }),
        },
        ValueAxisLabel: "ZAR");

    /// <summary>A two-line trend chart over a shared category axis.</summary>
    public static ChartSpec SampleLine() => new(
        ChartType.Line,
        "Weekly Sign-ups",
        new[]
        {
            new ChartSeries("This year", new[]
            {
                new ChartDataPoint("W1", 120),
                new ChartDataPoint("W2", 168),
                new ChartDataPoint("W3", 154),
                new ChartDataPoint("W4", 205),
                new ChartDataPoint("W5", 246),
                new ChartDataPoint("W6", 233),
            }),
            new ChartSeries("Last year", new[]
            {
                new ChartDataPoint("W1", 90),
                new ChartDataPoint("W2", 110),
                new ChartDataPoint("W3", 132),
                new ChartDataPoint("W4", 150),
                new ChartDataPoint("W5", 149),
                new ChartDataPoint("W6", 178),
            }),
        });

    /// <summary>A pie chart with percentage labels.</summary>
    public static ChartSpec SamplePie() => new(
        ChartType.Pie,
        "Traffic by Channel",
        new[]
        {
            new ChartSeries("Channels", new[]
            {
                new ChartDataPoint("Direct", 38),
                new ChartDataPoint("Search", 27),
                new ChartDataPoint("Social", 19),
                new ChartDataPoint("Referral", 11),
                new ChartDataPoint("Email", 5),
            }),
        },
        ShowValueLabels: true);

    /// <summary>All four samples, e.g. for a one-pass render test.</summary>
    public static IReadOnlyList<ChartSpec> All() => new[]
    {
        SampleBar(),
        SampleGroupedBar(),
        SampleLine(),
        SamplePie(),
    };
}
