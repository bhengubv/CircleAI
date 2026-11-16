// IndexingQueueTests.cs
//
// THE INDEXING, QUEUED. The owner, 2026-11-12:
//
//   "We queue the indexing for whenever the phone is idle - best at bedtime but runs whenever it can - user stays updated -
//    "Until indexing is complete AI is functioning at xx%.""
//   "Later they get told "The more you use it the more it learns what the user needs" (What to keep in RAM - warm cached
//    index)"
//
// Written from those words, not from the engine's code. Each test quotes the words it checks and measures them with what
// the engine reports: the units done of how many, the line the person is told, what a stopped unit left on flash, the
// bytes a finished queue's files hold against one sitting's, the pieces RAM served after a night.
//
// ON A SMALL MODEL, AT THE ENGINE'S CLASSES. The engine's generator (ManagedChatGenerator) drives the same queue from its
// idle clock on a phone; that wiring is run in TheIndexOnTheEnginesIdleClockTests, on a model that speaks, with the vector
// kernel's layout taken for the test and a phone's readings given in the phone's place.

using System.Globalization;
using CircleAI.Assistant.Lifecycle;
using CircleAI.Core;
using CircleAI.Inference.Managed.Gguf;
using CircleAI.Inference.Managed.Model;
using CircleAI.Inference.Managed.Store;
using CircleAI.Linking;
using Xunit;

namespace CircleAI.Tests;

public sealed class IndexingQueueTests
{
    private const int Layers = 4;
    private const int Ff = SyntheticQwen35.FeedForward; // 384 neurons a layer

    // What Watch reads, three passages of eight words, and the words the tests then ask about.
    private static readonly int[][] Passages =
    [
        [3, 8, 12, 20, 6, 11, 14, 27],
        [5, 9, 30, 2, 17, 25, 4, 22],
        [13, 7, 29, 16, 10, 31, 19, 26],
    ];
    private static readonly int[] Asked = [5, 9, 30, 2, 17, 21, 8, 12, 24, 15];

    // Watch's passages, the core index (step 3), a unit a layer of step 2's pieces, step 2's tiled layers, the check.
    private const int Units = 3 + 1 + Layers + 1 + 1;

    private static IndexingPlan Plan(int groups = 2, int list = Ff, long core = long.MaxValue, long shared = long.MaxValue) =>
        new(Passages, groups, list, core, shared, Channels: 2, ReadBytes: FlashFile.Page);

    // ---- "We queue the indexing" ... "user stays updated" ----

    // THE OWNER: "We queue the indexing"; "user stays updated - "Until indexing is complete AI is functioning at xx%."";
    // "Later they get told "The more you use it the more it learns what the user needs"". The work is units in a queue, done
    // one at a time; after each the person's line carries the share done; it never says 100 before the last unit; and once
    // the last is done the line is the later one. Another process reads the same share from flash.
    [Fact]
    public void The_indexing_is_a_queue_of_units_and_the_person_is_told_how_far_it_has_got()
    {
        WithModel(401, (file, directory) =>
        {
            var host = new Host(file);
            using var queue = IndexingQueue.Open(file, directory, Plan());
            Assert.Equal(new IndexingStatus(0, Units), queue.Status);
            Assert.Equal("Until indexing is complete AI is functioning at 0%.", ReadinessSentences.Indexing(queue.Status.Percent));

            for (var done = 1; done <= Units; done++)
            {
                Assert.False(queue.Complete);
                // Watch's passages and the check read words through the model; every other unit works from flash alone.
                Assert.Equal(done <= Passages.Length || done == Units, queue.NextReadsTheModel);
                var (watched, checkedBefore) = (host.Watched, host.Checked);
                Assert.True(queue.RunNext(host), $"unit {done} of {Units} did not run");
                Assert.Equal(done <= Passages.Length || done == Units, host.Watched + host.Checked > watched + checkedBefore);
                // The last unit is the check, and what it measured is kept with how far the queue has got.
                Assert.Equal(new IndexingStatus(done, Units, done == Units ? host.Agreement : null), queue.Status);
                Assert.Equal(queue.Status, IndexingQueue.ReadStatus(directory));
                if (done == Units) break;
                var percent = done * 100 / Units;
                Assert.True(queue.Status.Percent == percent && percent < 100, $"after {done} of {Units} units the share said is {queue.Status.Percent}%");
                Assert.Equal(string.Create(CultureInfo.InvariantCulture, $"Until indexing is complete AI is functioning at {percent}%."),
                    IndexReadiness.Line(queue.Status));
            }

            Assert.True(queue.Complete);
            Assert.Equal(100, queue.Status.Percent);
            Assert.Equal("The more you use it the more it learns what the user needs.", IndexReadiness.Line(queue.Status));
            Assert.False(queue.RunNext(host), "a finished queue ran a unit");
            Assert.Equal(new IndexingStatus(Units, Units, host.Agreement), queue.Status);
            Assert.Equal(Passages.Length, host.Watched);
            Assert.Equal(1, host.Checked);
        });
    }

    // THE OWNER: "runs whenever it can". The phone is picked up between units and the process may be gone by the next idle
    // moment: started again before every unit, the queue is where it was, no unit is done twice, and what it leaves on flash
    // is to the byte what one sitting leaves.
    [Fact]
    public void Started_again_before_every_unit_it_carries_on_and_leaves_what_one_sitting_leaves()
    {
        WithModel(403, (file, directory) =>
        {
            var sitting = Directory.CreateDirectory(Path.Combine(directory, "one-sitting")).FullName;
            var stopped = Directory.CreateDirectory(Path.Combine(directory, "stopped")).FullName;
            using (var queue = IndexingQueue.Open(file, sitting, Plan(groups: 4, list: 96)))
            {
                var whole = new Host(file);
                while (queue.RunNext(whole)) { }
                Assert.True(queue.Complete);
            }

            var host = new Host(file);
            for (var unit = 0; unit < Units; unit++)
            {
                using var queue = IndexingQueue.Open(file, stopped, Plan(groups: 4, list: 96));
                Assert.True(queue.Status.Done == unit, $"started again after {unit} units, the queue says {queue.Status.Done} are done");
                Assert.True(queue.RunNext(host), $"unit {unit + 1} did not run after a restart");
            }
            using (var queue = IndexingQueue.Open(file, stopped, Plan(groups: 4, list: 96)))
            {
                Assert.True(queue.Complete, "started again after the last unit, the queue is not complete");
                Assert.False(queue.RunNext(host));
            }

            Assert.True(host.Watched == Passages.Length && host.Checked == 1,
                $"over the restarts Watch read {host.Watched} passages of {Passages.Length} and the check ran {host.Checked} times");
            SameFiles(sitting, stopped);
        });
    }

    // THE OWNER: "whenever the phone is idle". A question arrives while a unit is in hand: the unit stops at its next look,
    // nothing is recorded as done, and the unit runs again from its start when the phone is idle - leaving, in the end, what
    // one sitting leaves. Stopped at its second look and, in another run, its fourth, so a unit is cut where it had written.
    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    public void A_question_stops_the_unit_in_hand_and_the_unit_runs_again_when_the_phone_is_idle(int look)
    {
        WithModel(405, (file, directory) =>
        {
            var sitting = Directory.CreateDirectory(Path.Combine(directory, "one-sitting")).FullName;
            var asked = Directory.CreateDirectory(Path.Combine(directory, "asked")).FullName;
            using (var queue = IndexingQueue.Open(file, sitting, Plan(groups: 4, list: 96)))
            {
                var whole = new Host(file);
                while (queue.RunNext(whole)) { }
            }

            var host = new Host(file);
            var stops = 0;
            using (var queue = IndexingQueue.Open(file, asked, Plan(groups: 4, list: 96)))
            {
                for (var unit = 0; unit < Units; unit++)
                {
                    var looks = 0;
                    var ran = queue.RunNext(host, () => ++looks >= look);
                    if (!ran)
                    {
                        stops++;
                        Assert.True(queue.Status.Done == unit, $"unit {unit + 1} was stopped and the queue says {queue.Status.Done} are done");
                        Assert.Equal(queue.Status, IndexingQueue.ReadStatus(asked));
                        Assert.True(queue.RunNext(host), $"unit {unit + 1} did not run again after it was stopped");
                    }
                    Assert.Equal(unit + 1, queue.Status.Done);
                }
                Assert.True(queue.Complete);
            }

            // Every kind of unit that takes time looks: Watch's passages, the layers' pieces, step 2's tiled layers and the check.
            Assert.True(stops >= Passages.Length + Layers, $"only {stops} of {Units} units stopped for a question at their look {look}");
            SameFiles(sitting, asked);
        });
    }

    // THE OWNER: "whenever the phone is idle" - a question must not wait for the indexing. How long it waits is how often a
    // unit looks: Watch looks before every layer of a passage, and a layer's unit before every group's piece.
    [Fact]
    public void A_unit_looks_for_a_waiting_question_at_every_layer_and_every_piece()
    {
        WithModel(407, (file, directory) =>
        {
            var host = new Host(file);
            using var queue = IndexingQueue.Open(file, directory, Plan(groups: 4, list: 96));
            var looks = new int[Units];
            for (var unit = 0; unit < Units; unit++)
            {
                var at = unit;
                Assert.True(queue.RunNext(host, () => { looks[at]++; return false; }));
            }
            for (var passage = 0; passage < Passages.Length; passage++)
                Assert.True(looks[passage] >= Layers, $"Watch's passage {passage + 1} looked {looks[passage]} times over {Layers} layers");
            var core = ThoughtIndex.Open(queue.IndexPath)!;
            for (var layer = 0; layer < Layers; layer++)
            {
                var pieces = Enumerable.Range(0, core.Groups).Count(g => core.Size(layer, g) > 0);
                var unit = Passages.Length + 1 + layer;
                Assert.True(looks[unit] >= pieces, $"layer {layer}'s unit looked {looks[unit]} times over {pieces} pieces");
            }
        });
    }

