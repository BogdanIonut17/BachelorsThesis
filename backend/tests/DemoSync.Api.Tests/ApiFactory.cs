using DemoSync.Api.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DemoSync.Api.Tests;

public class ApiFactory : WebApplicationFactory<Program>
{
    public string StorageRoot { get; } = Path.Combine(Path.GetTempPath(), "demosync-tests", Guid.NewGuid().ToString("N"));

    /// <summary>When set, the fake extractor blocks until this task completes.</summary>
    protected virtual Task? ProcessingGate => null;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Storage:RootPath", StorageRoot);

        var gate = ProcessingGate;
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IMediaExtractor>();
            services.AddSingleton<IMediaExtractor>(new FakeMediaExtractor(gate));
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        if (disposing && Directory.Exists(StorageRoot))
        {
            try
            {
                Directory.Delete(StorageRoot, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort cleanup of temporary test storage.
            }
        }
    }
}

public sealed class GatedApiFactory : ApiFactory
{
    private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

    protected override Task? ProcessingGate => _gate.Task;

    public void ReleaseProcessing() => _gate.TrySetResult();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            ReleaseProcessing();
        }

        base.Dispose(disposing);
    }
}
