// LiveHearing.cs
//
// THE EARS AT WORK WHILE SOMEBODY IS STILL TALKING.
//
// The microphone hands over a block every 100 ms and must not wait: a capture loop that stops to
// think drops audio. So this takes the blocks and goes, and a thread of its own feeds the model and
// writes down what has been said so far. An update that runs long is not a fault: whatever came in
// meanwhile goes into the next one, so on a slow phone the updates get further apart and the words
// are late, and nothing is lost.

using System.Collections.Concurrent;
using CircleAI.Inference.Managed.Runtime;

namespace CircleAI.Voice;

/// <summary>What the ears do with audio as it arrives: <see cref="MoonshineTranscriber.Hearing"/>.</summary>
internal interface IHearing : IDisposable
{
    /// <summary>Where the time went, for the log.</summary>
    string Spent { get; }

    /// <summary>Takes in audio, PCM 16-bit 16 kHz mono, in pieces of any size.</summary>
    void Hear(ReadOnlySpan<byte> pcm16);

    /// <summary>Writes down what has settled, and returns the words so far.</summary>
    string Update();

    /// <summary>What the words would be if the audio ended here, the last frames read without what follows them.</summary>
    string Peek();

    /// <summary>Writes down everything, the last frames included, and returns the words.</summary>
    string Finish();
}

/// <summary>One person saying one thing, being written down on a thread of its own.</summary>
public sealed class LiveHearing : IAsyncDisposable
{
    /// <summary>How much audio goes by between updates, in bytes: half a second.</summary>
    /// <remarks>
    /// A SECOND OF SPEECH COST THE TINY MODEL 0,4 s OF WORK AT THIS PACE ON THE P30 ON 2026-11-06, and
    /// the Small one all of it. Sooner means more of the same window read again for the same few words.
    /// </remarks>
    public const int BytesPerUpdate = 16_000;

    /// <summary>A quiet this long, and the ears say what the words would be if that was the end of them.</summary>
    /// <remarks>
    /// THE MICROPHONE IS NOT DONE FOR ANOTHER 800 ms: a pause to think is not an ending, so the gate holds
    /// a full second. Nothing says the rest of the turn has to wait for it. <see cref="Settled"/> says the
    /// words look finished, and <see cref="WentOn"/> takes it back.
    /// </remarks>
    public static readonly TimeSpan SettledAfter = TimeSpan.FromMilliseconds(200);

    private readonly IHearing _hearing;
    private readonly int _bytesPerUpdate;
    private readonly ConcurrentQueue<(byte[] Block, TimeSpan Quiet)> _audio = new();
    private readonly SemaphoreSlim _news = new(0);
    private readonly TaskCompletionSource<string> _final = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Thread _worker;
    private volatile bool _finishing;
    private volatile string _words = string.Empty;
    private string _still = string.Empty;
    private bool _settled;
    private volatile int _sameWords;

    /// <summary>Starts hearing on <paramref name="ears"/>.</summary>
    /// <param name="ears">The model.</param>
    /// <param name="bytesPerUpdate">How much audio goes by between updates.</param>
    public LiveHearing(MoonshineTranscriber ears, int bytesPerUpdate = BytesPerUpdate)
        : this((ears ?? throw new ArgumentNullException(nameof(ears))).Begin(), bytesPerUpdate)
    {
    }

    internal LiveHearing(IHearing hearing, int bytesPerUpdate)
    {
        _hearing = hearing;
        _bytesPerUpdate = bytesPerUpdate;
        _worker = new Thread(Work) { IsBackground = true, Name = "Ears" };
        _worker.Start();
    }

    /// <summary>The words so far. They can still change.</summary>
    public string Words => _words;

    /// <summary>How many updates in a row have written nothing new, with audio still coming in above the quiet.</summary>
    /// <remarks>
    /// THE ONE THING THAT CAN END A TURN IN A NOISY ROOM. A gate goes by level, and a fan, a road or a room
    /// of people is above it for as long as they go on: on the P30 on 2026-11-07 the microphone stayed open
    /// until each recording ran out, every time. Noise does not write words. When the writing stops, so
    /// has the person.
    /// </remarks>
    public int SameWordsFor => _sameWords;

    /// <summary>Raised on the ears' own thread when the words so far have changed.</summary>
    public event Action<string>? Changed;

    /// <summary>Raised on the ears' own thread when more of the words have stopped changing: the start of what was said.</summary>
    /// <remarks>
    /// NEVER THE LAST WORD, which is the one still being said: the ears heard "Why is this?" on their way
    /// to "Why is the sky blue?" on the P30 on 2026-11-06. A word that goes on to change costs whoever read
    /// it a reading of the question from its start, which is what it would have had to do anyway.
    /// </remarks>
    public event Action<string>? HeldStill;

