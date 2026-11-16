// IndexingWhen.cs
//
// WHEN THE INDEXING RUNS, AND HOW MUCH FLASH IT TAKES. The owner, 2026-11-12: "We queue the indexing for whenever the
// phone is idle - best at bedtime but runs whenever it can". The queue (IndexingQueue) is the work; these are the
// decisions about it, kept apart from whoever holds the model so they can be read and tested as what they are - rules
// over what the phone says about itself. Nothing about a device is written here: the phone's own readings go in.
//
// OFF ITS CHARGER THE PHONE ANSWERS AND BUILDS NOTHING (the owner, 2026-11-15: "if we only perform while tethered then
// we are not performing"; "we should be able to tap in and out without anything crashing out"). On the P30 that day an
// index asked for by name went on reading Watch's passages, the CPU flat out, after the phone came off its cable and into
// a hand - and two minutes later the phone's own power manager had ended the engine's service and would not let it start
// again. Answering is what must work untethered; the index is built once, on the charger (the design: "processes it
// once (overnight, on the charger)").
//
//   IDLE      nobody is asking the engine anything and has not for a little while, nothing says the person is using the
//             phone (InUse: its screen), and the phone is neither hot nor low.
//   BEDTIME   idle and on charge: the work runs unit after unit, without pause.
//   OFF CHARGE nothing: a unit running stands aside the moment the phone comes off its charger.
//   UNKNOWN   idle on a device that does not say whether it charges: a unit, then a rest as long as the unit took.

using CircleAI.Core;
using CircleAI.Inference.Managed.Gguf;
using CircleAI.Inference.Managed.Model;

namespace CircleAI.Inference.Managed.Store;

/// <summary>How much indexing to do now.</summary>
public enum IndexingPace
{
    /// <summary>None: the phone is in use, hot, low, or off its charger.</summary>
    Wait = 0,

    /// <summary>One unit, then a rest: the phone is idle and does not say whether it is on charge.</summary>
    OneUnit = 1,

    /// <summary>Unit after unit: the phone is idle and on charge - bedtime.</summary>
    BackToBack = 2,
}

/// <summary>When the indexing queue's units run (<see cref="IndexingWhen"/>'s notes).</summary>
public static class IndexingWhen
{
    /// <summary>How long the engine must have been left alone before indexing takes it: somebody may be mid-conversation.</summary>
    public static readonly TimeSpan Quiet = TimeSpan.FromSeconds(30);

    /// <summary>The least time between two nights' revisions.</summary>
    public static readonly TimeSpan Night = TimeSpan.FromHours(20);

    /// <summary>
    /// How much indexing to do now: none while a call is <paramref name="wanted"/> (waiting for the engine or running on
    /// it), while the person is using the phone (<paramref name="inUse"/>; null when the device does not say), while the
    /// phone is hot, low or off its charger, or until the engine has been <paramref name="quiet"/> for
    /// <see cref="Quiet"/>; unit after unit on charge; a unit at a time on a device that does not say whether it charges.
    /// </summary>
    public static IndexingPace Pace(bool wanted, TimeSpan quiet, PowerState power, bool? inUse)
    {
        ArgumentNullException.ThrowIfNull(power);
        if (wanted || inUse == true || power.Hot || power.Low || OffItsCharger(power) || quiet < Quiet) return IndexingPace.Wait;
        return power.Charging == true ? IndexingPace.BackToBack : IndexingPace.OneUnit;
    }

    /// <summary>Whether the phone says it is off its charger: the index builds nothing then, and a unit running stands aside.</summary>
    public static bool OffItsCharger(PowerState power)
    {
        ArgumentNullException.ThrowIfNull(power);
        return power.Charging == false;
    }

    /// <summary>
    /// Whether the person is using the phone, as its screen says it: no when the screen is dark; yes when it is lit and
    /// would go dark by itself were the phone left alone; and null - the screen does not say - when the phone itself is
    /// keeping it lit.
    /// </summary>
    /// <param name="screenLit">Whether the screen is on.</param>
    /// <param name="keptLitByThePhone">
    /// Whether the phone is keeping its screen on whoever is or is not there: set to stay awake while it charges, and
    /// on charge.
    /// </param>
    /// <remarks>
    /// THE OWNER, 2026-11-13, of a P30 on its cable whose indexing had not begun: "p30 is idle right? so it should be
    /// running". It was idle, and the rule here was "the screen is off": that phone has Stay awake on, so on the cable
    /// its screen never goes off and the rule never saw it idle, however long it lay untouched. A lit screen is a sign
    /// of use only because a phone left alone darkens it; where the phone is told not to, the sign is gone, and what
    /// is left to go by is whether anybody is asking (<see cref="Pace"/>'s quiet).
    /// </remarks>
    public static bool? InUse(bool screenLit, bool keptLitByThePhone) => !screenLit ? false : keptLitByThePhone ? null : true;

