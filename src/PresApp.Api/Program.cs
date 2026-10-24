using PresApp.Api.Auth;
using PresApp.Api.Cloudflare;
using PresApp.Api.Endpoints;
using PresApp.Api.Hosting;
using PresApp.Infrastructure;
using PresApp.Infrastructure.Data;
using PresApp.Infrastructure.Seeding;
using Microsoft.EntityFrameworkCore;
using Serilog;

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory
});

builder.Host.UseWindowsService(o => o.ServiceName = "PresApp");
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true);

builder.Host.UseSerilog((context, logger) => logger
    .ReadFrom.Configuration(context.Configuration)
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .WriteTo.File(Path.Combine(AppContext.BaseDirectory, "logs", "presapp-.log"),
        rollingInterval: RollingInterval.Day, retainedFileCountLimit: 30));

var connectionString = builder.Configuration.GetConnectionString("PresApp")
    ?? throw new InvalidOperationException("Missing connection string 'PresApp'.");

builder.Services.AddPresAppData(connectionString);
builder.Services.AddPresAppAuth(builder.Configuration, builder.Environment);
builder.Services.Configure<CloudflareOptions>(builder.Configuration.GetSection(CloudflareOptions.Section));
builder.Services.AddHttpClient<CloudflareAccessAdmin>(c => c.Timeout = TimeSpan.FromSeconds(15));
builder.Services.AddExceptionHandler<PresAppExceptionHandler>();
builder.Services.AddProblemDetails();
builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));

var app = builder.Build();

// Command line tools (run on the server, not as a service).
if (args.Length >= 2 && args[0] == "set-emergency-password")
{
    return await EmergencyAccess.SetPasswordFromConsoleAsync(app.Services, args[1]);
}

await PrepareDatabaseAsync(app);

app.UseExceptionHandler();
app.UseSerilogRequestLogging(o => o.GetLevel = (ctx, _, ex) =>
    ex is not null || ctx.Response.StatusCode >= 500 ? Serilog.Events.LogEventLevel.Error : Serilog.Events.LogEventLevel.Debug);
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();
app.UseMiddleware<RequestAuditMiddleware>();

app.MapHealthEndpoints();
app.MapEmergencyEndpoints();
app.MapMeEndpoints();
app.MapCatalogEndpoints();
app.MapAdminUserEndpoints();

// Unknown /api routes are 404; everything else is the React app.
app.MapFallback("/api/{**path}", () => Results.NotFound());
app.MapFallbackToFile("index.html");

await app.RunAsync();
return 0;

static async Task PrepareDatabaseAsync(WebApplication app)
{
    if (!app.Configuration.GetValue("Database:MigrateOnStartup", true)) return;
    await using var scope = app.Services.CreateAsyncScope();
    var db = scope.ServiceProvider.GetRequiredService<PresAppDbContext>();
    if (db.Database.IsSqlServer()) await db.Database.MigrateAsync();
    else await db.Database.EnsureCreatedAsync();
    var bootstrap = app.Configuration[$"{PresAppAuthOptions.Section}:{nameof(PresAppAuthOptions.BootstrapAdminEmail)}"];
    await scope.ServiceProvider.GetRequiredService<DatabaseSeeder>().SeedAsync(bootstrap);
}

public partial class Program;
