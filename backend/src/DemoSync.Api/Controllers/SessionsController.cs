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

    public SessionsController(
        IUploadValidator validator,
        ISessionUploadService uploadService,
        ISessionRepository repository,
        IBackgroundJobQueue queue)
    {
        _validator = validator;
        _uploadService = uploadService;
        _repository = repository;
        _queue = queue;
    }

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
