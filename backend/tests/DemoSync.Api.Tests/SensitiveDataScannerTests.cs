using DemoSync.Api.Services;
using Xunit;

namespace DemoSync.Api.Tests;

public class SensitiveDataScannerTests
{
    private readonly SensitiveDataScanner _scanner = new();

    [Fact]
    public void Scan_DetectsAndMasksOpenAiStyleKey()
    {
        const string secret = "sk-abcdefghijklmnopqrstuvwxyz123456";

        var result = _scanner.Scan($"export OPENAI_API_KEY={secret}");

        var match = Assert.Single(result.Matches);
        Assert.Equal("OpenAiApiKey", match.RuleName);
        Assert.Equal(1, match.LineNumber);
        Assert.DoesNotContain(secret, match.MaskedPreview);
        Assert.DoesNotContain(secret, result.SanitizedContent);
        Assert.Contains("[REDACTED:OpenAiApiKey]", result.SanitizedContent);
    }

    [Fact]
    public void Scan_DetectsGoogleApiKey()
    {
        var secret = "AIza" + new string('a', 35);

        var result = _scanner.Scan($"key = \"{secret}\"");

        Assert.Contains(result.Matches, m => m.RuleName == "GoogleApiKey");
        Assert.DoesNotContain(secret, result.SanitizedContent);
    }

    [Theory]
    [InlineData("password: hunter2hunter2")]
    [InlineData("PASSWD=SuperSecretValue")]
    [InlineData("db_pwd = abc12345678")]
    [InlineData("client_secret: s3cr3tvalue99")]
    public void Scan_DetectsAndMasksPasswords(string line)
    {
        var secretValue = line[(line.IndexOfAny([':', '=']) + 1)..].Trim();

        var result = _scanner.Scan(line);

        var match = Assert.Single(result.Matches);
        Assert.Equal("Password", match.RuleName);
        Assert.DoesNotContain(secretValue, match.MaskedPreview);
        Assert.DoesNotContain(secretValue, result.SanitizedContent);
    }

    [Fact]
    public void Scan_DetectsBearerAndJwtTokens()
    {
        const string jwt = "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.dBjftJeZ4CVPmB92K27uhbUJU1p1r";
        var content = $"Authorization: Bearer abcdefghijklmnop12345\njwt value {jwt}";

        var result = _scanner.Scan(content);

        Assert.Contains(result.Matches, m => m.RuleName == "BearerToken" && m.LineNumber == 1);
        Assert.Contains(result.Matches, m => m.RuleName == "JwtToken" && m.LineNumber == 2);
        Assert.DoesNotContain("abcdefghijklmnop12345", result.SanitizedContent);
        Assert.DoesNotContain(jwt, result.SanitizedContent);
    }

    [Fact]
    public void Scan_DetectsDatabaseConnectionStrings()
    {
        const string content =
            "Server=db.internal;Database=app;User Id=sa;Password=P@ssw0rdValue;\n" +
            "DATABASE_URL=postgres://admin:topsecretpw@db.example.com:5432/app";

        var result = _scanner.Scan(content);

        Assert.Equal(2, result.Matches.Count(m => m.RuleName == "DbConnectionString"));
        Assert.DoesNotContain("P@ssw0rdValue", result.SanitizedContent);
        Assert.DoesNotContain("topsecretpw", result.SanitizedContent);
        Assert.DoesNotContain(result.Matches, m => m.RuleName == "Password");
    }

    [Fact]
    public void Scan_ReportsCorrectLineNumbersAndPreservesCleanLines()
    {
        const string content = "line one\nline two\npassword=abcdefgh1234\nline four\n";

        var result = _scanner.Scan(content);

        var match = Assert.Single(result.Matches);
        Assert.Equal(3, match.LineNumber);
        var lines = result.SanitizedContent.Split('\n');
        Assert.Equal("line one", lines[0]);
        Assert.Equal("line two", lines[1]);
        Assert.Equal("line four", lines[3]);
    }

    [Fact]
    public void Scan_KeepsJsonLinesParseableAfterMasking()
    {
        const string line = "[1.5,\"o\",\"password=hunter2hunter2\\r\\n\"]";

        var result = _scanner.Scan(line);

        Assert.DoesNotContain("hunter2hunter2", result.SanitizedContent);
        using var document = System.Text.Json.JsonDocument.Parse(result.SanitizedContent);
        Assert.Equal(3, document.RootElement.GetArrayLength());
    }

    [Fact]
    public void Scan_CleanContentProducesNoMatchesAndIsUnchanged()
    {
        const string content = "dotnet build\r\nBuild succeeded.\r\n";

        var result = _scanner.Scan(content);

        Assert.Empty(result.Matches);
        Assert.Equal(content, result.SanitizedContent);
    }
}
