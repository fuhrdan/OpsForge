using OpenTelemetry.Exporter;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://127.0.0.1:5091");
var tracing = builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource.AddService("OpsForge.DemoService"))
    .WithTracing(provider => provider.AddAspNetCoreInstrumentation());
var otlpEndpoint = Environment.GetEnvironmentVariable("OPSFORGE_OTLP_ENDPOINT");
if (!string.IsNullOrWhiteSpace(otlpEndpoint))
{
    if (!Uri.TryCreate(otlpEndpoint, UriKind.Absolute, out var endpoint) ||
        endpoint.Scheme is not ("http" or "https"))
        throw new InvalidOperationException("OPSFORGE_OTLP_ENDPOINT must be an absolute http(s) URL, such as http://localhost:4317.");
    tracing.WithTracing(provider => provider.AddOtlpExporter(options =>
    {
        options.Endpoint = endpoint;
        options.Protocol = OtlpExportProtocol.Grpc;
    }));
}

var app = builder.Build();

app.MapGet("/", () => Results.Ok(new
{
    service = "OpsForge.DemoService",
    version = "0.9.0",
    status = "healthy",
    processId = Environment.ProcessId,
    timeUtc = DateTimeOffset.UtcNow
}));

app.MapGet("/health", () => Results.Ok(new
{
    ok = true,
    service = "OpsForge.DemoService",
    version = "0.9.0",
    processId = Environment.ProcessId,
    timeUtc = DateTimeOffset.UtcNow
}));

Console.Title = "OpsForge Demo Service v0.9.0";
Console.WriteLine("OpsForge.DemoService v0.9.0");
Console.WriteLine("Health endpoint: http://127.0.0.1:5091/health");
Console.WriteLine("OpsForge monitors this process, its HTTP endpoint, and TCP port 5091.");
Console.WriteLine();

app.Run();
