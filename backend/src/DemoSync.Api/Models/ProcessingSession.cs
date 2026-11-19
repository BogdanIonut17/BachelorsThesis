namespace DemoSync.Api.Models;

public sealed class ProcessingSession
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public string VideoFileName { get; set; } = string.Empty;

    public string LogFileName { get; set; } = string.Empty;

    public SessionStatus Status { get; set; } = SessionStatus.Uploaded;

    public int ProgressPercent { get; set; }

    public double? ManualReferenceOffsetSeconds { get; set; }

    public bool IsAutoSyncFeasible { get; set; }

    public string? SyncWarningMessage { get; set; }

    public int ParsedEventCount { get; set; }

    public List<SensitiveMatch> DetectedSecrets { get; set; } = [];

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public bool HasSyncAnchor => IsAutoSyncFeasible || ManualReferenceOffsetSeconds.HasValue;

    public ProcessingSession Clone() => new()
    {
        Id = Id,
        VideoFileName = VideoFileName,
        LogFileName = LogFileName,
        Status = Status,
        ProgressPercent = ProgressPercent,
        ManualReferenceOffsetSeconds = ManualReferenceOffsetSeconds,
        IsAutoSyncFeasible = IsAutoSyncFeasible,
        SyncWarningMessage = SyncWarningMessage,
        ParsedEventCount = ParsedEventCount,
        DetectedSecrets = [.. DetectedSecrets],
        CreatedAt = CreatedAt
    };
}
