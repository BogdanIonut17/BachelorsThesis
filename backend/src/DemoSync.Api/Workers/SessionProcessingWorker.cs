using DemoSync.Api.Models;
using DemoSync.Api.Services;
using Microsoft.Extensions.Options;

namespace DemoSync.Api.Workers;

public sealed class ProcessingOptions
{
    public int StepDelayMilliseconds { get; set; } = 1500;
}

public sealed class SessionProcessingWorker : BackgroundService
{
    private static readonly int[] ProgressSteps = [25, 60, 100];

    private readonly IBackgroundJobQueue _queue;
    private readonly ISessionRepository _repository;
    private readonly ProcessingOptions _options;
    private readonly ILogger<SessionProcessingWorker> _logger;

    public SessionProcessingWorker(
        IBackgroundJobQueue queue,
        ISessionRepository repository,
        IOptions<ProcessingOptions> options,
        ILogger<SessionProcessingWorker> logger)
    {
        _queue = queue;
        _repository = repository;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            Guid sessionId;
            try
            {
                sessionId = await _queue.DequeueAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            try
            {
                await ProcessAsync(sessionId, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Processing of session {SessionId} failed.", sessionId);
                _repository.Update(sessionId, s => s.Status = SessionStatus.Failed);
            }
        }
    }

    public async Task ProcessAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        var started = false;
        var snapshot = _repository.Update(sessionId, session =>
        {
            if (session.Status != SessionStatus.Queued)
            {
                return;
            }

            if (!session.HasSyncAnchor)
            {
                session.Status = SessionStatus.RequiresManualReference;
                return;
            }

            session.Status = SessionStatus.Processing;
            session.ProgressPercent = 0;
            started = true;
        });

        if (snapshot is null || !started)
        {
            return;
        }

        foreach (var progress in ProgressSteps)
        {
            await Task.Delay(_options.StepDelayMilliseconds, cancellationToken);

            var isLast = progress == ProgressSteps[^1];
            _repository.Update(sessionId, session =>
            {
                session.ProgressPercent = progress;
                if (isLast)
                {
                    session.Status = SessionStatus.ReadyForReview;
                }
            });
        }
    }
}
