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
builder.Services.AddScoped<PresApp.Api.Assets.AssetQueries>();
builder.Services.AddScoped<PresApp.Api.Assets.AssetEditor>();
builder.Services.AddScoped<PresApp.Api.Assets.RemovedFromSiteAlignment>();
builder.Services.AddScoped<PresApp.Api.Excel.UnitWorkbookExporter>();
builder.Services.AddScoped<PresApp.Api.Excel.PipingWorkbookExporter>();
builder.Services.AddScoped<PresApp.Api.Piping.PipingNoteService>();
builder.Services.AddScoped<PresApp.Api.Piping.PipingSummaryService>();
builder.Services.AddScoped<PresApp.Api.Excel.ExcelImportService>();
builder.Services.AddScoped<PresApp.Api.Excel.OnboardingService>();
builder.Services.AddScoped<PresApp.Api.Excel.ExcelTemplateService>();
builder.Services.AddScoped<PresApp.Api.Reporting.UnitDashboard>();
builder.Services.AddScoped<PresApp.Api.Monitoring.MonitoringService>();
builder.Services.AddScoped<PresApp.Api.Groups.GroupService>();
builder.Services.AddScoped<PresApp.Api.Piping.PipeSpecService>();
builder.Services.AddScoped<PresApp.Api.CustomFields.CustomFieldService>();
builder.Services.Configure<PresApp.Api.Simpro.SimproOptions>(builder.Configuration.GetSection(PresApp.Api.Simpro.SimproOptions.Section));
builder.Services.AddSingleton<PresApp.Api.Simpro.ISimproConnectionFactory, PresApp.Api.Simpro.SqlServerSimproConnectionFactory>();
builder.Services.AddScoped<PresApp.Api.Simpro.SimproStore>();
builder.Services.AddScoped<PresApp.Api.Simpro.SimproSyncService>();
if (!args.Contains("import-sharepoint") && !args.Contains("set-emergency-password") && !args.Contains(PresApp.Api.Assets.RemovedFromSiteAlignment.ToolName))
    builder.Services.AddHostedService<PresApp.Api.Simpro.SimproScheduler>();
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

if (args.Length >= 2 && args[0] == "import-sharepoint")
{
    await PrepareDatabaseAsync(app);
    return await RunSharePointImportAsync(app, args[1], args.Contains("--apply"));
}

if (args.Length >= 2 && args[0] == PresApp.Api.Assets.RemovedFromSiteAlignment.ToolName)
{
    await PrepareDatabaseAsync(app);
    return await RunRemovedFromSiteAlignmentAsync(app, args[1], args.Contains("--apply"));
}

await PrepareDatabaseAsync(app);

app.UseExceptionHandler();
app.UseSerilogRequestLogging(o => o.GetLevel = (ctx, _, ex) =>
    ctx.RequestAborted.IsCancellationRequested ? Serilog.Events.LogEventLevel.Debug
    : ex is ApiProblem ? Serilog.Events.LogEventLevel.Information   // expected answers (400, 404, 409), not failures
    : ex is not null || ctx.Response.StatusCode >= 500 ? Serilog.Events.LogEventLevel.Error
    : Serilog.Events.LogEventLevel.Debug);
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseMiddleware<CsrfHeaderMiddleware>();
app.UseAuthentication();
app.UseAuthorization();
app.UseMiddleware<RequestAuditMiddleware>();

app.MapHealthEndpoints();
app.MapEmergencyEndpoints();
app.MapMeEndpoints();
app.MapCatalogEndpoints();
app.MapAdminUserEndpoints();
app.MapAssetEndpoints();
app.MapExcelEndpoints();
app.MapSimproEndpoints();
app.MapMonitoringEndpoints();
app.MapCustomFieldEndpoints();
app.MapOptionSetEndpoints();

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

static async Task<int> RunRemovedFromSiteAlignmentAsync(WebApplication app, string folder, bool apply)
{
    Directory.CreateDirectory(folder);
    await using var scope = app.Services.CreateAsyncScope();
    var tool = scope.ServiceProvider.GetRequiredService<PresApp.Api.Assets.RemovedFromSiteAlignment>();
    var rows = await tool.RunAsync(apply);
    var file = Path.Combine(folder, $"removed-from-site-{(apply ? "applied" : "preview")}-{DateTime.Now:yyyyMMdd-HHmm}.csv");
    await File.WriteAllTextAsync(file, PresApp.Api.Assets.RemovedFromSiteAlignment.ToCsv(rows), new System.Text.UTF8Encoding(true));
    Console.WriteLine(apply ? "Alignment APPLIED (cases A and B of active assets)." : "Preview only: nothing was saved. Add --apply to change cases A and B.");
    Console.WriteLine(PresApp.Api.Assets.RemovedFromSiteAlignment.Summary(rows));
    Console.WriteLine($"Report: {file}");
    return 0;
}

static async Task<int> RunSharePointImportAsync(WebApplication app, string folder, bool apply)
{
    await using var scope = app.Services.CreateAsyncScope();
    var importer = scope.ServiceProvider.GetRequiredService<PresApp.Infrastructure.SharePointMigration.SharePointImporter>();
    var report = await importer.RunAsync(folder, apply);
    var file = Path.Combine(folder, $"migration-report-{DateTime.Now:yyyyMMdd-HHmm}.md");
    await File.WriteAllTextAsync(file, report.ToMarkdown());
    Console.WriteLine(apply ? "Migration APPLIED." : "Preview only: nothing was saved. Add --apply to load the data.");
    Console.WriteLine($"Verification: {(report.CountsMatch ? "all counts match" : "DIFFERENCES FOUND")}. Items to review: {report.Issues.Count}.");
    Console.WriteLine($"Report: {file}");
    return report.CountsMatch ? 0 : 2;
}

public partial class Program;
