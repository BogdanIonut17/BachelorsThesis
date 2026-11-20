using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using DemoSync.Api.Models;

namespace DemoSync.Api.Services;

public sealed class MediaExtractionException : Exception
{
    public MediaExtractionException(string message)
        : base(message)
    {
    }

    public MediaExtractionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public static class FfmpegOutputParser
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);

    private static readonly Regex SilenceStartPattern = new(
        @"silence_start:\s*(?<value>-?[0-9]*\.?[0-9]+(?:[eE][-+]?[0-9]+)?)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        RegexTimeout);

    private static readonly Regex SilenceEndPattern = new(
        @"silence_end:\s*(?<value>-?[0-9]*\.?[0-9]+(?:[eE][-+]?[0-9]+)?)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        RegexTimeout);

    public static VideoMetadata ParseProbeJson(string json)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException exception)
        {
            throw new MediaExtractionException("ffprobe returned output that is not valid JSON.", exception);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("streams", out var streams)
                || streams.ValueKind != JsonValueKind.Array)
            {
                throw new MediaExtractionException("ffprobe output does not contain any streams.");
            }

            JsonElement? videoStream = null;
            var hasAudio = false;

            foreach (var stream in streams.EnumerateArray())
            {
                var codecType = ReadString(stream, "codec_type");
                if (codecType == "video" && videoStream is null && !IsAttachedPicture(stream))
                {
                    videoStream = stream;
                }
                else if (codecType == "audio")
                {
                    hasAudio = true;
                }
            }

            if (videoStream is not { } video)
            {
                throw new MediaExtractionException("The uploaded file does not contain a video stream.");
            }

            var width = ReadInt(video, "width");
            var height = ReadInt(video, "height");

            var frameRate = ParseFrameRate(ReadString(video, "r_frame_rate"));
            if (frameRate <= 0)
            {
                frameRate = ParseFrameRate(ReadString(video, "avg_frame_rate"));
            }

            var duration = 0d;
            if (root.TryGetProperty("format", out var format) && format.ValueKind == JsonValueKind.Object)
            {
                duration = ReadDouble(format, "duration");
            }

            if (duration <= 0)
            {
                duration = ReadDouble(video, "duration");
            }

            if (duration <= 0)
            {
                throw new MediaExtractionException("Could not determine the video duration from ffprobe output.");
            }

            return new VideoMetadata(duration, width, height, frameRate, hasAudio);
        }
    }

    /// <summary>Parses rational ("30000/1001") or decimal ("25") frame rates. Returns 0 when unparsable.</summary>
    public static double ParseFrameRate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return 0;
        }

        var parts = value.Split('/');
        if (parts.Length == 2
            && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var numerator)
            && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var denominator))
        {
            return denominator == 0 ? 0 : numerator / denominator;
        }

        return parts.Length == 1
            && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var plain)
            && double.IsFinite(plain)
                ? plain
                : 0;
    }

    /// <summary>
    /// Converts <c>silencedetect</c> stderr into contiguous active/silent intervals that cover
    /// the whole timeline [0, totalDurationSeconds].
    /// </summary>
    public static IReadOnlyList<AudioActivityInterval> ParseSilenceDetect(string stderr, double totalDurationSeconds)
    {
        if (totalDurationSeconds <= 0 || !double.IsFinite(totalDurationSeconds))
        {
            return [];
        }

        var silences = new List<(double Start, double End)>();
        double? openStart = null;

        foreach (var rawLine in stderr.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');

            var startMatch = SilenceStartPattern.Match(line);
            if (startMatch.Success && TryParse(startMatch.Groups["value"].Value, out var start))
            {
                openStart = Math.Clamp(start, 0, totalDurationSeconds);
                continue;
            }

            var endMatch = SilenceEndPattern.Match(line);
            if (endMatch.Success && TryParse(endMatch.Groups["value"].Value, out var end))
            {
                var silenceStart = openStart ?? 0;
                var silenceEnd = Math.Clamp(end, 0, totalDurationSeconds);
                if (silenceEnd > silenceStart)
                {
                    silences.Add((silenceStart, silenceEnd));
                }

                openStart = null;
            }
        }

        if (openStart is { } trailingStart && trailingStart < totalDurationSeconds)
        {
            silences.Add((trailingStart, totalDurationSeconds));
        }

        var intervals = new List<AudioActivityInterval>();
        var cursor = 0d;

        foreach (var (start, end) in silences.OrderBy(s => s.Start))
        {
            var effectiveStart = Math.Max(start, cursor);
            if (end <= effectiveStart)
            {
                continue;
            }

            if (effectiveStart > cursor)
            {
                intervals.Add(new AudioActivityInterval(cursor, effectiveStart, IsActive: true));
            }

            intervals.Add(new AudioActivityInterval(effectiveStart, end, IsActive: false));
            cursor = end;
        }

        if (cursor < totalDurationSeconds)
        {
            intervals.Add(new AudioActivityInterval(cursor, totalDurationSeconds, IsActive: true));
        }

        return intervals;
    }

    private static bool TryParse(string value, out double result) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out result) && double.IsFinite(result);

    private static bool IsAttachedPicture(JsonElement stream) =>
        stream.TryGetProperty("disposition", out var disposition)
        && disposition.ValueKind == JsonValueKind.Object
        && disposition.TryGetProperty("attached_pic", out var attached)
        && attached.ValueKind == JsonValueKind.Number
        && attached.TryGetInt32(out var flag)
        && flag == 1;

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int ReadInt(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)
            ? number
            : 0;

    private static double ReadDouble(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            return 0;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetDouble(out var number) && double.IsFinite(number) => number,
            JsonValueKind.String when TryParse(value.GetString() ?? string.Empty, out var parsed) => parsed,
            _ => 0
        };
    }
}
