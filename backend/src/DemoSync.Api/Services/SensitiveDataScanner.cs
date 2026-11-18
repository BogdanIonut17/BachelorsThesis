using System.Text;
using System.Text.RegularExpressions;
using DemoSync.Api.Models;

namespace DemoSync.Api.Services;

public sealed record SensitiveScanResult(IReadOnlyList<SensitiveMatch> Matches, string SanitizedContent);

public interface ISensitiveDataScanner
{
    SensitiveScanResult Scan(string content);
}

public sealed class SensitiveDataScanner : ISensitiveDataScanner
{
    private const RegexOptions Options = RegexOptions.Compiled | RegexOptions.CultureInvariant;

    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);

    private sealed record Rule(string Name, Regex Pattern);

    // Ordered by priority: broader constructs (connection strings, JWTs) claim their ranges first so
    // narrower rules (passwords, tokens) never double-report text that is already masked.
    private static readonly Rule[] Rules =
    [
        new("DbConnectionString", new Regex(
            @"(?i)\b(?:Server|Data Source|Host)\s*=[^;\r\n]+;[^\r\n]*?\b(?:Password|Pwd)\s*=[^;\r\n""']+",
            Options, RegexTimeout)),
        new("DbConnectionString", new Regex(
            @"(?i)\b(?:postgres(?:ql)?|mysql|mariadb|mongodb(?:\+srv)?|redis|rediss|sqlserver|mssql|amqps?)://[^\s:@/]+:[^\s@]+@[^\s""']+",
            Options, RegexTimeout)),
        new("JwtToken", new Regex(
            @"\beyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}",
            Options, RegexTimeout)),
        new("BearerToken", new Regex(
            @"(?i)\bBearer\s+[A-Za-z0-9\-._~+/]{8,}=*",
            Options, RegexTimeout)),
        new("OpenAiApiKey", new Regex(
            @"sk-[a-zA-Z0-9]{20,}",
            Options, RegexTimeout)),
        new("GoogleApiKey", new Regex(
            @"AIza[0-9A-Za-z\-_]{35}",
            Options, RegexTimeout)),
        // Value excludes JSON/escape delimiters so masking a .cast/.jsonl line keeps it parseable.
        new("Password", new Regex(
            @"(?i)(?:password|passwd|pwd|secret|token)\s*[:=]\s*\\?[""']?(?:(?!\\[rnt""'\\])[^\s""'\]}])+",
            Options, RegexTimeout))
    ];

    public SensitiveScanResult Scan(string content)
    {
        ArgumentNullException.ThrowIfNull(content);

        var matches = new List<SensitiveMatch>();
        var sanitized = new StringBuilder(content.Length);

        var lineNumber = 0;
        var position = 0;
        while (position <= content.Length)
        {
            var lineEnd = content.IndexOf('\n', position);
            var hasNewline = lineEnd >= 0;
            var line = hasNewline ? content[position..lineEnd] : content[position..];
            lineNumber++;

            sanitized.Append(MaskLine(line, lineNumber, matches));

            if (!hasNewline)
            {
                break;
            }

            sanitized.Append('\n');
            position = lineEnd + 1;
        }

        return new SensitiveScanResult(matches, sanitized.ToString());
    }

    private static string MaskLine(string line, int lineNumber, List<SensitiveMatch> matches)
    {
        if (line.Length == 0)
        {
            return line;
        }

        var claimed = new List<(int Start, int End, string Rule)>();

        foreach (var rule in Rules)
        {
            MatchCollection found;
            try
            {
                found = rule.Pattern.Matches(line);
            }
            catch (RegexMatchTimeoutException)
            {
                continue;
            }

            foreach (Match match in found)
            {
                var start = match.Index;
                var end = match.Index + match.Length;

                if (claimed.Any(c => start < c.End && end > c.Start))
                {
                    continue;
                }

                claimed.Add((start, end, rule.Name));
                matches.Add(new SensitiveMatch(lineNumber, rule.Name, Mask(match.Value)));
            }
        }

        if (claimed.Count == 0)
        {
            return line;
        }

        var builder = new StringBuilder(line.Length);
        var cursor = 0;
        foreach (var (start, end, ruleName) in claimed.OrderBy(c => c.Start))
        {
            builder.Append(line, cursor, start - cursor);
            builder.Append("[REDACTED:").Append(ruleName).Append(']');
            cursor = end;
        }

        builder.Append(line, cursor, line.Length - cursor);
        return builder.ToString();
    }

    private static string Mask(string value)
    {
        if (value.Length <= 8)
        {
            return new string('*', value.Length);
        }

        return string.Concat(value.AsSpan(0, 4), new string('*', Math.Min(value.Length - 4, 12)));
    }
}