    // DESIGN: "Index (the core). A map of what relates to what, and each word fetches only its pieces." Once the queue is
    // done, what it left on flash serves: step 2's pieces complete, the model's words its own (ASSUMED: nine in ten, the
    // index listing every neuron), a word's knowledge layers its pieces - and ONLY STEP 2 REACHES STEP 1 (the owner,
    // 2026-11-15): the layers' attention and ffn_down come tiled from step 2, nothing read from the model's own file and
    // nothing repacked.
    [Fact]
    public void When_the_queue_is_done_the_model_answers_from_what_it_left_on_flash()
    {
        WithModel(409, (file, directory) =>
        {
            var host = new Host(file);
            using var queue = IndexingQueue.Open(file, directory, Plan());
            while (queue.RunNext(host)) { }
            Assert.True(queue.Complete);

            var core = ThoughtIndex.Open(queue.IndexPath);
            Assert.NotNull(core);
            var split = queue.Split();
            Assert.NotNull(split);   // step 2 on flash opens as this model's
            Assert.True(split.Pieces.HoldsAll(core), "with flash for all of it, step 2's pieces are not complete");
            Assert.True(CachedIndex.LayersTakeTheirOwnDown(core) && !split.Pieces.HoldsDown, "setup: an index of every neuron takes the layers' own ffn_down");
            Assert.True(split.Mixing is { InPlace: false } && split.Down is { InPlace: false }, "step 2 does not hold every layer's attention and ffn_down tiled");
            Assert.True(host.Checked == 1 && host.CheckedCore?.FilePath == queue.IndexPath, "the check did not run on the index the queue built");
            Assert.Equal(split.Pieces.FilePath, host.CheckedSplit?.Pieces.FilePath);

            var own = Logits(LanguageModel.Load(file, null, headFromStore: false, pairedOverride: true), Asked).Select(Top).ToArray();
            var model = LanguageModel.Load(file, null, headFromStore: false, ffnFromStore: true, pairedOverride: true, mixFromStore: true);
            using var serving = new IndexedFeedForward(core, split.Pieces, file, warmBudget: 0, channels: 2, readBytes: FlashFile.Page, withDown: false);
            using var stream = new LayerStream(split.Mixing, split.Down, channels: 2, readBytes: FlashFile.Page, warmBudget: 0, slots: 2);
            model.Stream = stream;
            model.Index = serving;
            stream.TakeParts();
            var words = Logits(model, Asked).Select(Top).ToArray();
            var counts = serving.TakeCounts();
            var (_, streamed, _) = stream.TakeCounts();
            var repacked = stream.TakeParts().Repacking;
            model.Index = null;
            model.Stream = null;

            var same = words.Zip(own).Count(p => p.First == p.Second);
            Assert.True(same * 10 >= Asked.Length * 9, $"from what the queue left, the model gave its own word {same} times in {Asked.Length}");
            Assert.Equal((long)Asked.Length * Layers, counts.Warm + counts.Ahead + counts.Asked + counts.Again);
            Assert.Equal(0, counts.FromModel);
            Assert.Equal(TimeSpan.Zero, repacked);
            var beyondLayers = streamed - Asked.Length * (split.Mixing!.DataBytes + split.Down!.DataBytes);
            Assert.True(beyondLayers <= stream.Slots * stream.SlotBytes, $"the layers' stream read {beyondLayers} bytes beyond step 2's attention and ffn_down");
        });
    }

    // DESIGN: "Core index, on flash: complete and lasting"; "Nothing about a device is written into the code. The device
    // measures itself". A phone with flash for only part of the core's pieces: the queue still finishes, and the pieces it
    // wrote are the groups the most of Watch's words fell in.
    [Fact]
    public void With_flash_for_part_of_the_pieces_the_most_used_groups_come_first_and_the_queue_still_finishes()
    {
        WithModel(411, (file, directory) =>
        {
            var whole = Directory.CreateDirectory(Path.Combine(directory, "whole")).FullName;
            long all;
            using (var queue = IndexingQueue.Open(file, whole, Plan(groups: 4, list: 96)))
            {
                var all4 = new Host(file);
                while (queue.RunNext(all4)) { }
                var complete = CachedIndex.Open(queue.CorePath, file, ThoughtIndex.Open(queue.IndexPath)!)!;
                all = complete.Count * complete.PieceStride;
            }

            var part = Directory.CreateDirectory(Path.Combine(directory, "part")).FullName;
            using var tight = IndexingQueue.Open(file, part, Plan(groups: 4, list: 96, core: all / 2, shared: 0));
            var host = new Host(file);
            while (tight.RunNext(host)) { }
            Assert.True(tight.Complete);
            var core = ThoughtIndex.Open(tight.IndexPath)!;
            var store = CachedIndex.Open(tight.CorePath, file, core)!;
            var groups = Enumerable.Range(0, Layers).SelectMany(il => Enumerable.Range(0, core.Groups).Select(g => (Layer: il, Group: g)))
                .Where(p => core.Size(p.Layer, p.Group) > 0).ToList();
            var kept = groups.Where(p => store.Entry(p.Layer, p.Group) is not null).Select(p => core.Size(p.Layer, p.Group)).ToList();
            var left = groups.Where(p => store.Entry(p.Layer, p.Group) is null).Select(p => core.Size(p.Layer, p.Group)).ToList();
            Assert.True(kept.Count > 0 && left.Count > 0 && !store.HoldsAll(core), $"with flash for half, the core holds {kept.Count} of {groups.Count} pieces");
            Assert.True(store.Count * store.PieceStride <= all / 2, $"the core's pieces take {store.Count * store.PieceStride} bytes of the {all / 2} it was given");
            Assert.True(kept.Min() >= left.Max(), $"the core keeps a group {kept.Min()} of Watch's words fell in and leaves out one with {left.Max()}");

            // Given no room for step 2's tiled layers, none is written: the layers are read from the model's own file in place.
            Assert.False(File.Exists(tight.MixPath) || File.Exists(tight.DownPath), "step 2's layers were written with no room given them");
            Assert.True(tight.Split() is { Mixing: null, Down: null }, "step 2 says it holds layers it was given no room for");
        });
    }

    // "Nothing about a device is written into the code. The device measures itself" - and a phone's free flash changes.
    // Until the first layer's pieces are written, the queue takes what the phone offers now. After that the flash it was
    // given stands - unless it was room for only part of the pieces and the phone now offers a good deal more (ASSUMED: a
    // quarter more): then the pieces are written again, all that now fit, and Watch and the core index are not done again.
    [Fact]
    public void A_phone_with_more_room_than_before_gets_more_of_the_pieces()
    {
        WithModel(429, (file, directory) =>
        {
            var host = new Host(file);
            var all = IndexingBudget.CompleteCore(Layers, 4, 96, SyntheticQwen35.Embedding, FlashFile.Page);

            // Before any piece is written: what is offered now.
            using (var queue = IndexingQueue.Open(file, directory, Plan(groups: 4, list: 96, core: all / 2, shared: 0)))
            {
                for (var unit = 0; unit < 2; unit++) Assert.True(queue.RunNext(host));
            }
            using (var queue = IndexingQueue.Open(file, directory, Plan(groups: 4, list: 96, core: all / 4, shared: 0)))
            {
                Assert.Equal(2, queue.Status.Done);
                Assert.Equal(all / 4, queue.Plan.CoreBudget);
                while (queue.RunNext(host)) { }
                Assert.True(queue.Complete);
                var core = ThoughtIndex.Open(queue.IndexPath)!;
                Assert.False(CachedIndex.Open(queue.CorePath, file, core)!.HoldsAll(core), "setup: a quarter of the room held every piece");
            }
            var (watched, checks) = (host.Watched, host.Checked);

            // A little more room: the work stands as it is.
            using (var queue = IndexingQueue.Open(file, directory, Plan(groups: 4, list: 96, core: all / 4 + all / 32, shared: 0)))
                Assert.True(queue.Complete && queue.Plan.CoreBudget == all / 4, "an eighth more room, and the finished work did not stand");

            // Room for all of them: the pieces are written again - Watch and the core index are not.
            using (var queue = IndexingQueue.Open(file, directory, Plan(groups: 4, list: 96, core: all, shared: 0)))
            {
                Assert.True(queue.Status.Done == Passages.Length + 1 && queue.Plan.CoreBudget == all,
                    $"offered room for every piece, the queue says {queue.Status.Done} units are done with {queue.Plan.CoreBudget} bytes of {all}");
                while (queue.RunNext(host)) { }
                Assert.True(queue.Complete);
                var core = ThoughtIndex.Open(queue.IndexPath)!;
                Assert.True(CachedIndex.Open(queue.CorePath, file, core)!.HoldsAll(core), "with room for every piece, the core's pieces are not complete");
            }
            Assert.True(host.Watched == watched && host.Checked == checks + 1, "Watch read again for pieces that only needed more room");
        });
    }

