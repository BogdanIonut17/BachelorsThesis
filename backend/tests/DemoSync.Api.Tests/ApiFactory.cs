using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace DemoSync.Api.Tests;

public sealed class ApiFactory : WebApplicationFactory<Program>
{
    public string StorageRoot { get; } = Path.Combine(Path.GetTempPath(), "demosync-tests", Guid.NewGuid().ToString("N"));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Storage:RootPath", StorageRoot);
        builder.UseSetting("Processing:StepDelayMilliseconds", "10");
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
