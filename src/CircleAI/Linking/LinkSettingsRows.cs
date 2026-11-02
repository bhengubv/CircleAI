// LinkSettingsRows.cs
//
// THE COLUMN ORDER FOR STEP 4'S ROWS - SETTINGS, WHAT IT CAN DO NOW, AND EVERY MODEL ON ITS WAY
// IN - IN ONE PLACE, BOTH DIRECTIONS.
//
// The same rule as LinkSetupRows and LinkAskRows, with one more: EVERY ROW CARRIES EVERY COLUMN
// OF ITS IN-PROCESS TYPE. The setup row once had four columns where the service had measured
// five things, and the fifth - how much of a stopped download was already down - died at this
// seam for a month. LinkSettingsRowsTests counts each type's members against its row, so a
// member added to the type and not to the wire fails a test instead of a person.
// Columns are only ever APPENDED; every reader tolerates a short row.

using System;
using System.Collections.Generic;
using System.Globalization;
using CircleAI.Assistant;
using CircleAI.Core;

namespace CircleAI.Linking;

/// <summary>Row layouts for the settings verbs, the live abilities, and the setup board's items.</summary>
public static class LinkSettingsRows
{
    /// <summary>The first column of a setup-board item row in a SetupProgress reply.</summary>
    /// <remarks>
    /// A TAG, because those rows ride after the run's own summary row, which an older client reads
    /// as row 0 and nothing else - so the summary keeps its six columns and the items mark themselves.
    /// </remarks>
    public const string ItemTag = "item";

    // ── settings: [key, value, default, kind, title, description] ─────────────

    /// <summary>One preference as a row.</summary>
    public static IReadOnlyList<string> Setting(SettingRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return new[] { row.Key, row.Value, row.Default, row.Kind.ToString(), row.Title, row.Description };
    }

    /// <summary>A preference read back, or null when the row has no key.</summary>
    public static SettingRow? ReadSetting(IReadOnlyList<string> row)
    {
        var key = LinkSetupRows.Text(row, 0);
        if (key.Length == 0) return null;
        return new SettingRow(
            key,
            LinkSetupRows.Text(row, 1),
            LinkSetupRows.Text(row, 2),
            Enum.TryParse<PreferenceKind>(LinkSetupRows.Text(row, 3), out var kind) ? kind : PreferenceKind.Text,
            LinkSetupRows.Text(row, 4),
            LinkSetupRows.Text(row, 5));
    }

    // ── what it can do now: [title, state, detail, progress] ──────────────────

    /// <summary>One ability's live state as a row; an empty progress column means none is measured.</summary>
    public static IReadOnlyList<string> CanDo(CanDoRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return new[]
        {
            row.Title,
            row.State.ToString(),
            Clean(row.Detail),
            row.Progress is { } p ? p.ToString("R", CultureInfo.InvariantCulture) : string.Empty,
        };
    }

    /// <summary>An ability's live state read back. A state this build does not know reads as Cannot.</summary>
    public static CanDoRow ReadCanDo(IReadOnlyList<string> row)
    {
        var progress = LinkSetupRows.Text(row, 3);
        return new CanDoRow(
            LinkSetupRows.Text(row, 0),
            Enum.TryParse<NowState>(LinkSetupRows.Text(row, 1), out var state) ? state : NowState.Cannot,
            LinkSetupRows.Text(row, 2),
            progress.Length == 0 ? null : LinkSetupRows.Fraction(row, 3));
    }

    // ── a model on its way in: [item, modelId, title, state, done, total, rate, secondsLeft, detail, attempt, sinceMs]

    /// <summary>One setup-board item as a row.</summary>
    public static IReadOnlyList<string> Item(SetupItemStatus item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var c = CultureInfo.InvariantCulture;
        return new[]
        {
            ItemTag,
            item.ModelId,
            Clean(item.Title),
            item.State.ToString(),
            item.BytesDone.ToString(c),
            item.BytesTotal.ToString(c),
            item.BytesPerSecond.ToString("R", c),
            item.Remaining.TotalSeconds.ToString("R", c),
            Clean(item.Detail),
            item.Attempt.ToString(c),
            item.Since.ToUnixTimeMilliseconds().ToString(c),
        };
    }

    /// <summary>A setup-board item read back, or null when the row is not one.</summary>
    public static SetupItemStatus? ReadItem(IReadOnlyList<string> row)
    {
        if (LinkSetupRows.Text(row, 0) != ItemTag) return null;
        var id = LinkSetupRows.Text(row, 1);
        if (id.Length == 0) return null;

        var sinceMs = LinkSetupRows.Number(row, 10);
        return new SetupItemStatus(
            id,
            LinkSetupRows.Text(row, 2),
            Enum.TryParse<SetupItemState>(LinkSetupRows.Text(row, 3), out var state) ? state : SetupItemState.Queued,
            LinkSetupRows.Number(row, 4),
            LinkSetupRows.Number(row, 5),
            LinkSetupRows.Fraction(row, 6),
            TimeSpan.FromSeconds(Math.Max(0, LinkSetupRows.Fraction(row, 7))),
            LinkSetupRows.Text(row, 8),
            (int)LinkSetupRows.Number(row, 9, 1),
            sinceMs > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(sinceMs) : DateTimeOffset.MinValue);
    }

    /// <summary>Every setup-board item in a SetupProgress reply, skipping its summary row.</summary>
    public static IReadOnlyList<SetupItemStatus> ReadItems(IReadOnlyList<IReadOnlyList<string>> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var items = new List<SetupItemStatus>();
        foreach (var row in rows)
            if (ReadItem(row) is { } item) items.Add(item);
        return items;
    }

    /// <summary>The unit separator joins fields on the wire, so it can never be inside one.</summary>
    private static string Clean(string? text) => string.IsNullOrEmpty(text) ? string.Empty : text.Replace('\u001F', ' ');
}
