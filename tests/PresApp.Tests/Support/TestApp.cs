using System.Net.Http.Headers;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using PresApp.Api.Auth;
using PresApp.Domain.Security;
using PresApp.Infrastructure.Auditing;
using PresApp.Infrastructure.Data;

namespace PresApp.Tests.Support;

/// <summary>
/// The real API on an in-memory SQLite database, with a fake Cloudflare that signs tokens
/// with a key generated for the test run.
/// </summary>
public sealed class TestApp : WebApplicationFactory<Program>
{
    public const string TeamDomain = "test.cloudflareaccess.com";
    public const string Audience = "presapp-test-aud";
    public const string AdminEmail = "admin@enermech.com";

    private readonly SqliteConnection connection = new("DataSource=:memory:");
    public RsaSecurityKey SigningKey { get; } = new(RSA.Create(2048)) { KeyId = "test-key" };

    public TestApp() => connection.Open();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:PresApp", "unused");
        builder.UseSetting("Auth:CloudflareTeamDomain", TeamDomain);
        builder.UseSetting("Auth:CloudflareAudience", Audience);
        builder.UseSetting("Auth:BootstrapAdminEmail", AdminEmail);

        builder.ConfigureTestServices(services =>
        {
            foreach (var d in services.Where(d =>
                         d.ServiceType == typeof(DbContextOptions<PresAppDbContext>) ||
                         d.ServiceType == typeof(IDbContextOptionsConfiguration<PresAppDbContext>)).ToList())
            {
                services.Remove(d);
            }
            services.AddDbContext<PresAppDbContext>((sp, o) => o
                .UseSqlite(connection)
                .AddInterceptors(sp.GetRequiredService<AuditSaveChangesInterceptor>()));

            services.AddSingleton(sp => new CloudflareKeyProvider(
                new HttpClient(new FakeCertsHandler(SigningKey)),
                sp.GetRequiredService<IOptions<PresAppAuthOptions>>(),
                TimeProvider.System,
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<CloudflareKeyProvider>>()));
        });
    }

    public string CreateToken(string email, string? audience = null, DateTime? expires = null, SecurityKey? key = null)
    {
        var now = DateTime.UtcNow;
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = "https://" + TeamDomain,
            Audience = audience ?? Audience,
            Claims = new Dictionary<string, object> { ["email"] = email },
            IssuedAt = now.AddMinutes(-1),
            NotBefore = now.AddMinutes(-1),
            Expires = expires ?? now.AddHours(1),
            SigningCredentials = new SigningCredentials(key ?? SigningKey, SecurityAlgorithms.RsaSha256)
        });
    }

    public HttpClient ClientFor(string email)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add(CloudflareAccessHandler.HeaderName, CreateToken(email));
        return client;
    }

    public async Task<T> WithDbAsync<T>(Func<PresAppDbContext, Task<T>> action)
    {
        await using var scope = Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<PresAppDbContext>());
    }

    public async Task AddUserAsync(string email, string role)
    {
        await WithDbAsync(async db =>
        {
            var roleId = await db.Roles.Where(r => r.Name == role).Select(r => r.Id).FirstAsync();
            db.Users.Add(new AppUser { Email = email, DisplayName = email, RoleId = roleId, CreatedAtUtc = DateTime.UtcNow, CreatedBy = "test" });
            return await db.SaveChangesAsync();
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing) connection.Dispose();
    }

    private sealed class FakeCertsHandler(RsaSecurityKey key) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var jwk = JsonWebKeyConverter.ConvertFromRSASecurityKey(new RsaSecurityKey(key.Rsa.ExportParameters(false)) { KeyId = key.KeyId });
            jwk.Use = "sig";
            jwk.Alg = SecurityAlgorithms.RsaSha256;
            var json = System.Text.Json.JsonSerializer.Serialize(new { keys = new[] { new
            {
                kid = jwk.Kid, kty = jwk.Kty, alg = jwk.Alg, use = jwk.Use, n = jwk.N, e = jwk.E
            } } });
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(json, new MediaTypeHeaderValue("application/json"))
            });
        }
    }
}
