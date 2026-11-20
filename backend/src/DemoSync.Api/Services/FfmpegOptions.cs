namespace DemoSync.Api.Services;

public sealed class FfmpegOptions
{
    public string FfmpegPath { get; set; } = "ffmpeg";

    public string FfprobePath { get; set; } = "ffprobe";

    public double FrameSampleIntervalSeconds { get; set; } = 2.0;

    public int MaxExtractedFrames { get; set; } = 120;

    public double SilenceNoiseDb { get; set; } = -35;

    public double MinSilenceDurationSeconds { get; set; } = 1.5;

    public int ProcessTimeoutSeconds { get; set; } = 900;
}
