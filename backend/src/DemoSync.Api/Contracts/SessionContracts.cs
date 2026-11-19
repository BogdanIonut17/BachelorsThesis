using System.ComponentModel.DataAnnotations;
using DemoSync.Api.Models;

namespace DemoSync.Api.Contracts;

public sealed class UploadSessionRequest
{
    [Required]
    public IFormFile VideoFile { get; set; } = null!;

    [Required]
    public IFormFile LogFile { get; set; } = null!;

    public double? ManualReferenceOffsetSeconds { get; set; }
}

public sealed class SetReferencePointRequest
{
    [Required]
    public double? ManualReferenceOffsetSeconds { get; set; }
}

public sealed record ProcessingSessionDto(
    Guid Id,
    string VideoFileName,
    string LogFileName,
    SessionStatus Status,
    int ProgressPercent,
    double? ManualReferenceOffsetSeconds,
    bool IsAutoSyncFeasible,
    string? SyncWarningMessage,
    int ParsedEventCount,
    IReadOnlyList<SensitiveMatch> DetectedSecrets,
    DateTimeOffset CreatedAt)
{
    public static ProcessingSessionDto From(ProcessingSession session) => new(
        session.Id,
        session.VideoFileName,
        session.LogFileName,
        session.Status,
        session.ProgressPercent,
        session.ManualReferenceOffsetSeconds,
        session.IsAutoSyncFeasible,
        session.SyncWarningMessage,
        session.ParsedEventCount,
        session.DetectedSecrets.ToList(),
        session.CreatedAt);
}
