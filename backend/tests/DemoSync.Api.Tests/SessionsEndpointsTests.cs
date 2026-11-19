using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using DemoSync.Api.Contracts;
using DemoSync.Api.Models;
using Xunit;

namespace DemoSync.Api.Tests;

public class SessionsEndpointsTests : IClassFixture<ApiFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly ApiFactory _factory;
    private readonly HttpClient _client;

    public SessionsEndpointsTests(ApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Upload_WithValidFiles_Returns202AndEventuallyReadyForReview()
    {
        using var content = BuildForm("demo.mp4", "video/mp4", "demo.cast", "application/octet-stream", TestFiles.ValidCast);

        var response = await _client.PostAsync("/api/sessions/upload", content);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var dto = await ReadDto(response);
        Assert.Equal(SessionStatus.Queued, dto.Status);
        Assert.True(dto.IsAutoSyncFeasible);
        Assert.Equal(3, dto.ParsedEventCount);
        Assert.Equal($"/api/sessions/{dto.Id}/status", response.Headers.Location?.OriginalString);

        var final = await WaitForStatus(dto.Id, SessionStatus.ReadyForReview);
        Assert.Equal(100, final.ProgressPercent);

        var sessionDirectory = Path.Combine(_factory.StorageRoot, "Sessions", dto.Id.ToString("D"));
        Assert.True(File.Exists(Path.Combine(sessionDirectory, "sanitized.log")));
        Assert.True(File.Exists(Path.Combine(sessionDirectory, "recording.mp4")));
    }

    [Fact]
    public async Task Upload_WithSecretsInLog_ReportsMaskedMatchesAndSanitizesCopy()
    {
        const string secret = "sk-abcdefghijklmnopqrstuvwxyz123456";
        var log = TestFiles.ValidCast + $"[3.0,\"o\",\"export KEY={secret}\\r\\n\"]\n";
        using var content = BuildForm("demo.webm", "video/webm", "demo.cast", "application/octet-stream", log);

        var response = await _client.PostAsync("/api/sessions/upload", content);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var dto = await ReadDto(response);
        var match = Assert.Single(dto.DetectedSecrets);
        Assert.Equal(5, match.LineNumber);
        Assert.DoesNotContain(secret, match.MaskedPreview);

        var sanitized = await File.ReadAllTextAsync(
            Path.Combine(_factory.StorageRoot, "Sessions", dto.Id.ToString("D"), "sanitized.log"));
        Assert.DoesNotContain(secret, sanitized);
    }

    [Fact]
    public async Task Upload_WithUnsupportedVideoExtension_Returns400()
    {
        using var content = BuildForm("demo.avi", "video/mp4", "demo.cast", "application/octet-stream", TestFiles.ValidCast);

        var response = await _client.PostAsync("/api/sessions/upload", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Upload_WithEmptyLog_Returns400()
    {
        using var content = BuildForm("demo.mp4", "video/mp4", "demo.log", "text/plain", string.Empty);

        var response = await _client.PostAsync("/api/sessions/upload", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Upload_WithoutLogFile_Returns400()
    {
        using var content = new MultipartFormDataContent();
        content.Add(FileContent("video"u8.ToArray(), "video/mp4"), "videoFile", "demo.mp4");

        var response = await _client.PostAsync("/api/sessions/upload", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Upload_WithUnsyncableLog_RequiresManualReferenceUntilPatched()
    {
        using var content = BuildForm("demo.mp4", "video/mp4", "demo.jsonl", "application/x-ndjson", TestFiles.JsonlWithoutTimestamps);

        var response = await _client.PostAsync("/api/sessions/upload", content);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var dto = await ReadDto(response);
        Assert.Equal(SessionStatus.RequiresManualReference, dto.Status);
        Assert.False(dto.IsAutoSyncFeasible);
        Assert.NotNull(dto.SyncWarningMessage);

        await Task.Delay(100);
        Assert.Equal(SessionStatus.RequiresManualReference, (await GetStatus(dto.Id)).Status);

        var patch = await _client.PatchAsJsonAsync(
            $"/api/sessions/{dto.Id}/reference-point",
            new { manualReferenceOffsetSeconds = 4.25 });

        Assert.Equal(HttpStatusCode.OK, patch.StatusCode);
        var patched = await ReadDto(patch);
        Assert.Equal(SessionStatus.Queued, patched.Status);
        Assert.Equal(4.25, patched.ManualReferenceOffsetSeconds);

        await WaitForStatus(dto.Id, SessionStatus.ReadyForReview);
    }

    [Fact]
    public async Task Upload_WithUnsyncableLogAndManualOffset_IsQueuedImmediately()
    {
        using var content = BuildForm("demo.mp4", "video/mp4", "demo.jsonl", "application/x-ndjson", TestFiles.JsonlWithoutTimestamps);
        content.Add(new StringContent("2.5"), "manualReferenceOffsetSeconds");

        var response = await _client.PostAsync("/api/sessions/upload", content);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var dto = await ReadDto(response);
        Assert.Equal(SessionStatus.Queued, dto.Status);
        Assert.Equal(2.5, dto.ManualReferenceOffsetSeconds);

        await WaitForStatus(dto.Id, SessionStatus.ReadyForReview);
    }

    [Fact]
    public async Task Status_ForUnknownSession_Returns404()
    {
        var response = await _client.GetAsync($"/api/sessions/{Guid.NewGuid()}/status");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ReferencePoint_ForUnknownSession_Returns404()
    {
        var response = await _client.PatchAsJsonAsync(
            $"/api/sessions/{Guid.NewGuid()}/reference-point",
            new { manualReferenceOffsetSeconds = 1.0 });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ReferencePoint_WithoutOffset_Returns400()
    {
        var response = await _client.PatchAsJsonAsync($"/api/sessions/{Guid.NewGuid()}/reference-point", new { });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private static MultipartFormDataContent BuildForm(
        string videoName,
        string videoMime,
        string logName,
        string logMime,
        string logContent)
    {
        var form = new MultipartFormDataContent();
        form.Add(FileContent("fake-video-bytes"u8.ToArray(), videoMime), "videoFile", videoName);
        form.Add(FileContent(System.Text.Encoding.UTF8.GetBytes(logContent), logMime), "logFile", logName);
        return form;
    }

    private static ByteArrayContent FileContent(byte[] bytes, string mime)
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(mime);
        return content;
    }

    private static async Task<ProcessingSessionDto> ReadDto(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<ProcessingSessionDto>(JsonOptions))!;

    private async Task<ProcessingSessionDto> GetStatus(Guid id) =>
        (await _client.GetFromJsonAsync<ProcessingSessionDto>($"/api/sessions/{id}/status", JsonOptions))!;

    private async Task<ProcessingSessionDto> WaitForStatus(Guid id, SessionStatus expected)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        ProcessingSessionDto dto;
        do
        {
            dto = await GetStatus(id);
            if (dto.Status == expected)
            {
                return dto;
            }

            await Task.Delay(25);
        }
        while (DateTime.UtcNow < deadline);

        throw new Xunit.Sdk.XunitException($"Session {id} did not reach {expected}; last status was {dto.Status}.");
    }
}
