// IndexingQueue.cs
//
// THE INDEXING, QUEUED (the indexed mind). Building a model's index - Watch, the core index, step 2's pieces and layers,
// the check - is hours of a phone's idle time for a model bigger than its RAM, and a phone is picked up long before that.
// So the work is a queue of units, each small enough to stand aside for a question:
//
//   Watch reads one passage           a unit a passage; its records go after the passages before it
//   the core index (step 3)           built from what Watch saw
//   step 2's pieces, a layer          a unit a layer, written where they will stay: a thought at a time, its layers in order
//   step 2's layers                   the pieces take their place, and every layer's attention - with its ffn_down where
//                                     the layers take their own - is tiled once beside them, so no pass repacks step 1
//   the check                         the index against every neuron, by whoever holds the model
//
// "1) Download llm, 2) split, organise by thoughts, 3) create CORE index. Select coherent portions from 3 into RAM for
// 4) warm index. Only 4 ever reaches into 3, only 3 reaches into 2 and only 2 reaches into 1" (the owner, 2026-11-15).
//
// A unit that is done is recorded on flash before the next begins, so a process that goes - the phone picked up, the
// engine let go, the battery flat - carries on from the last unit recorded and does none of them twice. A unit that is
// stopped part way records nothing and runs again from its start; what it had written is written over. The record is a
// small text file beside the index, which is also where any other process reads how far the indexing has got. Before
// every unit the queue looks that what the units before it left is still on flash, and starts again when it is not.
//
// WHEN A UNIT RUNS is not decided here (IndexingWhen): whoever holds the model asks for the next unit when the phone is idle.

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using CircleAI.Inference.Managed.Gguf;
using CircleAI.Inference.Managed.Model;

namespace CircleAI.Inference.Managed.Store;

/// <summary>How far the indexing has got: units of work done, of how many.</summary>
/// <param name="Done">Units done and recorded.</param>
/// <param name="Total">Units in the queue.</param>
/// <param name="Agreement">
/// What the check measured, the queue's last unit, as a rate per thousand: of the held-out words the check read, how
/// many in every thousand the index gave the same top word as every neuron gives (nine hundred is nine in ten). Null
/// until the check has run.
/// </param>
/// <param name="EveryNeuron">
/// Whether the index lists every neuron of each layer, by this engine (<see cref="IndexingQueue.EngineVersion"/>): the last
/// share tried (<see cref="IndexingRungs"/>).
/// </param>
/// <param name="FromItsOwnFile">
/// Whether no index was built because it would list every neuron: the brain computes every neuron from the model's own
/// file - what the check measures an index against - so it serves with no check to run
/// (<see cref="IndexingQueue.EveryNeuronFromItsOwnFile"/>).
/// </param>
public readonly record struct IndexingStatus(int Done, int Total, int? Agreement = null, bool EveryNeuron = false, bool FromItsOwnFile = false)
{
    /// <summary>
    /// The least the check must measure for an index to serve, a rate per thousand: nine in ten of the held-out words
    /// giving the same top word as every neuron gives (nine hundred in every thousand, whatever the count checked).
    /// </summary>
    /// <remarks>
    /// "REPORT ONLY PASS OR FAIL AGAINST A MEASUREMENT" (the owner's rule for the indexed mind), AND THE CHECK IS THE
    /// MEASUREMENT. It was logged and decided nothing: on the P30 on 2026-11-13 the 27B's index - eight thought groups a
    /// layer, lists of 2,624 of 17,408 neurons - was complete with "the same top word as every neuron 25.0% of the
    /// time", and a complete index made its brain ready to take the phone over. The measure is kept with the record,
    /// and an index under this serves nobody: every neuron is computed, and its brain is not ready (BrainInWaiting).
    /// </remarks>
    public const int LeastAgreement = 900;

    /// <summary>Whether every unit is done.</summary>
    public bool Complete => Total > 0 && Done >= Total;

    /// <summary>
    /// Whether the index serves: every unit done, and its check measured <see cref="LeastAgreement"/> or better - or none was
    /// built, and every neuron is computed from the model's own file (<see cref="FromItsOwnFile"/>).
    /// </summary>
    public bool Serves => Complete && (FromItsOwnFile || Agreement is >= LeastAgreement);

    /// <summary>Whether every unit is done and its check measured under <see cref="LeastAgreement"/>: it serves nobody.</summary>
    public bool Failed => Complete && Agreement is not null && !Serves;

    /// <summary>
    /// Whether its check failed with every neuron listed, by this engine: no share is left to list. The engine builds no
    /// index of every neuron for the climb (<see cref="FromItsOwnFile"/>), so this is an index asked for by name, and the
    /// engine follows it with every neuron from the model's own file when it next opens the model's indexing. Any other
    /// index that failed its check is followed by one listing more neurons.
    /// </summary>
    /// <remarks>
    /// "IF IT FAILS THEN CHECK WHY IT FAILED. CLEAN UP AND TRY AGAIN, EVERYTHING DEPENDS ON THE REASON WHY" (the owner,
    /// 2026-11-15). An index lists a share of each layer's neurons, and too small a share is a reason with a remedy - list
    /// more. An index of every neuron was taken to read exactly what every neuron reads, so one that still failed was
    /// called the engine's fault and the end of its brain until the engine changed. IT DID NOT READ THE SAME: its pieces
    /// hold each neuron's output weights rounded to fifteen levels (DownPiece), and a layer whose stream brings the mixing
    /// alone reads that copy. Ten test models' indexes of every neuron, read from flash, measured 906 to 1,000 per
    /// thousand with it, and all ten 1,000 with the model's own weights (2026-11-15); the P30's 4B measured 87.5%.
    /// </remarks>
    public bool NothingLeftToTry => Failed && EveryNeuron;

    /// <summary>The share done, 0 to 100, rounded down: 100 only once every unit is done.</summary>
    public int Percent => Complete ? 100 : Total <= 0 ? 0 : Math.Min(99, (int)((long)Done * 100 / Total));

    /// <summary>The share done, 0 to 1.</summary>
    public double Fraction => Total <= 0 ? 0 : Math.Min(1.0, (double)Done / Total);
}

