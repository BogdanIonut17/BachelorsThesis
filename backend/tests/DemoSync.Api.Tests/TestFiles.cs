using System.Text;
using Microsoft.AspNetCore.Http;

namespace DemoSync.Api.Tests;

internal static class TestFiles
{
    public const string ValidCast =
        "{\"version\":2,\"width\":80,\"height\":24,\"timestamp\":1700000000}\n" +
        "[0.5,\"o\",\"user@host:~$ dotnet build\\r\\n\"]\n" +
        "[2.0,\"o\",\"Build succeeded.\\r\\n\"]\n" +
        "[2.5,\"o\",\"process exited with code 0\\r\\n\"]\n";

    public const string CastWithoutTimestamps =
        "{\"version\":2,\"width\":80,\"height\":24}\n" +
        "[\"n/a\",\"o\",\"user@host:~$ dotnet build\\r\\n\"]\n" +
        "[\"n/a\",\"o\",\"Build succeeded.\\r\\n\"]\n";

    public const string JsonlWithoutTimestamps =
        "{\"event\":\"command\",\"output\":\"docker build .\"}\n" +
        "{\"event\":\"exit\",\"output\":\"exit code 0\"}\n";

    public static IFormFile Create(string fileName, string content, string contentType) =>
        Create(fileName, Encoding.UTF8.GetBytes(content), contentType);

    public static IFormFile Create(string fileName, byte[] content, string contentType)
    {
        var stream = new MemoryStream(content);
        return new FormFile(stream, 0, content.Length, "file", fileName)
        {
            Headers = new HeaderDictionary(),
            ContentType = contentType
        };
    }

    public static IFormFile CreateWithReportedLength(string fileName, long length, string contentType)
    {
        var stream = new MemoryStream([1]);
        return new FormFile(stream, 0, length, "file", fileName)
        {
            Headers = new HeaderDictionary(),
            ContentType = contentType
        };
    }
}