    // The flash a queue was given stands while its pieces are being written: started again with less room free, it carries
    // on. Asked for another index - other groups - it starts again.
    [Fact]
    public void The_same_work_keeps_the_flash_it_was_given_and_other_work_starts_again()
    {
        WithModel(413, (file, directory) =>
        {
            var host = new Host(file);
            long given;
            using (var queue = IndexingQueue.Open(file, directory, Plan(groups: 4, list: 96, core: 1L << 30, shared: 1L << 20)))
            {
                for (var unit = 0; unit < 5; unit++) Assert.True(queue.RunNext(host));
                given = queue.Plan.CoreBudget;
            }
            using (var queue = IndexingQueue.Open(file, directory, Plan(groups: 4, list: 96, core: 1L << 29, shared: 1L << 19)))
            {
                Assert.Equal(5, queue.Status.Done);
                Assert.True(queue.Plan.CoreBudget == given && queue.Plan.SharedBudget == 1L << 20,
                    $"started again, the queue's flash is {queue.Plan.CoreBudget} and {queue.Plan.SharedBudget}, not what it was given");
            }
            using (var queue = IndexingQueue.Open(file, directory, Plan(groups: 2, list: 96)))
                Assert.Equal(new IndexingStatus(0, Units), queue.Status);
        });
    }

    // A phone short of room clears an app's cache folder, which is where the index lives. What the record says is done is
    // then not on flash: the queue starts again, with the flash it is offered now, rather than building on files that are
    // gone - whether they went while the engine was away or from under it between two units.
    [Fact]
    public void With_its_files_gone_from_flash_the_queue_starts_again()
    {
        WithModel(425, (file, directory) =>
        {
            var host = new Host(file);
            string watch, index;
            using (var queue = IndexingQueue.Open(file, directory, Plan(groups: 4, list: 96, core: 1L << 30)))
            {
                for (var unit = 0; unit < 6; unit++) Assert.True(queue.RunNext(host));
                (watch, index) = (queue.WatchPath, queue.IndexPath);

                // From under it, between two units: the next unit it does is the first again.
                File.Delete(index);
                Assert.True(queue.RunNext(host));
                Assert.True(queue.Status.Done == 1, $"with the core index gone from under it, the queue went on to say {queue.Status.Done} units are done");
                for (var unit = 1; unit < 6; unit++) Assert.True(queue.RunNext(host));
            }
            File.Delete(index);
            using (var queue = IndexingQueue.Open(file, directory, Plan(groups: 4, list: 96, core: 1L << 29)))
            {
                Assert.Equal(new IndexingStatus(0, Units), queue.Status);
                Assert.Equal(1L << 29, queue.Plan.CoreBudget);
                Assert.True(queue.RunNext(host) && queue.RunNext(host), "the queue did not carry on from the start");
            }
            File.Delete(watch);
            using (var queue = IndexingQueue.Open(file, directory, Plan(groups: 4, list: 96)))
            {
                Assert.Equal(0, queue.Status.Done);
                while (queue.RunNext(host)) { }
                Assert.True(queue.Complete);
            }
        });
    }

    // Two indexes of one model, the same shape and other neurons - Watch read other words. The pieces written for one are
    // not taken for the other's: served under the wrong lists they would compute the wrong neurons and say nothing of it.
    [Fact]
    public void Pieces_written_for_another_indexs_lists_are_not_taken_for_this_ones()
    {
        WithModel(427, (file, directory) =>
        {
            var host = new Host(file);
            var one = Directory.CreateDirectory(Path.Combine(directory, "one")).FullName;
            var other = Directory.CreateDirectory(Path.Combine(directory, "other")).FullName;
            string corePieces;
            ThoughtIndex first, second;
            using (var queue = IndexingQueue.Open(file, one, Plan(groups: 4, list: 96)))
            {
                while (queue.RunNext(host)) { }
                corePieces = queue.CorePath;
                first = ThoughtIndex.Open(queue.IndexPath)!;
            }
            var otherWords = Plan(groups: 4, list: 96) with { Passages = [[21, 23, 28, 18, 24, 15, 2, 9], [30, 31, 3, 5, 7, 11, 13, 17]] };
            using (var queue = IndexingQueue.Open(file, other, otherWords))
            {
                while (queue.RunNext(host)) { }
                second = ThoughtIndex.Open(queue.IndexPath)!;
            }

            Assert.True(first.Fingerprint != second.Fingerprint, "setup: the two indexes list the same neurons");
            Assert.NotNull(CachedIndex.Open(corePieces, file, first));
            Assert.Null(CachedIndex.Open(corePieces, file, second));
        });
    }

    // "REPORT ONLY PASS OR FAIL AGAINST A MEASUREMENT." The check is the queue's measurement, and on the P30 on 2026-11-13 it
    // read "the same top word as every neuron 25.0% of the time" of an index that was then complete - and complete was all
    // anything asked. The measure is kept with the record; an index under nine words in ten (ASSUMED: nine in ten) does not
    // serve and nothing is said of it; and a record that says complete with no measure has its check run again.
    [Fact]
    public void The_checks_measure_is_kept_and_an_index_that_falls_short_does_not_serve()
    {
        WithModel(431, (file, directory) =>
        {
            var now = new DateTimeOffset(2026, 11, 13, 9, 33, 0, TimeSpan.Zero);
            var host = new Host(file) { Agreement = 250 };
            string record;
            using (var queue = IndexingQueue.Open(file, directory, Plan(groups: 4, list: 96)))
            {
                while (queue.RunNext(host)) { }
                record = queue.StatePath;
                Assert.True(queue.Complete);
                Assert.Equal(250, queue.Status.Agreement);
                Assert.False(queue.Status.Serves, "an index that agrees with every neuron one word in four serves");
                Assert.Equal(queue.Status, IndexingQueue.ReadStatus(directory));
                Assert.Equal(queue.Status, IndexingQueue.ReadStatus(directory, Path.GetFileName(file.FilePath)));
            }
            Assert.Null(IndexReadiness.Read(directory, now));   // nothing is said of an index that serves nobody

            // Started again: the work stands, its measure with it, and the check is not run a second time.
            using (var queue = IndexingQueue.Open(file, directory, Plan(groups: 4, list: 96)))
            {
                Assert.True(queue.Complete && queue.Status.Agreement == 250 && !queue.Status.Serves);
                Assert.False(queue.RunNext(host));
                Assert.Equal(1, host.Checked);
            }

            // A record that says complete and keeps no measure - one written before the measure was kept, its pieces on
            // flash as that build left them: the check runs again, and this time the index passes. In a folder of its
            // own, because the index above failed its check and what a failed index leaves is removed (2026-11-14).
            var earlier = Directory.CreateDirectory(Path.Combine(directory, "earlier")).FullName;
            var passing = new Host(file) { Agreement = 969 };
            using (var queue = IndexingQueue.Open(file, earlier, Plan(groups: 4, list: 96)))
            {
                while (queue.RunNext(passing)) { }
                record = queue.StatePath;
            }
            File.WriteAllLines(record, File.ReadAllLines(record).Where(line => !line.StartsWith("agrees=", StringComparison.Ordinal)));
            using (var queue = IndexingQueue.Open(file, earlier, Plan(groups: 4, list: 96)))
            {
                Assert.False(queue.Complete, "a complete index nobody measured was taken as it stood");
                Assert.Equal(queue.Status.Total - 1, queue.Status.Done);
                Assert.True(queue.RunNext(passing));
                Assert.Equal(2, passing.Checked);
                Assert.True(queue.Status.Serves && queue.Status.Agreement == 969);
            }
            Assert.Equal(ReadinessSentences.Learning, IndexReadiness.Read(earlier, now)?.Detail);

            Assert.True(new IndexingStatus(71, 71, 900).Serves);
            Assert.False(new IndexingStatus(71, 71, 899).Serves);
            Assert.False(new IndexingStatus(71, 71).Serves);        // never measured
            Assert.False(new IndexingStatus(70, 71, 1000).Serves);  // not complete
        });
    }

    // A MEASURE IS OF THE PIECES IT WAS TAKEN ON. Written again - the phone now offers room for all of them - or gone from
    // flash, the index is not complete and its measure is not kept: nothing serves on a check of other pieces, the person
    // is told the share again, and the check runs again when the pieces are back.
    [Fact]
    public void A_checks_measure_does_not_outlive_the_pieces_it_was_taken_on()
    {
        WithModel(433, (file, directory) =>
        {
            var now = new DateTimeOffset(2026, 11, 13, 21, 0, 0, TimeSpan.Zero);
            var host = new Host(file) { Agreement = 969 };
            var all = IndexingBudget.CompleteCore(Layers, 4, 96, SyntheticQwen35.Embedding, FlashFile.Page);
            string index;
            using (var queue = IndexingQueue.Open(file, directory, Plan(groups: 4, list: 96, core: all / 4, shared: 0)))
            {
                while (queue.RunNext(host)) { }
                Assert.True(queue.Status.Serves, "setup: the index with a quarter of its pieces does not serve");
                index = queue.IndexPath;
            }
            Assert.Equal(ReadinessSentences.Learning, IndexReadiness.Read(directory, now)?.Detail);

            // Room for every piece now: the pieces are written again, and what was measured on the old ones is not kept.
            host.Agreement = 250;
            using (var queue = IndexingQueue.Open(file, directory, Plan(groups: 4, list: 96, core: all, shared: 0)))
            {
                Assert.True(!queue.Complete && queue.Status.Agreement is null && !queue.Status.Serves,
                    $"its pieces to be written again, the index says {queue.Status}");
                Assert.Equal(queue.Status, IndexingQueue.ReadStatus(directory));   // and any process reads the same
                var told = IndexReadiness.Read(directory, now);
                Assert.True(told is { Phase: BrainPhase.Loading } && told.Detail == "Until indexing is complete AI is functioning at 40%.",
                    $"while the pieces are written again the service says \"{told?.Detail}\"");

                while (queue.RunNext(host)) { }
                Assert.True(queue.Complete && queue.Status.Agreement == 250 && !queue.Status.Serves,
                    $"checked again on the new pieces, the index says {queue.Status}");
                Assert.Equal(2, host.Checked);
            }
            Assert.Null(IndexReadiness.Read(directory, now));

            // THAT CHECK FAILED, SO THE QUEUE REMOVED WHAT THE INDEX LEFT - by its own hand, which is not "files gone from
            // flash" (2026-11-14): the record stands with its measure, and the index is not built again into the same
            // failure. Files that go from under an index that has not failed are the test above and the one below.
            Assert.False(File.Exists(index), "the core index of an index that failed its check is still on flash");
            using (var queue = IndexingQueue.Open(file, directory, Plan(groups: 4, list: 96, core: all, shared: 0)))
            {
                Assert.True(queue.Complete && queue.Status.Agreement == 250 && !queue.Status.Serves, $"opened again, the failed index says {queue.Status}");
                Assert.False(queue.RunNext(host));
            }
            Assert.Equal(new IndexingStatus(Units, Units, 250), IndexingQueue.ReadStatus(directory));
        });
    }

