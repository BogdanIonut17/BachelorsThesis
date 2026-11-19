using System.Text;
using DemoSync.Api.Models;

namespace DemoSync.Api.Services;

public interface ISessionUploadService
{
    Task<ProcessingSession> CreateSessionAsync(
        IFormFile videoFile,
        IFormFile logFile,
        double? manualReferenceOffsetSeconds,
        CancellationToken cancellationToken);
}

public sealed class SessionUploadService : ISessionUploadService
{
    private readonly ISessionStorage _storage;
    private readonly ISessionRepository _repository;
    private readonly ILogIngestionService _ingestion;
    private readonly ISensitiveDataScanner _scanner;
    private readonly IBackgroundJobQueue _queue;

    public SessionUploadService(
        ISessionStorage storage,
        ISessionRepository repository,
        ILogIngestionService ingestion,
        ISensitiveDataScanner scanner,
        IBackgroundJobQueue queue)
    {
        _storage = storage;
        _repository = repository;
        _ingestion = ingestion;
        _scanner = scanner;
        _queue = queue;
    }

    public async Task<ProcessingSession> CreateSessionAsync(
        IFormFile videoFile,
        IFormFile logFile,
        double? manualReferenceOffsetSeconds,
        CancellationToken cancellationToken)
    {
        var sessionId = Guid.NewGuid();

        try
        {
            var stored = await _storage.SaveUploadAsync(sessionId, videoFile, logFile, cancellationToken);
            var logText = await ReadTextAsync(logFile, cancellationToken);

            var scan = _scanner.Scan(logText);
            await _storage.SaveSanitizedLogAsync(sessionId, scan.SanitizedContent, cancellationToken);

            var analysis = _ingestion.Analyze(logText, stored.LogFileName, manualReferenceOffsetSeconds);

            var session = new ProcessingSession
            {
                Id = sessionId,
                VideoFileName = stored.VideoFileName,
                LogFileName = stored.LogFileName,
                Status = analysis.RequiresManualReference ? SessionStatus.RequiresManualReference : SessionStatus.Queued,
                ManualReferenceOffsetSeconds = manualReferenceOffsetSeconds,
                IsAutoSyncFeasible = analysis.IsAutoSyncFeasible,
                SyncWarningMessage = analysis.SyncWarningMessage,
                ParsedEventCount = analysis.ParsedEventCount,
                DetectedSecrets = [.. scan.Matches]
            };

            _repository.Add(session);

            if (session.Status == SessionStatus.Queued)
            {
                await _queue.EnqueueAsync(session.Id, cancellationToken);
            }

            return session.Clone();
        }
        catch
        {
            _storage.Delete(sessionId);
            throw;
        }
    }

    private static async Task<string> ReadTextAsync(IFormFile file, CancellationToken cancellationToken)
    {
        await using var stream = file.OpenReadStream();
        using var reader = new StreamReader(stream, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true);
        return await reader.ReadToEndAsync(cancellationToken);
    }
}
