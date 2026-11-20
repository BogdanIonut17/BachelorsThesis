using DemoSync.Api.Contracts;
using DemoSync.Api.Models;
using DemoSync.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace DemoSync.Api.Controllers;

[ApiController]
[Route("api/sessions")]
public sealed class SessionsController : ControllerBase
{
    private const long MaxRequestBytes = 524_288_000;

    private readonly IUploadValidator _validator;
    private readonly ISessionUploadService _uploadService;
    private readonly ISessionRepository _repository;
    private readonly IBackgroundJobQueue _queue;
    private readonly ISessionStorage _storage;

    public SessionsController(
        IUploadValidator validator,
        ISessionUploadService uploadService,
        ISessionRepository repository,
        IBackgroundJobQueue queue,
        ISessionStorage storage)
    {
        _validator = validator;
        _uploadService = uploadService;
        _repository = repository;
        _queue = queue;
        _storage = storage;
    }

    private static readonly System.Text.RegularExpressions.Regex FrameFileNamePattern = new(
        @"^[A-Za-z0-9_\-]{1,64}\.jpg$",
        System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    [HttpGet("{id:guid}/artifacts")]
    [ProducesResponseType<SessionArtifactsDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public IActionResult GetArtifacts(Guid id)
    {
        var session = _repository.Get(id);
        if (session is null)
        {
            return NotFound();
        }

        if (session.Status != SessionStatus.ReadyForReview || session.Artifacts is null)
        {
            return Conflict(new ProblemDetails
            {
                Status = StatusCodes.Status409Conflict,
                Title = "Artifacts are not available yet.",
                Detail = $"Session is in status '{session.Status}'; artifacts are available once it is ReadyForReview."
            });
        }

        return Ok(SessionArtifactsDto.From(session.Artifacts, session.ManualReferenceOffsetSeconds));
    }

    [HttpGet("{id:guid}/video")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status206PartialContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult GetVideo(Guid id)
    {
        if (_repository.Get(id) is null)
        {
            return NotFound();
        }

        var path = _storage.GetVideoPath(id);
        if (path is null || !System.IO.File.Exists(path))
        {
            return NotFound();
        }

        return PhysicalFile(path, VideoContentType(path), enableRangeProcessing: true);
    }

    [HttpGet("{id:guid}/frames/{fileName}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult GetFrame(Guid id, string fileName)
    {
        if (!FrameFileNamePattern.IsMatch(fileName))
        {
            ModelState.AddModelError(nameof(fileName), "Frame file name is invalid.");
            return ValidationProblem(ModelState);
        }

        if (_repository.Get(id) is null)
        {
            return NotFound();
        }

        var path = _storage.GetFramePath(id, fileName);
        return System.IO.File.Exists(path) ? PhysicalFile(path, "image/jpeg") : NotFound();
    }

    private static string VideoContentType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".mp4" => "video/mp4",
        ".webm" => "video/webm",
        ".mkv" => "video/x-matroska",
        _ => "application/octet-stream"
    };

    [HttpPost("upload")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(MaxRequestBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = MaxRequestBytes)]
    [ProducesResponseType<ProcessingSessionDto>(StatusCodes.Status202Accepted)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Upload([FromForm] UploadSessionRequest request, CancellationToken cancellationToken)
    {
        if (request.ManualReferenceOffsetSeconds is { } offset && !double.IsFinite(offset))
        {
            ModelState.AddModelError(nameof(request.ManualReferenceOffsetSeconds), "Manual reference offset must be a finite number.");
        }

        var videoResult = _validator.ValidateVideo(request.VideoFile);
        var logResult = await _validator.ValidateLogAsync(request.LogFile, cancellationToken);

        foreach (var error in videoResult.Errors)
        {
            ModelState.AddModelError(nameof(request.VideoFile), error);
        }

        foreach (var error in logResult.Errors)
        {
            ModelState.AddModelError(nameof(request.LogFile), error);
        }

        if (!ModelState.IsValid)
        {
            return ValidationProblem(ModelState);
        }

        var session = await _uploadService.CreateSessionAsync(
            request.VideoFile,
            request.LogFile,
            request.ManualReferenceOffsetSeconds,
            cancellationToken);

        return Accepted($"/api/sessions/{session.Id}/status", ProcessingSessionDto.From(session));
    }

    [HttpGet("{id:guid}/status")]
    [ProducesResponseType<ProcessingSessionDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult GetStatus(Guid id)
    {
        var session = _repository.Get(id);
        return session is null ? NotFound() : Ok(ProcessingSessionDto.From(session));
    }

    [HttpPatch("{id:guid}/reference-point")]
    [ProducesResponseType<ProcessingSessionDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> SetReferencePoint(
        Guid id,
        [FromBody] SetReferencePointRequest request,
        CancellationToken cancellationToken)
    {
        var offset = request.ManualReferenceOffsetSeconds!.Value;
        if (!double.IsFinite(offset))
        {
            ModelState.AddModelError(nameof(request.ManualReferenceOffsetSeconds), "Manual reference offset must be a finite number.");
            return ValidationProblem(ModelState);
        }

        var shouldEnqueue = false;
        var session = _repository.Update(id, s =>
        {
            s.ManualReferenceOffsetSeconds = offset;

            if (s.Status == SessionStatus.RequiresManualReference)
            {
                s.Status = SessionStatus.Queued;
                s.ProgressPercent = 0;
                shouldEnqueue = true;
            }
        });

        if (session is null)
        {
            return NotFound();
        }

        if (shouldEnqueue)
        {
            await _queue.EnqueueAsync(id, cancellationToken);
        }

        return Ok(ProcessingSessionDto.From(session));
    }
}