    // An index that passed, and its files then gone from flash: from the start, and no measure of what is no longer there.
    [Fact]
    public void A_measure_does_not_outlive_files_that_go_from_under_an_index_that_passed()
    {
        WithModel(434, (file, directory) =>
        {
            var host = new Host(file) { Agreement = 969 };
            string index;
            using (var queue = IndexingQueue.Open(file, directory, Plan(groups: 4, list: 96)))
            {
                while (queue.RunNext(host)) { }
                Assert.True(queue.Status.Serves);
                index = queue.IndexPath;
            }

            File.Delete(index);
            using (var queue = IndexingQueue.Open(file, directory, Plan(groups: 4, list: 96)))
                Assert.Equal(new IndexingStatus(0, Units), queue.Status);
            Assert.Equal(new IndexingStatus(0, Units), IndexingQueue.ReadStatus(directory));
        });
    }

    // ---- "we need to clean up after ourselves or we will kill users phones" ----

    // THE OWNER, 2026-11-14, of the 3.6 GB the 27B's failed index left on a P30 with 4.3 GB free: "they can go we need to
    // clean up after ourselves or we will kill users phones". An index whose check fails serves nobody, so once the check
    // is on record everything the index left goes - the core index, step 2's pieces and its tiled layers. The record
    // stays: it is what says the check failed, and what keeps the index from being built again into the same failure.
    [Fact]
    public void An_index_that_fails_its_check_leaves_its_record_and_nothing_else()
    {
        WithModel(437, (file, directory) =>
        {
            var host = new Host(file) { Agreement = 250 };
            var name = Path.GetFileName(file.FilePath);
            using (var queue = IndexingQueue.Open(file, directory, Plan(groups: 4, list: 96)))
            {
                for (var unit = 0; unit < Units - 1; unit++) Assert.True(queue.RunNext(host));
                queue.TakeTidied();   // Watch's records went when the core index was built: the next test's
                Assert.True(File.Exists(queue.IndexPath) && File.Exists(queue.CorePath) && File.Exists(queue.MixPath),
                    "setup: the index is not on flash before its check");
                var held = new FileInfo(queue.IndexPath).Length + new FileInfo(queue.CorePath).Length + new FileInfo(queue.MixPath).Length;

                Assert.True(queue.RunNext(host));   // the check: one word in four
                var (bytes, what) = queue.TakeTidied();
                Assert.Equal(held, bytes);
                Assert.Equal("every file of the index, which failed its check", what);
                Assert.Equal((0L, string.Empty), queue.TakeTidied());   // said once
            }

            Assert.Equal(new[] { name + ".indexing" }, Directory.EnumerateFiles(directory).Select(path => Path.GetFileName(path)!).ToArray());
            Assert.Equal(new IndexingStatus(Units, Units, 250), IndexingQueue.ReadStatus(directory, name));

            // Started again, however often: complete, failed, and not built again.
            for (var start = 0; start < 2; start++)
            {
                using var queue = IndexingQueue.Open(file, directory, Plan(groups: 4, list: 96));
                Assert.True(queue.Complete && queue.Status.Agreement == 250 && !queue.Status.Serves, $"opened again, the failed index says {queue.Status}");
                Assert.False(queue.RunNext(host));
                Assert.Equal((0L, string.Empty), queue.TakeTidied());
            }
            Assert.Equal(1, host.Checked);
            Assert.Equal(Passages.Length, host.Watched);
        });
    }

    // Watch keeps every word it reads a layer at a time - the thought's sketch and the neurons that fired - and the core
    // index is built from those records once. Nothing opens them again, so they go when the core index is on record: on
    // the 27B, 144 KB for every word Watch read. The index is built, checked, serves and is found again without them.
    [Fact]
    public void Watchs_records_go_once_the_core_index_is_built_from_them()
    {
        WithModel(439, (file, directory) =>
        {
            var host = new Host(file) { Agreement = 969 };
            using (var queue = IndexingQueue.Open(file, directory, Plan(groups: 4, list: 96)))
            {
                for (var passage = 0; passage < Passages.Length; passage++) Assert.True(queue.RunNext(host));
                Assert.True(File.Exists(queue.WatchPath), "setup: Watch's records are not on flash for the core index to be built from");
                var records = new FileInfo(queue.WatchPath).Length;
                Assert.Equal((0L, string.Empty), queue.TakeTidied());   // nothing goes while they are still to be read

                Assert.True(queue.RunNext(host));   // the core index is built from what Watch saw
                Assert.False(File.Exists(queue.WatchPath), "Watch's records are still on flash after the core index was built from them");
                Assert.True(File.Exists(queue.IndexPath));
                Assert.Equal((records, "Watch's records, which the core index is built from"), queue.TakeTidied());

                while (queue.RunNext(host)) { }
                Assert.True(queue.Status.Serves);
                Assert.Equal((0L, string.Empty), queue.TakeTidied());   // an index that passed keeps what serves
                Assert.True(File.Exists(queue.CorePath) && File.Exists(queue.MixPath));
            }

            // Found again by the next load, complete and serving, with no records to look for.
            using (var queue = IndexingQueue.Open(file, directory, Plan(groups: 4, list: 96)))
            {
                Assert.True(queue.Complete && queue.Status.Serves, $"opened again without Watch's records, the index says {queue.Status}");
                Assert.False(queue.RunNext(host));
            }
            Assert.Equal(Passages.Length, host.Watched);
            Assert.Equal(1, host.Checked);
        });
    }

    // THAT P30: the index failed under an earlier build, which removed nothing, and the brain is not loaded again once
    // its index has failed - so its engine never opens the queue. What it left is removed from the record alone, by any
    // process and with no model open: the service does it at every start. An index that passed is not touched.
    [Fact]
    public void What_an_earlier_build_left_of_a_failed_index_is_removed_from_its_record_alone()
    {
        WithModel(441, (file, directory) =>
        {
            var name = Path.GetFileName(file.FilePath);
            var failed = Directory.CreateDirectory(Path.Combine(directory, "failed")).FullName;
            var passed = Directory.CreateDirectory(Path.Combine(directory, "passed")).FullName;
            var host = new Host(file) { Agreement = 969 };
            long left = 0;
            foreach (var folder in new[] { failed, passed })
            {
                using var queue = IndexingQueue.Open(file, folder, Plan(groups: 4, list: 96));
                while (queue.RunNext(host)) { }
                if (folder == failed) left = new FileInfo(queue.IndexPath).Length + new FileInfo(queue.CorePath).Length + new FileInfo(queue.MixPath).Length;
            }

            // As the earlier build left it: every file on flash - Watch's records among them - a write that was stopped,
            // and a record that says one word in four.
            var record = Path.Combine(failed, name + ".indexing");
            File.WriteAllLines(record, File.ReadAllLines(record).Select(line => line.StartsWith("agrees=", StringComparison.Ordinal) ? "agrees=250" : line));
            File.WriteAllBytes(Path.Combine(failed, name + ".watch"), new byte[8192]);
            File.WriteAllBytes(Path.Combine(failed, name + ".core.partial"), new byte[4096]);

            var removed = IndexingQueue.RemoveFailed(failed);
            Assert.Equal(new[] { new FailedIndexRemoved(name, 250, left + 8192 + 4096) }, removed.ToArray());
            Assert.Equal(new[] { name + ".indexing" }, Directory.EnumerateFiles(failed).Select(path => Path.GetFileName(path)!).ToArray());
            Assert.Equal(new IndexingStatus(Units, Units, 250), IndexingQueue.ReadStatus(failed, name));
            Assert.Empty(IndexingQueue.RemoveFailed(failed));   // nothing is left to remove the next time

            // The index that passed keeps every file that serves.
            Assert.Empty(IndexingQueue.RemoveFailed(passed));
            Assert.Equal(4, Directory.EnumerateFiles(passed).Count());   // the record, the core index, step 2's pieces, its tiled attention
            using (var queue = IndexingQueue.Open(file, passed, Plan(groups: 4, list: 96)))
                Assert.True(queue.Status.Serves);

            // And nothing is said of a folder that is not there.
            Assert.Empty(IndexingQueue.RemoveFailed(Path.Combine(directory, "nowhere")));
        });
    }

