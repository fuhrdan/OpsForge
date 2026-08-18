var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://127.0.0.1:5091");

var app = builder.Build();

app.MapGet("/", () => Results.Ok(new
{
    service = "OpsForge.DemoService",
    version = "0.7.2",
    status = "healthy",
    processId = Environment.ProcessId,
    timeUtc = DateTimeOffset.UtcNow
}));

app.MapGet("/health", () => Results.Ok(new
{
    ok = true,
    service = "OpsForge.DemoService",
    version = "0.7.2",
    processId = Environment.ProcessId,
    timeUtc = DateTimeOffset.UtcNow
}));

Console.Title = "OpsForge Demo Service v0.7.2";
Console.WriteLine("OpsForge.DemoService v0.7.2");
Console.WriteLine("Health endpoint: http://127.0.0.1:5091/health");
Console.WriteLine("OpsForge monitors this process, its HTTP endpoint, and TCP port 5091.");
Console.WriteLine();

app.Run();