    /// <summary>Raised on the ears' own thread when they have gone quiet with these words written down.</summary>
    public event Action<string>? Settled;

    /// <summary>Raised on the ears' own thread when they carry on after <see cref="Settled"/>.</summary>
    public event Action? WentOn;

    /// <summary>Where the time went, for the log.</summary>
    public string Spent => _hearing.Spent;

    /// <summary>Takes a block of audio, PCM 16-bit 16 kHz mono, and returns at once.</summary>
    /// <param name="block">The audio.</param>
    /// <param name="quiet">How long it has been quiet for, this block included.</param>
    public void Hear(byte[] block, TimeSpan quiet)
    {
        _audio.Enqueue((block, quiet));
        _news.Release();
    }

    /// <summary>The whole of what was said, when the audio has ended.</summary>
    /// <param name="ct">Stops waiting; the work in hand finishes by itself.</param>
    public Task<string> FinishAsync(CancellationToken ct = default)
    {
        _finishing = true;
        _news.Release();
        return _final.Task.WaitAsync(ct);
    }

    private void Work()
    {
        try
        {
            var sinceUpdate = 0;
            var quiet = TimeSpan.Zero;
            while (true)
            {
                _news.Wait();

                // READ FIRST: everything queued before the end was said is in the queue by now.
                var finishing = _finishing;
                while (_audio.TryDequeue(out var heard))
                {
                    _hearing.Hear(heard.Block);
                    sinceUpdate += heard.Block.Length;
                    quiet = heard.Quiet;
                }

                // WHAT WAS SETTLED IS WHAT WAS SAID: nothing but quiet has come in since, and reading it all
                // again takes the cores from the brain in the middle of the answer.
                if (finishing)
                {
                    _final.TrySetResult(_settled && _words.Length > 0 ? _words : _hearing.Finish());
                    return;
                }

                if (quiet >= SettledAfter && !_settled)
                {
                    sinceUpdate = 0;

                    // EVERYTHING ELSE IS WAITING FOR THESE WORDS, and on the P30 on 2026-11-06 they took half
                    // a second beside a brain reading ahead and a sixth of one alone. The hold is not let go
                    // here: a read ahead let loose now would take a token's time from the answer on its way.
                    // The answer lets it go, and if none comes it runs out.
                    BrainHold.For(TimeSpan.FromSeconds(1));
                    Peek();

                    _settled = true;
                    if (_words.Length > 0) Settled?.Invoke(_words);
                    continue;
                }

                if (quiet < SettledAfter && _settled)
                {
                    _settled = false;
                    WentOn?.Invoke();
                }

                // NOTHING NEW CAN COME OF A QUIET, and whoever was told has the cores to use. Going quiet is
                // no time to start one either: it would still be running when the words are all in.
                if (quiet > TimeSpan.Zero || sinceUpdate < _bytesPerUpdate) continue;
                sinceUpdate = 0;
                Update(still: true);
            }
        }
        catch (Exception e)
        {
            _final.TrySetException(e);
        }
    }

    private void Peek()
    {
        var words = _hearing.Peek();
        if (string.Equals(words, _words, StringComparison.Ordinal)) return;
        _words = words;
        Changed?.Invoke(words);
    }

    private void Update(bool still)
    {
        var words = _hearing.Update();
        if (still && Still(words) is var steady && steady.Length > _still.Length)
        {
            _still = steady;
            HeldStill?.Invoke(steady);
        }

        if (string.Equals(words, _words, StringComparison.Ordinal))
        {
            _sameWords++;
            return;
        }
        _sameWords = 0;
        _words = words;
        Changed?.Invoke(words);
    }

    /// <summary><paramref name="words"/> without the last one, or what ended it; nothing until there are two.</summary>
    /// <remarks>
    /// A word's own ending changes as the sentence goes on: "Name one." became "Name one kind of bird."
    /// AND ONE WORD IS NOT YET SOMEBODY SPEAKING: the ears wrote "Thank you." on the breath before "Why is
    /// the sky blue?" on the P30 on 2026-11-06.
    /// </remarks>
    internal static string Still(string words)
    {
        var last = words.TrimEnd().LastIndexOf(' ');
        if (last <= 0) return string.Empty;

        var still = words[..last].TrimEnd(' ', '.', ',', '?', '!', ';', ':');
        return still.Contains(' ', StringComparison.Ordinal) ? still : string.Empty;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        _finishing = true;
        _news.Release();
        try { await _final.Task.ConfigureAwait(false); }
        catch (Exception e) when (e is not OutOfMemoryException) { /* it said so to whoever was waiting */ }

        _hearing.Dispose();
        _news.Dispose();
    }
}
