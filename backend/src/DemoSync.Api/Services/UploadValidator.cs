using System.Text;

namespace DemoSync.Api.Services;

public static class UploadLimits
{
    public const long MaxVideoBytes = 524_288_000;
    public const long MaxLogBytes = 26_214_400;
}

public sealed record UploadValidationResult(IReadOnlyList<string> Errors)
{
    public bool IsValid => Errors.Count == 0;
}

public interface IUploadValidator
{
    UploadValidationResult ValidateVideo(IFormFile? file);

    Task<UploadValidationResult> ValidateLogAsync(IFormFile? file, CancellationToken cancellationToken = default);
}

public sealed class UploadValidator : IUploadValidator
{
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mkv", ".webm"
    };

    private static readonly HashSet<string> VideoMimeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "video/mp4", "video/x-matroska", "video/matroska", "video/webm"
    };

    private static readonly HashSet<string> LogExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cast", ".jsonl", ".log"
    };

    private static readonly HashSet<string> LogMimeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "application/json", "application/x-ndjson", "application/jsonl", "application/octet-stream",
        "application/x-asciicast", "application/x-asciinema"
    };

    public UploadValidationResult ValidateVideo(IFormFile? file)
    {
        var errors = new List<string>();

        if (file is null || file.Length == 0)
        {
            errors.Add("Screen recording is required and must not be empty.");
            return new UploadValidationResult(errors);
        }

        var extension = Path.GetExtension(file.FileName);
        if (!VideoExtensions.Contains(extension))
        {
            errors.Add($"Screen recording extension '{extension}' is not supported. Allowed: .mp4, .mkv, .webm.");
        }

        if (string.IsNullOrWhiteSpace(file.ContentType) || !VideoMimeTypes.Contains(StripParameters(file.ContentType)))
        {
            errors.Add($"Screen recording MIME type '{file.ContentType}' is not supported.");
        }

        if (file.Length > UploadLimits.MaxVideoBytes)
        {
            errors.Add("Screen recording exceeds the 500 MB limit.");
        }

        return new UploadValidationResult(errors);
    }

    public async Task<UploadValidationResult> ValidateLogAsync(IFormFile? file, CancellationToken cancellationToken = default)
    {
        var errors = new List<string>();

        if (file is null || file.Length == 0)
        {
            errors.Add("Execution log is required and must not be empty.");
            return new UploadValidationResult(errors);
        }

        var extension = Path.GetExtension(file.FileName);
        if (!LogExtensions.Contains(extension))
        {
            errors.Add($"Execution log extension '{extension}' is not supported. Allowed: .cast, .jsonl, .log.");
        }

        if (string.IsNullOrWhiteSpace(file.ContentType) || !IsAllowedLogMimeType(StripParameters(file.ContentType)))
        {
            errors.Add($"Execution log MIME type '{file.ContentType}' is not supported.");
        }

        if (file.Length > UploadLimits.MaxLogBytes)
        {
            errors.Add("Execution log exceeds the 25 MB limit.");
            return new UploadValidationResult(errors);
        }

        if (errors.Count == 0 && !await IsUtf8TextAsync(file, cancellationToken))
        {
            errors.Add("Execution log must be valid UTF-8 text.");
        }

        return new UploadValidationResult(errors);
    }

    private static bool IsAllowedLogMimeType(string mimeType) =>
        mimeType.StartsWith("text/", StringComparison.OrdinalIgnoreCase) || LogMimeTypes.Contains(mimeType);

    private static string StripParameters(string contentType)
    {
        var separator = contentType.IndexOf(';');
        return (separator >= 0 ? contentType[..separator] : contentType).Trim();
    }

    private static async Task<bool> IsUtf8TextAsync(IFormFile file, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = file.OpenReadStream();
            using var memory = new MemoryStream();
            await stream.CopyToAsync(memory, cancellationToken);

            var text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                .GetString(memory.GetBuffer(), 0, (int)memory.Length);

            return !text.Contains('\0');
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }
}
