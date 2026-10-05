using System.Threading.RateLimiting;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Shortener.Api.Orchestration;
using Shortener.Api.Persistence;
using Shortener.Api.Services;

var builder = WebApplication.CreateBuilder(args);
var dataDirectory = Path.Combine(builder.Environment.ContentRootPath, "data");
var webRootDirectory = Path.Combine(builder.Environment.ContentRootPath, "wwwroot");
Directory.CreateDirectory(dataDirectory);
Directory.CreateDirectory(webRootDirectory);
var connectionString = builder.Configuration.GetConnectionString("Shortener")
    ?? $"Data Source={Path.Combine(dataDirectory, "shortener.db")}";

builder.Services.AddDbContext<AppDbContext>(options => options.UseSqlite(connectionString));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<ShortLinkService>();
builder.Services.AddSingleton<AuditTrail>();
builder.Services.AddSingleton<IAgentProvider, DeterministicAgentProvider>();
builder.Services.AddSingleton<OrchestrationService>();
builder.Services.AddControllers().AddJsonOptions(options =>
    options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddOpenApi();
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("create-link", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 20,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
            AutoReplenishment = true
        }));
    options.AddPolicy("redirect", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 120,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
            AutoReplenishment = true
        }));
});

var app = builder.Build();
await using (var scope = app.Services.CreateAsyncScope())
{
    await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.EnsureCreatedAsync();
}

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

var webRootProvider = new PhysicalFileProvider(webRootDirectory);
app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = webRootProvider });
app.UseStaticFiles(new StaticFileOptions { FileProvider = webRootProvider });
app.UseRateLimiter();
app.MapControllers();
app.MapGet("/health", () => Results.Ok(new
{
    status = "ok",
    staticAssetsAvailable = webRootProvider.GetFileInfo("index.html").Exists
        && webRootProvider.GetFileInfo("app.js").Exists
        && webRootProvider.GetFileInfo("styles.css").Exists
}));
app.MapGet("/{code:regex(^[A-Za-z0-9_-]+$)}", async (string code, HttpContext context, ShortLinkService links, CancellationToken cancellationToken) =>
{
    var link = await links.ResolveAsync(
        code,
        context.Request.Headers.Referer.FirstOrDefault(),
        null,
        cancellationToken);
    return link is null ? Results.NotFound() : Results.Redirect(link.TargetUrl);
}).RequireRateLimiting("redirect");

app.Run();

public partial class Program;
