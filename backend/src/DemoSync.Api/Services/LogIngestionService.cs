using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DemoSync.Api.Services;

public enum LogFormat
{
    AsciinemaCast,
    StructuredJsonl
}

public sealed record LogAnalysisResult(
    LogFormat Format,
    int ParsedEventCount,
    int AnchorableEventCount,
    bool HasReliableTimestamps,
    bool IsAutoSyncFeasible,
    string? SyncWarningMessage,
    bool RequiresManualReference);

public interface ILogIngestionService
{
    LogAnalysisResult Analyze(string content, string fileName, double? manualReferenceOffsetSeconds);
}

public sealed class LogIngestionService : ILogIngestionService
{
    public const int MinimumAnchorableEvents = 2;

    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);

    private static readonly HashSet<string> AnchorableEventNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "command", "cmd", "exec", "execute", "shell", "build", "container", "docker",
        "exit", "exit_code", "exitcode", "test", "compile", "deploy"
    };

    private static readonly Regex[] AnchorPatterns =
    [
        new(@"(?m)^\s*(?:\S+@\S+(?::\S*)?\s*)?[$#â¯]\s+[A-Za-z./~][^\r\n]*", Options, RegexTimeout),
        new(@"(?m)^\s*PS\s[^>\r\n]*>\s*\S", Options, RegexTimeout),
        new(@"\b(?:docker|podman|kubectl|dotnet|npm|pnpm|yarn|cargo|gradle|mvn|cmake|msbuild|pip|tsc|webpack)\s+[a-z\-]+", Options | RegexOptions.IgnoreCase, RegexTimeout),
        new(@"Successfully (?:built|tagged)|Build (?:succeeded|FAILED)|Step \d+/\d+|Compiled successfully|npm ERR!|error CS\d+|Pulling from|Container \S+\s+(?:Started|Created|Running|Healthy)", Options | RegexOptions.IgnoreCase, RegexTimeout),
        new(@"\bexit(?:ed)?\s+(?:with\s+)?(?:code|status)\s*[:=]?\s*-?\d+|\$\?\s*=?\s*-?\d+|\bexit\s+code\b", Options | RegexOptions.IgnoreCase, RegexTimeout)
    ];

    private const RegexOptions Options = RegexOptions.Compiled | RegexOptions.CultureInvariant;

    public LogAnalysisResult Analyze(string content, string fileName, double? manualReferenceOffsetSeconds)
    {
        ArgumentNullException.ThrowIfNull(content);

        var lines = SplitLines(content);
        var format = DetectFormat(fileName, lines);

        var parse = format == LogFormat.AsciinemaCast ? ParseCast(lines) : ParseJsonl(lines);

        var hasReliableTimestamps = parse.ParsedEvents > 0 && parse.TimestampProblem is null;
        var enoughAnchors = parse.AnchorableEvents >= MinimumAnchorableEvents;
        var feasible = hasReliableTimestamps && enoughAnchors;

        string? warning = null;
        if (!feasible)
        {
            var reasons = new List<string>();
            if (!hasReliableTimestamps)
            {
                reasons.Add(parse.ParsedEvents == 0
                    ? "No parsable log events were found."
                    : $"Timestamps are missing or corrupted ({parse.TimestampProblem}).");
            }

            if (!enoughAnchors)
            {
                reasons.Add($"Found {parse.AnchorableEvents} anchorable technical event(s); at least {MinimumAnchorableEvents} are required.");
            }

            warning = $"Automatic synchronization is not possible. {string.Join(' ', reasons)} Provide a manual reference point.";
        }

        return new LogAnalysisResult(
            format,
            parse.ParsedEvents,
            parse.AnchorableEvents,
            hasReliableTimestamps,
            feasible,
            warning,
            RequiresManualReference: !feasible && !manualReferenceOffsetSeconds.HasValue);
    }

    private static LogFormat DetectFormat(string fileName, IReadOnlyList<string> lines)
    {
        var extension = Path.GetExtension(fileName);

        if (extension.Equals(".cast", StringComparison.OrdinalIgnoreCase))
        {
            return LogFormat.AsciinemaCast;
        }

        if (extension.Equals(".jsonl", StringComparison.OrdinalIgnoreCase))
        {
            return LogFormat.StructuredJsonl;
        }

        return lines.Count > 0 && LooksLikeCastHeader(lines[0]) ? LogFormat.AsciinemaCast : LogFormat.StructuredJsonl;
    }

    private static bool LooksLikeCastHeader(string line)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("version", out _);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static ParseOutcome ParseCast(IReadOnlyList<string> lines)
    {
        string? problem = null;

        if (lines.Count == 0)
        {
            problem = "empty file";
        }
        else if (!TryReadCastHeader(lines[0], out var headerProblem))
        {
            problem = headerProblem;
        }

        var parsed = 0;
        var anchorable = 0;
        var previous = double.NegativeInfinity;

        for (var i = 1; i < lines.Count; i++)
        {
            if (!TryParseCastEvent(lines[i], out var time, out var type, out var text))
            {
                problem ??= $"line {i + 1} is not a valid [time, type, data] event";
                continue;
            }

            parsed++;

            if (time is null)
            {
                problem ??= $"line {i + 1} has an invalid time offset";
            }
            else if (time.Value < previous)
            {
                problem ??= $"line {i + 1} is not monotonic";
            }
            else
            {
                previous = time.Value;
            }

            if (IsAnchorable(type == "x" ? "exit" : null, text))
            {
                anchorable++;
            }
        }

        return new ParseOutcome(parsed, anchorable, problem);
    }

    private static bool TryReadCastHeader(string line, out string problem)
    {
        problem = "invalid asciinema header";

        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("version", out var version)
                || version.ValueKind != JsonValueKind.Number
                || !version.TryGetInt32(out var versionNumber)
                || versionNumber != 2)
            {
                problem = "header must declare asciinema version 2";
                return false;
            }

            if (!root.TryGetProperty("timestamp", out var timestamp) || timestamp.ValueKind != JsonValueKind.Number)
            {
                problem = "header has no numeric timestamp";
                return false;
            }

            return true;
        }
        catch (JsonException)
        {
            problem = "header is not valid JSON";
            return false;
        }
    }

    private static bool TryParseCastEvent(string line, out double? time, out string type, out string text)
    {
        time = null;
        type = string.Empty;
        text = string.Empty;

        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() < 3)
            {
                return false;
            }

            var typeElement = root[1];
            var textElement = root[2];
            if (typeElement.ValueKind != JsonValueKind.String || textElement.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            type = typeElement.GetString() ?? string.Empty;
            text = textElement.GetString() ?? string.Empty;

            var timeElement = root[0];
            if (timeElement.ValueKind == JsonValueKind.Number
                && timeElement.TryGetDouble(out var seconds)
                && double.IsFinite(seconds)
                && seconds >= 0)
            {
                time = seconds;
            }

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static ParseOutcome ParseJsonl(IReadOnlyList<string> lines)
    {
        string? problem = null;
        var parsed = 0;
        var anchorable = 0;
        var previous = double.NegativeInfinity;

        for (var i = 0; i < lines.Count; i++)
        {
            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(lines[i]);
            }
            catch (JsonException)
            {
                problem ??= $"line {i + 1} is not valid JSON";
                continue;
            }

            using (document)
            {
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    problem ??= $"line {i + 1} is not a JSON object";
                    continue;
                }

                var eventName = ReadString(root, "event");
                var output = ReadString(root, "output");
                if (eventName is null && output is null)
                {
                    problem ??= $"line {i + 1} has neither 'event' nor 'output'";
                    continue;
                }

                parsed++;

                if (!TryReadTimestamp(root, out var seconds))
                {
                    problem ??= $"line {i + 1} has a missing or invalid timestamp";
                }
                else if (seconds < previous)
                {
                    problem ??= $"line {i + 1} is not monotonic";
                }
                else
                {
                    previous = seconds;
                }

                if (IsAnchorable(eventName, output))
                {
                    anchorable++;
                }
            }
        }

        return new ParseOutcome(parsed, anchorable, problem);
    }

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool TryReadTimestamp(JsonElement element, out double seconds)
    {
        seconds = 0;

        if (!element.TryGetProperty("timestamp", out var value))
        {
            return false;
        }

        switch (value.ValueKind)
        {
            case JsonValueKind.Number when value.TryGetDouble(out var number) && double.IsFinite(number):
                seconds = number;
                return true;

            case JsonValueKind.String when DateTimeOffset.TryParse(
                value.GetString(),
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var moment):
                seconds = moment.ToUnixTimeMilliseconds() / 1000d;
                return true;

            default:
                return false;
        }
    }

    private static bool IsAnchorable(string? eventName, string? text)
    {
        if (eventName is not null && AnchorableEventNames.Contains(eventName))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        foreach (var pattern in AnchorPatterns)
        {
            try
            {
                if (pattern.IsMatch(text))
                {
                    return true;
                }
            }
            catch (RegexMatchTimeoutException)
            {
                // Treat pathological input as non-anchorable.
            }
        }

        return false;
    }

    private static List<string> SplitLines(string content)
    {
        var normalized = content.TrimStart('\uFEFF');
        return normalized
            .Split('\n')
            .Select(line => line.TrimEnd('\r'))
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToList();
    }

    private readonly record struct ParseOutcome(int ParsedEvents, int AnchorableEvents, string? TimestampProblem);
}
