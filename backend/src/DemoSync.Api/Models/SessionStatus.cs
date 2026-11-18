namespace DemoSync.Api.Models;

public enum SessionStatus
{
    Uploaded,
    Queued,
    Processing,
    RequiresManualReference,
    ReadyForReview,
    Failed
}
