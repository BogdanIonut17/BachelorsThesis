using DemoSync.Api.Services;
using Xunit;

namespace DemoSync.Api.Tests;

public class UploadValidatorTests
{
    private readonly UploadValidator _validator = new();

    [Theory]
    [InlineData("demo.mp4", "video/mp4")]
    [InlineData("demo.mkv", "video/x-matroska")]
    [InlineData("DEMO.WEBM", "video/webm")]
    public void ValidateVideo_AcceptsSupportedFiles(string name, string mime)
    {
        var result = _validator.ValidateVideo(TestFiles.Create(name, new byte[] { 1, 2, 3 }, mime));

        Assert.True(result.IsValid);
    }

    [Theory]
    [InlineData("demo.avi")]
    [InlineData("demo.exe")]
    [InlineData("demo")]
    public void ValidateVideo_RejectsInvalidExtensions(string name)
    {
        var result = _validator.ValidateVideo(TestFiles.Create(name, new byte[] { 1 }, "video/mp4"));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("extension"));
    }

    [Fact]
    public void ValidateVideo_RejectsEmptyFile()
    {
        var result = _validator.ValidateVideo(TestFiles.Create("demo.mp4", Array.Empty<byte>(), "video/mp4"));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("empty"));
    }

    [Fact]
    public void ValidateVideo_RejectsNull()
    {
        Assert.False(_validator.ValidateVideo(null).IsValid);
    }

    [Fact]
    public void ValidateVideo_RejectsUnsupportedMimeType()
    {
        var result = _validator.ValidateVideo(TestFiles.Create("demo.mp4", new byte[] { 1 }, "text/html"));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("MIME"));
    }

    [Fact]
    public void ValidateVideo_RejectsFilesOverFiveHundredMegabytes()
    {
        var file = TestFiles.CreateWithReportedLength("demo.mp4", UploadLimits.MaxVideoBytes + 1, "video/mp4");

        var result = _validator.ValidateVideo(file);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("500 MB"));
    }

    [Theory]
    [InlineData("session.cast", "application/octet-stream")]
    [InlineData("session.jsonl", "application/x-ndjson")]
    [InlineData("session.log", "text/plain")]
    public async Task ValidateLogAsync_AcceptsSupportedFiles(string name, string mime)
    {
        var result = await _validator.ValidateLogAsync(TestFiles.Create(name, "hello world", mime));

        Assert.True(result.IsValid);
    }

    [Theory]
    [InlineData("session.txt")]
    [InlineData("session.json")]
    [InlineData("session.cast.exe")]
    public async Task ValidateLogAsync_RejectsInvalidExtensions(string name)
    {
        var result = await _validator.ValidateLogAsync(TestFiles.Create(name, "hello", "text/plain"));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("extension"));
    }

    [Fact]
    public async Task ValidateLogAsync_RejectsEmptyFile()
    {
        var result = await _validator.ValidateLogAsync(TestFiles.Create("session.log", string.Empty, "text/plain"));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("empty"));
    }

    [Fact]
    public async Task ValidateLogAsync_RejectsNonUtf8Content()
    {
        var invalid = new byte[] { 0x68, 0x69, 0xFF, 0xFE, 0xFA };

        var result = await _validator.ValidateLogAsync(TestFiles.Create("session.log", invalid, "text/plain"));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("UTF-8"));
    }

    [Fact]
    public async Task ValidateLogAsync_RejectsFilesOverTwentyFiveMegabytes()
    {
        var file = TestFiles.CreateWithReportedLength("session.log", UploadLimits.MaxLogBytes + 1, "text/plain");

        var result = await _validator.ValidateLogAsync(file);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("25 MB"));
    }
}
