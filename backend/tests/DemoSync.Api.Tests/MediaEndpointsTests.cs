using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using DemoSync.Api.Contracts;
using DemoSync.Api.Models;
using Xunit;

namespace DemoSync.Api.Tests;

internal static class MediaTestHelpers
{
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public static async Task<ProcessingSessionDto> UploadAsync(
        HttpClient client,
        string log = TestFiles.ValidCast,
        string logName = "demo.cast",
        double? manualOffset = null)
    {
        using var form = new MultipartFormDataContent();
        form.Add(Bytes("fake-video-bytes"u8.ToArray(), "video/mp4"), "videoFile", "demo.mp4");
        form.Add(Bytes(System.Text.Encoding.UTF8.GetBytes(log), "application/octet-stream"), "logFile", logName);
        if (manualOffset is { } offset)
        {
            form.Add(new StringContent(offset.ToString(System.Globalization.CultureInfo.InvariantCulture)), "manualReferenceOffsetSeconds");
        }

        var response = await client.PostAsync("/api/sessions/upload", form);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ProcessingSessionDto>(JsonOptions))!;
    }

    public static async Task<ProcessingSessionDto> WaitForStatusAsync(HttpClient client, Guid id, SessionStatus expected)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        ProcessingSessionDto dto;
        do
        {
            dto = (await client.GetFromJsonAsync<ProcessingSessionDto>($"/api/sessions/{id}/status", JsonOptions))!;
            if (dto.Status == expected)
            {
                return dto;
            }

            await Task.Delay(25);
        }
        while (DateTime.UtcNow < deadline);

        throw new Xunit.Sdk.XunitException($"Session {id} did not reach {expected}; last status was {dto.Status} ({dto.ErrorMessage}).");
    }

    private static ByteArrayContent Bytes(byte[] bytes, string mime)
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue(mime);
        return content;
    }
}

