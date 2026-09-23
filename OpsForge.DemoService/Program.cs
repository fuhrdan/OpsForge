var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://127.0.0.1:5091");

var app = builder.Build();

app.MapGet("/", () => Results.Ok(new
{
    service = "OpsForge.DemoService",
    version = "0.8.0",
    status = "healthy",
    processId = Environment.ProcessId,
    timeUtc = DateTimeOffset.UtcNow
}));

app.MapGet("/health", () => Results.Ok(new
{
    ok = true,
    service = "OpsForge.DemoService",
    version = "0.8.0",
    processId = Environment.ProcessId,
    timeUtc = DateTimeOffset.UtcNow
}));

Console.Title = "OpsForge Demo Service v0.8.0";
Console.WriteLine("OpsForge.DemoService v0.8.0");
Console.WriteLine("Health endpoint: http://127.0.0.1:5091/health");
Console.WriteLine("OpsForge monitors this process, its HTTP endpoint, and TCP port 5091.");
Console.WriteLine();

app.Run();
