using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.IdentityModel.Tokens;
using PresApp.Api.Auth;
using PresApp.Domain.Security;
using PresApp.Tests.Support;

namespace PresApp.Tests;

public sealed class AuthenticationTests : IClassFixture<TestApp>
{
    private readonly TestApp app;
    public AuthenticationTests(TestApp app) => this.app = app;

    [Fact]
    public async Task Registered_user_with_valid_Cloudflare_token_gets_profile_and_permissions()
    {
        var me = await app.ClientFor(TestApp.AdminEmail).GetFromJsonAsync<JsonElement>("/api/me");

        Assert.Equal(TestApp.AdminEmail, me.GetProperty("email").GetString());
        Assert.Equal(RoleNames.Admin, me.GetProperty("role").GetString());
        var permissions = me.GetProperty("permissions").EnumerateArray().Select(p => p.GetString()).ToList();
        Assert.Equal(Permissions.All.Count, permissions.Count);
    }

    [Fact]
    public async Task Unregistered_email_is_rejected_with_not_registered_code()
    {
        var response = await app.ClientFor("stranger@example.com").GetAsync("/api/me");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("not_registered", body.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Request_without_token_is_rejected()
    {
        var response = await app.CreateClient().GetAsync("/api/me");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("wrong-audience")]
    [InlineData("expired")]
    [InlineData("foreign-key")]
    public async Task Invalid_tokens_are_rejected(string kind)
    {
        var token = kind switch
        {
            "wrong-audience" => app.CreateToken(TestApp.AdminEmail, audience: "other-app"),
            "expired" => app.CreateToken(TestApp.AdminEmail, expires: DateTime.UtcNow.AddMinutes(-10)),
            _ => app.CreateToken(TestApp.AdminEmail, key: new RsaSecurityKey(RSA.Create(2048)) { KeyId = "test-key" })
        };
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Add(CloudflareAccessHandler.HeaderName, token);

        var response = await client.GetAsync("/api/me");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Emergency_login_does_not_exist_on_the_public_port()
    {
        var response = await app.CreateClient().PostAsJsonAsync("/api/emergency/login", new { email = TestApp.AdminEmail, password = "x" });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Health_is_public()
    {
        var response = await app.CreateClient().GetAsync("/api/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
