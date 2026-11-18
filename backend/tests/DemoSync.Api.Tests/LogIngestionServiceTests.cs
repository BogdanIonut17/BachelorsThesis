using DemoSync.Api.Services;
using Xunit;

namespace DemoSync.Api.Tests;

public class LogIngestionServiceTests
{
    private readonly LogIngestionService _service = new();

    [Fact]
    public void Analyze_ParsesValidCastAndMarksSyncFeasible()
    {
        var result = _service.Analyze(TestFiles.ValidCast, "demo.cast", null);

        Assert.Equal(LogFormat.AsciinemaCast, result.Format);
        Assert.Equal(3, result.ParsedEventCount);
        Assert.True(result.AnchorableEventCount >= 2);
        Assert.True(result.HasReliableTimestamps);
        Assert.True(result.IsAutoSyncFeasible);
        Assert.False(result.RequiresManualReference);
        Assert.Null(result.SyncWarningMessage);
    }

    [Fact]
    public void Analyze_CastWithoutTimestamps_RequiresManualReference()
    {
        var result = _service.Analyze(TestFiles.CastWithoutTimestamps, "demo.cast", null);

        Assert.False(result.IsAutoSyncFeasible);
        Assert.True(result.RequiresManualReference);
        Assert.False(result.HasReliableTimestamps);
        Assert.Contains("Timestamps are missing or corrupted", result.SyncWarningMessage);
    }

    [Fact]
    public void Analyze_JsonlWithoutTimestamps_RequiresManualReference()
    {
        var result = _service.Analyze(TestFiles.JsonlWithoutTimestamps, "demo.jsonl", null);

        Assert.Equal(LogFormat.StructuredJsonl, result.Format);
        Assert.Equal(2, result.ParsedEventCount);
        Assert.False(result.IsAutoSyncFeasible);
        Assert.True(result.RequiresManualReference);
    }

    [Fact]
    public void Analyze_ManualOffsetOverridesRequirementButKeepsAutoSyncInfeasible()
    {
        var result = _service.Analyze(TestFiles.JsonlWithoutTimestamps, "demo.jsonl", 3.5);

        Assert.False(result.IsAutoSyncFeasible);
        Assert.False(result.RequiresManualReference);
        Assert.NotNull(result.SyncWarningMessage);
    }

    [Fact]
    public void Analyze_NonMonotonicTimestamps_AreRejected()
    {
        const string content =
            "{\"version\":2,\"timestamp\":1700000000}\n" +
            "[5.0,\"o\",\"$ docker build .\"]\n" +
            "[1.0,\"o\",\"Successfully built abc\"]\n";

        var result = _service.Analyze(content, "demo.cast", null);

        Assert.False(result.IsAutoSyncFeasible);
        Assert.True(result.RequiresManualReference);
        Assert.Contains("not monotonic", result.SyncWarningMessage);
    }

    [Fact]
    public void Analyze_FewerThanTwoAnchorableEvents_RequiresManualReference()
    {
        const string content =
            "{\"version\":2,\"timestamp\":1700000000}\n" +
            "[0.1,\"o\",\"hello\"]\n" +
            "[0.2,\"o\",\"world\"]\n" +
            "[0.3,\"o\",\"$ ls -la\"]\n";

        var result = _service.Analyze(content, "demo.cast", null);

        Assert.True(result.HasReliableTimestamps);
        Assert.Equal(1, result.AnchorableEventCount);
        Assert.False(result.IsAutoSyncFeasible);
        Assert.True(result.RequiresManualReference);
        Assert.Contains("anchorable", result.SyncWarningMessage);
    }

    [Fact]
    public void Analyze_CastWithWrongVersion_RequiresManualReference()
    {
        var content = TestFiles.ValidCast.Replace("\"version\":2", "\"version\":1");

        var result = _service.Analyze(content, "demo.cast", null);

        Assert.False(result.IsAutoSyncFeasible);
        Assert.True(result.RequiresManualReference);
    }

    [Fact]
    public void Analyze_JsonlWithNumericAndIsoTimestamps_IsFeasible()
    {
        const string content =
            "{\"timestamp\":\"2025-01-01T10:00:00Z\",\"event\":\"command\",\"output\":\"dotnet test\"}\n" +
            "{\"timestamp\":\"2025-01-01T10:00:05Z\",\"event\":\"output\",\"output\":\"Build succeeded.\"}\n" +
            "{\"timestamp\":\"2025-01-01T10:00:06Z\",\"event\":\"exit\",\"output\":\"exit code 0\"}\n";

        var result = _service.Analyze(content, "demo.jsonl", null);

        Assert.True(result.IsAutoSyncFeasible);
        Assert.Equal(3, result.ParsedEventCount);

        var numeric =
            "{\"timestamp\":1.5,\"event\":\"command\",\"output\":\"npm run build\"}\n" +
            "{\"timestamp\":2.5,\"event\":\"exit\",\"output\":\"exit code 0\"}\n";

        Assert.True(_service.Analyze(numeric, "demo.jsonl", null).IsAutoSyncFeasible);
    }

    [Fact]
    public void Analyze_LogFileWithCastHeader_IsDetectedAsCast()
    {
        var result = _service.Analyze(TestFiles.ValidCast, "demo.log", null);

        Assert.Equal(LogFormat.AsciinemaCast, result.Format);
        Assert.True(result.IsAutoSyncFeasible);
    }

    [Fact]
    public void Analyze_PlainTextLog_RequiresManualReference()
    {
        var result = _service.Analyze("just some plain text\nanother line\n", "demo.log", null);

        Assert.False(result.IsAutoSyncFeasible);
        Assert.True(result.RequiresManualReference);
    }
}
