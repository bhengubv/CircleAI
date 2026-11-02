// TranslationThroughTheBrainTests.cs
//
// Translation as the plan has it (S8): ONE engine - LlmTranslationEngine owns the prompt - run on
// whatever brain the device chose, through IChatGenerator. On the service that brain is reached
// through its one door, so the engine's generator is the ask queue (AskFrontGenerator) and each
// translation is a plain ask: no persona, no notes, no shortcut, no thinking. The capability that
// used to write its own prompt now asks the brain's translate door. And spoken translation is one
// request where the ears, the brain and the voice already meet: transcribe, translate, speak.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using CircleAI.Assistant;
using CircleAI.Assistant.Lifecycle;
using CircleAI.Assistant.Requests;
using CircleAI.Inference;
using CircleAI.Languages.Translation;
using CircleAI.Linking;
using Xunit;

namespace CircleAI.Tests;

public sealed class TranslationThroughTheBrainTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "circleai-translate-" + Guid.NewGuid().ToString("N"));

    public TranslationThroughTheBrainTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>Records the prompt and the options instead of running a model.</summary>
    private sealed class Spy : IChatGenerator
    {
        public string Last { get; private set; } = string.Empty;
        public GenerationOptions? Options { get; private set; }
        public string Reply { get; init; } = "Sawubona";

        public Task<string> GenerateAsync(IReadOnlyList<ChatMessage> messages, GenerationOptions? options = null, CancellationToken ct = default)
        {
            Last = messages[^1].Content;
            Options = options;
            return Task.FromResult(Reply);
        }

        public async IAsyncEnumerable<string> StreamAsync(IReadOnlyList<ChatMessage> messages, GenerationOptions? options = null,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            Last = messages[^1].Content;
            Options = options;
            await Task.CompletedTask;
            yield return Reply;
        }

        public void Dispose() { }
    }

    // ── the engine ──────────────────────────────────────────────────────

    [Fact]
    public async Task A_source_nobody_knows_is_left_unnamed_rather_than_guessed()
    {
        var spy = new Spy();
        await new LlmTranslationEngine(spy, BrainTranslation.Name).TranslateAsync(new("sawubona", "auto", "en"));

        Assert.StartsWith("Translate the following text into English.", spy.Last, StringComparison.Ordinal);
        Assert.DoesNotContain(" from ", spy.Last);
        Assert.Contains("only the translation", spy.Last, StringComparison.OrdinalIgnoreCase);   // the clause that must survive
        Assert.EndsWith("sawubona", spy.Last, StringComparison.Ordinal);

        foreach (var unknown in new[] { "", "und", "AUTO" })
        {
            await new LlmTranslationEngine(spy, BrainTranslation.Name).TranslateAsync(new("hello", unknown, "zu"));
            Assert.StartsWith("Translate the following text into isiZulu.", spy.Last, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task A_translation_is_asked_plain_with_no_thinking_and_room_for_its_words()
    {
        var spy = new Spy();
        await new LlmTranslationEngine(spy).TranslateAsync(new("where is the toilet", "en", "zu"));
        Assert.True(spy.Options!.Plain);
        Assert.Equal(ReasoningEffort.Off, spy.Options.Effort);
        Assert.Equal(128, spy.Options.MaxTokens);

        await foreach (var _ in new LlmTranslationEngine(spy).StreamTranslateAsync(new(new string('a', 3000), "en", "zu"))) { }
        Assert.True(spy.Options!.Plain);
        Assert.Equal(1024, spy.Options.MaxTokens);   // a long text gets room, within a ceiling
    }

    // ── the capability no longer writes its own prompt ───────────────────

    /// <summary>A brain that records what it was asked.</summary>
    private sealed class RecordingBrain : IBrain
    {
        public string Answer { get; init; } = "Sawubona";
        public List<string> Prompts { get; } = [];
        public int MaxImageEdge => 0;
        public Task<BrainState> StateAsync(CancellationToken ct = default) => Task.FromResult(new BrainState(true, ""));
        public Task<string> AskAsync(string prompt, Action<string>? token = null, CancellationToken ct = default)
        {
            Prompts.Add(prompt);
            return Task.FromResult(Answer);
        }
        public Task<string> SeeAsync(string question, byte[] image, Action<string>? token = null, CancellationToken ct = default) => Task.FromResult("");
        public Task<string> AskWithToolsAsync(string prompt, CancellationToken ct = default) => Task.FromResult("");
    }

    [Fact]
    public async Task How_do_you_say_is_answered_by_the_engines_prompt_on_the_brain()
    {
        var brain = new RecordingBrain();
        var did = await new TranslateCapability(brain).DoAsync(new Ask("how do you say good morning in isiZulu"));

        Assert.True(did.Done);
        Assert.Equal("Sawubona", did.Say);
        var prompt = Assert.Single(brain.Prompts);
        Assert.StartsWith("Translate the following text into isiZulu.", prompt, StringComparison.Ordinal);   // the engine's words
        Assert.DoesNotContain("Reply with the translation only", prompt);                                       // not the old copy's
        Assert.EndsWith("good morning", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_brain_with_a_translate_door_of_its_own_is_asked_through_it()
    {
        // The linked app's brain overrides the door with the service's translate verb; the
        // capability does not know or care which brain it has.
        var brain = new DoorBrain();
        var did = await new TranslateCapability(brain).DoAsync(new Ask("how do you say thank you in isiZulu"));

        Assert.Equal("Ngiyabonga", did.Say);
        Assert.Equal(("thank you", "auto", "zu"), brain.Asked);
        Assert.Empty(brain.Prompts);
    }

    private sealed class DoorBrain : IBrain
    {
        public (string, string?, string)? Asked { get; private set; }
        public List<string> Prompts { get; } = [];
        public int MaxImageEdge => 0;
        public Task<BrainState> StateAsync(CancellationToken ct = default) => Task.FromResult(new BrainState(true, ""));
        public Task<string> AskAsync(string prompt, Action<string>? token = null, CancellationToken ct = default)
        {
            Prompts.Add(prompt);
            return Task.FromResult("a turn");
        }
        public Task<string> SeeAsync(string question, byte[] image, Action<string>? token = null, CancellationToken ct = default) => Task.FromResult("");
        public Task<string> AskWithToolsAsync(string prompt, CancellationToken ct = default) => Task.FromResult("");
        public Task<string> TranslateAsync(string text, string? fromTag, string toTag, CancellationToken ct = default)
        {
            Asked = (text, fromTag, toTag);
            return Task.FromResult("Ngiyabonga");
        }
    }

    // ── the service's brain, through its one door ───────────────────────

    /// <summary>The brain behind the queue: records each job, answers with a fixed text.</summary>
    private sealed class QueueBrain : IBrainWorker
    {
        public BrainWorkerStatus Status { get; } =
            BrainWorkerStatus.NotStarted(DateTimeOffset.UtcNow) with { Phase = BrainPhase.Ready, ModelId = "Ternary-Bonsai-2-2B" };
        public event EventHandler<BrainWorkerStatus>? StatusChanged { add { } remove { } }
        public readonly List<BrainJob> Jobs = [];
        public string Answer = "Ngiyabonga kakhulu";
        public Failure? Fail;
        public TaskCompletionSource? Hold;
        public readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task EnsureReadyAsync(CancellationToken ct) => Task.CompletedTask;

        public async IAsyncEnumerable<BrainChunk> GenerateAsync(BrainJob job, [EnumeratorCancellation] CancellationToken ct)
        {
            lock (Jobs) Jobs.Add(job);
            Started.TrySetResult();
            if (Hold is { } hold) await hold.Task.WaitAsync(ct);
            await Task.Yield();
            if (Fail is { } failure) throw new BrainWorkerException(failure);
            foreach (var word in Answer.Split(' '))
                yield return new BrainChunk(word + " ");
            yield return new BrainChunk(string.Empty, Final: true);
        }

        public Task ReleaseAsync(CancellationToken ct) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private SqliteAskStore Store() => new(Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".db"));

    private static readonly ServiceLimits Quick = ServiceLimits.Default with { TextWriteInterval = TimeSpan.Zero };

    [Fact]
    public async Task The_engine_runs_on_the_services_brain_as_a_plain_ask_through_the_queue()
    {
        using var store = Store();
        var brain = new QueueBrain();
        await using var front = new RequestCoordinator(store, brain, Quick,
            shortcuts: [new HelloShortcut()], effort: new EffortRouter(form: () => CircleAI.Core.FormFactor.Phone, speed: _ => 1.47, self: () => null));
        front.Start();

        var engine = new LlmTranslationEngine(new AskFrontGenerator(front, "app:translator"), BrainTranslation.Name);
        var result = await engine.TranslateAsync(new("hello", "en", "zu"));

        Assert.Equal("Ngiyabonga kakhulu", result.TranslatedText);
        var job = Assert.Single(brain.Jobs);
        Assert.True(job.Plain);                                   // no persona, no notes on the far side
        Assert.Equal(ReasoningEffort.Off, job.Effort);            // a translation never thinks first
        Assert.StartsWith("Translate the following text from English to isiZulu.", Assert.Single(job.Messages).Content, StringComparison.Ordinal);

        // A record like any question: queued, answered, and the caller's own.
        var record = Assert.Single(front.List("app:translator", 5));
        Assert.True(record.Plain);
        Assert.Equal(AskState.Succeeded, record.State);
    }

    [Fact]
    public async Task The_queues_generator_streams_what_is_written_and_says_why_when_it_cannot()
    {
        using var store = Store();
        var brain = new QueueBrain();
        await using var front = new RequestCoordinator(store, brain, Quick);
        front.Start();
        var generator = new AskFrontGenerator(front, "app:1");

        var pieces = new List<string>();
        await foreach (var piece in generator.StreamAsync([new("user", "Translate this.\n\nthank you")])) pieces.Add(piece);
        Assert.Equal("Ngiyabonga kakhulu", string.Concat(pieces).Trim());

        // A failure that is not retried ends the ask at once, with its sentence.
        brain.Fail = new Failure(FailureKind.ManagedError, Certainty.Certain, WorkPhase.Decode, null, "test", "test", DateTimeOffset.UtcNow);
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => generator.GenerateAsync([new("user", "again")]));
        Assert.False(string.IsNullOrWhiteSpace(refused.Message));   // the queue's own sentence, for a person

        await Assert.ThrowsAsync<ArgumentException>(() => generator.GenerateAsync([new("system", "no user message")]));
    }

    [Fact]
    public async Task A_caller_that_stops_waiting_withdraws_its_ask_so_nobody_is_answered_for_nothing()
    {
        using var store = Store();
        var brain = new QueueBrain { Hold = new TaskCompletionSource() };
        await using var front = new RequestCoordinator(store, brain, Quick);
        front.Start();

        using var stop = new CancellationTokenSource();
        var asking = new AskFrontGenerator(front, "app:1").GenerateAsync([new("user", "Translate this.\n\nhello")], ct: stop.Token);
        await brain.Started.Task.WaitAsync(TimeSpan.FromMinutes(2));
        stop.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => asking);
        Assert.Equal(AskState.Cancelled, Assert.Single(front.List("app:1", 5)).State);
    }

    private sealed class HelloShortcut : IAskShortcut
    {
        public string Name => "hello";
        public ValueTask<string?> TryAnswerAsync(AskRecord ask, CancellationToken ct) =>
            ValueTask.FromResult<string?>(ask.Asked.Contains("hello", StringComparison.OrdinalIgnoreCase) ? "Hi!" : null);
    }

    // ── spoken words in, spoken words out ───────────────────────────────

    private sealed class FakeSpeech : ILinkSpeech
    {
        public string Heard { get; init; } = "where is the toilet";
        public bool Mute { get; init; }
        public List<string> Calls { get; } = [];

        public Task<string> TranscribeAsync(byte[] pcm16, string? language, CancellationToken ct = default)
        {
            Calls.Add($"transcribe {language}");
            return Task.FromResult(Heard);
        }

        public Task<byte[]> SpeakAsync(string text, string? language, CancellationToken ct = default)
        {
            Calls.Add($"speak {language}: {text}");
            if (Mute) throw new InvalidOperationException("no voice for that language");
            return Task.FromResult(new byte[3200]);
        }
    }

    private static LinkAudioRequest Spoken(string? from = "en", string to = "zu") =>
        new(LinkAudioVerb.Translate, new byte[32000], Language: from, Target: to);

    [Fact]
    public async Task The_ears_transcribe_the_brain_translates_and_the_voice_speaks_in_that_order()
    {
        var speech = new FakeSpeech();
        var spy = new Spy { Reply = "Ikuphi indlu yangasese?" };

        var reply = await LinkSpeechTranslation.TranslateAsync(speech, new LlmTranslationEngine(spy, BrainTranslation.Name), Spoken());

        Assert.True(reply.Ok);
        Assert.Equal("where is the toilet", reply.Heard);
        Assert.Equal("Ikuphi indlu yangasese?", reply.Text);
        Assert.Equal(3200, reply.Audio.Length);
        Assert.Equal(["transcribe en", "speak zu: Ikuphi indlu yangasese?"], speech.Calls);
        Assert.EndsWith("where is the toilet", spy.Last, StringComparison.Ordinal);   // the heard words reached the engine
    }

    [Fact]
    public async Task A_quiet_room_is_a_result_and_a_mute_voice_keeps_the_words()
    {
        var silence = await LinkSpeechTranslation.TranslateAsync(new FakeSpeech { Heard = "  " }, new LlmTranslationEngine(new Spy()), Spoken());
        Assert.True(silence.Ok);
        Assert.Equal(string.Empty, silence.Heard);
        Assert.Empty(silence.Audio);

        var mute = await LinkSpeechTranslation.TranslateAsync(new FakeSpeech { Mute = true }, new LlmTranslationEngine(new Spy()), Spoken());
        Assert.True(mute.Ok);
        Assert.Equal("Sawubona", mute.Text);
        Assert.Empty(mute.Audio);

        Assert.False((await LinkSpeechTranslation.TranslateAsync(new FakeSpeech(), new LlmTranslationEngine(new Spy()),
            new LinkAudioRequest(LinkAudioVerb.Translate, [], Target: "zu"))).Ok);
    }

    // ── the wire ────────────────────────────────────────────────────────

    private sealed class Wire : LinkAudioCodec.IWriter, LinkAudioCodec.IReader
    {
        private readonly List<object?> _fields = [];
        private int _at;
        public void WriteInt(int value) => _fields.Add(value);
        public void WriteString(string? value) => _fields.Add(value);
        public void WriteBytes(byte[] value) => _fields.Add(value);
        public int ReadInt() => (int)_fields[_at++]!;
        public string? ReadString() => (string?)_fields[_at++];
        public byte[] ReadBytes() => (byte[])_fields[_at++]!;
        public void Rewind() => _at = 0;
        public object? this[int i] { get => _fields[i]; set => _fields[i] = value; }
    }

    [Fact]
    public void A_spoken_translation_crosses_as_version_two_and_comes_back_with_what_was_heard()
    {
        var w = new Wire();
        Assert.True(LinkAudioCodec.TryWriteRequest(w, Spoken("af", "zu"), out _));
        Assert.Equal(LinkAudioCodec.TranslateWireVersion, w[0]);
        w.Rewind();
        var back = LinkAudioCodec.TryReadRequest(w)!;
        Assert.Equal((LinkAudioVerb.Translate, "af", "zu"), (back.Verb, back.Language, back.Target));

        var r = new Wire();
        LinkAudioCodec.WriteReply(r, LinkAudioReply.Translated("waar is die toilet", "Ikuphi indlu yangasese?", new byte[64]));
        r.Rewind();
        var reply = LinkAudioCodec.ReadReply(r);
        Assert.Equal(("waar is die toilet", "Ikuphi indlu yangasese?", 64), (reply.Heard, reply.Text, reply.Audio.Length));

        // Transcribe and Speak are version 1 exactly as before.
        var t = new Wire();
        LinkAudioCodec.WriteReply(t, LinkAudioReply.Transcribed("hello"));
        Assert.Equal(LinkAudioCodec.WireVersion, t[0]);
        t.Rewind();
        Assert.Null(LinkAudioCodec.ReadReply(t).Heard);
    }

    [Fact]
    public void A_translation_in_the_old_layout_or_into_nowhere_is_refused_not_misread()
    {
        var old = new Wire();
        LinkAudioCodec.TryWriteRequest(old, Spoken(), out _);
        old[0] = LinkAudioCodec.WireVersion;   // version 1 claiming to be a translation
        old.Rewind();
        Assert.Null(LinkAudioCodec.TryReadRequest(old));

        var nowhere = new Wire();
        LinkAudioCodec.TryWriteRequest(nowhere, new LinkAudioRequest(LinkAudioVerb.Translate, new byte[10], Target: ""), out _);
        nowhere.Rewind();
        Assert.Null(LinkAudioCodec.TryReadRequest(nowhere));
    }

    [Fact]
    public void The_translate_verb_is_on_the_chat_floor_and_its_arguments_survive_the_wire()
    {
        Assert.Equal(LinkScope.Chat, LinkVerbs.RequiredScope(LinkVerb.Translate));
        Assert.False(LinkVerbs.FirstPartyOnly(LinkVerb.Translate));

        var request = new LinkVerbRequest(LinkVerb.Translate, Text: "thank you",
            Args: new Dictionary<string, string> { [LinkTranslateRows.ArgFrom] = "en", [LinkTranslateRows.ArgTo] = "zu" });
        var back = LinkVerbCodec.TryDecodeRequest(LinkVerbCodec.Encode(request))!;
        Assert.Equal(LinkVerb.Translate, back.Verb);
        Assert.Equal("thank you", back.Text);
        Assert.Equal("zu", back.Arg(LinkTranslateRows.ArgTo));

        var rows = LinkVerbCodec.DecodeReply(LinkVerbCodec.Encode(LinkRowsReply.Success([LinkTranslateRows.Row("Ngiyabonga", "en", "zu")])));
        Assert.Equal("Ngiyabonga", LinkTranslateRows.Translation(rows.Rows));
        Assert.Equal(string.Empty, LinkTranslateRows.Translation([]));
    }
}