    // The engine of a brain that is loaded finds the same thing when it opens its queue: what an earlier build left of
    // a failed index goes, and the index stays failed.
    [Fact]
    public void A_queue_opened_on_a_failed_index_an_earlier_build_left_removes_what_it_left()
    {
        WithModel(443, (file, directory) =>
        {
            var name = Path.GetFileName(file.FilePath);
            var host = new Host(file) { Agreement = 969 };
            using (var queue = IndexingQueue.Open(file, directory, Plan(groups: 4, list: 96)))
                while (queue.RunNext(host)) { }
            var record = Path.Combine(directory, name + ".indexing");
            File.WriteAllLines(record, File.ReadAllLines(record).Select(line => line.StartsWith("agrees=", StringComparison.Ordinal) ? "agrees=250" : line));

            using (var queue = IndexingQueue.Open(file, directory, Plan(groups: 4, list: 96)))
            {
                var (bytes, what) = queue.TakeTidied();
                Assert.True(bytes > 0, "nothing was removed of a failed index an earlier build left on flash");
                Assert.Equal("what an index that failed its check left", what);
                Assert.True(queue.Complete && queue.Status.Agreement == 250 && !queue.Status.Serves);
                Assert.False(queue.RunNext(host));
            }
            Assert.Equal(new[] { name + ".indexing" }, Directory.EnumerateFiles(directory).Select(path => Path.GetFileName(path)!).ToArray());
            Assert.Equal(1, host.Checked);
        });
    }

    // A failed index is built again as the same work for one reason: it had room for only part of its pieces, and the
    // phone now offers a good deal more. Then from the start - what Watch saw went with the rest.
    [Fact]
    public void A_failed_index_is_built_again_only_when_the_phone_offers_a_good_deal_more_room()
    {
        WithModel(445, (file, directory) =>
        {
            var host = new Host(file) { Agreement = 250 };
            var all = IndexingBudget.CompleteCore(Layers, 4, 96, SyntheticQwen35.Embedding, FlashFile.Page);
            using (var queue = IndexingQueue.Open(file, directory, Plan(groups: 4, list: 96, core: all / 4, shared: 0)))
            {
                while (queue.RunNext(host)) { }
                Assert.True(queue.Complete && !queue.Status.Serves);
            }

            // The same room, or a little more: it stays failed.
            foreach (var core in new[] { all / 4, all / 4 + all / 32 })
            {
                using var queue = IndexingQueue.Open(file, directory, Plan(groups: 4, list: 96, core: core, shared: 0));
                Assert.True(queue.Complete && queue.Status.Agreement == 250, $"offered {core} bytes for its pieces, the failed index says {queue.Status}");
            }

            // Room for every piece: from the start, with no measure, and it runs to its check again.
            host.Agreement = 969;
            using (var queue = IndexingQueue.Open(file, directory, Plan(groups: 4, list: 96, core: all, shared: 0)))
            {
                Assert.Equal(new IndexingStatus(0, Units), queue.Status);
                Assert.Equal(all, queue.Plan.CoreBudget);
                while (queue.RunNext(host)) { }
                Assert.True(queue.Status.Serves && queue.Status.Agreement == 969);
            }
            Assert.Equal(2, host.Checked);
            Assert.Equal(2 * Passages.Length, host.Watched);
        });
    }

    // "user stays updated" - and is not left reading the share of an index that then failed its check. The line said
    // while the index was built is taken back when the check says it serves nobody.
    [Fact]
    public void The_line_said_while_an_index_was_built_is_taken_back_when_its_check_fails()
    {
        WithModel(435, (file, directory) =>
        {
            var now = new DateTimeOffset(2026, 11, 13, 21, 0, 0, TimeSpan.Zero);
            var host = new Host(file) { Agreement = 250 };
            var name = Path.GetFileName(file.FilePath);
            var tracker = new ReadinessTracker();
            tracker.Report(ComponentState.Of(ReadinessComponents.Brain, BrainPhase.Ready, ReadinessSentences.Ready, now, "the-model"));

            using var queue = IndexingQueue.Open(file, directory, Plan(groups: 4, list: 96));
            for (var unit = 0; unit < Units - 1; unit++) Assert.True(queue.RunNext(host));
            IndexReadiness.Report(tracker, directory, now, name);
            Assert.Equal("Until indexing is complete AI is functioning at 90%.", IndexReadiness.Caption(tracker.Current));

            Assert.True(queue.RunNext(host));   // the check: one word in four
            Assert.True(queue.Complete && !queue.Status.Serves);
            IndexReadiness.Report(tracker, directory, now, name);
            Assert.Equal(string.Empty, IndexReadiness.Caption(tracker.Current));
            Assert.DoesNotContain(tracker.Current.Components, c => c.Component == ReadinessComponents.Index && c.Detail.Length > 0);
        });
    }

    // ---- "whenever the phone is idle - best at bedtime but runs whenever it can" ----

    // THE OWNER: "whenever the phone is idle - best at bedtime but runs whenever it can". Idle is nobody asking and the
    // screen off (the phone's own word for it: IdleModelReview, "IDLE IS THE SCREEN BEING OFF"), with the phone neither hot
    // nor low. Idle on charge - bedtime - it works without pause. OFF ITS CHARGER IT BUILDS NOTHING (the owner,
    // 2026-11-15: "if we only perform while tethered then we are not performing" - the phone off its cable answers; the
    // index is built on the charger). A device that does not say whether it charges does a unit and rests as long as the
    // unit took (ASSUMED: half its time, so a device in a pocket does not warm).
    [Fact]
    public void The_indexing_waits_while_the_phone_is_in_use_and_works_when_it_is_idle_best_on_charge()
    {
        var quiet = TimeSpan.FromMinutes(5);
        var onCharge = new PowerState(80, true, false);
        var offCharge = new PowerState(80, false, false);

        Assert.Equal(IndexingPace.Wait, IndexingWhen.Pace(wanted: true, quiet, onCharge, inUse: false));          // a question is waiting
        Assert.Equal(IndexingPace.Wait, IndexingWhen.Pace(wanted: false, quiet, onCharge, inUse: true));          // the screen is on
        Assert.Equal(IndexingPace.Wait, IndexingWhen.Pace(wanted: false, quiet, new PowerState(80, true, true), inUse: false));   // hot
        Assert.Equal(IndexingPace.Wait, IndexingWhen.Pace(wanted: false, quiet, new PowerState(12, false, false), inUse: false)); // low
        Assert.Equal(IndexingPace.Wait, IndexingWhen.Pace(wanted: false, TimeSpan.FromSeconds(5), onCharge, inUse: false));       // just used

        Assert.Equal(IndexingPace.BackToBack, IndexingWhen.Pace(wanted: false, quiet, onCharge, inUse: false));   // bedtime
        Assert.Equal(IndexingPace.BackToBack, IndexingWhen.Pace(wanted: false, quiet, new PowerState(12, true, false), inUse: false));
        Assert.Equal(IndexingPace.Wait, IndexingWhen.Pace(wanted: false, quiet, offCharge, inUse: false));        // off its charger: it answers, builds nothing
        Assert.True(IndexingWhen.OffItsCharger(offCharge) && !IndexingWhen.OffItsCharger(onCharge) && !IndexingWhen.OffItsCharger(PowerState.Unknown));
        Assert.Equal(IndexingPace.OneUnit, IndexingWhen.Pace(wanted: false, quiet, PowerState.Unknown, inUse: null)); // a device that says nothing

        Assert.Equal(TimeSpan.Zero, IndexingWhen.Rest(TimeSpan.FromSeconds(40), onCharge));
        Assert.Equal(TimeSpan.FromSeconds(40), IndexingWhen.Rest(TimeSpan.FromSeconds(40), offCharge));
        Assert.Equal(TimeSpan.FromSeconds(40), IndexingWhen.Rest(TimeSpan.FromSeconds(40), PowerState.Unknown));
    }

    // THE OWNER, 2026-11-13, of a P30 lying untouched on its cable with "Stay awake" on, whose indexing had not begun:
    // "p30 is idle right? so it should be running". A lit screen says somebody is using the phone when it would go dark
    // by itself were the phone left alone. Lit because the phone is set to keep it lit while it charges, it says nothing -
    // it stays lit for a week untouched - and the indexing goes by what else it knows: nobody asking, and not for a little
    // while, the phone neither hot nor low.
    [Fact]
    public void A_screen_the_phone_itself_keeps_lit_is_no_sign_that_anybody_is_using_it()
    {
        Assert.Equal(false, IndexingWhen.InUse(screenLit: false, keptLitByThePhone: false));
        Assert.Equal(false, IndexingWhen.InUse(screenLit: false, keptLitByThePhone: true));
        Assert.Equal(true, IndexingWhen.InUse(screenLit: true, keptLitByThePhone: false));
        Assert.Null(IndexingWhen.InUse(screenLit: true, keptLitByThePhone: true));

        var quiet = TimeSpan.FromMinutes(5);
        var onTheCable = new PowerState(82, true, false);
        var keptLit = IndexingWhen.InUse(screenLit: true, keptLitByThePhone: true);

        // That P30: it is idle, so it runs - and on charge, without pause.
        Assert.Equal(IndexingPace.BackToBack, IndexingWhen.Pace(wanted: false, quiet, onTheCable, keptLit));

        // A lit screen that would go dark by itself still means somebody is there.
        Assert.Equal(IndexingPace.Wait, IndexingWhen.Pace(wanted: false, quiet, onTheCable, IndexingWhen.InUse(screenLit: true, keptLitByThePhone: false)));

        // And everything else that makes the indexing wait still does.
        Assert.Equal(IndexingPace.Wait, IndexingWhen.Pace(wanted: true, quiet, onTheCable, keptLit));
        Assert.Equal(IndexingPace.Wait, IndexingWhen.Pace(wanted: false, TimeSpan.FromSeconds(5), onTheCable, keptLit));
        Assert.Equal(IndexingPace.Wait, IndexingWhen.Pace(wanted: false, quiet, new PowerState(82, true, true), keptLit));
    }

