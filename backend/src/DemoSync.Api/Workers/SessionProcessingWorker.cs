using DemoSync.Api.Models;
using DemoSync.Api.Services;

namespace DemoSync.Api.Workers;

public sealed class SessionProcessingWorker : BackgroundService
{
    private readonly IBackgroundJobQueue _queue;
    private readonly ISessionRepository _repository;
    private readonly ISessionStorage _storage;
    private readonly IMediaExtractor _extractor;
    private readonly ILogger<SessionProcessingWorker> _logger;

    public SessionProcessingWorker(
        IBackgroundJobQueue queue,
        ISessionRepository repository,
        ISessionStorage storage,
        IMediaExtractor extractor,
        ILogger<SessionProcessingWorker> logger)
    {
        _queue = queue;
        _repository = repository;
        _storage = storage;
        _extractor = extractor;
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

                var message = exception is MediaExtractionException
                    ? exception.Message
                    : "An unexpected error occurred while processing the session.";

                _repository.Update(sessionId, session =>
                {
                    session.Status = SessionStatus.Failed;
                    session.ProcessingStage = "Failed";
                    session.ErrorMessage = message;
                });
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
            session.ProcessingStage = "Starting";
            session.ErrorMessage = null;
            started = true;
        });

        if (snapshot is null || !started)
        {
            return;
        }

        var videoPath = _storage.GetVideoPath(sessionId)
            ?? throw new MediaExtractionException("The uploaded video file could not be found on disk.");
        var sessionDirectory = _storage.GetSessionDirectory(sessionId);

        var output = await _extractor.ExtractAsync(
            sessionId,
            videoPath,
            sessionDirectory,
            (percent, stage) =>
            {
                ReportProgress(sessionId, percent, stage);
                return Task.CompletedTask;
            },
            cancellationToken);

        ReportProgress(sessionId, 95, "Saving artifacts");

        var events = _repository.Get(sessionId)?.TechnicalEvents ?? [];
        var artifacts = new SessionArtifacts(output.Video, output.Frames, output.AudioIntervals, events);
        await _storage.SaveArtifactsAsync(sessionId, artifacts, cancellationToken);

        _repository.Update(sessionId, session =>
        {
            session.Artifacts = artifacts;
            session.ProgressPercent = 100;
            session.ProcessingStage = "Completed";
            session.Status = SessionStatus.ReadyForReview;
        });
    }

    private void ReportProgress(Guid sessionId, int percent, string stage) =>
        _repository.Update(sessionId, session =>
        {
            session.ProgressPercent = percent;
            session.ProcessingStage = stage;
        });
}
