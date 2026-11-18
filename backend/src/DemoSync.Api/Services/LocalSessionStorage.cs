using Microsoft.Extensions.Options;

namespace DemoSync.Api.Services;

public sealed class StorageOptions
{
    public string? RootPath { get; set; }
}

public sealed record StoredUpload(string VideoFileName, string LogFileName, string VideoPath, string LogPath);

public interface ISessionStorage
{
    Task<StoredUpload> SaveUploadAsync(Guid sessionId, IFormFile videoFile, IFormFile logFile, CancellationToken cancellationToken);

    Task SaveSanitizedLogAsync(Guid sessionId, string sanitizedContent, CancellationToken cancellationToken);

    void Delete(Guid sessionId);
}

public sealed class LocalSessionStorage : ISessionStorage
{
    public const string SanitizedLogFileName = "sanitized.log";

    private readonly string _sessionsRoot;

    public LocalSessionStorage(IOptions<StorageOptions> options, IWebHostEnvironment environment)
    {
        var root = string.IsNullOrWhiteSpace(options.Value.RootPath)
            ? Path.Combine(environment.ContentRootPath, "App_Data")
            : options.Value.RootPath;

        _sessionsRoot = Path.Combine(Path.GetFullPath(root), "Sessions");
    }

    public async Task<StoredUpload> SaveUploadAsync(
        Guid sessionId,
        IFormFile videoFile,
        IFormFile logFile,
        CancellationToken cancellationToken)
    {
        var directory = GetSessionDirectory(sessionId);
        Directory.CreateDirectory(directory);

        var videoName = SanitizeFileName(videoFile.FileName, "recording");
        var logName = SanitizeFileName(logFile.FileName, "execution.log");

        var videoPath = Path.Combine(directory, "recording" + Path.GetExtension(videoName).ToLowerInvariant());
        var logPath = Path.Combine(directory, "execution" + Path.GetExtension(logName).ToLowerInvariant());

        await CopyAsync(videoFile, videoPath, cancellationToken);
        await CopyAsync(logFile, logPath, cancellationToken);

        return new StoredUpload(videoName, logName, videoPath, logPath);
    }

    public Task SaveSanitizedLogAsync(Guid sessionId, string sanitizedContent, CancellationToken cancellationToken)
    {
        var directory = GetSessionDirectory(sessionId);
        Directory.CreateDirectory(directory);

        return File.WriteAllTextAsync(
            Path.Combine(directory, SanitizedLogFileName),
            sanitizedContent,
            new System.Text.UTF8Encoding(false),
            cancellationToken);
    }

    public void Delete(Guid sessionId)
    {
        var directory = GetSessionDirectory(sessionId);
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    public string GetSessionDirectory(Guid sessionId) => Path.Combine(_sessionsRoot, sessionId.ToString("D"));

    public static string SanitizeFileName(string? fileName, string fallback)
    {
        var name = Path.GetFileName(fileName?.Replace('\\', '/') ?? string.Empty);
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Where(c => !invalid.Contains(c) && !char.IsControl(c)).ToArray()).Trim();

        if (cleaned.Length == 0)
        {
            return fallback;
        }

        return cleaned.Length > 200 ? cleaned[^200..] : cleaned;
    }

    private static async Task CopyAsync(IFormFile source, string destinationPath, CancellationToken cancellationToken)
    {
        await using var input = source.OpenReadStream();
        await using var output = new FileStream(
            destinationPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 81920,
            useAsync: true);

        await input.CopyToAsync(output, cancellationToken);
    }
}
