using System.Globalization;
using System.Text.Json.Serialization;
using DemoSync.Api.Services;
using DemoSync.Api.Workers;
using Microsoft.AspNetCore.Http.Features;

// Form values such as "2.5" must bind identically regardless of the host machine's locale.
CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;

var builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddControllers()
    .AddJsonOptions(options => options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));

builder.Services.Configure<FormOptions>(options => options.MultipartBodyLengthLimit = 524_288_000);
builder.Services.Configure<StorageOptions>(builder.Configuration.GetSection("Storage"));
builder.Services.Configure<FfmpegOptions>(builder.Configuration.GetSection("Ffmpeg"));

builder.Services.AddSingleton<ISessionRepository, InMemorySessionRepository>();
builder.Services.AddSingleton<ISessionStorage, LocalSessionStorage>();
builder.Services.AddSingleton<IBackgroundJobQueue, BackgroundJobQueue>();
builder.Services.AddSingleton<IProcessRunner, ProcessRunner>();
builder.Services.AddSingleton<IMediaExtractor, FfmpegMediaExtractor>();
builder.Services.AddSingleton<IUploadValidator, UploadValidator>();
builder.Services.AddSingleton<ILogIngestionService, LogIngestionService>();
builder.Services.AddSingleton<ISensitiveDataScanner, SensitiveDataScanner>();
builder.Services.AddSingleton<ISessionUploadService, SessionUploadService>();
builder.Services.AddHostedService<SessionProcessingWorker>();

var app = builder.Build();

app.MapControllers();

app.Run();

public partial class Program;