/// <summary>What an index is to be built from, and the flash it may take.</summary>
/// <param name="Passages">What Watch reads, as tokens: a passage a unit, each read from a fresh conversation.</param>
/// <param name="Groups">Thought groups a layer.</param>
/// <param name="ListNeurons">Neurons in each group's list (rounded up to whole tiles).</param>
/// <param name="CoreBudget">The flash step 2's pieces may take; the groups the most words fell in come first.</param>
/// <param name="SharedBudget">
/// The flash step 2's layers may take - every layer's attention, and its ffn_down where the layers take their own, tiled
/// (<see cref="IndexingBudget.SharedLayers"/>); 0 where there is no room for them, and the layers read the model's own
/// file in place.
/// </param>
/// <param name="Channels">Readers the device's flash takes at once.</param>
/// <param name="ReadBytes">The read the device's flash runs at nearly full speed.</param>
/// <param name="Share">
/// The share of each layer's neurons the lists were asked for, in hundredths (<see cref="IndexingRungs"/>); 0 where it is
/// not known. Kept with the record, so whoever reads it knows which share was tried.
/// </param>
/// <param name="Coherent">
/// Whether a word's thought is found once, at the first layer, and kept through every layer (<see cref="ThoughtIndex.Coherent"/>);
/// false where each layer groups its words on its own. Kept with the record.
/// </param>
public sealed record IndexingPlan(IReadOnlyList<int[]> Passages, int Groups, int ListNeurons, long CoreBudget, long SharedBudget,
    int Channels = 4, int ReadBytes = 1 << 20, int Share = 0, bool Coherent = false);

/// <summary>
/// STEP 2 ON FLASH (the owner, 2026-11-15: "2) split, organise by thoughts"): the thoughts' <paramref name="Pieces"/>, and
/// every layer's attention (<paramref name="Mixing"/>) - with its ffn_down (<paramref name="Down"/>) where the layers take
/// their own (<see cref="CachedIndex.LayersTakeTheirOwnDown(ThoughtIndex)"/>) - tiled once, so no pass repacks the model's
/// own file. A layer store that is not there (no room for it) leaves those layers to step 2's reach into step 1: read from
/// the model's own file in place and repacked as they land.
/// </summary>
public sealed record SplitByThoughts(CachedIndex Pieces, TileStore? Mixing, TileStore? Down)
{
    /// <summary>The kinds of the feed-forward's set step 2's ffn_down store holds: ffn_down alone.</summary>
    public static IReadOnlyCollection<int> DownKinds { get; } = [2];
}

/// <summary>
/// The shares of each layer's neurons an index lists, tried in turn: an index whose check fails is followed by one that
/// lists the next share, up to every neuron.
/// </summary>
/// <remarks>
/// TOO FEW NEURONS IS A REASON WITH A REMEDY (the owner, 2026-11-15: "if it fails then check why it failed. clean up and
/// try again, everything depends on the reason why"). Every index on the P30 was built at fifteen in a hundred - lists of
/// 1,392 of the 4B's 9,216 neurons, 1,856 of the 9B's 12,288, 2,624 of the 27B's 17,408 - and every one failed its check:
/// the same top word as every neuron 21.9%, 12.5% and 25.0% of the time. A failed index's files are removed as before;
/// the next lists more. AT EVERY NEURON NO INDEX IS BUILT (the owner, 2026-11-15, of a brain that needs every neuron: "Yes,
/// take over"): the model's own file is every neuron, exact, and an index of every neuron is only a copy of it - 1.64
/// times the bytes of its feed-forward, its output weights rounded - so the brain computes every neuron from its own file
/// (<see cref="IndexingStatus.FromItsOwnFile"/>). One asked for by name is still built as asked.
/// </remarks>
public static class IndexingRungs
{
    /// <summary>The shares tried, in hundredths, smallest first. The first is the engine's own (ManagedChatGenerator).</summary>
    public static IReadOnlyList<int> Shares { get; } = [15, 30, 50, 100];

    /// <summary>The share to try after an index listing <paramref name="tried"/> failed its check; null after every neuron.</summary>
    public static int? Next(int tried)
    {
        foreach (var share in Shares)
            if (share > tried) return share;
        return null;
    }

    /// <summary>The share of <paramref name="feedForward"/> neurons a list of <paramref name="listNeurons"/> is, in hundredths.</summary>
    public static int ShareOf(int listNeurons, int feedForward) =>
        feedForward <= 0 ? 0 : listNeurons >= feedForward ? 100 : (int)Math.Round(100.0 * listNeurons / feedForward);

    /// <summary>The thought groups a layer an index at <paramref name="share"/> is built with: one where it lists every neuron.</summary>
    /// <remarks>
    /// EVERY GROUP WOULD HOLD THE SAME LIST. A group's pieces are its listed neurons' weights, written to flash for it, so
    /// eight groups of every neuron are eight copies of the layer's feed-forward: for the 4B, 6.3 GB where one group's
    /// pieces are 784 MB (the P30, 2026-11-15).
    /// </remarks>
    public static int GroupsFor(int share, int groups) => share >= 100 ? 1 : groups;
}

/// <summary>What an index of a model was last tried as, read from its record (<see cref="IndexingQueue.Attempt"/>).</summary>
/// <param name="Groups">Thought groups a layer.</param>
/// <param name="ListNeurons">Neurons in each group's list.</param>
/// <param name="Share">The share of each layer's neurons that is, in hundredths (<see cref="IndexingRungs"/>).</param>
/// <param name="Status">How far it got, and how its check went.</param>
/// <param name="ThisEngine">Whether this engine made it (<see cref="IndexingQueue.EngineVersion"/>).</param>
/// <param name="Coherent">Whether its thoughts were coherent: found at the first layer and kept through every layer.</param>
public readonly record struct IndexAttempt(int Groups, int ListNeurons, int Share, IndexingStatus Status, bool ThisEngine, bool Coherent = false);

/// <summary>What was removed of an index whose check failed (<see cref="IndexingQueue.RemoveFailed"/>).</summary>
/// <param name="Model">The model's file name.</param>
/// <param name="Agreement">What its check measured, per thousand.</param>
/// <param name="Bytes">The bytes given back to the flash.</param>
public readonly record struct FailedIndexRemoved(string Model, int Agreement, long Bytes);

/// <summary>Whoever holds the model while its index is built: the queue's units that need the model run through it.</summary>
public interface IIndexingHost
{
    /// <summary>
    /// Reads <paramref name="words"/> as one passage from a fresh conversation, every neuron computed, with
    /// <paramref name="watch"/> recording. Asks <paramref name="stop"/> as it goes (a layer at most between looks) and
    /// returns false when it said to stop: the passage is then read again later.
    /// </summary>
    bool Watch(IReadOnlyList<int> words, ThoughtWatch watch, Func<bool>? stop);

