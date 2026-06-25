// NightlyAdapterTrainer.cs
//
// (Phase D3) Periodically drains the FeedbackTrainingQueue, runs LoRA
// gradient steps against the current model handle, saves the adapter
// to disk, and applies it atomically. Idle-and-charging gating is
// host-supplied via the ShouldFireNow predicate.

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CircleAI.Inference;

/// <param name="MinBatchSize">Minimum samples to bother training. Skip otherwise.</param>
/// <param name="MaxSamplesPerRun">Cap per nightly run so a backlog can't lock the device.</param>
/// <param name="LearningRate">Adam-style LR for the LoRA adapter parameters.</param>
/// <param name="LoRARank">Rank of the LoRA decomposition; lower = smaller adapter.</param>
/// <param name="AdapterPath">Where to persist the trained adapter file.</param>
/// <param name="Interval">How often to check whether to train. Default 6 hours.</param>
/// <param name="ShouldFireNow">Optional gate (battery, charging, idle) — defaults to "always".</param>
/// <param name="Tokenizer">Tokeniser to convert text → int IDs. Falls back to char-level mapping if null.</param>
public sealed record NightlyAdapterTrainerOptions(
    int           MinBatchSize     = 16,
    int           MaxSamplesPerRun = 256,
    float         LearningRate     = 1e-4f,
    int           LoRARank         = 8,
    string        AdapterPath      = "circleai-lora.mnn",
    TimeSpan?     Interval         = null,
    Func<bool>?   ShouldFireNow    = null,
    Func<string, int[]>? Tokenizer = null);

public sealed class NightlyAdapterTrainer : IHostedService, IAsyncDisposable
{
    private readonly IFeedbackTrainingQueue _queue;
    private readonly LoRAAdapterManager _adapter;
    private readonly NightlyAdapterTrainerOptions _opts;
    private readonly ILogger<NightlyAdapterTrainer> _logger;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public NightlyAdapterTrainer(
        IFeedbackTrainingQueue queue,
        LoRAAdapterManager adapter,
        NightlyAdapterTrainerOptions opts,
        ILogger<NightlyAdapterTrainer>? logger = null)
    {
        _queue   = queue   ?? throw new ArgumentNullException(nameof(queue));
        _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        _opts    = opts    ?? throw new ArgumentNullException(nameof(opts));
        _logger  = (ILogger<NightlyAdapterTrainer>?)logger ?? NullLogger<NightlyAdapterTrainer>.Instance;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_cts is not null) return Task.CompletedTask;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _loop = Task.Run(() => LoopAsync(_cts.Token), CancellationToken.None);
        _logger.LogInformation("[NightlyAdapterTrainer] started; interval={Interval}", _opts.Interval ?? TimeSpan.FromHours(6));
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_cts is null) return;
        _cts.Cancel();
        try { if (_loop is not null) await _loop.WaitAsync(cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        _cts.Dispose();
        _cts = null;
        _loop = null;
    }

    public async ValueTask DisposeAsync() => await StopAsync(CancellationToken.None).ConfigureAwait(false);

    private async Task LoopAsync(CancellationToken ct)
    {
        var interval = _opts.Interval ?? TimeSpan.FromHours(6);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (_opts.ShouldFireNow is null || _opts.ShouldFireNow())
                    await RunOnceAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[NightlyAdapterTrainer] run failed");
            }
            try { await Task.Delay(interval, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>(Phase D3) Drain + train in one pass. Public so a host can trigger manually.</summary>
    public async ValueTask RunOnceAsync(CancellationToken ct = default)
    {
        if (_queue.Pending < _opts.MinBatchSize)
        {
            _logger.LogDebug("[NightlyAdapterTrainer] skipping; pending={Pending} < min={Min}", _queue.Pending, _opts.MinBatchSize);
            return;
        }

        var samples = await _queue.DrainAsync(_opts.MaxSamplesPerRun, ct).ConfigureAwait(false);
        if (samples.Count == 0) return;

        var tokenizer = _opts.Tokenizer ?? CharTokenizer;
        var totalLoss = 0f;
        var stepCount = 0;
        foreach (var sample in samples)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var input  = tokenizer(sample.UserText);
                var target = tokenizer(sample.Polarity >= 0 ? sample.PreferredText : sample.AssistantText);
                if (input.Length == 0 || target.Length == 0) continue;

                var loss = _adapter.TrainStep(input, target, _opts.LearningRate, _opts.LoRARank);
                totalLoss += loss;
                stepCount++;
            }
            catch (NotSupportedException)
            {
                // Native MNN not built with training — re-queue and bail out.
                foreach (var s in samples) await _queue.EnqueueAsync(s, ct).ConfigureAwait(false);
                _logger.LogWarning("[NightlyAdapterTrainer] MNN training not enabled; samples re-queued, run skipped");
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[NightlyAdapterTrainer] step failed for sample at {At}", sample.AtUtc);
            }
        }

        if (stepCount > 0)
        {
            try
            {
                _adapter.SaveAdapter(_opts.AdapterPath);
                _adapter.Apply(_opts.AdapterPath);
                _logger.LogInformation(
                    "[NightlyAdapterTrainer] trained {Steps} steps, avg-loss={Loss:F4}, adapter saved to {Path}",
                    stepCount, totalLoss / stepCount, _opts.AdapterPath);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[NightlyAdapterTrainer] adapter save/apply failed");
            }
        }
    }

    /// <summary>(Phase D3) Char-level tokenizer fallback — every char becomes its UTF-16 code-unit value.</summary>
    private static int[] CharTokenizer(string text)
    {
        if (string.IsNullOrEmpty(text)) return Array.Empty<int>();
        var arr = new int[text.Length];
        for (var i = 0; i < text.Length; i++) arr[i] = text[i];
        return arr;
    }
}
