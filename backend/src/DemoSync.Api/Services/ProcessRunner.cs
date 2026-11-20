using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace DemoSync.Api.Services;

public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);

public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken);
}

/// <summary>Runs external executables without a shell; arguments are passed as discrete argv entries.</summary>
public sealed class ProcessRunner : IProcessRunner
{
    public async Task<ProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };

        try
        {
            process.Start();
        }
        catch (Exception exception) when (exception is Win32Exception or FileNotFoundException or DirectoryNotFoundException)
        {
            throw new MediaExtractionException(
                $"Could not start '{fileName}'. Ensure it is installed and on PATH, or configure its path in the 'Ffmpeg' settings.",
                exception);
        }

        process.StandardInput.Close();

        var standardOutput = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var standardError = process.StandardError.ReadToEndAsync(CancellationToken.None);

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);

        try
        {
            await process.WaitForExitAsync(timeoutSource.Token);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            await process.WaitForExitAsync(CancellationToken.None);

            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            throw new MediaExtractionException($"'{fileName}' did not finish within {timeout.TotalSeconds:0} seconds and was terminated.");
        }

        return new ProcessResult(process.ExitCode, await standardOutput, await standardError);
    }

    private static void TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
        {
            // The process already exited.
        }
    }
}
