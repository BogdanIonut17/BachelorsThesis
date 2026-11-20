using System.Diagnostics;
using DemoSync.Api.Services;
using Microsoft.Extensions.Options;
using Xunit;

namespace DemoSync.Api.Tests;

public class FfmpegMediaExtractorTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "demosync-ffmpeg", Guid.NewGuid().ToString("N"));

    public FfmpegMediaExtractorTests()
    {
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public async Task ExtractAsync_NonZeroExitCode_ThrowsDescriptiveException()
    {
        var runner = new ScriptedRunner((_, _) => new ProcessResult(1, string.Empty, "line1\nInvalid data found when processing input\n"));
        var extractor = Create(runner);

        var exception = await Assert.ThrowsAsync<MediaExtractionException>(() =>
            extractor.ExtractAsync(Guid.NewGuid(), "video.mp4", _directory, (_, _) => Task.CompletedTask, CancellationToken.None));

        Assert.Contains("ffprobe exited with code 1", exception.Message);
        Assert.Contains("Invalid data found", exception.Message);
    }

    [Fact]
    public async Task ExtractAsync_MissingBinary_ThrowsMediaExtractionException()
    {
        var options = new FfmpegOptions { FfprobePath = "definitely-not-an-installed-ffprobe", FfmpegPath = "definitely-not-an-installed-ffmpeg" };
        var extractor = new FfmpegMediaExtractor(Options.Create(options), new ProcessRunner());

        var exception = await Assert.ThrowsAsync<MediaExtractionException>(() =>
            extractor.ExtractAsync(Guid.NewGuid(), "video.mp4", _directory, (_, _) => Task.CompletedTask, CancellationToken.None));

        Assert.Contains("definitely-not-an-installed-ffprobe", exception.Message);
    }

    [Fact]
    public async Task ExtractAsync_UsesInvariantCultureAndComputedIntervalForFrameSampling()
    {
        const string probe = """
            { "streams": [ { "codec_type": "video", "width": 640, "height": 360, "r_frame_rate": "30/1" } ],
              "format": { "duration": "600.0" } }
            """;
        List<string>? frameArguments = null;

        var runner = new ScriptedRunner((file, args) =>
        {
            if (file == "ffprobe")
            {
                return new ProcessResult(0, probe, string.Empty);
            }

            frameArguments = args.ToList();
            var pattern = args[^1];
            var framesDirectory = Path.GetDirectoryName(pattern)!;
            File.WriteAllBytes(Path.Combine(framesDirectory, "frame_0001.jpg"), [1]);
            File.WriteAllBytes(Path.Combine(framesDirectory, "frame_0002.jpg"), [1]);
            return new ProcessResult(0, string.Empty, string.Empty);
        });

        var previous = System.Globalization.CultureInfo.CurrentCulture;
        System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("ro-RO");
        try
        {
            var progress = new List<(int, string)>();
            var output = await Create(runner).ExtractAsync(
                Guid.NewGuid(),
                "video.mp4",
                _directory,
                (percent, stage) =>
                {
                    progress.Add((percent, stage));
                    return Task.CompletedTask;
                },
                CancellationToken.None);

            Assert.Equal([(15, "Probing video metadata"), (45, "Extracting keyframes"), (75, "Extracting audio & activity")], progress);
            Assert.NotNull(frameArguments);
            // 600s / 120 frames = 5s effective interval, which exceeds the 2s configured minimum.
            Assert.Contains("fps=1/5", frameArguments!);
            Assert.Contains("120", frameArguments!);
            Assert.Equal(2, output.Frames.Count);
            Assert.Equal(5, output.Frames[1].TimestampSeconds);
            Assert.Empty(output.AudioIntervals);
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public async Task ExtractAsync_WithRealFfmpeg_ProducesFramesAudioAndActivity()
    {
        if (!FfmpegAvailable())
        {
            return;
        }

        var video = Path.Combine(_directory, "sample.mp4");
        await GenerateAsync(video, withAudio: true);

        var progress = new List<int>();
        var output = await new FfmpegMediaExtractor(Options.Create(new FfmpegOptions()), new ProcessRunner()).ExtractAsync(
            Guid.NewGuid(),
            video,
            _directory,
            (percent, _) =>
            {
                progress.Add(percent);
                return Task.CompletedTask;
            },
            CancellationToken.None);

        Assert.Equal([15, 45, 75], progress);
        Assert.Equal(320, output.Video.Width);
        Assert.Equal(240, output.Video.Height);
        Assert.Equal(10, output.Video.FrameRate, precision: 1);
        Assert.True(output.Video.HasAudio);
        Assert.InRange(output.Video.DurationSeconds, 5.5, 6.5);
        Assert.InRange(output.Frames.Count, 3, 4);
        Assert.All(output.Frames, f => Assert.True(File.Exists(Path.Combine(_directory, "frames", f.FileName))));
        Assert.True(File.Exists(Path.Combine(_directory, "audio.wav")));
        Assert.Contains(output.AudioIntervals, i => !i.IsActive && i.StartSeconds is > 1.5 and < 2.6);
        Assert.Contains(output.AudioIntervals, i => i.IsActive && i.StartSeconds < 0.1);
    }

    [Fact]
    public async Task ExtractAsync_WithRealFfmpegAndNoAudio_ReturnsEmptyAudioIntervals()
    {
        if (!FfmpegAvailable())
        {
            return;
        }

        var video = Path.Combine(_directory, "silent.mp4");
        await GenerateAsync(video, withAudio: false);

        var output = await new FfmpegMediaExtractor(Options.Create(new FfmpegOptions()), new ProcessRunner()).ExtractAsync(
            Guid.NewGuid(),
            video,
            _directory,
            (_, _) => Task.CompletedTask,
            CancellationToken.None);

        Assert.False(output.Video.HasAudio);
        Assert.Empty(output.AudioIntervals);
        Assert.NotEmpty(output.Frames);
        Assert.False(File.Exists(Path.Combine(_directory, "audio.wav")));
    }

    private static FfmpegMediaExtractor Create(IProcessRunner runner) =>
        new(Options.Create(new FfmpegOptions { FfmpegPath = "ffmpeg", FfprobePath = "ffprobe" }), runner);

    private static bool FfmpegAvailable()
    {
        try
        {
            foreach (var tool in new[] { "ffmpeg", "ffprobe" })
            {
                using var process = Process.Start(new ProcessStartInfo(tool, "-version")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                });

                if (process is null)
                {
                    return false;
                }

                process.WaitForExit(10_000);
                if (process.ExitCode != 0)
                {
                    return false;
                }
            }

            return true;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private static async Task GenerateAsync(string path, bool withAudio)
    {
        var arguments = new List<string> { "-nostdin", "-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", "testsrc=duration=6:size=320x240:rate=10" };
        if (withAudio)
        {
            arguments.AddRange(["-f", "lavfi", "-i", "sine=frequency=440:duration=2,apad=whole_dur=6", "-c:a", "aac"]);
        }

        arguments.AddRange(["-c:v", "mpeg4", "-t", "6", path]);

        var result = await new ProcessRunner().RunAsync("ffmpeg", arguments, TimeSpan.FromSeconds(60), CancellationToken.None);
        Assert.True(result.ExitCode == 0, result.StandardError);
    }

    private sealed class ScriptedRunner : IProcessRunner
    {
        private readonly Func<string, IReadOnlyList<string>, ProcessResult> _script;

        public ScriptedRunner(Func<string, IReadOnlyList<string>, ProcessResult> script)
        {
            _script = script;
        }

        public Task<ProcessResult> RunAsync(
            string fileName,
            IReadOnlyList<string> arguments,
            TimeSpan timeout,
            CancellationToken cancellationToken) =>
            Task.FromResult(_script(fileName, arguments));
    }
}
