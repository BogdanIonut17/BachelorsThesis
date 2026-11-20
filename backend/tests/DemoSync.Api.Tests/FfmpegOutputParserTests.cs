using System.Globalization;
using DemoSync.Api.Services;
using Xunit;

namespace DemoSync.Api.Tests;

public class FfmpegOutputParserTests
{
    private const string ProbeWithAudio = """
        {
          "streams": [
            { "index": 0, "codec_type": "video", "width": 1920, "height": 1080,
              "r_frame_rate": "30000/1001", "avg_frame_rate": "30000/1001", "duration": "12.512000" },
            { "index": 1, "codec_type": "audio", "sample_rate": "48000", "channels": 2 }
          ],
          "format": { "duration": "12.534000", "format_name": "mov,mp4,m4a,3gp,3g2,mj2" }
        }
        """;

    private const string ProbeWithoutAudio = """
        {
          "streams": [
            { "index": 0, "codec_type": "video", "width": 1280, "height": 720,
              "r_frame_rate": "25/1", "avg_frame_rate": "25/1" }
          ],
          "format": { "duration": "4.000000" }
        }
        """;

    [Fact]
    public void ParseProbeJson_ReadsFractionalFrameRateAndAudioStream()
    {
        var metadata = FfmpegOutputParser.ParseProbeJson(ProbeWithAudio);

        Assert.Equal(1920, metadata.Width);
        Assert.Equal(1080, metadata.Height);
        Assert.Equal(29.97, metadata.FrameRate, precision: 2);
        Assert.Equal(12.534, metadata.DurationSeconds, precision: 3);
        Assert.True(metadata.HasAudio);
    }

    [Fact]
    public void ParseProbeJson_WithoutAudioStream_ReportsNoAudio()
    {
        var metadata = FfmpegOutputParser.ParseProbeJson(ProbeWithoutAudio);

        Assert.False(metadata.HasAudio);
        Assert.Equal(25, metadata.FrameRate);
        Assert.Equal(4, metadata.DurationSeconds);
    }

    [Fact]
    public void ParseProbeJson_IsCultureInvariant()
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("ro-RO");
        try
        {
            var metadata = FfmpegOutputParser.ParseProbeJson(ProbeWithAudio);

            Assert.Equal(12.534, metadata.DurationSeconds, precision: 3);
            Assert.Equal(29.97, metadata.FrameRate, precision: 2);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void ParseProbeJson_FallsBackToAverageFrameRateAndStreamDuration()
    {
        const string json = """
            { "streams": [ { "codec_type": "video", "width": 640, "height": 360,
                             "r_frame_rate": "0/0", "avg_frame_rate": "15/1", "duration": "3.5" } ] }
            """;

        var metadata = FfmpegOutputParser.ParseProbeJson(json);

        Assert.Equal(15, metadata.FrameRate);
        Assert.Equal(3.5, metadata.DurationSeconds);
    }

    [Theory]
    [InlineData("{ \"streams\": [ { \"codec_type\": \"audio\" } ], \"format\": { \"duration\": \"2\" } }")]
    [InlineData("not json at all")]
    [InlineData("{ \"streams\": [ { \"codec_type\": \"video\", \"width\": 1, \"height\": 1, \"r_frame_rate\": \"1/1\" } ] }")]
    public void ParseProbeJson_RejectsUnusableOutput(string json)
    {
        Assert.Throws<MediaExtractionException>(() => FfmpegOutputParser.ParseProbeJson(json));
    }

    [Theory]
    [InlineData("30000/1001", 29.97)]
    [InlineData("25/1", 25)]
    [InlineData("24", 24)]
    [InlineData("0/0", 0)]
    [InlineData("garbage", 0)]
    [InlineData(null, 0)]
    public void ParseFrameRate_HandlesRationalAndPlainValues(string? input, double expected)
    {
        Assert.Equal(expected, FfmpegOutputParser.ParseFrameRate(input), precision: 2);
    }

    [Fact]
    public void ParseSilenceDetect_BuildsContiguousActiveAndSilentIntervals()
    {
        const string stderr =
            "[silencedetect @ 0000020a] silence_start: 1.5\r\n" +
            "[silencedetect @ 0000020a] silence_end: 4.25 | silence_duration: 2.75\r\n" +
            "size=N/A time=00:00:06.00 bitrate=N/A speed= 300x\r\n" +
            "[silencedetect @ 0000020a] silence_start: 8\r\n";

        var intervals = FfmpegOutputParser.ParseSilenceDetect(stderr, 10);

        Assert.Equal(
            [
                new AudioActivityIntervalExpectation(0, 1.5, true),
                new AudioActivityIntervalExpectation(1.5, 4.25, false),
                new AudioActivityIntervalExpectation(4.25, 8, true),
                new AudioActivityIntervalExpectation(8, 10, false)
            ],
            intervals.Select(i => new AudioActivityIntervalExpectation(i.StartSeconds, i.EndSeconds, i.IsActive)));
        AssertContiguous(intervals, 10);
    }

    [Fact]
    public void ParseSilenceDetect_WithoutSilence_ReturnsSingleActiveInterval()
    {
        var intervals = FfmpegOutputParser.ParseSilenceDetect("size=N/A time=00:00:06.00", 6);

        var interval = Assert.Single(intervals);
        Assert.Equal(0, interval.StartSeconds);
        Assert.Equal(6, interval.EndSeconds);
        Assert.True(interval.IsActive);
    }

    [Fact]
    public void ParseSilenceDetect_ClampsNegativeStartAndStartsWithSilence()
    {
        const string stderr =
            "[silencedetect @ 1] silence_start: -0.0013\n" +
            "[silencedetect @ 1] silence_end: 2.0 | silence_duration: 2.0013\n";

        var intervals = FfmpegOutputParser.ParseSilenceDetect(stderr, 5);

        Assert.Equal(2, intervals.Count);
        Assert.False(intervals[0].IsActive);
        Assert.Equal(0, intervals[0].StartSeconds);
        Assert.Equal(2, intervals[0].EndSeconds);
        Assert.True(intervals[1].IsActive);
        AssertContiguous(intervals, 5);
    }

    [Fact]
    public void ParseSilenceDetect_IsCultureInvariantAndHandlesZeroDuration()
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("ro-RO");
        try
        {
            var intervals = FfmpegOutputParser.ParseSilenceDetect(
                "silence_start: 1.5\nsilence_end: 2.5 | silence_duration: 1.0\n",
                4);

            Assert.Equal(3, intervals.Count);
            Assert.Equal(1.5, intervals[1].StartSeconds);
            Assert.Equal(2.5, intervals[1].EndSeconds);
            Assert.Empty(FfmpegOutputParser.ParseSilenceDetect("silence_start: 1", 0));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    private static void AssertContiguous(IReadOnlyList<DemoSync.Api.Models.AudioActivityInterval> intervals, double duration)
    {
        Assert.Equal(0, intervals[0].StartSeconds);
        Assert.Equal(duration, intervals[^1].EndSeconds);
        for (var i = 1; i < intervals.Count; i++)
        {
            Assert.Equal(intervals[i - 1].EndSeconds, intervals[i].StartSeconds);
            Assert.NotEqual(intervals[i - 1].IsActive, intervals[i].IsActive);
        }
    }

    private sealed record AudioActivityIntervalExpectation(double Start, double End, bool IsActive);
}
