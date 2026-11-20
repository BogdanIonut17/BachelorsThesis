using System.Globalization;
using DemoSync.Api.Models;
using Microsoft.Extensions.Options;

namespace DemoSync.Api.Services;

public sealed record MediaExtractionOutput(
    VideoMetadata Video,
    IReadOnlyList<ExtractedFrame> Frames,
    IReadOnlyList<AudioActivityInterval> AudioIntervals);

public interface IMediaExtractor
{
    Task<MediaExtractionOutput> ExtractAsync(
        Guid sessionId,
        string videoPath,
        string sessionDirectory,
        Func<int, string, Task> reportProgress,
        CancellationToken cancellationToken);
}

public sealed class FfmpegMediaExtractor : IMediaExtractor
{
    public const string FramesDirectoryName = "frames";
    public const string AudioFileName = "audio.wav";

    private readonly FfmpegOptions _options;
    private readonly IProcessRunner _runner;

    public FfmpegMediaExtractor(IOptions<FfmpegOptions> options, IProcessRunner runner)
    {
        _options = options.Value;
        _runner = runner;
    }

    public async Task<MediaExtractionOutput> ExtractAsync(
        Guid sessionId,
        string videoPath,
        string sessionDirectory,
        Func<int, string, Task> reportProgress,
        CancellationToken cancellationToken)
    {
        await reportProgress(15, "Probing video metadata");
        var metadata = await ProbeAsync(videoPath, cancellationToken);

        await reportProgress(45, "Extracting keyframes");
        var frames = await ExtractFramesAsync(videoPath, sessionDirectory, metadata, cancellationToken);

        await reportProgress(75, "Extracting audio & activity");
        var audioIntervals = metadata.HasAudio
            ? await ExtractAudioAsync(videoPath, sessionDirectory, metadata, cancellationToken)
            : [];

        return new MediaExtractionOutput(metadata, frames, audioIntervals);
    }

    private async Task<VideoMetadata> ProbeAsync(string videoPath, CancellationToken cancellationToken)
    {
        var result = await RunToolAsync(
            _options.FfprobePath,
            ["-v", "quiet", "-print_format", "json", "-show_format", "-show_streams", videoPath],
            cancellationToken);

        return FfmpegOutputParser.ParseProbeJson(result.StandardOutput);
    }

    private async Task<IReadOnlyList<ExtractedFrame>> ExtractFramesAsync(
        string videoPath,
        string sessionDirectory,
        VideoMetadata metadata,
        CancellationToken cancellationToken)
    {
        var maxFrames = Math.Max(1, _options.MaxExtractedFrames);
        var interval = Math.Max(_options.FrameSampleIntervalSeconds, metadata.DurationSeconds / maxFrames);
        if (interval <= 0 || !double.IsFinite(interval))
        {
            interval = 2.0;
        }

        var framesDirectory = Path.Combine(sessionDirectory, FramesDirectoryName);
        if (Directory.Exists(framesDirectory))
        {
            Directory.Delete(framesDirectory, recursive: true);
        }

        Directory.CreateDirectory(framesDirectory);

        await RunToolAsync(
            _options.FfmpegPath,
            [
                "-nostdin", "-hide_banner", "-loglevel", "error", "-y",
                "-i", videoPath,
                "-vf", $"fps=1/{interval.ToString("0.######", CultureInfo.InvariantCulture)}",
                "-frames:v", maxFrames.ToString(CultureInfo.InvariantCulture),
                "-q:v", "4",
                Path.Combine(framesDirectory, "frame_%04d.jpg")
            ],
            cancellationToken);

        var files = Directory
            .EnumerateFiles(framesDirectory, "frame_*.jpg")
            .Select(Path.GetFileName)
            .OfType<string>()
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        if (files.Count == 0)
        {
            throw new MediaExtractionException("ffmpeg did not produce any keyframes for the uploaded video.");
        }

        return files
            .Select((name, index) => new ExtractedFrame(
                index,
                Math.Min(index * interval, metadata.DurationSeconds),
                name))
            .ToList();
    }

    private async Task<IReadOnlyList<AudioActivityInterval>> ExtractAudioAsync(
        string videoPath,
        string sessionDirectory,
        VideoMetadata metadata,
        CancellationToken cancellationToken)
    {
        var audioPath = Path.Combine(sessionDirectory, AudioFileName);

        await RunToolAsync(
            _options.FfmpegPath,
            [
                "-nostdin", "-hide_banner", "-loglevel", "error", "-y",
                "-i", videoPath,
                "-vn", "-ac", "1", "-ar", "16000", "-c:a", "pcm_s16le",
                audioPath
            ],
            cancellationToken);

        var noise = _options.SilenceNoiseDb.ToString("0.###", CultureInfo.InvariantCulture);
        var minimumSilence = _options.MinSilenceDurationSeconds.ToString("0.###", CultureInfo.InvariantCulture);

        var detection = await RunToolAsync(
            _options.FfmpegPath,
            [
                "-nostdin", "-hide_banner", "-nostats",
                "-i", audioPath,
                "-af", $"silencedetect=noise={noise}dB:d={minimumSilence}",
                "-f", "null", "-"
            ],
            cancellationToken);

        return FfmpegOutputParser.ParseSilenceDetect(detection.StandardError, metadata.DurationSeconds);
    }

    private async Task<ProcessResult> RunToolAsync(
        string executable,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var timeout = TimeSpan.FromSeconds(Math.Max(1, _options.ProcessTimeoutSeconds));
        var result = await _runner.RunAsync(executable, arguments, timeout, cancellationToken);

        if (result.ExitCode != 0)
        {
            throw new MediaExtractionException(
                $"{Path.GetFileNameWithoutExtension(executable)} exited with code {result.ExitCode}: {Tail(result.StandardError)}");
        }

        return result;
    }

    private static string Tail(string stderr)
    {
        var lines = stderr
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .TakeLast(3);

        var text = string.Join(" | ", lines);
        if (text.Length == 0)
        {
            return "no error output.";
        }

        return text.Length > 400 ? text[^400..] : text;
    }
}
