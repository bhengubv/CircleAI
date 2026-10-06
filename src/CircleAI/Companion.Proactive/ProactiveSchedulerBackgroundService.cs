// ProactiveSchedulerBackgroundService.cs
//
// (3.2.0) IHostedService that ticks the scheduler every minute. Lifted
// from CircleUp.Web's WorkflowSchedulerBackgroundService — same shape,
// no host-specific assumptions.

using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CircleAI.Companion.Proactive;

/// <summary>
/// (3.2.0) Hosted service that calls
/// <see cref="IProactiveScheduler.RefreshAsync"/> once at startup, then
/// loops on a one-minute timer calling <see cref="IProactiveScheduler.TickAsync"/>.
/// Refresh interval is configurable through
/// <see cref="ProactiveSchedulerOptions"/>.
/// </summary>
public sealed class ProactiveSchedulerBackgroundService : BackgroundService
{
    private readonly IProactiveScheduler _scheduler;
    private readonly ILogger<ProactiveSchedulerBackgroundService> _logger;
    private readonly ProactiveSchedulerOptions _options;

    public ProactiveSchedulerBackgroundService(
        IProactiveScheduler scheduler,
        ILogger<ProactiveSchedulerBackgroundService> logger,
        ProactiveSchedulerOptions? options = null)
    {
        _scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
        _logger    = logger    ?? throw new ArgumentNullException(nameof(logger));
        _options   = options ?? new ProactiveSchedulerOptions();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Initial refresh — populate the scheduler before the first tick.
        try
        {
            await _scheduler.RefreshAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Initial proactive scheduler refresh failed.");
        }

        var lastRefresh = DateTimeOffset.UtcNow;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_options.TickInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            var now = DateTimeOffset.UtcNow;
            try
            {
                if ((now - lastRefresh) >= _options.RefreshInterval)
                {
                    await _scheduler.RefreshAsync(stoppingToken).ConfigureAwait(false);
                    lastRefresh = now;
                }
                await _scheduler.TickAsync(now, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Proactive scheduler tick failed; will retry on next interval.");
            }
        }
    }
}

/// <summary>(3.2.0) Tunable knobs for the background tick loop.</summary>
public sealed class ProactiveSchedulerOptions
{
    /// <summary>How often the scheduler ticks. Default 1 minute.</summary>
    public TimeSpan TickInterval { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>How often the source is re-snapshotted. Default 5 minutes.</summary>
    public TimeSpan RefreshInterval { get; init; } = TimeSpan.FromMinutes(5);
}