    // "WITHOUT HARMING THE PHONE" - by the phone's own measure. That P30 says where its system begins ending other apps'
    // processes for memory: 315 MB available ("oom 322,560K"). The indexing stands aside a quarter above that line
    // (ASSUMED: a quarter), and under 256 MB on a device that gives no line. Not by its swap: the phone lives with its
    // swap nearly full - eleven days up, "status normal", "Total number of kills: 0" - and a rule that kept an eighth of
    // the swap free stopped the queue's last unit twice on dips that harmed nothing.
    [Fact]
    public void The_indexing_stands_aside_while_the_phone_is_short_of_memory()
    {
        const long mb = 1L << 20;
        var line = 322_560L * 1024;   // that phone's own

        // That phone through today's units: 550 to 1000 MB available, and 485 MB at the least.
        Assert.False(IndexingWhen.ShortOfMemory(978 * mb, line));
        Assert.False(IndexingWhen.ShortOfMemory(571 * mb, line));   // the check, with 245 MB of swap free: nothing was wrong
        Assert.False(IndexingWhen.ShortOfMemory(485 * mb, line));   // and with 7 MB of swap free: not one process was ended

        // Within a quarter of the line, and under it.
        Assert.True(IndexingWhen.ShortOfMemory(380 * mb, line));
        Assert.True(IndexingWhen.ShortOfMemory(300 * mb, line));
        Assert.False(IndexingWhen.ShortOfMemory(line + line / 4, line));

        // A device that gives no line is held to the least there is; one that says nothing is not stopped.
        Assert.True(IndexingWhen.ShortOfMemory(200 * mb, null));
        Assert.False(IndexingWhen.ShortOfMemory(300 * mb, null));
        Assert.False(IndexingWhen.ShortOfMemory(null, line));
        Assert.False(IndexingWhen.ShortOfMemory(null, null));

        // As the phone itself says it.
        var phone = CircleAI.Inference.Managed.Runtime.MemoryNow.Parse(
        [
            "MemTotal:        3776516 kB",
            "MemFree:           81608 kB",
            "MemAvailable:     485692 kB",
            "SwapCached:        12345 kB",
            "SwapTotal:       2584920 kB",
            "SwapFree:           7180 kB",
        ]);
        Assert.Equal(485692L * 1024, phone.Available);
        Assert.Equal(7180L * 1024, phone.SwapFree);
        Assert.Equal(2584920L * 1024, phone.SwapTotal);
        Assert.False(IndexingWhen.ShortOfMemory(phone.Available, line));
    }

    // "RAM IS THE TURBO NOT THE ENGINE" - and the turbo takes RAM that is really spare. On that P30 the check came to its
    // pass through the index with 751 MB "available" and 669 MB of 2,524 MB of swap free; the warm index took about 300 MB
    // and sixteen seconds later the phone had 294 MB of swap free. What it may take is what is available over the reserve
    // and the stream, and on a phone that swaps no more than half the swap free above the eighth kept free (ASSUMED: half).
    [Fact]
    public void The_warm_index_takes_only_RAM_the_phone_has_to_spare()
    {
        const long mb = 1L << 20;
        var reserve = 384 * mb;
        var stream = 70 * mb;

        // That phone at that moment: 297 MB by what is available, 177 MB by its swap - the swap decides.
        Assert.Equal((669 * mb - 2524 * mb / 8) / 2, IndexingWhen.WarmRoom(751 * mb, 669 * mb, 2524 * mb, reserve, stream));
        Assert.InRange(IndexingWhen.WarmRoom(751 * mb, 669 * mb, 2524 * mb, reserve, stream), 170 * mb, 180 * mb);

        // Short of swap already: none of it, and every piece comes from flash.
        Assert.Equal(0, IndexingWhen.WarmRoom(751 * mb, 294 * mb, 2524 * mb, reserve, stream));

        // Plenty of swap free: what is available decides.
        Assert.Equal(297 * mb, IndexingWhen.WarmRoom(751 * mb, 2400 * mb, 2524 * mb, reserve, stream));

        // A device with no swap, and one that says nothing.
        Assert.Equal(297 * mb, IndexingWhen.WarmRoom(751 * mb, 0, 0, reserve, stream));
        Assert.Equal(0, IndexingWhen.WarmRoom(300 * mb, null, null, reserve, stream));
        Assert.Equal(IndexingWhen.UnknownWarmRoom, IndexingWhen.WarmRoom(null, null, null, reserve, stream));
    }

    // "Nothing about a device is written into the code. The device measures itself": the flash the index may take is what
    // the phone has free less what is kept for the phone. With room for every piece of step 2 and its tiled layers, it is
    // all of both; short of that the pieces take the room, the groups the most words fell in first, and the layers are
    // read from the model's own file in place.
    [Fact]
    public void The_flash_the_index_takes_is_what_the_phone_has_free_less_what_is_kept_for_the_phone()
    {
        const long gb = 1L << 30;
        var roomy = IndexingBudget.For(free: 64 * gb, completeCore: 8 * gb, shared: 3 * gb);
        Assert.Equal((8 * gb, 3 * gb), roomy);

        var both = IndexingBudget.For(free: 12 * gb, completeCore: 8 * gb, shared: 3 * gb);
        Assert.Equal((8 * gb, 3 * gb), both);   // 11 GB of the 11 left over the reserve

        var tight = IndexingBudget.For(free: 10 * gb, completeCore: 8 * gb, shared: 3 * gb);
        Assert.Equal((8 * gb, 0L), tight);       // every piece, and no room left for the layers

        var little = IndexingBudget.For(free: 5 * gb, completeCore: 8 * gb, shared: 3 * gb);
        Assert.Equal((5 * gb - IndexingBudget.Reserve, 0L), little);

        var none = IndexingBudget.For(free: IndexingBudget.Reserve / 2, completeCore: 8 * gb, shared: 3 * gb);
        Assert.Equal((0L, 0L), none);
    }

    // Step 2's tiled layers take what a written store of every layer's attention takes - and every layer's ffn_down beside
    // it where the lists are long enough that the layers take their own.
    [Fact]
    public void Step_2s_layers_take_the_attention_and_the_ffn_down_where_the_layers_take_their_own()
    {
        WithModel(447, (file, directory) =>
        {
            var mix = TileStore.Write(file, Path.Combine(directory, "model.mix"), TileSet.Mixing, channels: 2, readBytes: FlashFile.Page);
            var down = TileStore.Write(file, Path.Combine(directory, "model.down"), TileSet.FeedForward, channels: 2, readBytes: FlashFile.Page, kinds: SplitByThoughts.DownKinds);
            Assert.True(CachedIndex.LayersTakeTheirOwnDown(Ff, Ff) && !CachedIndex.LayersTakeTheirOwnDown(96, Ff), "setup: the threshold is not between a quarter and every neuron");
            Assert.Equal(new FileInfo(mix.FilePath).Length, IndexingBudget.SharedLayers(file, 96));
            Assert.Equal(new FileInfo(mix.FilePath).Length + new FileInfo(down.FilePath).Length, IndexingBudget.SharedLayers(file, Ff));
        });
    }

    // ---- "The more you use it the more it learns what the user needs" (What to keep in RAM - warm cached index) ----

    // THE OWNER: "The more you use it the more it learns what the user needs" (What to keep in RAM - step 4).
    // A day's questions are remembered on flash across the engine going and coming back; at night the queue revises step
    // 2's heat from them, the pieces staying where they lie; and the next morning, with RAM for the day's groups and no
    // more, the day's question finds every piece it needs in step 4.
    [Fact]
    public void What_the_person_uses_is_kept_on_flash_and_after_a_night_RAM_holds_it()
    {
        WithModel(415, (file, directory) =>
        {
            using var queue = IndexingQueue.Open(file, directory, Plan(groups: 4, list: 96, shared: 0));
            var indexer = new Host(file);
            while (queue.RunNext(indexer)) { }
            var core = ThoughtIndex.Open(queue.IndexPath)!;
            var pieces = CachedIndex.Open(queue.CorePath, file, core)!;

            // The day: one word asked twelve times, a word whose groups the morning's step 4 - chosen by what Watch saw,
            // RAM for as many groups as the word wants - did not wholly hold. Its use written to flash as the turns end.
            var usePath = Path.Combine(directory, "model.use");
            var word = -1;
            List<(int Layer, int Group)> dayGroups = [];
            for (var candidate = 2; candidate < SyntheticQwen35.Vocabulary && word < 0; candidate++)
            {
                using var day = new IndexedFeedForward(core, pieces, file, warmBudget: 0, channels: 2, readBytes: FlashFile.Page);
                var asked = FromTheIndex(file, day);
                for (var time = 0; time < 12; time++) asked.Forward(candidate, asked.NewState(8));
                var used = day.TakeUsage();
                var groups = Enumerable.Range(0, Layers).SelectMany(il => Enumerable.Range(0, core.Groups).Select(g => (Layer: il, Group: g)))
                    .Where(p => used[p.Layer * core.Groups + p.Group] > 0).ToList();
                using var morning = new IndexedFeedForward(core, pieces, file, warmBudget: groups.Count * pieces.PieceStride, channels: 2, readBytes: FlashFile.Page);
                var before = FromTheIndex(file, morning);
                before.Forward(candidate, before.NewState(8));
                if (morning.TakeCounts().Warm == Layers) continue;
                (word, dayGroups) = (candidate, groups);
                IndexUse.Open(usePath, Layers * core.Groups).Add(used);
            }
            Assert.True(word >= 0, "setup: the morning's step 4 already holds the groups of every word tried");

            // The night, in a process that was not there in the day: the use read back from flash, step 2's heat revised.
            var use = IndexUse.Open(usePath, Layers * core.Groups);
            Assert.True(use.Since == 12L * Layers, $"the day's {12 * Layers} words were kept on flash as {use.Since}");
            var night = queue.Revise(use.Ranking());
            use.Revised(new DateTimeOffset(2026, 11, 13, 2, 0, 0, TimeSpan.Zero));
            Assert.Equal(pieces.Count, night.Count);   // the pieces stay where they lie
            Assert.True(dayGroups.All(g => night.Heat(g.Layer, g.Group) > 0), "step 2's heat does not carry the groups the day used");
            Assert.True(dayGroups.All(g => CachedIndex.Open(queue.CorePath, file, core)!.Heat(g.Layer, g.Group) > 0), "the night's heat did not reach flash");

            // The next morning: RAM for the day's groups and no more. The word again, a new conversation.
            using var next = new IndexedFeedForward(core, CachedIndex.Open(queue.CorePath, file, core), file,
                warmBudget: dayGroups.Count * night.PieceStride, channels: 2, readBytes: FlashFile.Page);
            var again = FromTheIndex(file, next);
            again.Forward(word, again.NewState(8));
            var counts = next.TakeCounts();
            Assert.True(counts.Warm == Layers && counts.Bytes == 0,
                $"the morning after a day of word {word}, it found {counts.Warm} of its {Layers} pieces in step 4 and read {counts.Bytes} bytes from flash");
        });
    }