    /// <summary>
    /// The check, once the index is on flash - step 3, <paramref name="core"/>, and step 2, <paramref name="split"/>:
    /// held-out words through every neuron and through the index. Returns how often they agree, a rate per thousand - of
    /// the held-out words, how many in every thousand the index gave the same top word - which the queue keeps with its record
    /// (<see cref="IndexingStatus.Agreement"/>). Null when <paramref name="stop"/> said to stop: it is run again later.
    /// </summary>
    int? Check(ThoughtIndex core, SplitByThoughts split, Func<bool>? stop);
}

/// <summary>A model's indexing as a queue of units, each recorded on flash as it is done (<see cref="IndexingQueue"/>'s notes).</summary>
public sealed class IndexingQueue : IDisposable
{
    private const string Kind = "indexing";
    private const string Header = "CAIINDEXING 1";

    private enum Step { Done, Stopped, Again }

    private readonly GgufFile _model;
    private readonly string _name, _request;
    private readonly int _layers, _embedding, _feedForward, _passages, _total;
    private int _done;
    private long _watchBytes;        // the watch file's length after the last passage recorded
    private int _agreement = -1;     // what the check measured, per thousand; -1 until it has run
    private ThoughtIndex? _core;
    private PieceStoreWriter? _writer;
    private int _disposed;

    private IndexingQueue(GgufFile model, string directory, IndexingPlan plan)
    {
        _model = model;
        var config = ModelConfig.FromGguf(model);
        _layers = config.Layers;
        _embedding = config.Embedding;
        _feedForward = config.FeedForward;
        _passages = plan.Passages.Count;
        _total = _passages + 1 + _layers + 1 + 1;
        _name = Path.GetFileName(model.FilePath);
        Directory = directory;
        Plan = plan;
        _request = Request(model, config, plan);
    }

    /// <summary>Where the queue's files are.</summary>
    public string Directory { get; }

    /// <summary>The plan in force: the one asked for, with the flash this work was given when it began.</summary>
    public IndexingPlan Plan { get; private set; }

    /// <summary>Watch's records.</summary>
    public string WatchPath => At("watch");

    /// <summary>The core index.</summary>
    public string IndexPath => At("index");

    /// <summary>Step 2's pieces: each thought's neurons at each layer.</summary>
    public string CorePath => At("core");

    /// <summary>Step 2's attention: every layer's mixing, tiled.</summary>
    public string MixPath => At("mix");

    /// <summary>Step 2's ffn_down: every layer's, tiled, where the layers take their own.</summary>
    public string DownPath => At("down");

    /// <summary>The queue's own record of how far it has got.</summary>
    public string StatePath => At(Kind);

    /// <summary>How far it has got.</summary>
    public IndexingStatus Status => new(Volatile.Read(ref _done), _total, _agreement >= 0 ? _agreement : null, EveryNeuron: Plan.Share >= 100);

    /// <summary>
    /// What makes an index this engine's: the versions of what it writes (Watch's records, the core index, the pieces) and
    /// <see cref="Revision"/>. Kept with the record, so whoever reads it knows which engine an index that failed its check
    /// with every neuron listed was made by (<see cref="IndexingStatus.NothingLeftToTry"/>).
    /// </summary>
    public static string EngineVersion { get; } = string.Create(CultureInfo.InvariantCulture,
        $"{ThoughtWatch.Version}.{ThoughtIndex.Version}.{CachedIndex.Version}.{Revision}");

    /// <summary>
    /// Raised with any change to how an index is built, served or checked that writes nothing new to flash - so an index
    /// that failed with every neuron listed is tried again by the engine that may mend it.
    /// </summary>
    /// <remarks>
    /// 2: an index of every neuron is not built for the climb; every neuron comes from the model's own file. 3: step 2 holds
    /// every layer's attention and ffn_down tiled, and no cached portion sits between step 4 and step 2.
    /// </remarks>
    public const int Revision = 3;

    /// <summary>
    /// Where a copy of every record whose check failed, and of every index built after one, is kept outside the engine's
    /// own folder - set by a host whose platform clears that folder (the Android service, in both its processes). Null keeps
    /// none.
    /// </summary>
    public static string? KeptDirectory { get; set; }

    /// <summary>Whether every unit is done.</summary>
    public bool Complete => Status.Complete;

    /// <summary>
    /// Whether the next unit reads words through the model - Watch's passages and the check. The others work from flash
    /// alone, so whoever holds the model can let its layers go from RAM while they run.
    /// </summary>
    public bool NextReadsTheModel
    {
        get
        {
            var done = Volatile.Read(ref _done);
            return done < _passages || done == _total - 1;
        }
    }

    /// <summary>The next unit, in words, for a log.</summary>
    public string Next
    {
        get
        {
            var done = Volatile.Read(ref _done);
            if (done >= _total) return "nothing: the index is complete";
            if (done < _passages) return string.Create(CultureInfo.InvariantCulture, $"Watch reads passage {done + 1} of {_passages}");
            if (done == _passages) return "the core index is built from what Watch saw";
            if (done <= _passages + _layers) return string.Create(CultureInfo.InvariantCulture, $"layer {done - _passages} of {_layers}'s pieces go to flash (step 2, a thought at a time)");
            return done == _passages + _layers + 1 ? "step 2's layers go to flash, tiled: every layer's attention, and its ffn_down where the layers take their own" : "the index is checked against every neuron";
        }
    }

    private string At(string kind) => Path.Combine(Directory, _name + "." + kind);

