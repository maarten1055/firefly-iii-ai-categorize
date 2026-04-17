using System.Threading.Channels;

namespace FireflyCategorizer.Services;

public sealed class JobQueue : BackgroundService
{
    private readonly Channel<Func<CancellationToken, Task>> _channel = Channel.CreateUnbounded<Func<CancellationToken, Task>>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false,
    });
    private readonly ILogger<JobQueue> _logger;

    public JobQueue(ILogger<JobQueue> logger)
    {
        _logger = logger;
    }

    public ValueTask EnqueueAsync(Func<CancellationToken, Task> workItem, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workItem);
        return _channel.Writer.WriteAsync(workItem, cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var workItem in _channel.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                await workItem(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Queued job execution failed.");
            }
        }
    }
}