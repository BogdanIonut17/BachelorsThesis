using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using DemoSync.Api.Models;

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
    bool RequiresManualReference,
    IReadOnlyList<TechnicalEvent> Events);

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
        new(@"(?m)^\s*(?:\S+@\S+(?::\S*)?\s*)?[$#\u276F]\s+[A-Za-z./~][^\r\n]*", Options, RegexTimeout),
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
            RequiresManualReference: !feasible && !manualReferenceOffsetSeconds.HasValue,
            parse.Events);
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
        var events = new List<TechnicalEvent>();

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

            var eventName = type == "x" ? "exit" : null;
            var cleanText = StripAnsi(text);
            var isAnchorable = IsAnchorable(eventName, cleanText);
            if (isAnchorable)
            {
                anchorable++;
            }

            if (type != "r" && (cleanText.Length > 0 || type == "x"))
            {
                events.Add(CreateEvent(events.Count, time ?? 0, type, eventName, cleanText, isAnchorable));
            }
        }

        return new ParseOutcome(parsed, anchorable, problem, events);
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
        var events = new List<TechnicalEvent>();
        var rawTimes = new List<double?>();

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

                var hasTimestamp = TryReadTimestamp(root, out var seconds);
                if (!hasTimestamp)
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

                var cleanOutput = output is null ? null : StripAnsi(output);
                var isAnchorable = IsAnchorable(eventName, cleanOutput);
                if (isAnchorable)
                {
                    anchorable++;
                }

                var text = cleanOutput ?? string.Empty;
                if (text.Length > 0 || !string.IsNullOrEmpty(eventName))
                {
                    events.Add(CreateEvent(events.Count, 0, eventName ?? "output", eventName, text, isAnchorable));
                    rawTimes.Add(hasTimestamp ? seconds : null);
                }
            }
        }

        // Structured logs may carry absolute (epoch / ISO-8601) timestamps; expose them relative to the first event.
        var origin = rawTimes.FirstOrDefault(time => time.HasValue);
        if (origin.HasValue)
        {
            for (var i = 0; i < events.Count; i++)
            {
                var relative = rawTimes[i] is { } raw ? Math.Max(0, raw - origin.Value) : 0;
                events[i] = events[i] with { TimestampSeconds = relative };
            }
        }

        return new ParseOutcome(parsed, anchorable, problem, events);
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

    private static readonly Regex AnsiPattern = new(
        @"\x1B\[[0-9;?]*[ -/]*[@-~]|\x1B\][^\x07\x1B]*(?:\x07|\x1B\\)",
        Options,
        RegexTimeout);

    private static readonly HashSet<string> CommandEventNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "command", "cmd", "exec", "execute", "shell"
    };

    private static readonly HashSet<string> ExitEventNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "exit", "exit_code", "exitcode"
    };

    private static readonly Regex[] CommandPatterns = [AnchorPatterns[0], AnchorPatterns[1]];

    private static readonly Regex ExitCodePattern = AnchorPatterns[4];

    private static readonly Regex ErrorPattern = new(
        @"(?<!\b0\s+)\b(?:error|errors|failed|failure)\b(?!\s*[:=]\s*0\b)|\b(?:exception|fatal|panic|traceback|denied)\b|npm ERR!|error CS\d+",
        Options | RegexOptions.IgnoreCase,
        RegexTimeout);

    private static readonly Regex ContainerPattern = new(
        @"\b(?:docker|podman|kubectl|container|containers|compose|image|pulling|pull complete|dockerfile)\b|Step \d+/\d+|Successfully (?:built|tagged)|Container \S+\s+(?:Started|Created|Running|Healthy)",
        Options | RegexOptions.IgnoreCase,
        RegexTimeout);

    private static readonly Regex BuildPattern = new(
        @"\b(?:build|builds|built|compile|compiled|compiling|compilation|msbuild|webpack|vite|tsc|cargo|gradle|mvn|cmake|restore|bundle|bundled)\b",
        Options | RegexOptions.IgnoreCase,
        RegexTimeout);

    private const int MaxEventTextLength = 500;

    private static string StripAnsi(string text)
    {
        string stripped;
        try
        {
            stripped = AnsiPattern.Replace(text, string.Empty);
        }
        catch (RegexMatchTimeoutException)
        {
            stripped = text;
        }

        var builder = new System.Text.StringBuilder(stripped.Length);
        for (var i = 0; i < stripped.Length; i++)
        {
            var c = stripped[i];
            if (c == '\r')
            {
                if (i + 1 < stripped.Length && stripped[i + 1] == '\n')
                {
                    continue;
                }

                builder.Append('\n');
            }
            else if (c == '\n' || c == '\t' || !char.IsControl(c))
            {
                builder.Append(c);
            }
        }

        return builder.ToString().Trim();
    }

    private static TechnicalEvent CreateEvent(
        int index,
        double timestampSeconds,
        string eventType,
        string? eventName,
        string text,
        bool isAnchorable)
    {
        var preview = text.Length > MaxEventTextLength ? text[..MaxEventTextLength] + "…" : text;
        return new TechnicalEvent(index, timestampSeconds, eventType, Classify(eventName, text), preview, isAnchorable);
    }

    private static TechnicalEventCategory Classify(string? eventName, string text)
    {
        if ((eventName is not null && CommandEventNames.Contains(eventName)) || MatchesAny(CommandPatterns, text))
        {
            return TechnicalEventCategory.Command;
        }

        if ((eventName is not null && ExitEventNames.Contains(eventName)) || SafeIsMatch(ExitCodePattern, text))
        {
            return TechnicalEventCategory.ExitCode;
        }

        if (SafeIsMatch(ErrorPattern, text))
        {
            return TechnicalEventCategory.Error;
        }

        if ((eventName is not null && eventName.Equals("container", StringComparison.OrdinalIgnoreCase))
            || (eventName is not null && eventName.Equals("docker", StringComparison.OrdinalIgnoreCase))
            || SafeIsMatch(ContainerPattern, text))
        {
            return TechnicalEventCategory.Container;
        }

        if ((eventName is not null && (eventName.Equals("build", StringComparison.OrdinalIgnoreCase)
                || eventName.Equals("compile", StringComparison.OrdinalIgnoreCase)))
            || SafeIsMatch(BuildPattern, text))
        {
            return TechnicalEventCategory.Build;
        }

        return TechnicalEventCategory.Output;
    }

    private static bool MatchesAny(Regex[] patterns, string text) => patterns.Any(pattern => SafeIsMatch(pattern, text));

    private static bool SafeIsMatch(Regex pattern, string text)
    {
        try
        {
            return pattern.IsMatch(text);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
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

    private readonly record struct ParseOutcome(
        int ParsedEvents,
        int AnchorableEvents,
        string? TimestampProblem,
        IReadOnlyList<TechnicalEvent> Events);
}