    /// <summary>
    /// The queue for <paramref name="model"/>'s index in <paramref name="directory"/>: where an earlier sitting's record
    /// says it had got to when that was the same work - the same model, passages, groups and lists; from the start
    /// otherwise. The flash in <paramref name="plan"/> is what the phone offers now: it is taken until the first pieces
    /// are written, and after that the flash the work was given stands - unless that was room for only part of the pieces
    /// and the phone now offers a good deal more, when the pieces are written again.
    /// </summary>
    public static IndexingQueue Open(GgufFile model, string directory, IndexingPlan plan)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.Passages.Count == 0 || plan.Passages.Any(p => p is null || p.Length == 0)) throw new ArgumentException("Watch needs at least one passage, and none of them empty.", nameof(plan));
        ArgumentOutOfRangeException.ThrowIfLessThan(plan.Groups, 1, nameof(plan));
        ArgumentOutOfRangeException.ThrowIfLessThan(plan.ListNeurons, 1, nameof(plan));

        System.IO.Directory.CreateDirectory(directory);
        var queue = new IndexingQueue(model, directory, plan);
        if (Read(queue.StatePath) is { } held && held.Name == queue._name && held.Length == model.Length && held.Request == queue._request && held.Total == queue._total)
        {
            queue._done = Math.Clamp(held.Done, 0, queue._total);
            queue._watchBytes = held.WatchBytes;
            queue._agreement = (int)Math.Clamp(held.Agreement, -1, 1000);
            queue.Plan = plan with
            {
                CoreBudget = held.CoreBudget,
                SharedBudget = held.SharedBudget,
                ReadBytes = held.ReadBytes > 0 ? (int)Math.Min(held.ReadBytes, int.MaxValue) : plan.ReadBytes,
            };
            if (FailedItsCheck(held))
            {
                // A FAILED INDEX KEEPS ITS RECORD AND NOTHING ELSE. Its files serve nobody and are removed when its check
                // fails (Tidy) - and here, where an earlier build left them. So what the record says is done is not asked
                // to be on flash: complete and failed is where it stays, and it is not built again as the same work - the
                // engine opens the next share as other work (IndexingRungs, ManagedChatGenerator.OpenIndex). One thing
                // builds this one again: it had room for only part of its pieces and the phone now offers a good deal
                // more. Then from the start - what Watch saw went with the rest.
                queue.Tidied(RemovePieces(directory, queue._name), "what an index that failed its check left");
                if (queue.OfferedMoreRoom(plan))
                {
                    queue._done = 0;
                    queue._watchBytes = 0;
                    queue._agreement = -1;
                    queue._core = null;
                    queue.Plan = plan;
                }
            }
            else if (!queue.Holds())
            {
                // What the record says is done is not all on flash (a cleared cache, a file cut short): from the start,
                // with the flash that is offered now.
                queue._done = 0;
                queue._watchBytes = 0;
                queue._agreement = -1;
                queue._core = null;
                queue.Plan = plan;
            }
            else
            {
                queue.TakeTheRoomOffered(plan);
            }

            // EVERY UNIT DONE AND NO MEASURE KEPT - a record from before the check's measure was kept with it: the check
            // is the unit that runs again, so the index is judged before it serves.
            if (queue._done >= queue._total && queue._agreement < 0) queue._done = queue._total - 1;
        }
        queue.Commit();
        return queue;
    }

    // THE FLASH THE WORK WAS GIVEN STANDS WHILE ITS PIECES ARE WRITTEN: where each piece lies in its file depends on it.
    // Two things move it. Until the first layer's pieces are written nothing depends on it, so what the phone offers now
    // is taken - a phone's free flash changes from one day to the next. And where the work was given room for only part
    // of the core's pieces and the phone now offers a quarter more than that or better - something was cleared off it -
    // the pieces are written again, more of them: Watch and the core index stand, and the queue is not complete (so no
    // index serves) until the pieces are back.
    private void TakeTheRoomOffered(IndexingPlan offered)
    {
        if (_done <= _passages + 1)
        {
            Plan = offered;
            return;
        }
        if (!OfferedMoreRoom(offered)) return;
        Plan = offered;
        _done = _passages + 1;
        _agreement = -1;
    }

    // Whether the work was given room for only part of the core's pieces and the phone now offers a quarter more or better.
    private bool OfferedMoreRoom(IndexingPlan offered)
    {
        var list = ThoughtIndex.ListOf(Plan.ListNeurons, _feedForward);
        var all = IndexingBudget.CompleteCore(_layers, Plan.Groups, list, _embedding, Plan.ReadBytes, withDown: !CachedIndex.LayersTakeTheirOwnDown(list, _feedForward));
        return Plan.CoreBudget < all && offered.CoreBudget >= Plan.CoreBudget + Plan.CoreBudget / 4;
    }

    // ---- what is left behind, removed ----
    //
    // "WE NEED TO CLEAN UP AFTER OURSELVES OR WE WILL KILL USERS PHONES" (the owner, 2026-11-14). An index is gigabytes in
    // an app's cache folder, on phones that have a few free. Two things in it are read by nothing once a unit is
    // recorded, and they go when it is:
    //
    //   WATCH'S RECORDS, once the core index is built from them. Every word Watch read is kept a layer at a time - the
    //   thought's sketch and the neurons that fired - 2,306 bytes a layer on the 27B (ThoughtWatch.RecordBytes), 144 KB
    //   a word. The core index is what the pieces are written from; nothing opens the records again.
    //
    //   EVERY FILE OF AN INDEX WHOSE CHECK FAILED. It serves nobody (IndexingStatus.Serves). On the P30 on 2026-11-14
    //   the 27B's pieces took 3.6 GB of a phone with 4.3 GB free, and nothing would ever have removed them.
    //
    // THE RECORD STAYS. It is a few hundred bytes and it is what says the check failed, and at which share: without it the
    // index would be built again at that share, for hours, into the same failure - where the next is built listing more
    // neurons (IndexingRungs).

    // What an index is kept in beside its record, each with the unfinished copy a stopped write leaves - "cached" the
    // portion an engine before Revision 3 wrote between step 4 and step 2, removed where one was left.
    private static readonly string[] PieceKinds = ["watch", "index", "core", "mix", "down", "cached", "use"];

    private long _tidied;
    private string _tidiedWhat = string.Empty;

    /// <summary>
    /// What the queue has removed from flash since this was last asked, and what it was: nothing read again once a unit
    /// was recorded - Watch's records after the core index is built, every file of an index whose check failed.
    /// </summary>
    public (long Bytes, string What) TakeTidied()
    {
        var taken = (_tidied, _tidiedWhat);
        _tidied = 0;
        _tidiedWhat = string.Empty;
        return taken;
    }

    private void Tidied(long bytes, string what)
    {
        if (bytes <= 0) return;
        _tidied += bytes;
        _tidiedWhat = what;
    }

    // After a unit is recorded: what it leaves that nothing reads again.
    private void Tidy(int unitDone)
    {
        if (unitDone == _passages)
        {
            Tidied(Remove(WatchPath), "Watch's records, which the core index is built from");
        }
        else if (unitDone == _total - 1 && _agreement is >= 0 and < IndexingStatus.LeastAgreement)
        {
            _core = null;
            Tidied(RemovePieces(Directory, _name), "every file of the index, which failed its check");
        }
    }

    /// <summary>
    /// Removes what the index of the model whose file is named <paramref name="modelFile"/> left in
    /// <paramref name="directory"/> - Watch's records, the core index, step 2's pieces and layers, a cached portion an
    /// older engine left, and the count of its use, each with any unfinished copy - and keeps its record, which says how
    /// its check went. Returns the bytes given back to the flash.
    /// </summary>
    public static long RemovePieces(string directory, string modelFile)
    {
        if (string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(modelFile)) return 0;
        var name = Path.GetFileName(modelFile);
        long freed = 0;
        foreach (var kind in PieceKinds)
        {
            freed += Remove(Path.Combine(directory, name + "." + kind));
            freed += Remove(Path.Combine(directory, name + "." + kind + ".partial"));
        }
        return freed;
    }

    /// <summary>
    /// Removes what every index in <paramref name="directory"/> whose check failed left there (<see cref="RemovePieces"/>),
    /// by any process and with no model open: for a brain that is not loaded again once its index has failed. Returns
    /// each one that had something to remove.
    /// </summary>
    public static IReadOnlyList<FailedIndexRemoved> RemoveFailed(string directory)
    {
        var removed = new List<FailedIndexRemoved>();
        try
        {
            if (string.IsNullOrWhiteSpace(directory) || !System.IO.Directory.Exists(directory)) return removed;
            foreach (var record in System.IO.Directory.EnumerateFiles(directory, "*." + Kind).ToList())
            {
                if (Read(record) is not { } held || !FailedItsCheck(held)) continue;
                var freed = RemovePieces(directory, held.Name);
                if (freed > 0) removed.Add(new FailedIndexRemoved(held.Name, (int)held.Agreement, freed));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // What could not be listed is left, and found the next time.
        }
        return removed;
    }

    /// <summary>
    /// Keeps a copy, in <paramref name="keptDirectory"/>, of the record of every index in <paramref name="directory"/> whose
    /// check failed or that was built after one failed (a share above the first, <see cref="IndexingRungs"/>) - among them
    /// every record that says its brain computes every neuron from the model's own file
    /// (<see cref="IndexingStatus.FromItsOwnFile"/>) - and lets a kept copy go once the record there says an index serves,
    /// or that an index at the first share has begun again. Returns the models whose record was kept now, and those whose
    /// kept copy was let go.
    /// </summary>
    /// <remarks>
    /// THE RECORD IS WHAT SAYS WHICH SHARE FAILED, AND IT LIVES IN A FOLDER THE PHONE CLEARS (the owner, 2026-11-14: "Yes,
    /// do it", of keeping it somewhere Android cannot clear it). The engine's folder is the app's cache folder, emptied when
    /// the phone is short of room; a copy kept outside it is read wherever the engine's own is gone (BrainInWaiting.IndexOf,
    /// <see cref="Attempt"/>), so the next index still lists more neurons than the one that failed and is not built again
    /// at a share already tried. The same bytes, written to a file of their own first and then into place, so a reader
    /// never sees half of one. A record that is not this engine's, or says nothing yet, is left as it is. A BRAIN THAT
    /// COMPUTES EVERY NEURON FROM ITS OWN FILE has nothing on flash beside it but that record: cleared with the cache, its
    /// climb would build its indexes again from the first share, hours of the phone's idle time, to the same end.
    /// </remarks>
    public static (IReadOnlyList<string> Kept, IReadOnlyList<string> LetGo) KeepFailed(string directory, string keptDirectory)
    {
        var kept = new List<string>();
        var letGo = new List<string>();
        if (string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(keptDirectory)) return (kept, letGo);
        try
        {
            if (!System.IO.Directory.Exists(directory)) return (kept, letGo);
            foreach (var record in System.IO.Directory.EnumerateFiles(directory, "*." + Kind).ToList())
            {
                try
                {
                    if (Read(record) is not { } held) continue;
                    var copy = Path.Combine(keptDirectory, Path.GetFileName(record));
                    var status = StatusOf(held);
                    var revision = held.Share > IndexingRungs.Shares[0] && (!status.Serves || status.FromItsOwnFile);
                    if (!FailedItsCheck(held) && !revision)
                    {
                        // IT SERVES, OR AN INDEX AT THE FIRST SHARE HAS BEGUN AGAIN: how that goes is what its record says now.
                        if (File.Exists(copy))
                        {
                            File.Delete(copy);
                            letGo.Add(held.Name);
                        }
                        continue;
                    }
                    var text = File.ReadAllText(record, Encoding.UTF8);
                    if (File.Exists(copy) && string.Equals(File.ReadAllText(copy, Encoding.UTF8), text, StringComparison.Ordinal)) continue;
                    System.IO.Directory.CreateDirectory(keptDirectory);
                    File.WriteAllText(copy + ".partial", text, Encoding.UTF8);
                    File.Move(copy + ".partial", copy, overwrite: true);
                    kept.Add(held.Name);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // This one could not be kept now: it is tried again the next time.
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // What could not be listed is kept the next time.
        }
        return (kept, letGo);
    }

    // Every unit done, and the check measured under what an index must reach.
    private static bool FailedItsCheck(Held held) =>
        held.Total > 0 && held.Done >= held.Total && held.Agreement is >= 0 and < IndexingStatus.LeastAgreement;

    // A file's bytes, once it is gone; nothing where it is not there, is held open or may not be removed - it is left,
    // and tried again the next time.
    private static long Remove(string path)
    {
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists) return 0;
            var bytes = file.Length;
            file.Delete();
            return bytes;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    /// <summary>
    /// The groups a layer and the neurons a list that an index of <paramref name="model"/> in <paramref name="directory"/>
    /// was begun with, from its record; null when none was begun.
    /// </summary>
    public static (int Groups, int ListNeurons)? Asked(GgufFile model, string directory)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (string.IsNullOrWhiteSpace(directory)) return null;
        var held = Read(Path.Combine(directory, Path.GetFileName(model.FilePath) + "." + Kind));
        return held is { Groups: > 0, List: > 0 } && held.Length == model.Length ? ((int)held.Groups, (int)held.List) : null;
    }

    /// <summary>
    /// What an index of <paramref name="model"/> was last tried as: its record in <paramref name="directory"/>, or - where
    /// the engine's folder holds none - the copy kept in <paramref name="keptDirectory"/> (<see cref="KeptDirectory"/> when
    /// none is given). Null when neither holds a record of this model's file.
    /// </summary>
    /// <remarks>
    /// WHAT TO BUILD NEXT IS READ FROM HERE. An index whose check failed is followed by one listing the next share
    /// (<see cref="IndexingRungs"/>); the share it was tried at is its record's, or - for a record from before the share
    /// was kept - the share its lists are of this model's neurons.
    /// </remarks>
    public static IndexAttempt? Attempt(GgufFile model, string directory, string? keptDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        var record = Path.GetFileName(model.FilePath) + "." + Kind;
        var held = (string.IsNullOrWhiteSpace(directory) ? null : Read(Path.Combine(directory, record)))
                   ?? ((keptDirectory ?? KeptDirectory) is { Length: > 0 } kept ? Read(Path.Combine(kept, record)) : null);
        if (held is not { Groups: > 0, List: > 0 } || held.Length != model.Length) return null;
        var share = held.Share > 0
            ? (int)Math.Min(held.Share, 100)
            : IndexingRungs.ShareOf((int)Math.Min(held.List, int.MaxValue), ModelConfig.FromGguf(model).FeedForward);
        return new IndexAttempt((int)held.Groups, (int)held.List, share, StatusOf(held),
            string.Equals(held.Engine, EngineVersion, StringComparison.Ordinal), held.Coherent);
    }

    /// <summary>
    /// Records in <paramref name="directory"/> that <paramref name="model"/> is not being indexed, so whoever reads how far
    /// the indexing there has got (<see cref="ReadStatus"/>) does not take an earlier model's record for this one's.
    /// Nothing is written where no record says anything.
    /// </summary>
    public static void NotIndexing(GgufFile model, string directory)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        try
        {
            if (ReadStatus(directory) is null) return;   // nothing there that could be taken for this model's
            var name = Path.GetFileName(model.FilePath);
            var path = Path.Combine(directory, name + "." + Kind);
            var text = string.Create(CultureInfo.InvariantCulture, $"{Header}\nmodel={name}\nlength={model.Length}\nrequest=-\ndone=0\ntotal=0\n");
            File.WriteAllText(path + ".partial", text, Encoding.UTF8);
            File.Move(path + ".partial", path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Nothing is said; an earlier record may stand until the next load.
        }
    }

    // What the record of a brain that computes every neuron from its own file says in place of a request, and where it
    // says so: no work is the same work as it, so any index asked for after it is begun from the start.
    private const string EveryNeuronRequest = "every-neuron";
    private const string FromModel = "model";

    /// <summary>
    /// Records in <paramref name="directory"/> - and, where one is set, in <see cref="KeptDirectory"/> - that no index of
    /// <paramref name="model"/> is built because it would list every neuron: the brain computes every neuron from the
    /// model's own file (<see cref="IndexingStatus.FromItsOwnFile"/>). Returns what the record says. Nothing of an index is
    /// written, and what one left is not removed here (<see cref="RemovePieces"/>).
    /// </summary>
    public static IndexingStatus EveryNeuronFromItsOwnFile(GgufFile model, string directory)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        var name = Path.GetFileName(model.FilePath);
        var text = string.Create(CultureInfo.InvariantCulture,
            $"{Header}\nmodel={name}\nlength={model.Length}\nrequest={EveryNeuronRequest}\ngroups=1\nlist={ModelConfig.FromGguf(model).FeedForward}\nshare=100\nengine={EngineVersion}\nread=0\ncore=0\nshared=0\ndone=1\ntotal=1\nwatch=0\nagrees=-1\nfrom={FromModel}\n");
        WriteRecord(directory, name, text);
        try
        {
            if (KeptDirectory is { Length: > 0 } kept) WriteRecord(kept, name, text);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Kept the next time housekeeping keeps the records (KeepFailed).
        }
        return ReadStatus(directory, name) ?? throw new InvalidDataException($"{name}'s record did not read back from {directory}.");
    }

    // A record, written to a file of its own first and then into place, so a reader never sees half of it.
    private static void WriteRecord(string directory, string modelFile, string text)
    {
        System.IO.Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, modelFile + "." + Kind);
        File.WriteAllText(path + ".partial", text, Encoding.UTF8);
        File.Move(path + ".partial", path, overwrite: true);
    }

    /// <summary>
    /// How far the indexing in <paramref name="directory"/> has got, read from its record on flash - by any process; null
    /// when nothing is being indexed there.
    /// </summary>
    public static IndexingStatus? ReadStatus(string directory)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(directory) || !System.IO.Directory.Exists(directory)) return null;
            var newest = System.IO.Directory.EnumerateFiles(directory, "*." + Kind).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
            return newest is not null && Read(newest) is { } held ? StatusOf(held) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The name of the record an index of the model whose file is named <paramref name="modelFile"/> keeps.</summary>
    public static string RecordName(string modelFile)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelFile);
        return Path.GetFileName(modelFile) + "." + Kind;
    }

    /// <summary>
    /// How far the indexing of the model whose file is named <paramref name="modelFile"/> has got, from its own record
    /// in <paramref name="directory"/>: null when no engine has recorded an indexing of that model there - nothing of
    /// it at all, or that it is not being indexed.
    /// </summary>
    /// <remarks>
    /// BY NAME, FOR WHOEVER ASKS ABOUT A MODEL THE ENGINE IS NOT HOLDING. The newest record
    /// (<see cref="ReadStatus(string)"/>) is the model loaded last; a phone that answers with one brain while another's
    /// index is built in its idle time has two, and the one a person is told about is the one being built.
    /// </remarks>
    public static IndexingStatus? ReadStatus(string directory, string modelFile)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(modelFile)) return null;
            return Read(Path.Combine(directory, Path.GetFileName(modelFile) + "." + Kind)) is { } held ? StatusOf(held) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// Does the next unit and records it. False when there is none left, or when <paramref name="stop"/> said to stop
    /// before it was done: then nothing is recorded and the unit runs again, from its start, the next time.
    /// </summary>
    public bool RunNext(IIndexingHost host, Func<bool>? stop = null)
    {
        ArgumentNullException.ThrowIfNull(host);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        while (true)
        {
            if (Complete || stop?.Invoke() == true) return false;
            if (!Holds())
            {
                // WHAT THE UNITS SO FAR LEFT IS NOT ALL ON FLASH - a phone short of room clears a cache folder under a
                // running app. The queue goes back to its start rather than building on files that are gone.
                _writer?.Dispose();
                _writer = null;
                _core = null;
                _watchBytes = 0;
                _agreement = -1;
                Volatile.Write(ref _done, 0);
                Commit();
            }
            var done = _done;
            var step = done < _passages ? Watch(done, host, stop)
                : done == _passages ? BuildCore(stop)
                : done <= _passages + _layers ? WriteLayer(done - _passages - 1, stop)
                : done == _passages + _layers + 1 ? SplitLayers(stop)
                : Check(host, stop);
            if (step == Step.Again) continue;   // an earlier unit's work was not there: the queue went back to it
            if (step == Step.Stopped) return false;
            Volatile.Write(ref _done, _done + 1);
            Commit();
            Tidy(done);   // only once the unit is on record: a file is never removed for work that may be asked for again
            return true;
        }
    }

    /// <summary>
    /// REVISE, once the index is complete: the heat of step 2's pieces written again from <paramref name="use"/> (a count a
    /// group, by layer then group), in their own table, so step 4 warms the thoughts used most first. The pieces stay where
    /// they lie. Returns step 2's pieces as they now read.
    /// </summary>
    public CachedIndex Revise(IReadOnlyList<long> use)
    {
        ArgumentNullException.ThrowIfNull(use);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (!Complete) throw new InvalidOperationException("The index is revised once it is complete.");
        var core = Core();
        var pieces = CachedIndex.Open(CorePath, _model, core) ?? throw new InvalidDataException($"{CorePath} does not open as this model's pieces.");
        return CachedIndex.Revise(pieces, use);
    }

    /// <summary>
    /// Step 2 as the units left it on flash: the pieces, and the layers' attention and ffn_down where they were written.
    /// Null where the pieces are not there.
    /// </summary>
    public SplitByThoughts? Split()
    {
        var core = Core();
        if (CachedIndex.Open(CorePath, _model, core) is not { } pieces) return null;
        return new SplitByThoughts(pieces, TileStore.Open(_model, MixPath, TileSet.Mixing),
            CachedIndex.LayersTakeTheirOwnDown(core) ? TileStore.Open(_model, DownPath, TileSet.FeedForward, SplitByThoughts.DownKinds) : null);
    }

    // ---- the units ----

    // WATCH READS ONE PASSAGE: its records go after the passages recorded before it, anything a stopped reading left cut off.
    private Step Watch(int passage, IIndexingHost host, Func<bool>? stop)
    {
        long bytes;
        using (var watch = ThoughtWatch.Append(WatchPath, _layers, _embedding, _feedForward, ThoughtIndex.ListOf(Plan.ListNeurons, _feedForward),
                   keep: passage == 0 ? 0 : _watchBytes))
        {
            if (!host.Watch(Plan.Passages[passage], watch, stop)) return Step.Stopped;
            bytes = watch.Bytes;
        }
        _watchBytes = bytes;
        return Step.Done;
    }

    private Step BuildCore(Func<bool>? stop)
    {
        var core = ThoughtIndex.TryBuild(ThoughtWatch.Read(WatchPath), IndexPath, Plan.Groups, Plan.ListNeurons, stop, Plan.Coherent);
        if (core is null) return Step.Stopped;
        _core = core;
        return Step.Done;
    }

    // A LAYER'S PIECES, written where they will stay in a file that takes its place once every layer is in - without their
    // ffn_down where the layers take their own, whole, from step 2's layers.
    private Step WriteLayer(int layer, Func<bool>? stop)
    {
        if (_writer is null)
        {
            _writer = BeginPieces(resume: layer > 0);
            if (layer > 0 && !_writer.Resumed) return BackToFirstLayer();
        }
        return _writer.Write(layer, stop) ? Step.Done : Step.Stopped;
    }

    private PieceStoreWriter BeginPieces(bool resume)
    {
        var core = Core();
        return PieceStoreWriter.Begin(core, _model, CorePath, Plan.CoreBudget, Plan.Channels, Plan.ReadBytes, resume: resume,
            withDown: !CachedIndex.LayersTakeTheirOwnDown(core));
    }

    // The unfinished file of pieces is not there, or not this plan's: its layers are written again from the first.
    private Step BackToFirstLayer()
    {
        _writer?.Dispose();
        _writer = null;
        _agreement = -1;
        Volatile.Write(ref _done, _passages + 1);
        Commit();
        return Step.Again;
    }

    // STEP 2'S PIECES TAKE THEIR PLACE, AND ITS LAYERS ARE TILED BESIDE THEM: every layer's attention, and its ffn_down
    // where the layers take their own, read from step 1 and repacked once - here, on the charger - so no pass repacks them.
    // On the P30 on 2026-11-15 a pass of the 9B that read its layers from the model's own file spent 43 of its 52 seconds
    // repacking them (TileStore.ReadLayer). A store already whole is not written again; one stopped part way is.
    private Step SplitLayers(Func<bool>? stop)
    {
        var core = Core();
        if (_writer is null && File.Exists(CorePath + ".partial"))
        {
            _writer = BeginPieces(resume: true);
            if (!_writer.Resumed) return BackToFirstLayer();
        }
        if (_writer is not null)
        {
            _writer.Finish();
            _writer = null;
        }
        else if (CachedIndex.Open(CorePath, _model, core) is null)
        {
            // A sitting that ended after the pieces took their place and before that was recorded finds them; else they are gone.
            return BackToFirstLayer();
        }
        if (Plan.SharedBudget <= 0) return Step.Done;   // no room for step 2's layers: they are read from step 1 in place
        try
        {
            if (TileStore.Unreadable(_model, TileSet.Mixing) is null && TileStore.Open(_model, MixPath, TileSet.Mixing) is null)
                TileStore.Write(_model, MixPath, TileSet.Mixing, channels: Plan.Channels, readBytes: Plan.ReadBytes, stop: stop);
            if (CachedIndex.LayersTakeTheirOwnDown(core) && TileStore.Unreadable(_model, TileSet.FeedForward) is null
                && TileStore.Open(_model, DownPath, TileSet.FeedForward, SplitByThoughts.DownKinds) is null)
                TileStore.Write(_model, DownPath, TileSet.FeedForward, channels: Plan.Channels, readBytes: Plan.ReadBytes, kinds: SplitByThoughts.DownKinds, stop: stop);
        }
        catch (OperationCanceledException)
        {
            return Step.Stopped;
        }
        return Step.Done;
    }

    private Step Check(IIndexingHost host, Func<bool>? stop)
    {
        Core();
        if (Split() is not { } split) return BackToFirstLayer();
        if (host.Check(Core(), split, stop) is not { } measured) return Step.Stopped;
        _agreement = Math.Clamp(measured, 0, 1000);
        return Step.Done;
    }

    // Whether step 2's layers are on flash as the plan asked: none where it gave them no room, or the model's are not tiled.
    private bool LayersHeld(ThoughtIndex core)
    {
        if (Plan.SharedBudget <= 0) return true;
        if (TileStore.Unreadable(_model, TileSet.Mixing) is null && TileStore.Open(_model, MixPath, TileSet.Mixing) is null) return false;
        return !CachedIndex.LayersTakeTheirOwnDown(core) || TileStore.Unreadable(_model, TileSet.FeedForward) is not null
            || TileStore.Open(_model, DownPath, TileSet.FeedForward, SplitByThoughts.DownKinds) is not null;
    }

    private ThoughtIndex Core() => _core ??= ThoughtIndex.Open(IndexPath) is { } core && Fits(core)
        ? core
        : throw new InvalidDataException($"{IndexPath} does not open as this model's core index.");

    private bool Fits(ThoughtIndex core) =>
        core.Layers == _layers && core.Embedding == _embedding && core.FeedForward == _feedForward
        && core.Groups == Plan.Groups && core.ListNeurons == ThoughtIndex.ListOf(Plan.ListNeurons, _feedForward)
        && core.Coherent == Plan.Coherent;

    // Whether what the record says is done is on flash for the next unit to work from.
    private bool Holds()
    {
        if (_done == 0) return true;
        if (_done <= _passages)
        {
            // Watch's records so far: a watch of this model's shape, at least as long as the record says.
            try
            {
                var watch = ThoughtWatch.Read(WatchPath);
                return watch.Layers == _layers && watch.FeedForward == _feedForward && watch.Embedding == _embedding
                    && new FileInfo(WatchPath).Length >= _watchBytes && _watchBytes > 0;
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                return false;
            }
        }
        if (ThoughtIndex.Open(IndexPath) is not { } core || !Fits(core)) return false;
        _core = core;
        if (_done == _passages + 1) return true;                                             // no layer written yet
        if (_done <= _passages + 1 + _layers) return File.Exists(CorePath + ".partial") || CachedIndex.Open(CorePath, _model, core) is not null;
        return CachedIndex.Open(CorePath, _model, core) is not null && LayersHeld(core);
    }

    // ---- the record ----

    private sealed record Held(string Name, long Length, string Request, long CoreBudget, long SharedBudget, int Done, int Total, long WatchBytes,
        long Groups, long List, long ReadBytes, long Agreement, long Share = -1, string? Engine = null, string? From = null, bool Coherent = false);

    // FROM ITS OWN FILE BY WHICHEVER ENGINE WROTE IT: what the record says is so of the model's file, not of an index.
    private static IndexingStatus StatusOf(Held held) => new(held.Done, held.Total, held.Agreement >= 0 ? (int)Math.Min(held.Agreement, 1000) : null,
        EveryNeuron: held.Share >= 100 && string.Equals(held.Engine, EngineVersion, StringComparison.Ordinal),
        FromItsOwnFile: string.Equals(held.From, FromModel, StringComparison.Ordinal) && held.Done >= held.Total);

    // The record, written beside the index: to a file of its own first, then into place, so a reader never sees half of it.
    // The share its lists were asked for and the engine that made it go with it: what to try next when its check fails.
    private void Commit()
    {
        var text = string.Create(CultureInfo.InvariantCulture,
            $"{Header}\nmodel={_name}\nlength={_model.Length}\nrequest={_request}\ngroups={Plan.Groups}\nlist={Plan.ListNeurons}\nshare={Plan.Share}\nengine={EngineVersion}\nread={Plan.ReadBytes}\ncore={Plan.CoreBudget}\nshared={Plan.SharedBudget}\ndone={_done}\ntotal={_total}\nwatch={_watchBytes}\nagrees={_agreement}\n{(Plan.Coherent ? "thoughts=coherent\n" : string.Empty)}");
        var partial = StatePath + ".partial";
        File.WriteAllText(partial, text, Encoding.UTF8);
        File.Move(partial, StatePath, overwrite: true);
    }

    private static Held? Read(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var lines = File.ReadAllLines(path, Encoding.UTF8);
            if (lines.Length == 0 || lines[0] != Header) return null;
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var line in lines.Skip(1))
            {
                var at = line.IndexOf('=', StringComparison.Ordinal);
                if (at > 0) values[line[..at]] = line[(at + 1)..];
            }
            long Number(string key) => values.TryGetValue(key, out var v) && long.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : -1;
            var total = Number("total");
            var done = Number("done");
            if (!values.TryGetValue("model", out var name) || !values.TryGetValue("request", out var request) || total <= 0 || done < 0) return null;
            return new Held(name, Number("length"), request, Number("core"), Math.Max(0, Number("shared")), (int)Math.Min(done, total), (int)total, Math.Max(0, Number("watch")),
                Number("groups"), Number("list"), Number("read"), Number("agrees"), Number("share"),
                values.TryGetValue("engine", out var engine) && engine.Length > 0 ? engine : null,
                values.TryGetValue("from", out var from) && from.Length > 0 ? from : null,
                values.TryGetValue("thoughts", out var thoughts) && string.Equals(thoughts, "coherent", StringComparison.Ordinal));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // What makes two sittings the same work: the model, its shape, the layouts, the groups, the lists and what Watch reads.
    // Not the flash it may take - that is kept from the sitting that began the work.
    private static string Request(GgufFile model, ModelConfig config, IndexingPlan plan)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var number = new byte[8];
        void Add(long value)
        {
            System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(number, value);
            hash.AppendData(number);
        }
        Add(model.Length);
        Add(config.Layers);
        Add(config.Embedding);
        Add(config.FeedForward);
        Add(ThoughtWatch.Version);
        Add(ThoughtIndex.Version);
        Add(CachedIndex.Version);
        Add(plan.Groups);
        Add(ThoughtIndex.ListOf(plan.ListNeurons, config.FeedForward));
        if (plan.Coherent) Add(1);   // coherent thoughts are other work; an index of per-layer groups keeps the request it had
        Add(plan.Passages.Count);
        foreach (var passage in plan.Passages)
        {
            Add(passage.Length);
            foreach (var word in passage) Add(word);
        }
        return Convert.ToHexString(hash.GetHashAndReset(), 0, 12);
    }

    /// <summary>Lets go of the file it was writing. What is on flash stays, for the next sitting to carry on from.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _writer?.Dispose();
        _writer = null;
    }
}
