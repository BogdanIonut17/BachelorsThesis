using System.Text.Json;
using DemoSync.Api.Models;
using DemoSync.Api.Services;
using Xunit;

namespace DemoSync.Api.Tests;

public class LogEventExtractionTests
{
    private readonly LogIngestionService _service = new();

    private static string CastWith(params string[] texts)
    {
        var lines = new List<string> { "{\"version\":2,\"timestamp\":1700000000}" };
        for (var i = 0; i < texts.Length; i++)
        {
            lines.Add($"[{i + 1}.5,\"o\",{JsonSerializer.Serialize(texts[i])}]");
        }

        return string.Join('\n', lines) + "\n";
    }

    [Fact]
    public void Analyze_StripsAnsiEscapeCodesFromEventText()
    {
        var content = CastWith(
            "\u001b[1;32muser@host\u001b[0m:~$ \u001b[33mdocker build .\u001b[0m\r\n",
            "\u001b[2K\u001b[?25lStep 1/3 : FROM alpine\u001b[?25h\r\n");

        var result = _service.Analyze(content, "demo.cast", null);

        Assert.Equal(2, result.Events.Count);
        Assert.All(result.Events, e => Assert.DoesNotContain('\u001b', e.Text));
        Assert.Equal("user@host:~$ docker build .", result.Events[0].Text);
        Assert.Equal("Step 1/3 : FROM alpine", result.Events[1].Text);
        Assert.True(result.Events[0].IsAnchorable);
    }

    [Fact]
    public void Analyze_AnchorsOnAnsiColoredPrompt()
    {
        var content = CastWith(
            "\u001b[1;32m$\u001b[0m \u001b[1mnpm run build\u001b[0m\r\n",
            "\u001b[32mCompiled successfully\u001b[0m\r\n");

        var result = _service.Analyze(content, "demo.cast", null);

        Assert.True(result.IsAutoSyncFeasible);
        Assert.Equal(2, result.AnchorableEventCount);
    }

    [Theory]
    [InlineData("$ ls -la\r\n", TechnicalEventCategory.Command)]
    [InlineData("user@host:~/app$ dotnet test\r\n", TechnicalEventCategory.Command)]
    [InlineData("Build succeeded.\r\n", TechnicalEventCategory.Build)]
    [InlineData("Compiled successfully in 4s\r\n", TechnicalEventCategory.Build)]
    [InlineData("Container web  Started\r\n", TechnicalEventCategory.Container)]
    [InlineData("Step 2/5 : COPY . .\r\n", TechnicalEventCategory.Container)]
    [InlineData("error CS1002: ; expected\r\n", TechnicalEventCategory.Error)]
    [InlineData("Unhandled exception: boom\r\n", TechnicalEventCategory.Error)]
    [InlineData("process exited with code 1\r\n", TechnicalEventCategory.ExitCode)]
    [InlineData("hello world\r\n", TechnicalEventCategory.Output)]
    public void Analyze_ClassifiesEventsDeterministically(string text, TechnicalEventCategory expected)
    {
        var result = _service.Analyze(CastWith(text), "demo.cast", null);

        var technicalEvent = Assert.Single(result.Events);
        Assert.Equal(expected, technicalEvent.Category);
    }

    [Fact]
    public void Analyze_ExitEventTypeIsClassifiedAsExitCodeAndAnchorable()
    {
        const string content =
            "{\"version\":2,\"timestamp\":1700000000}\n" +
            "[1.0,\"o\",\"$ make\"]\n" +
            "[2.0,\"x\",\"0\"]\n";

        var result = _service.Analyze(content, "demo.cast", null);

        Assert.Equal(2, result.Events.Count);
        Assert.Equal(TechnicalEventCategory.ExitCode, result.Events[1].Category);
        Assert.True(result.Events[1].IsAnchorable);
        Assert.Equal("x", result.Events[1].EventType);
    }

    [Fact]
    public void Analyze_AssignsSequentialIndexesAndCastTimestamps()
    {
        var result = _service.Analyze(TestFiles.ValidCast, "demo.cast", null);

        Assert.Equal([0, 1, 2], result.Events.Select(e => e.Index));
        Assert.Equal([0.5, 2.0, 2.5], result.Events.Select(e => e.TimestampSeconds));
        Assert.All(result.Events, e => Assert.Equal("o", e.EventType));
        Assert.Equal(TechnicalEventCategory.Command, result.Events[0].Category);
    }

    [Fact]
    public void Analyze_SkipsResizeAndEmptyEvents()
    {
        const string content =
            "{\"version\":2,\"timestamp\":1700000000}\n" +
            "[0.1,\"r\",\"120x40\"]\n" +
            "[0.2,\"o\",\"\\u001b[0m\"]\n" +
            "[0.3,\"o\",\"$ ls\"]\n";

        var result = _service.Analyze(content, "demo.cast", null);

        var technicalEvent = Assert.Single(result.Events);
        Assert.Equal("$ ls", technicalEvent.Text);
        Assert.Equal(3, result.ParsedEventCount);
    }

    [Fact]
    public void Analyze_JsonlTimestampsAreExposedRelativeToFirstEvent()
    {
        const string content =
            "{\"timestamp\":\"2025-01-01T10:00:00Z\",\"event\":\"command\",\"output\":\"dotnet test\"}\n" +
            "{\"timestamp\":\"2025-01-01T10:00:05Z\",\"event\":\"build\",\"output\":\"\\u001b[32mBuild succeeded.\\u001b[0m\"}\n" +
            "{\"timestamp\":\"2025-01-01T10:00:06.5Z\",\"event\":\"exit\",\"output\":\"exit code 0\"}\n";

        var result = _service.Analyze(content, "demo.jsonl", null);

        Assert.Equal([0.0, 5.0, 6.5], result.Events.Select(e => e.TimestampSeconds));
        Assert.Equal(
            [TechnicalEventCategory.Command, TechnicalEventCategory.Build, TechnicalEventCategory.ExitCode],
            result.Events.Select(e => e.Category));
        Assert.Equal("Build succeeded.", result.Events[1].Text);
        Assert.Equal("build", result.Events[1].EventType);
    }

    [Fact]
    public void Analyze_MaskedSecretsFromSanitizedContentNeverAppearInEvents()
    {
        const string secret = "sk-abcdefghijklmnopqrstuvwxyz123456";
        var raw = CastWith("$ export OPENAI_API_KEY=" + secret + "\r\n", "Build succeeded.\r\n");

        var scan = new SensitiveDataScanner().Scan(raw);
        var result = _service.Analyze(scan.SanitizedContent, "demo.cast", null);

        Assert.All(result.Events, e => Assert.DoesNotContain(secret, e.Text));
        Assert.Contains(result.Events, e => e.Text.Contains("[REDACTED:OpenAiApiKey]"));
    }
}