    /// <summary>
    /// Whether the phone itself is keeping its screen lit: it is set to stay awake on a kind of charger
    /// (<paramref name="stayAwakeOn"/>) and is plugged into one of that kind (<paramref name="pluggedInto"/>). Both are
    /// sets of bits, a bit a kind of charger - mains 1, USB 2, wireless 4 - as Android gives its "Stay awake" setting
    /// and what the battery is plugged into.
    /// </summary>
    /// <remarks>
    /// On the P30 on 2026-11-13 the setting was 7 - every kind - and the phone was on its cable: its screen had not gone
    /// off in days, with nobody near it.
    /// </remarks>
    public static bool KeptLit(int stayAwakeOn, int pluggedInto) => (stayAwakeOn & pluggedInto) != 0;

    /// <summary>The share of its swap a phone keeps free of this work: an eighth.</summary>
    public const int SwapKeptFree = 8;

    /// <summary>The least memory the phone must have available for a unit to begin or go on, where it gives no line of its own.</summary>
    public const long LeastAvailable = 256L << 20;

    /// <summary>
    /// Whether the phone is too short of memory for the indexing to begin a unit or go on with one: what it has
    /// <paramref name="available"/> is within a quarter of its own low-memory line (<paramref name="systemLine"/>: where
    /// its system begins ending other apps' processes), or, where it gives no line, under
    /// <see cref="LeastAvailable"/>. A phone that does not say what it has available says nothing either way.
    /// </summary>
    /// <remarks>
    /// "WITHOUT HARMING THE PHONE" - BY THE PHONE'S OWN MEASURE, NOT BY ITS SWAP. The first rule here kept an eighth of
    /// the swap free, because on the P30 on 2026-11-13 a unit of the 27B's indexing was seen to take the swap from
    /// 898 MB free to 7 MB. That phone lives with its swap nearly full: eleven days up, 2.4 GB of 2.5 in zram, memory
    /// "status normal", and not one low-memory kill ("Total number of kills: 0") - through that 7 MB too. The swap rule
    /// stopped the last unit of the queue twice, once forty seconds from its end, on dips to 294 and 245 MB that
    /// harmed nothing. What harms a phone is its system ending processes for memory, and it says where that begins
    /// (315 MB there): the unit stands aside a quarter above it, gives its buffers back, and is tried again later.
    /// </remarks>
    public static bool ShortOfMemory(long? available, long? systemLine) =>
        available is { } has && has < (systemLine is { } line && line > 0 ? line + line / 4 : LeastAvailable);

    /// <summary>What the warm index is given where the phone does not say what it has available.</summary>
    public const long UnknownWarmRoom = 128L << 20;

    /// <summary>
    /// The RAM the warm index may take - "RAM is the turbo": what the phone has <paramref name="available"/> beyond
    /// the <paramref name="reserve"/> kept for it and what the layers' stream holds, and, on a phone that swaps, no more
    /// than half of the swap that is free above the share kept free (<see cref="SwapKeptFree"/>) - so that taking it
    /// does not leave the phone short of memory (<see cref="ShortOfMemory"/>).
    /// </summary>
    /// <remarks>
    /// "AVAILABLE" COUNTS WHAT THE PHONE COULD DROP, NOT WHAT IT HAS SPARE. On the P30 on 2026-11-13 the check came to
    /// its pass through the index with 751 MB available and 669 MB of 2,524 MB of swap free; the warm index took what
    /// "available" left it, about 300 MB, and sixteen seconds later 294 MB of swap was free and the check stood aside.
    /// </remarks>
    public static long WarmRoom(long? available, long? swapFree, long? swapTotal, long reserve, long streamBytes)
    {
        if (available is not { } room) return UnknownWarmRoom;
        room = Math.Max(0, room - reserve - streamBytes);
        if (swapTotal is { } total && total > 0 && swapFree is { } free) room = Math.Min(room, Math.Max(0, free - total / SwapKeptFree) / 2);
        return room;
    }