    // "The more you use it the more it learns": what was used lately counts for more than what was used long ago (ASSUMED:
    // each night halves what the nights before it left), a night's work is due once there is use to learn from and a night
    // has passed, and it is done at bedtime.
    [Fact]
    public void Use_from_last_night_outweighs_use_from_long_ago_and_a_revise_is_due_once_a_night()
    {
        var directory = Directory.CreateTempSubdirectory("index-use-").FullName;
        try
        {
            var path = Path.Combine(directory, "model.use");
            var night1 = new DateTimeOffset(2026, 11, 12, 2, 0, 0, TimeSpan.Zero);
            var use = IndexUse.Open(path, 3);
            Assert.Equal(0, use.Since);
            use.Add([10, 0, 0]);                      // day one: group 0, ten words
            Assert.Equal(new long[] { 10, 0, 0 }, use.Ranking());
            use.Revised(night1);

            use = IndexUse.Open(path, 3);             // the engine went and came back
            Assert.True(use.Since == 0 && use.LastRevised == night1, "the night's revise was not kept on flash");
            use.Add([0, 6, 0]);                       // day two: group 1, six words
            use.Add([0, 0, 1]);
            Assert.Equal(7, use.Since);
            Assert.Equal(new long[] { 5, 6, 1 }, use.Ranking());  // day one's ten count for five now

            Assert.False(IndexingWhen.ReviseDue(usedSince: 0, night1, night1.AddDays(2), IndexingPace.BackToBack));    // nothing to learn from
            Assert.False(IndexingWhen.ReviseDue(usedSince: 7, night1, night1.AddHours(3), IndexingPace.BackToBack));   // the same night
            Assert.False(IndexingWhen.ReviseDue(usedSince: 7, night1, night1.AddDays(1), IndexingPace.OneUnit));       // not bedtime
            Assert.True(IndexingWhen.ReviseDue(usedSince: 7, night1, night1.AddDays(1), IndexingPace.BackToBack));

            Assert.Equal(new long[5], IndexUse.Open(path, 5).Ranking()); // another index's groups: nothing carried over
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    // ---- the line reaches the person ----

    // THE OWNER: "user stays updated". The service reports the index beside its brain - one component of the state every
    // app reads - with the line a person is shown; the row crosses the link whole; and a brain that is ready to answer
    // carries the line as its caption.
    [Fact]
    public void The_service_reports_the_line_and_it_reaches_an_app_across_the_link()
    {
        WithModel(417, (file, directory) =>
        {
            var now = new DateTimeOffset(2026, 11, 12, 21, 0, 0, TimeSpan.Zero);
            Assert.Null(IndexReadiness.Read(directory, now));   // no engine is indexing here

            var host = new Host(file);
            using var queue = IndexingQueue.Open(file, directory, Plan());
            for (var unit = 0; unit < 4; unit++) queue.RunNext(host);
            var during = IndexReadiness.Read(directory, now);
            Assert.NotNull(during);
            Assert.Equal(ReadinessComponents.Index, during.Component);
            Assert.Equal(BrainPhase.Loading, during.Phase);
            Assert.Equal(4.0 / Units, during.Progress);
            Assert.Equal("Until indexing is complete AI is functioning at 40%.", during.Detail);

            var tracker = new ReadinessTracker();
            tracker.Report(ComponentState.Of(ReadinessComponents.Brain, BrainPhase.Ready, ReadinessSentences.Ready, now, "the-model"));
            tracker.Report(during);
            var (_, _, crossed) = LinkAskRows.ReadStatus(LinkAskRows.Status(tracker.Current));
            var index = crossed!.Components.Single(c => c.Component == ReadinessComponents.Index);
            Assert.True(index.Detail == during.Detail && index.Progress == during.Progress && index.Phase == BrainPhase.Loading,
                "the index's row did not cross the link whole");
            Assert.Equal("Until indexing is complete AI is functioning at 40%.", IndexReadiness.Caption(crossed));

            while (queue.RunNext(host)) { }
            var after = IndexReadiness.Read(directory, now);
            Assert.True(after is { Phase: BrainPhase.Ready } && after.Detail == "The more you use it the more it learns what the user needs.",
                $"once indexing is complete the service says \"{after?.Detail}\"");
            tracker.Report(after!);
            Assert.Equal("The more you use it the more it learns what the user needs.", IndexReadiness.Caption(tracker.Current));

            // A brain that is not ready to answer says its own line, not the index's; no index, no line.
            tracker.Report(ComponentState.Of(ReadinessComponents.Brain, BrainPhase.Loading, ReadinessSentences.Loading, now));
            Assert.Equal(string.Empty, IndexReadiness.Caption(tracker.Current));
            Assert.Equal(string.Empty, IndexReadiness.Caption(new ReadinessTracker().Current));
        });
    }

    // "user stays updated" - and is not told about a model that is not being indexed. A phone changes to a model whose
    // layers all fit in RAM, with the earlier model's record still lying in the folder: the engine records that this one is
    // not indexed, whoever reads the folder takes that, and the line the service said earlier is taken back.
    [Fact]
    public void A_model_that_is_not_indexed_says_nothing_even_beside_another_models_record()
    {
        WithModel(419, (file, directory) =>
        {
            var now = new DateTimeOffset(2026, 11, 12, 21, 0, 0, TimeSpan.Zero);
            var tracker = new ReadinessTracker();
            tracker.Report(ComponentState.Of(ReadinessComponents.Brain, BrainPhase.Ready, ReadinessSentences.Ready, now, "the-model"));
            IndexReadiness.Report(tracker, directory, now);
            Assert.DoesNotContain(tracker.Current.Components, c => c.Component == ReadinessComponents.Index);   // nothing said where there is nothing

            Assert.Null(IndexingQueue.Asked(file, directory));
            using (var queue = IndexingQueue.Open(file, directory, Plan(groups: 4, list: 96)))
                Assert.True(queue.RunNext(new Host(file)));
            Assert.Equal((4, 96), IndexingQueue.Asked(file, directory));
            IndexReadiness.Report(tracker, directory, now);
            Assert.Equal("Until indexing is complete AI is functioning at 10%.", IndexReadiness.Caption(tracker.Current));

            GgufBuilder.WithFile(SyntheticQwen35.Build(GgmlType.PTQ1_0, 421, layers: Layers), other =>
            {
                using var fits = GgufFile.Open(other);
                IndexingQueue.NotIndexing(fits, directory);
                Assert.Null(IndexingQueue.Asked(fits, directory));
                return 0;
            });
            Assert.Null(IndexingQueue.ReadStatus(directory));
            Assert.Null(IndexReadiness.Read(directory, now));
            IndexReadiness.Report(tracker, directory, now);
            Assert.Equal(string.Empty, IndexReadiness.Caption(tracker.Current));
        });
    }

    // ---- "if it fails then check why it failed. clean up and try again" (the owner, 2026-11-15) ----

    // TOO FEW NEURONS IS A REASON WITH A REMEDY: the shares an index is tried at, in turn, up to every neuron - the first
    // the engine's own; the share a list is of a layer's neurons, as the P30's records hold them (lists of 1,392 of the
    // 4B's 9,216, 1,856 of the 9B's 12,288, 2,624 of the 27B's 17,408: fifteen in a hundred each); and one thought group
    // where every neuron is listed, because every group would hold the same list.
    [Fact]
    public void An_index_that_failed_is_followed_by_one_listing_the_next_share_up_to_every_neuron()
    {
        Assert.Equal(new[] { 15, 30, 50, 100 }, IndexingRungs.Shares.ToArray());
        Assert.Equal(CircleAI.Inference.Managed.ManagedChatGenerator.IndexPercent, IndexingRungs.Shares[0]);
        Assert.Equal(15, IndexingRungs.Next(0));
        Assert.Equal(30, IndexingRungs.Next(15));
        Assert.Equal(50, IndexingRungs.Next(30));
        Assert.Equal(100, IndexingRungs.Next(50));
        Assert.Null(IndexingRungs.Next(100));

        Assert.Equal(15, IndexingRungs.ShareOf(1392, 9216));
        Assert.Equal(15, IndexingRungs.ShareOf(1856, 12288));
        Assert.Equal(15, IndexingRungs.ShareOf(2624, 17408));
        Assert.Equal(100, IndexingRungs.ShareOf(9216, 9216));
        Assert.Equal(0, IndexingRungs.ShareOf(96, 0));

        Assert.Equal(1, IndexingRungs.GroupsFor(100, 8));
        Assert.Equal(8, IndexingRungs.GroupsFor(50, 8));
    }

    // The share its lists were asked for and the engine that made it go with the record, so what to try next is read from
    // it - from the engine's own folder, or the copy kept outside it where the phone cleared that folder. An index of every
    // neuron that failed, by this engine, leaves nothing to try; made by another engine, it is tried again; and a record
    // from before the share was kept is read by the share its lists are of the model's neurons.
    [Fact]
    public void What_an_index_was_tried_at_is_read_from_its_record_or_the_kept_copy()
    {
        WithModel(423, (file, directory) =>
        {
            var kept = Path.Combine(directory, "kept");
            var nothingKept = Path.Combine(directory, "nothing-kept");
            var name = Path.GetFileName(file.FilePath);
            var record = Path.Combine(directory, IndexingQueue.RecordName(name));
            Assert.Null(IndexingQueue.Attempt(file, directory, nothingKept));

            using (var queue = IndexingQueue.Open(file, directory, Plan(groups: 4, list: Ff * 30 / 100) with { Share = 30 }))
            {
                var host = new Host(file) { Agreement = 540 };
                while (queue.RunNext(host)) { }
                Assert.True(queue.Status is { Failed: true, EveryNeuron: false, NothingLeftToTry: false }, $"at 30 in a hundred the queue says {queue.Status}");
            }
            var tried = IndexingQueue.Attempt(file, directory, nothingKept)!.Value;
            Assert.Equal(30, tried.Share);
            Assert.Equal(4, tried.Groups);
            Assert.True(tried.ThisEngine);
            Assert.True(tried.Status is { Failed: true, Agreement: 540 });
            var text = File.ReadAllText(record);
            Assert.Contains("\nshare=30\n", text, StringComparison.Ordinal);
            Assert.Contains($"\nengine={IndexingQueue.EngineVersion}\n", text, StringComparison.Ordinal);

            // Every neuron, by this engine, and it still failed: nothing is left to try.
            using (var queue = IndexingQueue.Open(file, directory, Plan(groups: 1, list: Ff) with { Share = 100 }))
            {
                var host = new Host(file) { Agreement = 250 };
                while (queue.RunNext(host)) { }
                Assert.True(queue.Status.NothingLeftToTry, $"with every neuron listed the queue says {queue.Status}");
            }
            Assert.True(IndexingQueue.ReadStatus(directory, name) is { NothingLeftToTry: true });

            // Made by another engine: it is tried again.
            File.WriteAllLines(record, File.ReadAllLines(record).Select(l => l.StartsWith("engine=", StringComparison.Ordinal) ? "engine=0.0.0.0" : l));
            Assert.True(IndexingQueue.ReadStatus(directory, name) is { Failed: true, NothingLeftToTry: false });
            Assert.False(IndexingQueue.Attempt(file, directory, nothingKept)!.Value.ThisEngine);

            // From before the share was kept: read by the share its lists are of the model's neurons.
            File.WriteAllLines(record, File.ReadAllLines(record)
                .Where(l => !l.StartsWith("share=", StringComparison.Ordinal) && !l.StartsWith("engine=", StringComparison.Ordinal)));
            Assert.Equal(100, IndexingQueue.Attempt(file, directory, nothingKept)!.Value.Share);

            // The engine's folder cleared: the kept copy is what is read.
            Directory.CreateDirectory(kept);
            File.Move(record, Path.Combine(kept, Path.GetFileName(record)));
            Assert.Null(IndexingQueue.Attempt(file, directory, nothingKept));
            Assert.Equal(100, IndexingQueue.Attempt(file, directory, kept)!.Value.Share);
        });
    }

    // AT EVERY NEURON NO INDEX IS BUILT (the owner, 2026-11-15, of a brain that needs every neuron: "Yes, take over"): the
    // record says every neuron is computed from the model's own file, and that serves - complete, nothing measured,
    // nothing failed, nothing left untried, nothing said to the person of an index. It is what the next attempt reads; it
    // is kept where the phone does not clear the engine's folder, and read from there once that folder is cleared; and an
    // index asked for after it is other work, begun from the start - once one at the first share has begun, the kept copy
    // goes.
    [Fact]
    public void Every_neuron_from_the_models_own_file_serves_with_no_check_and_its_record_is_kept()
    {
        WithModel(424, (file, directory) =>
        {
            var kept = Path.Combine(directory, "kept");
            var nothingKept = Path.Combine(directory, "nothing-kept");
            var name = Path.GetFileName(file.FilePath);
            var record = Path.Combine(directory, IndexingQueue.RecordName(name));

            var status = IndexingQueue.EveryNeuronFromItsOwnFile(file, directory);

            Assert.True(status is { Complete: true, Serves: true, Failed: false, NothingLeftToTry: false, FromItsOwnFile: true, EveryNeuron: true, Agreement: null, Percent: 100 },
                $"the record says {status}");
            Assert.Equal(status, IndexingQueue.ReadStatus(directory, name));
            Assert.Equal(status, IndexingQueue.ReadStatus(directory));
            Assert.Null(IndexReadiness.Read(directory, DateTimeOffset.UtcNow, name));
            var text = File.ReadAllText(record);
            Assert.Contains("\nfrom=model\n", text, StringComparison.Ordinal);
            Assert.Contains($"\nengine={IndexingQueue.EngineVersion}\n", text, StringComparison.Ordinal);
            var tried = IndexingQueue.Attempt(file, directory, nothingKept)!.Value;
            Assert.True(tried is { Share: 100, Groups: 1, ListNeurons: Ff, ThisEngine: true, Status.FromItsOwnFile: true }, $"the attempt reads {tried}");
            Assert.Empty(IndexingQueue.RemoveFailed(directory));

            // Kept where the phone does not clear it, and read from there once the engine's folder is cleared.
            var (keptNow, letGo) = IndexingQueue.KeepFailed(directory, kept);
            Assert.Equal(new[] { name }, keptNow);
            Assert.Empty(letGo);
            File.Delete(record);
            Assert.Null(IndexingQueue.Attempt(file, directory, nothingKept));
            Assert.True(IndexingQueue.Attempt(file, directory, kept) is { Share: 100, Status.FromItsOwnFile: true }, "the kept copy did not read back");

            // An index asked for after it is other work, begun from the start.
            File.Copy(Path.Combine(kept, IndexingQueue.RecordName(name)), record);
            using (var queue = IndexingQueue.Open(file, directory, Plan(groups: 4, list: Ff * 15 / 100) with { Share = 15 }))
                Assert.True(queue.Status is { Done: 0, Complete: false, FromItsOwnFile: false }, $"the queue opened after it says {queue.Status}");
            Assert.True(IndexingQueue.ReadStatus(directory, name) is { FromItsOwnFile: false, Done: 0 });
            var (_, goneNow) = IndexingQueue.KeepFailed(directory, kept);
            Assert.Equal(new[] { name }, goneNow);
        });
    }

    // ---- what the tests are made of ----

    // Whoever holds the model while the queue works: here the model with every weight in RAM.
    private sealed class Host(GgufFile file) : IIndexingHost
    {
        private readonly LanguageModel _model = LanguageModel.Load(file, null, headFromStore: false, pairedOverride: true);

        public int Watched { get; private set; }

        public int Checked { get; private set; }

        public ThoughtIndex? CheckedCore { get; private set; }

        public SplitByThoughts? CheckedSplit { get; private set; }

        // What the check measures here, per thousand: every word the same, unless a test says otherwise.
        public int Agreement { get; set; } = 1000;

        public bool Watch(IReadOnlyList<int> words, ThoughtWatch watch, Func<bool>? stop)
        {
            _model.Watch = watch;
            _model.Interrupt = stop;
            try
            {
                _model.ForwardBatch([.. words], _model.NewState(64), new float[words.Count][]);
                Watched++;
                return true;
            }
            catch (OperationCanceledException)
            {
                return false;
            }
            finally
            {
                _model.Watch = null;
                _model.Interrupt = null;
            }
        }

        public int? Check(ThoughtIndex core, SplitByThoughts split, Func<bool>? stop)
        {
            if (stop?.Invoke() == true) return null;
            Checked++;
            CheckedCore = core;
            CheckedSplit = split;
            return Agreement;
        }
    }

    // What two queues left on flash, file by file: the same names holding the same bytes.
    private static void SameFiles(string one, string other)
    {
        var names = Directory.EnumerateFiles(one).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(names, Directory.EnumerateFiles(other).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToList());
        foreach (var name in names)
        {
            if (name!.EndsWith(".indexing", StringComparison.Ordinal)) continue;   // the queue's own record
            Assert.True(File.ReadAllBytes(Path.Combine(one, name)).AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(other, name))),
                $"{name} differs between the two");
        }
    }

    private static LanguageModel FromTheIndex(GgufFile file, IndexedFeedForward serving)
    {
        var model = LanguageModel.Load(file, null, headFromStore: false, ffnFromStore: true, pairedOverride: true, mixFromStore: false);
        model.Index = serving;
        return model;
    }

    private static List<float[]> Logits(LanguageModel model, int[] words)
    {
        var state = model.NewState(32);
        var all = new List<float[]>();
        foreach (var token in words)
        {
            var logits = new float[SyntheticQwen35.Vocabulary];
            model.Forward(token, state, logits);
            all.Add(logits);
        }
        return all;
    }

    private static int Top(float[] logits)
    {
        var best = 0;
        for (var i = 1; i < logits.Length; i++)
        {
            if (logits[i] > logits[best]) best = i;
        }
        return best;
    }

    private static void WithModel(int seed, Action<GgufFile, string> use)
    {
        var directory = Directory.CreateTempSubdirectory("indexing-queue-").FullName;
        try
        {
            GgufBuilder.WithFile(SyntheticQwen35.Build(GgmlType.PTQ1_0, seed, layers: Layers), path =>
            {
                using var file = GgufFile.Open(path);
                use(file, directory);
                return 0;
            });
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
