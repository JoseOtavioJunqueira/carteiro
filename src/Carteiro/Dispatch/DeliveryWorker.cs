using Microsoft.Extensions.Options;

namespace Carteiro.Dispatch;

/// <summary>
/// The polling loop. All the interesting logic is in <see cref="DeliveryProcessor"/>; this class
/// only decides when to call it.
/// </summary>
internal sealed partial class DeliveryWorker(
    DeliveryProcessor processor,
    IOptions<CarteiroOptions> options,
    ILogger<DeliveryWorker> logger) : BackgroundService
{
    private readonly WorkerOptions _options = options.Value.Worker;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            Log.WorkerDisabled(logger);
            return;
        }

        Log.WorkerStarted(logger, _options.BatchSize, _options.PollInterval);

        while (!stoppingToken.IsCancellationRequested)
        {
            var claimed = 0;

            try
            {
                claimed = await processor.RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Most likely the database is unreachable. Keep polling; it may come back.
                Log.RoundFailed(logger, ex);
            }

            // A full batch means there is probably more waiting, so go again immediately.
            // Anything less and the queue is drained: sleep.
            if (claimed < _options.BatchSize)
            {
                try
                {
                    await Task.Delay(_options.PollInterval, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        Log.WorkerStopped(logger);
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Information, Message = "Delivery worker is disabled in this instance")]
        public static partial void WorkerDisabled(ILogger logger);

        [LoggerMessage(Level = LogLevel.Information, Message = "Delivery worker started (batch size {BatchSize}, poll interval {PollInterval})")]
        public static partial void WorkerStarted(ILogger logger, int batchSize, TimeSpan pollInterval);

        [LoggerMessage(Level = LogLevel.Information, Message = "Delivery worker stopped")]
        public static partial void WorkerStopped(ILogger logger);

        [LoggerMessage(Level = LogLevel.Error, Message = "Delivery round failed")]
        public static partial void RoundFailed(ILogger logger, Exception exception);
    }
}