    /// <summary>
    /// How long to leave the phone alone after a unit that <paramref name="worked"/> this long: nothing on charge; as
    /// long again where the phone does not say it is charging, so a device in a pocket works half the time and does not
    /// warm. (A phone that says it is off its charger builds nothing at all: <see cref="Pace"/>.)
    /// </summary>
    public static TimeSpan Rest(TimeSpan worked, PowerState power)
    {
        ArgumentNullException.ThrowIfNull(power);
        return power.Charging == true || worked < TimeSpan.Zero ? TimeSpan.Zero : worked;
    }

    /// <summary>
    /// Whether tonight's revision is due: there is use to learn from (<paramref name="usedSince"/> words since the last
    /// one), a night has passed since <paramref name="lastRevised"/>, and it is bedtime.
    /// </summary>
    public static bool ReviseDue(long usedSince, DateTimeOffset lastRevised, DateTimeOffset now, IndexingPace pace) =>
        usedSince > 0 && pace == IndexingPace.BackToBack && now - lastRevised >= Night;
}

/// <summary>The flash an index may take on this phone: what it has free, less what is kept for the phone.</summary>
public static class IndexingBudget
{
    /// <summary>Flash kept free for the phone, whatever the index would like.</summary>
    public const long Reserve = 1L << 30;

    /// <summary>
    /// The most room asked for step 2's tiled layers before a model's file is here (ModelFit.IndexRoomBytes): ASSUMED - the
    /// room the cached portion was asked for before it was taken out (2026-11-15), so which brains are fetched does not
    /// change with it. Once the file is here the engine counts what the layers take (<see cref="SharedLayers"/>).
    /// </summary>
    public const long MostLayers = 2L << 30;

    // Where the phone does not say what is free: the room an index is given.
    private const long UnknownRoom = 2L << 30;

    /// <summary>
    /// The flash every piece of step 2 takes: a piece a group a layer, each <see cref="CachedIndex.Stride"/> long - without
    /// its ffn_down where <paramref name="withDown"/> is false - and the table that says where they lie.
    /// </summary>
    public static long CompleteCore(int layers, int groups, int listNeurons, int embedding, int readBytes, bool withDown = true)
    {
        var pieces = (long)layers * groups;
        return FlashFile.Pages(CachedIndex.HeaderBytes + pieces * CachedIndex.EntryBytes) + pieces * CachedIndex.Stride(listNeurons, embedding, readBytes, withDown);
    }

    /// <summary>
    /// The flash step 2's layers take for <paramref name="model"/> indexed with lists of <paramref name="listNeurons"/>
    /// neurons: every layer's attention tiled, and its ffn_down where the layers take their own
    /// (<see cref="CachedIndex.LayersTakeTheirOwnDown(int, int)"/>); 0 for what the model's file holds in no tiles.
    /// </summary>
    public static long SharedLayers(GgufFile model, int listNeurons)
    {
        ArgumentNullException.ThrowIfNull(model);
        long bytes = 0;
        if (TileStore.Unreadable(model, TileSet.Mixing) is null) bytes += Stored(TileStore.OverModel(model, TileSet.Mixing));
        if (CachedIndex.LayersTakeTheirOwnDown(listNeurons, ModelConfig.FromGguf(model).FeedForward) && TileStore.Unreadable(model, TileSet.FeedForward) is null)
            bytes += Stored(TileStore.OverModel(model, TileSet.FeedForward, SplitByThoughts.DownKinds));
        return bytes;

        // A written store: its header page, its index of 64 bytes a tensor in whole pages, and its tiles.
        static long Stored(TileStore over) => FlashFile.Page + FlashFile.Pages((long)over.Entries.Count * 64) + over.DataBytes;
    }

    /// <summary>
    /// The flash for step 2's pieces and for its layers, of <paramref name="free"/> bytes free (null when the phone does not
    /// say), <paramref name="completeCore"/> bytes for every piece and <paramref name="shared"/> for the layers: both, when
    /// there is room for both; else the pieces take the room - the groups the most words fell in first - and the layers
    /// are read from the model's own file in place, as they are with no index.
    /// </summary>
    public static (long Core, long Shared) For(long? free, long completeCore, long shared = 0)
    {
        var room = free is { } f ? Math.Max(0, f - Reserve) : UnknownRoom;
        completeCore = Math.Max(0, completeCore);
        shared = Math.Max(0, shared);
        if (room >= completeCore + shared) return (completeCore, shared);
        return (Math.Min(completeCore, room), 0);
    }
}
