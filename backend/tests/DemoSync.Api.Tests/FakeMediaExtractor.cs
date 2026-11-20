using DemoSync.Api.Models;
using DemoSync.Api.Services;

namespace DemoSync.Api.Tests;

/// <summary>Deterministic stand-in for FFmpeg so API tests run without system binaries.</summary>
public sealed class FakeMediaExtractor : IMediaExtractor
{
    private static readonly byte[] TinyJpeg = [0xFF, 0xD8, 0xFF, 0xD9];

    private readonly Task? _gate;

    public FakeMediaExtractor(Task? gate = null)
    {
        _gate = gate;
    }

    public async Task<MediaExtractionOutput> ExtractAsync(
        Guid sessionId,
        string videoPath,
        string sessionDirectory,
        Func<int, string, Task> reportProgress,
        CancellationToken cancellationToken)
    {
        await reportProgress(15, "Probing video metadata");
        await reportProgress(45, "Extracting keyframes");

        if (_gate is not null)
        {
            await _gate.WaitAsync(cancellationToken);
        }

        var framesDirectory = Path.Combine(sessionDirectory, FfmpegMediaExtractor.FramesDirectoryName);
        Directory.CreateDirectory(framesDirectory);
        await File.WriteAllBytesAsync(Path.Combine(framesDirectory, "frame_0001.jpg"), TinyJpeg, cancellationToken);
        await File.WriteAllBytesAsync(Path.Combine(framesDirectory, "frame_0002.jpg"), TinyJpeg, cancellationToken);

        await reportProgress(75, "Extracting audio & activity");

        return new MediaExtractionOutput(
            new VideoMetadata(12.5, 1280, 720, 29.97, true),
            [new ExtractedFrame(0, 0, "frame_0001.jpg"), new ExtractedFrame(1, 2, "frame_0002.jpg")],
            [
                new AudioActivityInterval(0, 4, true),
                new AudioActivityInterval(4, 6, false),
                new AudioActivityInterval(6, 12.5, true)
            ]);
    }
}