public class MediaEndpointsTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    private readonly HttpClient _client;

    public MediaEndpointsTests(ApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Artifacts_AfterReadyForReview_Returns200WithMediaAndEvents()
    {
        var created = await MediaTestHelpers.UploadAsync(_client, manualOffset: 2.5);
        var ready = await MediaTestHelpers.WaitForStatusAsync(_client, created.Id, SessionStatus.ReadyForReview);

        Assert.Equal("Completed", ready.ProcessingStage);
        Assert.Null(ready.ErrorMessage);
        Assert.NotNull(ready.Artifacts);

        var response = await _client.GetAsync($"/api/sessions/{created.Id}/artifacts");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var artifacts = (await response.Content.ReadFromJsonAsync<SessionArtifactsDto>(MediaTestHelpers.JsonOptions))!;
        Assert.Equal(1280, artifacts.Video.Width);
        Assert.Equal(2, artifacts.Frames.Count);
        Assert.Equal(3, artifacts.AudioIntervals.Count);
        Assert.Equal(3, artifacts.TechnicalEvents.Count);
        Assert.Equal(TechnicalEventCategory.Command, artifacts.TechnicalEvents[0].Category);
        Assert.Equal(2.5, artifacts.AppliedReferenceOffsetSeconds);

        var artifactsFile = Path.Combine(_factory.StorageRoot, "Sessions", created.Id.ToString("D"), "artifacts.json");
        Assert.True(File.Exists(artifactsFile));
    }

    [Fact]
    public async Task Artifacts_WithoutManualOffset_ReportsNullAppliedOffset()
    {
        var created = await MediaTestHelpers.UploadAsync(_client);
        await MediaTestHelpers.WaitForStatusAsync(_client, created.Id, SessionStatus.ReadyForReview);

        var artifacts = await _client.GetFromJsonAsync<SessionArtifactsDto>(
            $"/api/sessions/{created.Id}/artifacts",
            MediaTestHelpers.JsonOptions);

        Assert.Null(artifacts!.AppliedReferenceOffsetSeconds);
    }

    [Fact]
    public async Task Artifacts_NeverContainSecretsFromTheLog()
    {
        const string secret = "sk-abcdefghijklmnopqrstuvwxyz123456";
        var log = TestFiles.ValidCast + $"[3.0,\"o\",\"$ export KEY={secret}\\r\\n\"]\n";
        var created = await MediaTestHelpers.UploadAsync(_client, log);
        await MediaTestHelpers.WaitForStatusAsync(_client, created.Id, SessionStatus.ReadyForReview);

        var json = await _client.GetStringAsync($"/api/sessions/{created.Id}/artifacts");

        Assert.DoesNotContain(secret, json);
        Assert.Contains("[REDACTED:OpenAiApiKey]", json);
    }

    [Fact]
    public async Task Artifacts_ForUnknownSession_Returns404()
    {
        var response = await _client.GetAsync($"/api/sessions/{Guid.NewGuid()}/artifacts");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Artifacts_ForSessionAwaitingManualReference_Returns409()
    {
        var created = await MediaTestHelpers.UploadAsync(_client, TestFiles.JsonlWithoutTimestamps, "demo.jsonl");
        Assert.Equal(SessionStatus.RequiresManualReference, created.Status);

        var response = await _client.GetAsync($"/api/sessions/{created.Id}/artifacts");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Video_ReturnsFileWithRangeSupport()
    {
        var created = await MediaTestHelpers.UploadAsync(_client);

        var full = await _client.GetAsync($"/api/sessions/{created.Id}/video");

        Assert.Equal(HttpStatusCode.OK, full.StatusCode);
        Assert.Equal("video/mp4", full.Content.Headers.ContentType?.MediaType);
        Assert.Contains("bytes", full.Headers.AcceptRanges);
        Assert.Equal("fake-video-bytes", await full.Content.ReadAsStringAsync());

        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/sessions/{created.Id}/video");
        request.Headers.Range = new RangeHeaderValue(0, 3);
        var partial = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.PartialContent, partial.StatusCode);
        Assert.Equal("fake", await partial.Content.ReadAsStringAsync());
        Assert.Equal(16, partial.Content.Headers.ContentRange?.Length);
    }

    [Fact]
    public async Task Video_ForUnknownSession_Returns404()
    {
        var response = await _client.GetAsync($"/api/sessions/{Guid.NewGuid()}/video");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Frame_ReturnsJpegForExistingFile()
    {
        var created = await MediaTestHelpers.UploadAsync(_client);
        await MediaTestHelpers.WaitForStatusAsync(_client, created.Id, SessionStatus.ReadyForReview);

        var response = await _client.GetAsync($"/api/sessions/{created.Id}/frames/frame_0001.jpg");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/jpeg", response.Content.Headers.ContentType?.MediaType);
        Assert.NotEmpty(await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task Frame_MissingFile_Returns404()
    {
        var created = await MediaTestHelpers.UploadAsync(_client);
        await MediaTestHelpers.WaitForStatusAsync(_client, created.Id, SessionStatus.ReadyForReview);

        var response = await _client.GetAsync($"/api/sessions/{created.Id}/frames/frame_9999.jpg");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData("..%2F..%2Frecording.mp4")]
    [InlineData("..%5Crecording.mp4")]
    [InlineData("%2e%2e%2fartifacts.json")]
    [InlineData("frame_0001.png")]
    [InlineData("frame%200001.jpg")]
    [InlineData("..")]
    public async Task Frame_RejectsPathTraversalAndInvalidNames(string fileName)
    {
        var created = await MediaTestHelpers.UploadAsync(_client);
        await MediaTestHelpers.WaitForStatusAsync(_client, created.Id, SessionStatus.ReadyForReview);

        var response = await _client.GetAsync($"/api/sessions/{created.Id}/frames/{fileName}");

        Assert.True(
            response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound,
            $"Expected 400/404 for '{fileName}' but got {(int)response.StatusCode}.");
    }

    [Fact]
    public async Task Frame_InvalidNameReturns400WithValidationProblem()
    {
        var created = await MediaTestHelpers.UploadAsync(_client);

        var response = await _client.GetAsync($"/api/sessions/{created.Id}/frames/frame_0001.png");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}

public class PendingArtifactsTests : IClassFixture<GatedApiFactory>
{
    private readonly GatedApiFactory _factory;
    private readonly HttpClient _client;

    public PendingArtifactsTests(GatedApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Artifacts_WhileProcessing_Returns409ThenSucceedsAfterCompletion()
    {
        var created = await MediaTestHelpers.UploadAsync(_client);

        var processing = await MediaTestHelpers.WaitForStatusAsync(_client, created.Id, SessionStatus.Processing);
        Assert.Equal(45, processing.ProgressPercent);
        Assert.Equal("Extracting keyframes", processing.ProcessingStage);

        var pending = await _client.GetAsync($"/api/sessions/{created.Id}/artifacts");
        Assert.Equal(HttpStatusCode.Conflict, pending.StatusCode);

        _factory.ReleaseProcessing();
        await MediaTestHelpers.WaitForStatusAsync(_client, created.Id, SessionStatus.ReadyForReview);

        var ready = await _client.GetAsync($"/api/sessions/{created.Id}/artifacts");
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
    }
}
