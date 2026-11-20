namespace DemoSync.Api.Models;

public sealed record VideoMetadata(double DurationSeconds, int Width, int Height, double FrameRate, bool HasAudio);

public sealed record ExtractedFrame(int Index, double TimestampSeconds, string FileName);

public sealed record AudioActivityInterval(double StartSeconds, double EndSeconds, bool IsActive);

public enum TechnicalEventCategory
{
    Command,
    Build,
    Container,
    Error,
    ExitCode,
    Output
}

public sealed record TechnicalEvent(
    int Index,
    double TimestampSeconds,
    string EventType,
    TechnicalEventCategory Category,
    string Text,
    bool IsAnchorable);

public sealed record SessionArtifacts(
    VideoMetadata Video,
    IReadOnlyList<ExtractedFrame> Frames,
    IReadOnlyList<AudioActivityInterval> AudioIntervals,
    IReadOnlyList<TechnicalEvent> TechnicalEvents);
