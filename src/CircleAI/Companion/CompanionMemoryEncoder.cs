// CompanionMemoryEncoder.cs
//
// (M1) Fills the memory graph from real conversation — the piece that makes fused
// recall do something in production instead of starting empty. After each turn the
// session hands the exchange here and moves on; encoding happens on a background
// queue so the reply is never delayed. A full queue drops rather than blocks.
//
// It writes through SqliteKnowledgeGraph.AddTriple (not the IPersonalKnowledgeGraph
// relation seam) so each fact keeps its source turn and confidence — which the
// integrity work depends on.

using System;
using System.Collections.Generic;
using System.Threading.Channels;
using System.Threading.Tasks;
using CircleAI.Companion.HerJarvis;

namespace CircleAI.Companion;

/// <summary>(M1) Background writer: turn → knowledge graph, off the hot path.</summary>
public sealed class CompanionMemoryEncoder : IAsyncDisposable
{
    private readonly IKnowledgeGraphExtractor _extractor;
    private readonly SqliteKnowledgeGraph _graph;
    private readonly IBeliefExtractor? _beliefExtractor;
    private readonly SelfBeliefStore? _beliefs;
    private readonly Channel<EncodeJob> _queue;
    private readonly Task _drain;

    /// <summary>First error hit while draining, if any (diagnostics).</summary>
    public Exception? LastError { get; private set; }

    public CompanionMemoryEncoder(
        IKnowledgeGraphExtractor extractor, SqliteKnowledgeGraph graph,
        IBeliefExtractor? beliefExtractor = null, SelfBeliefStore? beliefs = null, int capacity = 256)
    {
        _extractor = extractor ?? throw new ArgumentNullException(nameof(extractor));
        _graph = graph ?? throw new ArgumentNullException(nameof(graph));
        _beliefExtractor = beliefExtractor;
        _beliefs = beliefs;
        _queue = Channel.CreateBounded<EncodeJob>(new BoundedChannelOptions(Math.Max(1, capacity))
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.DropWrite,   // never block a turn
        });
        _drain = Task.Run(DrainAsync);
    }

    /// <summary>Hand a turn to the encoder. Non-blocking; returns immediately.</summary>
    public void Enqueue(string userText, string assistantText, string episodeId)
    {
        if (string.IsNullOrWhiteSpace(episodeId)) return;
        _queue.Writer.TryWrite(new EncodeJob(userText ?? string.Empty, assistantText ?? string.Empty, episodeId));
    }

    private async Task DrainAsync()
    {
        await foreach (var job in _queue.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                // Give the memory node a readable name so recall hands back the actual
                // exchange, not an opaque id.
                await _graph.UpsertNodeAsync(
                    new KnowledgeNode(job.EpisodeId, "memory", job.UserText,
                        new Dictionary<string, string>())).ConfigureAwait(false);

                var triples = await _extractor
                    .ExtractFromTurnAsync(job.UserText, job.AssistantText, job.EpisodeId)
                    .ConfigureAwait(false);
                foreach (var t in triples)
                    _graph.AddTriple(t.Subject, t.Predicate, t.Object, t.Source, t.Confidence);

                // Form attributed beliefs from this turn — a third party's fact never
                // becomes the user's. Happens here, off the turn, at the point the false
                // belief would otherwise be created.
                if (_beliefExtractor is not null && _beliefs is not null)
                {
                    foreach (var b in await _beliefExtractor
                                 .ExtractAsync(job.UserText, job.EpisodeId).ConfigureAwait(false))
                        _beliefs.Record(b);
                }
            }
            catch (Exception ex)
            {
                LastError ??= ex;
                System.Diagnostics.Debug.WriteLine($"[CompanionMemoryEncoder] encode failed: {ex}");
            }
        }
    }

    /// <summary>Stops accepting work and waits for the queue to drain.</summary>
    public async ValueTask DisposeAsync()
    {
        _queue.Writer.TryComplete();
        try { await _drain.ConfigureAwait(false); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[CompanionMemoryEncoder] drain error: {ex.Message}"); }
    }

    private readonly record struct EncodeJob(string UserText, string AssistantText, string EpisodeId);
}
