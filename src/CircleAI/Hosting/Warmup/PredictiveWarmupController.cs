// PredictiveWarmupController.cs
//
// (RT-07) Background loop that polls the predictor and pre-warms the
// generator when a spike is forecast. The host wires it via DI; the
// controller is benign — it only fires WarmUpAsync; never holds the
// generator past one call.
//
// Callers feed arrivals into the predictor through RecordArrival; the
// controller does the rest.

using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CircleAI.Hosting.Warmup;

/// <summary>
/// (RT-07) Async background loop that polls an <see cref="IRequestPredictor"/>
/// and triggers <see cref="IAIService"/> pre-warm before predicted spikes.
/// </summary>
public sealed class PredictiveWarmupController : IAsyncDisposable
{
    private readonly IAIService _service;
    private readonly IRequestPredictor _predictor;
    private readonly PredictiveWarmupOptions _options;
    private readonly ILogger<PredictiveWarmupController> _logger;
    private readonly Func<DateTimeOffset> _clock;

    private CancellationTokenSource? _loopCts;
    private Task? _loopTask;
    private DateTimeOffset _lastWarmup = DateTimeOffset.MinValue;
    private bool _disposed;

    public PredictiveWarmupController(
        IAIService                       service,
        IRequestPredictor                predictor,
        PredictiveWarmupOptions          options,
        ILogger<PredictiveWarmupController>? logger = null,
        Func<DateTimeOffset>?            clock  = null)
    {
        _service   = service   ?? throw new ArgumentNullException(nameof(service));
        _predictor = predictor ?? throw new ArgumentNullException(nameof(predictor));
        _options   = options   ?? throw new ArgumentNullException(nameof(options));
        _logger    = logger    ?? NullLogger<PredictiveWarmupController>.Instance;
        _clock     = clock     ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Begin polling on a background loop. No-op when
    /// <see cref="PredictiveWarmupOptions.Enabled"/> is <c>false</c>.
    /// </summary>
    public Task StartAsync(CancellationToken ct = default)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(PredictiveWarmupController));
        if (!_options.Enabled || _loopTask is not null) return Task.CompletedTask;

        _loopCts  = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _loopTask = Task.Run(() => RunLoopAsync(_loopCts.Token), _loopCts.Token);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Convenience — record a request arrival on the underlying
    /// predictor at "now". Useful from observer hooks.
    /// </summary>
    public void NotifyArrival() => _predictor.RecordArrival(_clock());

    /// <summary>
    /// Run one prediction + decide-and-maybe-warm cycle. Returns
    /// <c>true</c> when warmup was triggered. Public for tests + manual
    /// poking; the loop calls this internally.
    /// </summary>
    public async Task<bool> TickAsync(CancellationToken ct = default)
    {
        var now      = _clock();
        var forecast = _predictor.Predict(now, _options.ForecastWindow);
        var score    = forecast.ProbabilityOfArrival * forecast.Confidence;
        if (score < _options.WarmupThreshold) return false;
        if (now - _lastWarmup < _options.MinTimeBetweenWarmups) return false;

        try
        {
            _lastWarmup = now;
            await _service.PrewarmAsync(ct).ConfigureAwait(false);
            _logger.LogInformation(
                "Predictive warmup fired (prob={Prob:0.00} conf={Conf:0.00} expected={Exp:0.00}).",
                forecast.ProbabilityOfArrival, forecast.Confidence, forecast.ExpectedCount);
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Predictive warmup failed.");
            return false;
        }
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        try
        {
            using var timer = new PeriodicTimer(_options.PollInterval);
            do { await TickAsync(ct).ConfigureAwait(false); }
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false));
        }
        catch (OperationCanceledException) { /* normal */ }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PredictiveWarmupController loop crashed.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        if (_loopCts is null) return;
        try { _loopCts.Cancel(); } catch { /* ignore */ }
        if (_loopTask is not null)
        {
            try { await _loopTask.ConfigureAwait(false); } catch { /* ignore */ }
        }
        _loopCts.Dispose();
    }
}
